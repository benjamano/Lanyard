using System.Net.Http;
using Lanyard.Client.Security;
using Lanyard.Shared.Kiosk;
using AutoUpdater = Lanyard.Client.AutoUpdate.AutoUpdate;

namespace Lanyard.Client.Enrolment;

// Run once at startup, before anything talks to the server: makes sure this kiosk has its own
// credential, pairing it (and waiting for an Admin) if it doesn't.
public static class KioskEnrolmentStartup
{
    public static async Task EnsureCredentialAsync(Guid clientId)
    {
        if (KioskCredentials.LoadSaved() is not null)
        {
            Console.WriteLine("Using this kiosk's saved credential.");
            return;
        }

        string serverUrl = Environment.GetEnvironmentVariable("LANYARD_SERVER_URL")!;
        string? legacySecret = KioskCredentials.LegacySecret;

        using HttpClient http = CreateServerClient();

        KioskEnrolmentClient enrolment = new(
            http,
            KioskCredentials.Store,
            new PairingWindowDisplay($"{Environment.MachineName} · kiosk {clientId} · {serverUrl}"),
            Console.WriteLine);

        string credential = await enrolment.EnsureCredentialAsync(clientId, Environment.MachineName, AutoUpdater.GetCurrentVersion(), legacySecret);

        KioskCredentials.Use(credential);

        // Swapped (or no longer needed): the shared secret shouldn't sit in config.json any more.
        if (legacySecret is not null)
        {
            VerifyEnvironmentVariables.RemoveLegacySecret();
        }
    }

    // True when the server definitely no longer accepts this kiosk's credential (revoked, say),
    // rather than just being unreachable.
    public static async Task<bool> IsCredentialRejectedAsync(Guid clientId)
    {
        if (KioskCredentials.Current is not string credential)
        {
            return false;
        }

        using HttpClient http = CreateServerClient();

        return await KioskEnrolmentClient.IsCredentialRejectedAsync(http, clientId, credential);
    }

    public static HttpClient CreateServerClient()
    {
        HttpClientHandler handler = new();

        if (bool.TryParse(Environment.GetEnvironmentVariable("LANYARD_CLIENT_ALLOW_INSECURE_SSL"), out bool allow) && allow)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        return new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri(Environment.GetEnvironmentVariable("LANYARD_SERVER_URL")!.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(20)
        };
    }
}
