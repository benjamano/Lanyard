using System.Text;
using System.Text.Json;

namespace Lanyard.Shared.LaserGame.Capture;

/// <summary>
/// Reads a <see cref="LaserCaptureFormat"/> file back. Anything wrong is a
/// <see cref="LaserCaptureFormatException"/> naming the line, except a partly written last line,
/// which is what a crash mid-recording leaves: that line is skipped and
/// <see cref="LaserCapture.Truncated"/> is set. Fields this version doesn't know are ignored, so
/// a newer kiosk can add optional fields without breaking older readers.
/// </summary>
public static class LaserCaptureReader
{
    /// <summary>Recordings are small (about 1 MB for a 15-minute game), so anything far bigger isn't one.</summary>
    public const long MaxFileBytes = 64L * 1024 * 1024;

    public static async Task<LaserCapture> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        string text;

        using (StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true))
        {
            text = await ReadCappedAsync(reader, cancellationToken);
        }

        return Parse(text);
    }

    public static LaserCapture Parse(string text)
    {
        string[] lines = text.Split('\n');

        // Without a final newline the last line may have been cut off mid-write.
        bool lastLineUnterminated = !text.EndsWith('\n');

        LaserCaptureHeader? header = null;
        LaserCaptureFooter? footer = null;
        List<LaserCapturePacket> packets = [];
        bool truncated = false;
        long lastOffsetMs = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            int lineNumber = i + 1;
            bool isLast = i == lines.Length - 1;

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonDocument document;

            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException) when (isLast && lastLineUnterminated && header != null)
            {
                truncated = true;
                break;
            }
            catch (JsonException ex)
            {
                throw new LaserCaptureFormatException($"Line {lineNumber} isn't valid JSON: {ex.Message}", lineNumber);
            }

            using (document)
            {
                JsonElement root = document.RootElement;

                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new LaserCaptureFormatException($"Line {lineNumber} should be a JSON object.", lineNumber);
                }

                if (header == null)
                {
                    header = ReadHeader(root, lineNumber);
                    continue;
                }

                if (footer != null)
                {
                    throw new LaserCaptureFormatException($"Line {lineNumber} comes after the end of the recording.", lineNumber);
                }

                if (root.TryGetProperty("end", out JsonElement end) && end.ValueKind == JsonValueKind.True)
                {
                    footer = ReadFooter(root, lineNumber);
                    continue;
                }

                LaserCapturePacket packet = ReadPacket(root, lineNumber);

                if (packet.OffsetMs < lastOffsetMs)
                {
                    throw new LaserCaptureFormatException($"Line {lineNumber} goes back in time ({packet.OffsetMs} ms after {lastOffsetMs} ms).", lineNumber);
                }

                lastOffsetMs = packet.OffsetMs;
                packets.Add(packet);
            }
        }

        if (header == null)
        {
            throw new LaserCaptureFormatException("The file is empty.");
        }

        return new LaserCapture(header, packets, footer) { Truncated = truncated };
    }

    private static LaserCaptureHeader ReadHeader(JsonElement root, int lineNumber)
    {
        if (!root.TryGetProperty("format", out JsonElement format) || format.ValueKind != JsonValueKind.String || format.GetString() != LaserCaptureFormat.FormatName)
        {
            throw new LaserCaptureFormatException("This isn't a Lanyard laser game recording.", lineNumber);
        }

        if (!root.TryGetProperty("version", out JsonElement versionElement) || versionElement.ValueKind != JsonValueKind.Number || !versionElement.TryGetInt32(out int version) || version < 1)
        {
            throw new LaserCaptureFormatException("The recording doesn't say which version of the format it uses.", lineNumber);
        }

        if (version > LaserCaptureFormat.CurrentVersion)
        {
            throw new LaserCaptureFormatException($"The recording uses format version {version}, which is newer than this version of Lanyard reads ({LaserCaptureFormat.CurrentVersion}). Update first.", lineNumber);
        }

        try
        {
            return root.Deserialize<LaserCaptureHeader>(LaserCaptureWriter.JsonOptions)
                ?? throw new LaserCaptureFormatException("The recording's header is empty.", lineNumber);
        }
        catch (JsonException ex)
        {
            throw new LaserCaptureFormatException($"The recording's header can't be read: {ex.Message}", lineNumber);
        }
    }

    private static LaserCaptureFooter ReadFooter(JsonElement root, int lineNumber) => new()
    {
        Packets = ReadOptionalInt64(root, "packets", lineNumber),
        Dropped = ReadOptionalInt64(root, "dropped", lineNumber),
        DurationMs = ReadOptionalInt64(root, "durationMs", lineNumber),
        Games = (int)ReadOptionalInt64(root, "games", lineNumber),
    };

    private static LaserCapturePacket ReadPacket(JsonElement root, int lineNumber)
    {
        if (!root.TryGetProperty("t", out JsonElement t) || t.ValueKind != JsonValueKind.Number || !t.TryGetInt64(out long offsetMs) || offsetMs < 0)
        {
            throw new LaserCaptureFormatException($"Line {lineNumber} has no valid time (\"t\").", lineNumber);
        }

        if (!root.TryGetProperty("p", out JsonElement p) || p.ValueKind != JsonValueKind.String)
        {
            throw new LaserCaptureFormatException($"Line {lineNumber} has no payload (\"p\").", lineNumber);
        }

        string hex = p.GetString()!;
        byte[] payload;

        try
        {
            payload = Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            throw new LaserCaptureFormatException($"Line {lineNumber}'s payload isn't valid hex.", lineNumber);
        }

        if (payload.Length == 0)
        {
            throw new LaserCaptureFormatException($"Line {lineNumber}'s payload is empty.", lineNumber);
        }

        return new LaserCapturePacket(offsetMs, ReadOptionalString(root, "s"), ReadOptionalString(root, "d"), payload);
    }

    private static string? ReadOptionalString(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long ReadOptionalInt64(JsonElement root, string name, int lineNumber)
    {
        if (!root.TryGetProperty(name, out JsonElement value))
        {
            return 0;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) && number >= 0
            ? number
            : throw new LaserCaptureFormatException($"Line {lineNumber}'s \"{name}\" isn't a whole number.", lineNumber);
    }

    private static async Task<string> ReadCappedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        StringBuilder text = new();
        char[] buffer = new char[16 * 1024];
        int read;

        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            text.Append(buffer, 0, read);

            if (text.Length > MaxFileBytes)
            {
                throw new LaserCaptureFormatException("The file is too big to be a laser game recording.");
            }
        }

        return text.ToString();
    }
}
