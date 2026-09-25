using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Scheduling;

public interface IClockInTerminalService
{
    // Managers see their own location's terminals; Admins every location's (or one, if given).
    Task<Result<List<ClockInTerminal>>> GetTerminalsAsync(LocationScope scope, int? locationId = null);

    // Registers a new terminal and returns its raw token for the caller to put in the device's
    // cookie. Authorisation is the caller's job (TerminalController checks the signed-in manager
    // is Admin or belongs to the location, and that they issued the pairing code themselves).
    Task<Result<PairedTerminal>> PairAsync(int locationId, string name, string createdByUserId);

    // The device's cookie token to its terminal, if the terminal is still active.
    Task<Result<TerminalSession>> ResolveByTokenAsync(string? rawToken);

    // Re-checked on every clock action and periodically by the open terminal page, so revoking a
    // terminal takes effect without waiting for the tablet to reload.
    Task<Result<TerminalSession>> GetActiveSessionAsync(Guid terminalId);

    Task<Result<bool>> RenameAsync(LocationScope scope, Guid terminalId, string name);
    Task<Result<bool>> RevokeAsync(LocationScope scope, Guid terminalId, string revokedByUserId);
}
