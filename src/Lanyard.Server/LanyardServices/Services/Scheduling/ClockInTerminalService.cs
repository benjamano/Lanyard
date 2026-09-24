using System.Security.Cryptography;
using System.Text;
using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Scheduling;

public class ClockInTerminalService(
    IDbContextFactory<ApplicationDbContext> factory,
    TimeProvider timeProvider,
    ILogger<ClockInTerminalService> logger) : IClockInTerminalService
{
    private const int MaxNameLength = 60;

    // LastSeenUtc is for "is this tablet still in use?" on the manage page, not an audit trail,
    // so it's only written when it's gone stale rather than on every page load.
    private static readonly TimeSpan LastSeenThrottle = TimeSpan.FromMinutes(5);

    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<ClockInTerminalService> _logger = logger;

    // A plain SHA-256 is right here (unlike PINs): the token is 256 random bits, so there is
    // nothing to brute-force, and the hash has to be searchable to find the terminal.
    public static string HashToken(string rawToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));

    public async Task<Result<List<ClockInTerminal>>> GetTerminalsAsync(LocationScope scope, int? locationId = null)
    {
        try
        {
            if (!scope.IsAdmin && !scope.IsManager)
            {
                return Result<List<ClockInTerminal>>.Fail("Only managers can see clock-in terminals.");
            }

            int? filter = scope.IsAdmin ? locationId : scope.LocationId;

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<ClockInTerminal> terminals = await ctx.ClockInTerminals
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.Location)
                .Where(x => filter == null || x.LocationId == filter)
                .OrderByDescending(x => x.IsActive)
                .ThenBy(x => x.Name)
                .ToListAsync();

            return Result<List<ClockInTerminal>>.Ok(terminals);
        }
        catch (Exception ex)
        {
            return Result<List<ClockInTerminal>>.Fail($"Failed to load clock-in terminals: {ex.Message}");
        }
    }

    public async Task<Result<PairedTerminal>> PairAsync(int locationId, string name, string createdByUserId)
    {
        try
        {
            string trimmed = name?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return Result<PairedTerminal>.Fail("Give the terminal a name, e.g. \"Front desk tablet\".");
            }

            if (trimmed.Length > MaxNameLength)
            {
                return Result<PairedTerminal>.Fail($"Terminal names can be at most {MaxNameLength} characters.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            bool locationActive = await ctx.Locations.AnyAsync(x => x.Id == locationId && x.IsActive);

            if (!locationActive)
            {
                return Result<PairedTerminal>.Fail("That location no longer exists.");
            }

            string rawToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');

            ClockInTerminal terminal = new()
            {
                Id = Guid.NewGuid(),
                LocationId = locationId,
                Name = trimmed,
                DeviceTokenHash = HashToken(rawToken),
                CreatedByUserId = createdByUserId,
                CreateDate = _timeProvider.GetUtcNow().UtcDateTime,
                LastSeenUtc = _timeProvider.GetUtcNow().UtcDateTime,
                IsActive = true
            };

            ctx.ClockInTerminals.Add(terminal);
            await ctx.SaveChangesAsync();

            _logger.LogInformation("Paired clock-in terminal {TerminalId} ({TerminalName}) at location {LocationId} by {UserId}",
                terminal.Id, terminal.Name, locationId, createdByUserId);

            return Result<PairedTerminal>.Ok(new PairedTerminal(terminal, rawToken));
        }
        catch (Exception ex)
        {
            return Result<PairedTerminal>.Fail($"Failed to pair the terminal: {ex.Message}");
        }
    }

    public async Task<Result<TerminalSession>> ResolveByTokenAsync(string? rawToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(rawToken))
            {
                return Result<TerminalSession>.Fail("This device isn't paired as a clock-in terminal.");
            }

            string hash = HashToken(rawToken);

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            ClockInTerminal? terminal = await ctx.ClockInTerminals
                .TagWithCallSite()
                .Include(x => x.Location)
                .FirstOrDefaultAsync(x => x.DeviceTokenHash == hash);

            if (terminal is null || !terminal.IsActive || terminal.Location is not { IsActive: true })
            {
                return Result<TerminalSession>.Fail("This device has been unpaired. A manager can pair it again from Manage > Rota > Clock-In Terminals.");
            }

            DateTime now = _timeProvider.GetUtcNow().UtcDateTime;

            if (terminal.LastSeenUtc is null || now - terminal.LastSeenUtc > LastSeenThrottle)
            {
                terminal.LastSeenUtc = now;
                await ctx.SaveChangesAsync();
            }

            return Result<TerminalSession>.Ok(ToSession(terminal));
        }
        catch (Exception ex)
        {
            return Result<TerminalSession>.Fail($"Failed to check this device: {ex.Message}");
        }
    }

    public async Task<Result<TerminalSession>> GetActiveSessionAsync(Guid terminalId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            ClockInTerminal? terminal = await ctx.ClockInTerminals
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.Location)
                .FirstOrDefaultAsync(x => x.Id == terminalId);

            if (terminal is null || !terminal.IsActive || terminal.Location is not { IsActive: true })
            {
                return Result<TerminalSession>.Fail("This device has been unpaired.");
            }

            return Result<TerminalSession>.Ok(ToSession(terminal));
        }
        catch (Exception ex)
        {
            return Result<TerminalSession>.Fail($"Failed to check this device: {ex.Message}");
        }
    }

    public async Task<Result<bool>> RenameAsync(LocationScope scope, Guid terminalId, string name)
    {
        try
        {
            string trimmed = name?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.Length > MaxNameLength)
            {
                return Result<bool>.Fail($"A terminal name must be 1-{MaxNameLength} characters.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            ClockInTerminal? terminal = await ctx.ClockInTerminals.FirstOrDefaultAsync(x => x.Id == terminalId);

            if (terminal is null)
            {
                return Result<bool>.Fail("Terminal not found.");
            }

            if (!SchedulingAccess.CanManageLocation(scope, terminal.LocationId))
            {
                return Result<bool>.Fail("You can only manage terminals at your own location.");
            }

            terminal.Name = trimmed;
            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to rename the terminal: {ex.Message}");
        }
    }

    public async Task<Result<bool>> RevokeAsync(LocationScope scope, Guid terminalId, string revokedByUserId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            ClockInTerminal? terminal = await ctx.ClockInTerminals.FirstOrDefaultAsync(x => x.Id == terminalId);

            if (terminal is null)
            {
                return Result<bool>.Fail("Terminal not found.");
            }

            if (!SchedulingAccess.CanManageLocation(scope, terminal.LocationId))
            {
                return Result<bool>.Fail("You can only manage terminals at your own location.");
            }

            if (!terminal.IsActive)
            {
                return Result<bool>.Ok(true);
            }

            terminal.IsActive = false;
            terminal.RevokedDateUtc = _timeProvider.GetUtcNow().UtcDateTime;
            terminal.RevokedByUserId = revokedByUserId;

            await ctx.SaveChangesAsync();

            _logger.LogInformation("Revoked clock-in terminal {TerminalId} at location {LocationId} by {UserId}",
                terminal.Id, terminal.LocationId, revokedByUserId);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to unpair the terminal: {ex.Message}");
        }
    }

    private static TerminalSession ToSession(ClockInTerminal terminal) =>
        new(terminal.Id, terminal.LocationId, terminal.Location!.CompanyId, terminal.Location.Name, terminal.Name);
}
