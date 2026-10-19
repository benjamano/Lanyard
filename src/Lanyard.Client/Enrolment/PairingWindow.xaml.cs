using System.Globalization;
using System.Windows;

namespace Lanyard.Client.Enrolment;

// Full screen while the kiosk waits to be paired, so whoever is standing at it can read the code
// out to an Admin. Closes itself once the kiosk has its credential.
public partial class PairingWindow : Window
{
    public PairingWindow(string footer)
    {
        InitializeComponent();
        FooterText.Text = footer;
    }

    public void ShowCode(string pairingCode, DateTime expiresUtc)
    {
        CodeText.Text = pairingCode;
        ExpiryText.Text = $"This code works until {expiresUtc.ToLocalTime().ToString("HH:mm", CultureInfo.GetCultureInfo("en-GB"))}. A new one appears after that.";
        StatusText.Text = string.Empty;
    }

    public void ShowStatus(string message) => StatusText.Text = message;
}
