namespace Lanyard.Application.Services.Authentication;

// Tells a user's open tabs that what their sign-in grants has changed (roles added or removed), so
// each tab re-issues its cookie and hands the new principal to its circuit (.NET 11 SignalR
// authentication refresh - see SessionRefreshListener and lanyardAuth in blazorStart.js). Without
// it a circuit keeps the roles it connected with until the tab reloads. Same in-process pattern as
// ITimeOffEventBus (and the same single-app-instance assumption).
public interface IUserSessionRefreshBus
{
    event Action<string>? RefreshRequested;

    void RequestRefresh(string userId);
}

public class UserSessionRefreshBus : IUserSessionRefreshBus
{
    public event Action<string>? RefreshRequested;

    public void RequestRefresh(string userId) => RefreshRequested?.Invoke(userId);
}
