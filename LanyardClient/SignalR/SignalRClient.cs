using Microsoft.AspNetCore.SignalR.Client;

public class SignalRClient : ISignalRClient
{
    private readonly HubConnection _connection;

    public SignalRClient(string serverUrl, Guid clientId, IEnumerable<Action<HubConnection>> registrations)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl(serverUrl + $"?clientId={clientId}")
            .WithAutomaticReconnect()
            .Build();

        foreach (Action<HubConnection> register in registrations)
        {
            register(_connection);
        }
    }

    public async Task StartAsync()
    {
        await Task.Delay(4000);

        try
        {
            await _connection.StartAsync();
            Console.WriteLine("SignalR connected.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error starting SignalR connection: {ex.Message}");
        }
    }
}