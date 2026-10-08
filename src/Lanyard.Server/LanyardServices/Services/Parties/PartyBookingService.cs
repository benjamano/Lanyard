using System.Net.Mail;
using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services.Parties;

public class PartyBookingService(IDbContextFactory<ApplicationDbContext> factory) : IPartyBookingService
{
    private const string NoAccess = "You can only manage parties at your own location.";

    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;

    public async Task<Result<List<PartyBooking>>> GetBookingsAsync(LocationScope scope, int locationId, DateOnly from, DateOnly to, bool includeCancelled = false)
    {
        try
        {
            if (!SchedulingAccess.CanManageLocation(scope, locationId))
            {
                return Result<List<PartyBooking>>.Fail(NoAccess);
            }

            DateTime fromUtc = RotaTime.StartOfDayUtc(from);
            DateTime toUtc = RotaTime.StartOfDayUtc(to.AddDays(1));

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<PartyBooking> bookings = await ctx.PartyBookings
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.PartyHost)
                .Where(x => x.LocationId == locationId && x.StartUtc >= fromUtc && x.StartUtc < toUtc)
                .Where(x => includeCancelled || x.Status == PartyBookingStatus.Booked)
                .OrderBy(x => x.StartUtc)
                .ThenBy(x => x.ChildName)
                .ToListAsync();

            return Result<List<PartyBooking>>.Ok(bookings);
        }
        catch (Exception ex)
        {
            return Result<List<PartyBooking>>.Fail($"Failed to load parties: {ex.Message}");
        }
    }

    public async Task<Result<PartyBooking>> GetBookingAsync(LocationScope scope, Guid bookingId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            PartyBooking? booking = await ctx.PartyBookings
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.PartyHost)
                .Include(x => x.Location)
                .FirstOrDefaultAsync(x => x.Id == bookingId);

            if (booking is null)
            {
                return Result<PartyBooking>.Fail("Party not found.");
            }

            if (!SchedulingAccess.CanManageLocation(scope, booking.LocationId))
            {
                return Result<PartyBooking>.Fail(NoAccess);
            }

            return Result<PartyBooking>.Ok(booking);
        }
        catch (Exception ex)
        {
            return Result<PartyBooking>.Fail($"Failed to load the party: {ex.Message}");
        }
    }

    public async Task<Result<PartyBooking>> SaveBookingAsync(LocationScope scope, PartyBooking booking, string actingUserId)
    {
        try
        {
            if (!SchedulingAccess.CanManageLocation(scope, booking.LocationId))
            {
                return Result<PartyBooking>.Fail(NoAccess);
            }

            Normalise(booking);

            string? validationError = Validate(booking);

            if (validationError is not null)
            {
                return Result<PartyBooking>.Fail(validationError);
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Location? location = await ctx.Locations
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.Id == booking.LocationId && x.IsActive);

            if (location is null)
            {
                return Result<PartyBooking>.Fail("That location doesn't exist or has been closed.");
            }

            if (booking.PartyHostUserId is string hostId)
            {
                bool hostWorksHere = await ctx.UserLocationMemberships
                    .AsNoTracking()
                    .TagWithCallSite()
                    .AnyAsync(x => x.UserId == hostId && x.LocationId == booking.LocationId);

                if (!hostWorksHere)
                {
                    return Result<PartyBooking>.Fail("The party host must be a member of staff at this location.");
                }
            }

            DateTime now = DateTime.UtcNow;
            PartyBooking saved;

            if (booking.Id == Guid.Empty)
            {
                // A fresh row rather than the caller's object, so a failed save leaves their form
                // exactly as it was (no half-assigned Id that would turn the retry into an update).
                saved = new PartyBooking
                {
                    Id = Guid.NewGuid(),
                    CompanyId = location.CompanyId,
                    LocationId = booking.LocationId,
                    Status = PartyBookingStatus.Booked,
                    CreateDate = now,
                    CreateByUserId = actingUserId
                };

                CopyEditableFields(booking, saved);
                ctx.PartyBookings.Add(saved);
            }
            else
            {
                PartyBooking? existing = await ctx.PartyBookings.FirstOrDefaultAsync(x => x.Id == booking.Id);

                if (existing is null)
                {
                    return Result<PartyBooking>.Fail("Party not found.");
                }

                // Moving a party to another location needs access to both ends.
                if (!SchedulingAccess.CanManageLocation(scope, existing.LocationId))
                {
                    return Result<PartyBooking>.Fail(NoAccess);
                }

                existing.LocationId = booking.LocationId;
                existing.CompanyId = location.CompanyId;
                existing.UpdateDate = now;
                existing.UpdateByUserId = actingUserId;
                CopyEditableFields(booking, existing);

                saved = existing;
            }

            await ctx.SaveChangesAsync();

            return Result<PartyBooking>.Ok(saved);
        }
        catch (Exception ex)
        {
            return Result<PartyBooking>.Fail($"Failed to save the party: {ex.Message}");
        }
    }

    public Task<Result<PartyBooking>> SetCancelledAsync(LocationScope scope, Guid bookingId, bool cancelled, string actingUserId) =>
        UpdateAsync(scope, bookingId, actingUserId, "update the party", x => x.Status = cancelled ? PartyBookingStatus.Cancelled : PartyBookingStatus.Booked);

    public Task<Result<PartyBooking>> SetDepositPaidAsync(LocationScope scope, Guid bookingId, bool paid, string actingUserId) =>
        UpdateAsync(scope, bookingId, actingUserId, "record the deposit", x =>
        {
            x.DepositPaidUtc = paid ? x.DepositPaidUtc ?? DateTime.UtcNow : null;

            // Undoing the deposit can't leave the party "paid in full".
            if (!paid)
            {
                x.BalancePaidUtc = null;
            }
        });

    public Task<Result<PartyBooking>> SetBalancePaidAsync(LocationScope scope, Guid bookingId, bool paid, string actingUserId) =>
        UpdateAsync(scope, bookingId, actingUserId, "record the payment", x =>
        {
            x.BalancePaidUtc = paid ? x.BalancePaidUtc ?? DateTime.UtcNow : null;

            // Paying in full covers the deposit too, so the deposit is marked paid with it.
            if (paid)
            {
                x.DepositPaidUtc ??= x.BalancePaidUtc;
            }
        });

    public async Task<Result<List<string>>> GetPartyTypeSuggestionsAsync(LocationScope scope, int locationId)
    {
        try
        {
            if (!SchedulingAccess.CanManageLocation(scope, locationId))
            {
                return Result<List<string>>.Fail(NoAccess);
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<string> types = await ctx.PartyBookings
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.LocationId == locationId && x.PartyType != string.Empty)
                .GroupBy(x => x.PartyType)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key)
                .Select(g => g.Key)
                .Take(20)
                .ToListAsync();

            return Result<List<string>>.Ok(types);
        }
        catch (Exception ex)
        {
            return Result<List<string>>.Fail($"Failed to load party types: {ex.Message}");
        }
    }

    private async Task<Result<PartyBooking>> UpdateAsync(LocationScope scope, Guid bookingId, string actingUserId, string action, Action<PartyBooking> change)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            PartyBooking? booking = await ctx.PartyBookings.FirstOrDefaultAsync(x => x.Id == bookingId);

            if (booking is null)
            {
                return Result<PartyBooking>.Fail("Party not found.");
            }

            if (!SchedulingAccess.CanManageLocation(scope, booking.LocationId))
            {
                return Result<PartyBooking>.Fail(NoAccess);
            }

            change(booking);
            booking.UpdateDate = DateTime.UtcNow;
            booking.UpdateByUserId = actingUserId;

            await ctx.SaveChangesAsync();

            return Result<PartyBooking>.Ok(booking);
        }
        catch (Exception ex)
        {
            return Result<PartyBooking>.Fail($"Failed to {action}: {ex.Message}");
        }
    }

    // Everything the booking form edits. Status, payment dates and audit fields are deliberately
    // not here: they only change through their own methods.
    private static void CopyEditableFields(PartyBooking from, PartyBooking to)
    {
        to.StartUtc = from.StartUtc;
        to.EndUtc = from.EndUtc;
        to.EatTimeUtc = from.EatTimeUtc;
        to.LaserTagTimeUtc = from.LaserTagTimeUtc;
        to.PartyType = from.PartyType;
        to.ChildName = from.ChildName;
        to.ChildAgeTurning = from.ChildAgeTurning;
        to.ContactName = from.ContactName;
        to.ContactPhone = from.ContactPhone;
        to.ContactEmail = from.ContactEmail;
        to.ExpectedChildren = from.ExpectedChildren;
        to.ExpectedAdults = from.ExpectedAdults;
        to.Room = from.Room;
        to.MenuType = from.MenuType;
        to.PartyHostUserId = from.PartyHostUserId;
        to.AllergyNotes = from.AllergyNotes;
        to.Notes = from.Notes;
        to.TotalPrice = from.TotalPrice;
        to.DepositAmount = from.DepositAmount;
        to.PaymentNotes = from.PaymentNotes;
    }

    private static void Normalise(PartyBooking booking)
    {
        booking.PartyType = booking.PartyType?.Trim() ?? string.Empty;
        booking.ChildName = booking.ChildName?.Trim() ?? string.Empty;
        booking.ContactName = booking.ContactName?.Trim() ?? string.Empty;
        booking.ContactPhone = booking.ContactPhone?.Trim() ?? string.Empty;
        booking.ContactEmail = NullIfBlank(booking.ContactEmail);
        booking.Room = NullIfBlank(booking.Room);
        booking.AllergyNotes = NullIfBlank(booking.AllergyNotes);
        booking.Notes = NullIfBlank(booking.Notes);
        booking.PaymentNotes = NullIfBlank(booking.PaymentNotes);
        booking.PartyHostUserId = NullIfBlank(booking.PartyHostUserId);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Validate(PartyBooking booking)
    {
        if (booking.ChildName.Length == 0)
        {
            return "Enter the birthday child's name.";
        }

        if (booking.ContactName.Length == 0)
        {
            return "Enter the name of the parent or guardian who booked.";
        }

        if (booking.ContactPhone.Length == 0)
        {
            return "Enter a contact phone number.";
        }

        if (booking.ContactEmail is string email && !MailAddress.TryCreate(email, out _))
        {
            return "That email address doesn't look right.";
        }

        if (booking.EndUtc <= booking.StartUtc)
        {
            return "The party must finish after it starts.";
        }

        if (booking.EatTimeUtc is DateTime eat && (eat < booking.StartUtc || eat > booking.EndUtc))
        {
            return "The eating time must be during the party.";
        }

        if (booking.LaserTagTimeUtc is DateTime laser && (laser < booking.StartUtc || laser > booking.EndUtc))
        {
            return "The laser tag time must be during the party.";
        }

        if (booking.ExpectedChildren < 1)
        {
            return "Enter how many children are coming.";
        }

        if (booking.ExpectedAdults is < 0)
        {
            return "The number of adults can't be negative.";
        }

        if (booking.ChildAgeTurning is < 0 or > 99)
        {
            return "Enter the age the child is turning.";
        }

        if (booking.TotalPrice is < 0 || booking.DepositAmount is < 0)
        {
            return "Prices can't be negative.";
        }

        if (booking.TotalPrice is decimal total && booking.DepositAmount is decimal deposit && deposit > total)
        {
            return "The deposit can't be more than the total price.";
        }

        return null;
    }
}
