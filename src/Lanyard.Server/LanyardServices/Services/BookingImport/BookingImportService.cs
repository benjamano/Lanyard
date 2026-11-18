using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.OperationsAlerts;
using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.BookingImport;

public sealed class BookingImportService : IBookingImportService
{
    public const string Source = "JustStepOnline";
    public const string ImportUserId = "juststep-import";
    public const string AlertUrl = "/manage/booking-import";
    private const string ProtectorPurpose = "BookingImport.Password";
    private const string NoAccess = "You can only manage booking import for your own location.";

    // How far around today the report is pulled: a little history (late changes) and a year ahead.
    private const int WindowBackDays = 7;
    private const int WindowForwardDays = 365;

    // Fallbacks when the report row carries no usable time.
    private static readonly TimeOnly DefaultStart = new(9, 0);
    private static readonly TimeSpan DefaultLength = TimeSpan.FromHours(2);

    private readonly IDbContextFactory<ApplicationDbContext> _factory;
    private readonly ISystemDbContextFactory _systemFactory;
    private readonly IJustStepClient _client;
    private readonly IOperationsAlertWriter _alertWriter;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BookingImportService> _logger;

    public BookingImportService(
        IDbContextFactory<ApplicationDbContext> factory,
        ISystemDbContextFactory systemFactory,
        IJustStepClient client,
        IOperationsAlertWriter alertWriter,
        IDataProtectionProvider dataProtection,
        TimeProvider timeProvider,
        ILogger<BookingImportService> logger)
    {
        _factory = factory;
        _systemFactory = systemFactory;
        _client = client;
        _alertWriter = alertWriter;
        _protector = dataProtection.CreateProtector(ProtectorPurpose);
        _timeProvider = timeProvider;
        _logger = logger;
    }

    private static string DedupKey(int locationId) => $"juststep-import:{locationId}";

    public async Task<Result<BookingImportSettingsView>> GetSettingsAsync(LocationScope scope, int locationId)
    {
        try
        {
            if (!SchedulingAccess.CanManageLocation(scope, locationId))
            {
                return Result<BookingImportSettingsView>.Fail(NoAccess);
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            BookingImportSettings? settings = await ctx.BookingImportSettings
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.LocationId == locationId);

            return Result<BookingImportSettingsView>.Ok(ToView(settings, locationId));
        }
        catch (Exception ex)
        {
            return Result<BookingImportSettingsView>.Fail($"Failed to load booking import settings: {ex.Message}");
        }
    }

    public async Task<Result<BookingImportSettingsView>> SaveSettingsAsync(LocationScope scope, BookingImportSettingsInput input, string actingUserId)
    {
        try
        {
            if (!SchedulingAccess.CanManageLocation(scope, input.LocationId))
            {
                return Result<BookingImportSettingsView>.Fail(NoAccess);
            }

            string username = input.Username?.Trim() ?? string.Empty;

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            // The company filter means a location in another company simply isn't found.
            Location? location = await ctx.Locations.FirstOrDefaultAsync(x => x.Id == input.LocationId);
            if (location is null)
            {
                return Result<BookingImportSettingsView>.Fail("That location wasn't found.");
            }

            BookingImportSettings? existing = await ctx.BookingImportSettings.FirstOrDefaultAsync(x => x.LocationId == input.LocationId);

            if (existing is null)
            {
                existing = new BookingImportSettings { CompanyId = location.CompanyId, LocationId = input.LocationId, Id = Guid.NewGuid() };
                ctx.BookingImportSettings.Add(existing);
            }

            if (!string.IsNullOrWhiteSpace(input.NewPassword))
            {
                existing.PasswordProtected = _protector.Protect(input.NewPassword.Trim());
            }

            // Can't turn it on without something to sign in with.
            if (input.Enabled && (username.Length == 0 || !existing.HasPassword))
            {
                return Result<BookingImportSettingsView>.Fail("Enter the Just Step Online email and password before switching import on.");
            }

            if (input.FrequencyHours is < BookingImportSchedule.MinFrequencyHours or > BookingImportSchedule.MaxFrequencyHours)
            {
                return Result<BookingImportSettingsView>.Fail("Import frequency must be between 1 and 24 hours.");
            }

            if (input.ActiveStartHour is < 0 or > 23 || input.ActiveEndHour is < 1 or > 24 || input.ActiveEndHour <= input.ActiveStartHour)
            {
                return Result<BookingImportSettingsView>.Fail("The 'run between' hours must be a valid daytime window (start before end).");
            }

            existing.Enabled = input.Enabled;
            existing.Username = username;
            existing.FrequencyHours = input.FrequencyHours;
            existing.ActiveStartHour = input.ActiveStartHour;
            existing.ActiveEndHour = input.ActiveEndHour;
            existing.UpdateDate = _timeProvider.GetUtcNow().UtcDateTime;
            existing.UpdatedByUserId = actingUserId;

            await ctx.SaveChangesAsync();

            // Turning import off clears any open "import failed" alert for this location - the sweep
            // skips disabled locations, so nothing else would ever resolve it.
            if (!input.Enabled)
            {
                await ResolveAlertAsync(location.CompanyId, input.LocationId, CancellationToken.None);
            }

            return Result<BookingImportSettingsView>.Ok(ToView(existing, input.LocationId));
        }
        catch (Exception ex)
        {
            return Result<BookingImportSettingsView>.Fail($"Failed to save booking import settings: {ex.Message}");
        }
    }

    public async Task<Result<bool>> TestConnectionAsync(LocationScope scope, int locationId, string username, string? password)
    {
        try
        {
            if (!SchedulingAccess.CanManageLocation(scope, locationId))
            {
                return Result<bool>.Fail(NoAccess);
            }

            string user = username?.Trim() ?? string.Empty;
            string? pass = string.IsNullOrWhiteSpace(password) ? null : password.Trim();

            if (pass is null)
            {
                await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();
                BookingImportSettings? settings = await ctx.BookingImportSettings
                    .AsNoTracking()
                    .TagWithCallSite()
                    .FirstOrDefaultAsync(x => x.LocationId == locationId);

                if (settings is null || !settings.HasPassword)
                {
                    return Result<bool>.Fail("Enter a password to test, or save one first.");
                }

                pass = TryUnprotect(settings.PasswordProtected);
                if (user.Length == 0)
                {
                    user = settings.Username;
                }
            }

            if (user.Length == 0 || string.IsNullOrEmpty(pass))
            {
                return Result<bool>.Fail("Enter the Just Step Online email and password to test.");
            }

            return await _client.TestLoginAsync(user, pass, CancellationToken.None);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Couldn't test the connection: {ex.Message}");
        }
    }

    public async Task<Result<BookingImportRunView>> RunNowAsync(LocationScope scope, int locationId)
    {
        try
        {
            if (!SchedulingAccess.CanManageLocation(scope, locationId))
            {
                return Result<BookingImportRunView>.Fail(NoAccess);
            }

            int companyId;
            await using (ApplicationDbContext ctx = await _factory.CreateDbContextAsync())
            {
                Location? location = await ctx.Locations.AsNoTracking().TagWithCallSite().FirstOrDefaultAsync(x => x.Id == locationId);
                if (location is null)
                {
                    return Result<BookingImportRunView>.Fail("That location wasn't found.");
                }

                companyId = location.CompanyId;
            }

            BookingImportRunView run = await RunImportAsync(companyId, locationId, BookingImportTrigger.Manual);
            return Result<BookingImportRunView>.Ok(run);
        }
        catch (Exception ex)
        {
            return Result<BookingImportRunView>.Fail($"Couldn't run the import: {ex.Message}");
        }
    }

    public async Task<Result<List<BookingImportRunView>>> GetHistoryAsync(LocationScope scope, int locationId, int take = 20)
    {
        try
        {
            if (!SchedulingAccess.CanManageLocation(scope, locationId))
            {
                return Result<List<BookingImportRunView>>.Fail(NoAccess);
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<BookingImportRun> runs = await ctx.BookingImportRuns
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.LocationId == locationId)
                .OrderByDescending(x => x.StartedAtUtc)
                .Take(Math.Clamp(take, 1, 200))
                .ToListAsync();

            return Result<List<BookingImportRunView>>.Ok([.. runs.Select(ToView)]);
        }
        catch (Exception ex)
        {
            return Result<List<BookingImportRunView>>.Fail($"Failed to load import history: {ex.Message}");
        }
    }

    public async Task<BookingImportRunView> RunImportAsync(int companyId, int locationId, BookingImportTrigger trigger, CancellationToken cancellationToken = default)
    {
        DateTime startedAt = _timeProvider.GetUtcNow().UtcDateTime;

        BookingImportRun run = new()
        {
            Id = Guid.NewGuid(),
            CompanyId = companyId,
            LocationId = locationId,
            StartedAtUtc = startedAt,
            Trigger = trigger,
            Status = BookingImportRunStatus.Failed
        };

        try
        {
            await using ApplicationDbContext ctx = await _systemFactory.CreateDbContextAsync(cancellationToken);

            BookingImportSettings? settings = await ctx.BookingImportSettings
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.LocationId == locationId, cancellationToken);

            if (settings is null || !settings.Enabled || !settings.HasPassword || string.IsNullOrWhiteSpace(settings.Username))
            {
                run.Status = BookingImportRunStatus.Skipped;
                run.Error = "Import is off or has no credentials for this location.";
                await ResolveAlertAsync(companyId, locationId, cancellationToken);
                return await SaveRunAsync(ctx, run, cancellationToken);
            }

            string password = TryUnprotect(settings.PasswordProtected);
            if (string.IsNullOrEmpty(password))
            {
                run.Status = BookingImportRunStatus.Failed;
                run.Error = "The stored password couldn't be read. Re-enter it on the booking import page.";
                await RaiseAlertAsync(companyId, locationId, run.Error, cancellationToken);
                return await SaveRunAsync(ctx, run, cancellationToken);
            }

            DateOnly today = RotaTime.Today(startedAt);
            DateOnly from = today.AddDays(-WindowBackDays);
            DateOnly to = today.AddDays(WindowForwardDays);
            Result<IReadOnlyList<ExternalBookingDto>> fetch = await _client.FetchBookingsAsync(
                settings.Username, password, from, to, cancellationToken);

            if (!fetch.IsSuccess)
            {
                bool badLogin = fetch.Error!.Contains("sign-in", StringComparison.OrdinalIgnoreCase) || fetch.Error.Contains("rejected", StringComparison.OrdinalIgnoreCase);
                run.Status = badLogin ? BookingImportRunStatus.LoginFailed : BookingImportRunStatus.Failed;
                run.Error = Truncate(fetch.Error);
                await RaiseAlertAsync(companyId, locationId, fetch.Error, cancellationToken);
                return await SaveRunAsync(ctx, run, cancellationToken);
            }

            await ApplyBookingsAsync(ctx, companyId, locationId, fetch.Data!, from, to, startedAt, run, cancellationToken);

            bool anyChanges = run.Imported + run.Updated > 0;
            run.Status = run.Failed > 0
                ? BookingImportRunStatus.PartialSuccess
                : anyChanges ? BookingImportRunStatus.Success : BookingImportRunStatus.NoChanges;

            await ResolveAlertAsync(companyId, locationId, cancellationToken);
            return await SaveRunAsync(ctx, run, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Booking import failed for location {LocationId}", locationId);
            run.Status = BookingImportRunStatus.Failed;
            run.Error = Truncate($"The import hit an error: {ex.Message}");

            try
            {
                await using ApplicationDbContext ctx = await _systemFactory.CreateDbContextAsync(cancellationToken);
                await RaiseAlertAsync(companyId, locationId, run.Error, cancellationToken);
                return await SaveRunAsync(ctx, run, cancellationToken);
            }
            catch (Exception saveEx)
            {
                _logger.LogError(saveEx, "Couldn't record the failed booking import run for location {LocationId}", locationId);
                return ToView(run);
            }
        }
    }

    // Upserts each report row into PartyBookings, keyed on (location, source, ref). New rows are
    // created as booked; an existing row is only written (and counted as Updated) when a mapped
    // field actually changed, so an unchanged run is a true no-op. A booking that has dropped out of
    // Just Step within the imported window is cancelled, so cancellations there flow through too.
    // A booking's Lanyard status is otherwise left alone, so a local cancellation sticks.
    private async Task ApplyBookingsAsync(ApplicationDbContext ctx, int companyId, int locationId,
        IReadOnlyList<ExternalBookingDto> bookings, DateOnly from, DateOnly to, DateTime now, BookingImportRun run, CancellationToken ct)
    {
        run.BookingsSeen = bookings.Count;

        // Just Step can list the same reference more than once in a window; keep the last and count
        // each ref once, so the run's totals are honest and we never insert the same booking twice.
        List<ExternalBookingDto> unique = [.. bookings
            .GroupBy(x => x.Ref)
            .Select(g => g.Last())];

        List<string> refs = [.. unique.Select(x => x.Ref)];

        Dictionary<string, PartyBooking> existing = await ctx.PartyBookings
            .Where(x => x.LocationId == locationId && x.ExternalSource == Source && x.ExternalRef != null && refs.Contains(x.ExternalRef))
            .ToDictionaryAsync(x => x.ExternalRef!, ct);

        foreach (ExternalBookingDto dto in unique)
        {
            try
            {
                TimeOnly startTime = dto.StartTime ?? DefaultStart;
                DateTime startUtc = RotaTime.ToUtc(dto.PartyDate, startTime);
                TimeOnly endTime = dto.EndTime ?? startTime.Add(DefaultLength);
                DateTime endUtc = RotaTime.ToUtc(dto.PartyDate, endTime);
                if (endUtc <= startUtc)
                {
                    endUtc = startUtc.Add(DefaultLength);
                }

                bool isNew = !existing.TryGetValue(dto.Ref, out PartyBooking? booking);

                if (isNew)
                {
                    booking = new PartyBooking
                    {
                        Id = Guid.NewGuid(),
                        CompanyId = companyId,
                        LocationId = locationId,
                        Status = PartyBookingStatus.Booked,
                        CreateDate = now,
                        CreateByUserId = ImportUserId,
                        ExternalSource = Source,
                        ExternalRef = dto.Ref
                    };
                    ctx.PartyBookings.Add(booking);
                    existing[dto.Ref] = booking;
                }

                // Assign the mapped fields. For an existing row EF only marks it Modified when a value
                // actually differs, so an identical booking produces no write. PaymentNotes is built
                // without the run time, so re-importing the same figures doesn't look like a change.
                booking!.StartUtc = startUtc;
                booking.EndUtc = endUtc;
                booking.PartyType = PartyTypeNormalizer.Normalize(dto.PartyType);
                booking.ChildName = dto.ChildName;
                booking.ChildAgeTurning = AgeTurning(dto.PartyDate, dto.ChildDob);
                booking.ContactName = dto.ContactName;
                booking.ContactPhone = dto.ContactPhone;
                booking.ExpectedChildren = dto.Quantity;
                booking.TotalPrice = dto.TotalDue;
                booking.PaymentNotes = PaymentNote(dto);
                ApplyPayment(booking, dto, now);
                booking.ExternalIsProvisional = dto.IsProvisional;

                if (isNew)
                {
                    booking.ExternalLastImportedUtc = now;
                    run.Imported++;
                }
                else if (ctx.Entry(booking).State == EntityState.Modified)
                {
                    booking.UpdateDate = now;
                    booking.UpdateByUserId = ImportUserId;
                    booking.ExternalLastImportedUtc = now;
                    run.Updated++;
                }
                // Otherwise unchanged: leave the row untouched (no write, not counted).
            }
            catch (Exception ex)
            {
                run.Failed++;
                _logger.LogWarning("Couldn't map Just Step booking {Ref} for location {LocationId}: {Error}", dto.Ref, locationId, ex.Message);
            }
        }

        run.Updated += await CancelDroppedBookingsAsync(ctx, locationId, refs, from, to, now, ct);

        await ctx.SaveChangesAsync(ct);
    }

    // Cancels still-booked imported parties inside the fetched window whose reference didn't come
    // back this time - they've been cancelled or removed in Just Step. Skips the whole step when the
    // report came back empty, so a one-off empty response can't wipe a window of bookings.
    private async Task<int> CancelDroppedBookingsAsync(ApplicationDbContext ctx, int locationId, List<string> seenRefs,
        DateOnly from, DateOnly to, DateTime now, CancellationToken ct)
    {
        if (seenRefs.Count == 0)
        {
            return 0;
        }

        DateTime windowFromUtc = RotaTime.StartOfDayUtc(from);
        DateTime windowToUtc = RotaTime.StartOfDayUtc(to.AddDays(1));

        List<PartyBooking> dropped = await ctx.PartyBookings
            .Where(x => x.LocationId == locationId
                && x.ExternalSource == Source
                && x.Status == PartyBookingStatus.Booked
                && x.StartUtc >= windowFromUtc && x.StartUtc < windowToUtc
                && (x.ExternalRef == null || !seenRefs.Contains(x.ExternalRef)))
            .ToListAsync(ct);

        foreach (PartyBooking booking in dropped)
        {
            booking.Status = PartyBookingStatus.Cancelled;
            booking.UpdateDate = now;
            booking.UpdateByUserId = ImportUserId;
            booking.ExternalLastImportedUtc = now;
        }

        if (dropped.Count > 0)
        {
            _logger.LogInformation("Cancelled {Count} booking(s) no longer in Just Step for location {LocationId}", dropped.Count, locationId);
        }

        return dropped.Count;
    }

    private static int? AgeTurning(DateOnly partyDate, DateOnly? dob)
    {
        if (dob is null)
        {
            return null;
        }

        int age = partyDate.Year - dob.Value.Year;
        return age is >= 1 and <= 30 ? age : null;
    }

    // No run time in here on purpose: re-importing the same figures must produce the same text, so
    // an unchanged booking isn't rewritten every run.
    private static string? PaymentNote(ExternalBookingDto dto)
    {
        if (dto.Paid is null && dto.Balance is null)
        {
            return null;
        }

        return $"Imported from Just Step: paid £{dto.Paid ?? 0m:0.00}, balance £{dto.Balance ?? 0m:0.00}.";
    }

    // Turns Just Step's "paid" and "balance" figures into the booking's payment state so it reads as
    // Paid in full / Deposit paid / Unpaid rather than always Unpaid. The paid timestamps are only
    // set when they are not already set, so re-importing the same figures doesn't rewrite the row
    // (and doesn't keep moving the recorded paid time). Just Step is the source of truth, so a
    // refund there clears the paid state here too.
    private static void ApplyPayment(PartyBooking booking, ExternalBookingDto dto, DateTime now)
    {
        decimal paid = dto.Paid ?? 0m;
        decimal balance = dto.Balance ?? 0m;
        bool hasPaid = paid > 0m;

        if (hasPaid && balance <= 0m)
        {
            // Fully paid.
            booking.DepositAmount = paid;
            booking.DepositPaidUtc ??= now;
            booking.BalancePaidUtc ??= now;
        }
        else if (hasPaid)
        {
            // A deposit or part payment, with a balance still owing.
            booking.DepositAmount = paid;
            booking.DepositPaidUtc ??= now;
            booking.BalancePaidUtc = null;
        }
        else
        {
            // Nothing paid.
            booking.DepositAmount = null;
            booking.DepositPaidUtc = null;
            booking.BalancePaidUtc = null;
        }
    }

    private async Task<BookingImportRunView> SaveRunAsync(ApplicationDbContext ctx, BookingImportRun run, CancellationToken ct)
    {
        run.CompletedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        ctx.BookingImportRuns.Add(run);
        await ctx.SaveChangesAsync(ct);
        return ToView(run);
    }

    private async Task RaiseAlertAsync(int companyId, int locationId, string? detail, CancellationToken ct)
    {
        try
        {
            Result<OperationsAlertRaiseOutcome> result = await _alertWriter.RaiseAsync(new OperationsAlertRaise(
                companyId, locationId, OperationsAlertType.PartyBookingImportFailed, OperationsAlertSeverity.Warning,
                DedupKey(locationId), "Booking import failed", Truncate(detail), AlertUrl,
                NotifyTopic: NotificationTopic.PartyBookingImportFailed), ct);

            if (!result.IsSuccess)
            {
                _logger.LogWarning("Couldn't raise booking import alert for location {LocationId}: {Error}", locationId, result.Error);
            }
        }
        catch (Exception ex)
        {
            // The alert is a side effect; it must never stop or fail the import.
            _logger.LogWarning(ex, "Error raising booking import alert for location {LocationId}", locationId);
        }
    }

    private async Task ResolveAlertAsync(int companyId, int locationId, CancellationToken ct)
    {
        try
        {
            await _alertWriter.ResolveAsync(companyId, DedupKey(locationId), "The booking import ran successfully.", ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error clearing booking import alert for location {LocationId}", locationId);
        }
    }

    private string TryUnprotect(string protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue))
        {
            return string.Empty;
        }

        try
        {
            return _protector.Unprotect(protectedValue);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't decrypt a stored booking import password");
            return string.Empty;
        }
    }

    private static string? Truncate(string? value) =>
        value is null ? null : value.Length <= 500 ? value : value[..500];

    private static BookingImportSettingsView ToView(BookingImportSettings? settings, int locationId) =>
        settings is null
            ? new BookingImportSettingsView(locationId, false, string.Empty, false,
                BookingImportSchedule.DefaultFrequencyHours, BookingImportSchedule.DefaultActiveStartHour, BookingImportSchedule.DefaultActiveEndHour, null)
            : new BookingImportSettingsView(locationId, settings.Enabled, settings.Username, settings.HasPassword,
                settings.FrequencyHours, settings.ActiveStartHour, settings.ActiveEndHour, settings.UpdateDate);

    private static BookingImportRunView ToView(BookingImportRun run) =>
        new(run.Id, run.LocationId, run.StartedAtUtc, run.CompletedAtUtc, run.Status, run.Trigger,
            run.BookingsSeen, run.Imported, run.Updated, run.Skipped, run.Failed, run.Error);
}
