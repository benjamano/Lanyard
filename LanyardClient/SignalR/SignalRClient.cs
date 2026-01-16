using Lanyard.Shared.DTO;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using System.Windows.Forms;

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
        Console.WriteLine("Waiting 5 seconds to start the Signal R connection.");

        await Task.Delay(5000);

        try
        {
            await _connection.StartAsync();
            Console.WriteLine("SignalR connected.");

            await SendAvailableScreensToServer();

            await SendAvailableAudioDevicesToServer();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error starting SignalR connection: {ex.Message}");
        }
    }

    private async Task SendAvailableScreensToServer()
    {
        IEnumerable<ClientAvailableScreenDTO> screens = Screen.AllScreens
            .Select(x=> new ClientAvailableScreenDTO()
            {
                ClientId = Guid.Parse(Environment.GetEnvironmentVariable("LANYARD_CLIENT_ID")!),
                Name = x.DeviceName,
                Width = x.Bounds.Width,
                Height = x.Bounds.Height,
                Index = Array.IndexOf(Screen.AllScreens, x)
            });

        await _connection.InvokeAsync("UpdateAvailableScreens", screens);
    }

    private async Task SendAvailableAudioDevicesToServer()
    {
        MMDeviceEnumerator enumerator = new();

        IEnumerable<ClientAvailableAudioDeviceDTO> devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(x => new ClientAvailableAudioDeviceDTO()
            {
                ClientId = Guid.Parse(Environment.GetEnvironmentVariable("LANYARD_CLIENT_ID")!),
                Name = x.FriendlyName,
                Id = x.ID,
            });

        await _connection.InvokeAsync("UpdateAvailableAudioDevices", devices);
    }
}