using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace Lanyard.Application.SignalR;

// Every live kiosk hub connection, so revoking (or rotating) a kiosk's credential can cut its
// connection off at once instead of waiting for it to drop. SignalR has no way to close a
// connection from outside the hub other than holding on to its HubCallerContext.
// One server process only: with several instances each would hold its own connections.
public class KioskConnectionRegistry
{
    private readonly ConcurrentDictionary<string, (Guid ClientId, HubCallerContext Context)> _connections = new();

    public void Register(Guid clientId, HubCallerContext context) => _connections[context.ConnectionId] = (clientId, context);

    public void Unregister(string connectionId) => _connections.TryRemove(connectionId, out _);

    // Closes every connection the kiosk has. Returns how many.
    public int Abort(Guid clientId)
    {
        int aborted = 0;

        foreach (KeyValuePair<string, (Guid ClientId, HubCallerContext Context)> connection in _connections)
        {
            if (connection.Value.ClientId != clientId)
            {
                continue;
            }

            connection.Value.Context.Abort();
            _connections.TryRemove(connection.Key, out _);
            aborted++;
        }

        return aborted;
    }
}
