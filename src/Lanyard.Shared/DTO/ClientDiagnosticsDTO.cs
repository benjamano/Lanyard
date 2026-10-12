namespace Lanyard.Shared.DTO;

/// <summary>
/// Sent by the server with <c>ClientHubMethods.RequestLogTail</c>. The kiosk clamps both values
/// to its own limits, so a bad request can't make it return more than the hub will accept.
/// </summary>
public class ClientLogTailRequestDTO
{
    public int MaxLines { get; set; }

    public int MaxBytes { get; set; }
}

/// <summary>
/// The kiosk's reply to <c>ClientHubMethods.RequestLogTail</c>: its most recent log lines,
/// oldest first, already redacted on the kiosk.
/// </summary>
public class ClientLogTailDTO
{
    public List<ClientLogLineDTO> Lines { get; set; } = [];

    /// <summary>True when older lines were left out to keep the reply under the size cap.</summary>
    public bool Truncated { get; set; }

    public string? ClientVersion { get; set; }

    public DateTime CapturedAtUtc { get; set; }
}

public class ClientLogLineDTO
{
    public DateTime TimestampUtc { get; set; }

    /// <summary>The Microsoft.Extensions.Logging level name, e.g. "Information" or "Error".</summary>
    public string Level { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;
}
