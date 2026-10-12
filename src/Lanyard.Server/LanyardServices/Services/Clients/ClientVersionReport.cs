using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Contracts;

namespace Lanyard.Application.Services.Clients;

public enum ClientVersionReportOutcome
{
    // The kiosk sent a valid version and it was recorded.
    Recorded,
    // No version on the connection: a client older than 1.0.41. The stored value is left alone.
    NotReported,
    // Something was sent but it isn't a short SemVer string. Ignored, stored value left alone.
    Rejected
}

public static class ClientVersionReport
{
    public static ClientVersionReportOutcome Apply(Client client, string? rawVersion, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(rawVersion))
        {
            return ClientVersionReportOutcome.NotReported;
        }

        if (!ClientVersionFormat.TryNormalize(rawVersion, out string version))
        {
            return ClientVersionReportOutcome.Rejected;
        }

        client.ReportedClientVersion = version;
        client.ClientVersionReportedAt = utcNow;

        return ClientVersionReportOutcome.Recorded;
    }
}
