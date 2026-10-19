using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Lanyard.Shared.Contracts;

namespace Lanyard.Shared.Kiosk;

// Where the kiosk keeps its credential. The kiosk's own implementation encrypts it with DPAPI.
public interface IKioskCredentialStore
{
    string? Load();
    void Save(string credential);
    void Delete();
}

// What the kiosk shows while it waits to be paired: the code, big enough to read across a room.
public interface IKioskPairingDisplay
{
    void ShowCode(string pairingCode, DateTime expiresUtc);
    void ShowStatus(string message);
    void Close();
}

// Gets this kiosk its own credential (see KioskEnrolmentRoutes for the server side):
//  1. One it already saved.
//  2. Otherwise, if it still has the old shared secret, swap that for a credential - the server
//     only allows this for a kiosk it already knows, and only once.
//  3. Otherwise show a pairing code and wait for an Admin to approve it.
// Never gives up: a kiosk without a credential can't do anything else, so it keeps asking (and
// keeps saying why on screen) until it has one.
public sealed class KioskEnrolmentClient(
    HttpClient http,
    IKioskCredentialStore store,
    IKioskPairingDisplay display,
    Action<string> log,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    // How long to wait before trying again when the server can't be reached or turned the kiosk down.
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public async Task<string> EnsureCredentialAsync(Guid clientId, string? machineName, string? clientVersion, string? legacySecret, CancellationToken cancellationToken = default)
    {
        string? saved = store.Load();

        if (!string.IsNullOrWhiteSpace(saved))
        {
            return saved;
        }

        if (!string.IsNullOrWhiteSpace(legacySecret))
        {
            string? exchanged = await TryExchangeLegacySecretAsync(clientId, legacySecret, cancellationToken);

            if (exchanged is not null)
            {
                store.Save(exchanged);
                log("Exchanged the shared secret for this kiosk's own credential.");
                return exchanged;
            }
        }

        try
        {
            string credential = await PairAsync(clientId, machineName, clientVersion, cancellationToken);
            store.Save(credential);
            return credential;
        }
        finally
        {
            display.Close();
        }
    }

    // True only when the server says, in so many words, that this credential is dead (revoked,
    // replaced, or from another server). Anything else - the server down, a timeout, a 500 - is
    // false, so a kiosk never throws away a good credential because of a blip.
    public static async Task<bool> IsCredentialRejectedAsync(HttpClient http, Guid clientId, string credential, CancellationToken cancellationToken = default)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, ClientUpdateRoutes.Feed(clientId));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);

            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);

            return response.StatusCode == HttpStatusCode.Unauthorized
                && response.Headers.WwwAuthenticate.Any(x => x.Parameter?.Contains("invalid_token", StringComparison.OrdinalIgnoreCase) == true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private async Task<string?> TryExchangeLegacySecretAsync(Guid clientId, string legacySecret, CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, $"{KioskEnrolmentRoutes.LegacyExchange}?{ClientConnectionQuery.ClientId}={clientId}");
            request.Headers.Add(ClientConnectionQuery.SecretHeader, legacySecret);

            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // Not known to the server, already has a credential, or exchanges are switched off:
                // all of these mean "pair it instead".
                log($"The server didn't swap the shared secret for a credential ({(int)response.StatusCode}); pairing instead.");
                return null;
            }

            ServerResult<KioskCredentialIssuedDTO>? result = await response.Content.ReadFromJsonAsync<ServerResult<KioskCredentialIssuedDTO>>(JsonOptions, cancellationToken);

            return string.IsNullOrWhiteSpace(result?.Data?.Credential) ? null : result.Data.Credential;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            log($"Couldn't swap the shared secret for a credential: {ex.Message}; pairing instead.");
            return null;
        }
    }

    private async Task<string> PairAsync(Guid clientId, string? machineName, string? clientVersion, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            KioskEnrolmentStartedDTO? started = await StartAsync(clientId, machineName, clientVersion, cancellationToken);

            if (started is null)
            {
                display.ShowStatus("Can't reach the Lanyard server. Trying again...");
                await _delay(RetryDelay, cancellationToken);
                continue;
            }

            display.ShowCode(started.PairingCode, started.ExpiresUtc);
            log($"Waiting to be paired. Pairing code {started.PairingCode}.");

            string? credential = await WaitForApprovalAsync(started, cancellationToken);

            if (credential is not null)
            {
                log("Paired: this kiosk has its own credential.");
                return credential;
            }
        }
    }

    // The credential once approved; null when this code is finished with (rejected, expired or
    // unknown to the server), so the caller asks for a new one.
    private async Task<string?> WaitForApprovalAsync(KioskEnrolmentStartedDTO started, CancellationToken cancellationToken)
    {
        TimeSpan interval = TimeSpan.FromSeconds(Math.Max(1, started.PollIntervalSeconds));

        while (true)
        {
            await _delay(interval, cancellationToken);

            KioskEnrolmentStatusDTO? status;

            try
            {
                using HttpRequestMessage request = new(HttpMethod.Get, KioskEnrolmentRoutes.Request(started.RequestId));
                request.Headers.Add(KioskEnrolmentRoutes.EnrolmentTokenHeader, started.PollToken);

                using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }

                if (!response.IsSuccessStatusCode)
                {
                    display.ShowStatus("The Lanyard server isn't answering properly. Still waiting...");
                    continue;
                }

                status = (await response.Content.ReadFromJsonAsync<ServerResult<KioskEnrolmentStatusDTO>>(JsonOptions, cancellationToken))?.Data;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                display.ShowStatus("Can't reach the Lanyard server. Still waiting...");
                continue;
            }

            switch (status?.State)
            {
                case KioskEnrolmentState.Approved when !string.IsNullOrWhiteSpace(status.Credential):
                    return status.Credential;

                case KioskEnrolmentState.Rejected:
                    display.ShowStatus("An Admin turned this kiosk down. Getting a new code...");
                    log("Pairing was rejected.");
                    await _delay(RetryDelay, cancellationToken);
                    return null;

                case KioskEnrolmentState.Expired:
                case KioskEnrolmentState.Approved:
                    return null;
            }
        }
    }

    private async Task<KioskEnrolmentStartedDTO?> StartAsync(Guid clientId, string? machineName, string? clientVersion, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await http.PostAsJsonAsync(
                KioskEnrolmentRoutes.Requests,
                new KioskEnrolmentRequestDTO(clientId, machineName, clientVersion),
                JsonOptions,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                log($"The server wouldn't start pairing ({(int)response.StatusCode}).");
                return null;
            }

            return (await response.Content.ReadFromJsonAsync<ServerResult<KioskEnrolmentStartedDTO>>(JsonOptions, cancellationToken))?.Data;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            log($"Couldn't reach the server to start pairing: {ex.Message}");
            return null;
        }
    }

    // The server wraps every API response in its Result<T> shape.
    private sealed class ServerResult<T>
    {
        public bool IsSuccess { get; set; }
        public T? Data { get; set; }
        public string? Error { get; set; }
    }
}
