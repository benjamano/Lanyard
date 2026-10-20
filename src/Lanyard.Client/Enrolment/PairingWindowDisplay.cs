using System.Windows;
using Lanyard.Shared.Kiosk;
using Application = System.Windows.Application;

namespace Lanyard.Client.Enrolment;

// Shows pairing on the kiosk's screen (and the console, which may be hidden). Startup runs on a
// background thread, so every window call goes through the WPF dispatcher.
public sealed class PairingWindowDisplay(string footer) : IKioskPairingDisplay
{
    private PairingWindow? _window;

    public void ShowCode(string pairingCode, DateTime expiresUtc)
    {
        Console.WriteLine($"Pairing code: {pairingCode}. In Lanyard, go to Manage > Clients > Add kiosk and type it in.");

        OnUiThread(() => Window().ShowCode(pairingCode, expiresUtc));
    }

    public void ShowStatus(string message)
    {
        Console.WriteLine(message);

        OnUiThread(() => Window().ShowStatus(message));
    }

    public void Close()
    {
        OnUiThread(() =>
        {
            _window?.Close();
            _window = null;
        });
    }

    private PairingWindow Window()
    {
        if (_window is null)
        {
            _window = new PairingWindow(footer);
            _window.Show();
        }

        return _window;
    }

    private static void OnUiThread(Action action)
    {
        Application? app = Application.Current;

        if (app is null)
        {
            return;
        }

        app.Dispatcher.Invoke(action);
    }
}
