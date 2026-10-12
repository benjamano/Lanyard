using Lanyard.Shared.DTO;
using System.Text.Json;

namespace Lanyard.Shared.Diagnostics;

/// <summary>
/// The kiosk's in-memory record of its most recent log lines, read by a diagnostics log tail.
/// The kiosk has no log file (logs go to the console and OTLP only), so this is the only place
/// "the last N lines" can come from. Thread-safe: every logger in the process writes to it.
/// Callers must redact before adding; this class only truncates.
/// </summary>
public sealed class LogLineBuffer
{
    public const int DefaultCapacity = 500;

    public const int MaxMessageLength = 1000;

    public const int MaxExceptionLength = 2000;

    // Each line costs a little more than its own JSON once it sits in the reply's array and
    // the hub message envelope; this keeps the estimate on the safe side.
    private const int PerLineOverheadBytes = 8;

    private const string TruncationMarker = "…";

    private readonly ClientLogLineDTO[] _lines;
    private readonly object _lock = new();
    private int _next;
    private int _count;

    public LogLineBuffer(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        _lines = new ClientLogLineDTO[capacity];
    }

    public int Capacity => _lines.Length;

    public void Add(DateTime timestampUtc, string level, string category, string message, string? exception = null)
    {
        string text = Truncate(message, MaxMessageLength);

        if (!string.IsNullOrEmpty(exception))
        {
            text += Environment.NewLine + Truncate(exception, MaxExceptionLength);
        }

        ClientLogLineDTO line = new()
        {
            TimestampUtc = timestampUtc,
            Level = level,
            Category = category,
            Message = text
        };

        lock (_lock)
        {
            _lines[_next] = line;
            _next = (_next + 1) % _lines.Length;
            _count = Math.Min(_count + 1, _lines.Length);
        }
    }

    /// <summary>
    /// The newest lines (at most <paramref name="maxLines"/>), oldest first, dropping the oldest
    /// of those until the serialized lines fit in <paramref name="maxBytes"/>. Truncated is set
    /// only when the byte cap dropped lines, not when the caller simply asked for fewer.
    /// </summary>
    public ClientLogTailDTO Snapshot(int maxLines, int maxBytes)
    {
        ClientLogLineDTO[] newestFirst;

        lock (_lock)
        {
            int take = Math.Clamp(maxLines, 0, _count);
            newestFirst = new ClientLogLineDTO[take];

            for (int i = 0; i < take; i++)
            {
                int index = (_next - 1 - i + _lines.Length) % _lines.Length;
                newestFirst[i] = _lines[index];
            }
        }

        List<ClientLogLineDTO> kept = [];
        int usedBytes = 0;
        bool truncated = false;

        foreach (ClientLogLineDTO line in newestFirst)
        {
            int lineBytes = JsonSerializer.SerializeToUtf8Bytes(line).Length + PerLineOverheadBytes;

            if (usedBytes + lineBytes > maxBytes)
            {
                truncated = true;
                break;
            }

            usedBytes += lineBytes;
            kept.Add(line);
        }

        kept.Reverse();

        return new ClientLogTailDTO
        {
            Lines = kept,
            Truncated = truncated
        };
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= maxLength
            ? value
            : string.Concat(value.AsSpan(0, maxLength), TruncationMarker);
    }
}
