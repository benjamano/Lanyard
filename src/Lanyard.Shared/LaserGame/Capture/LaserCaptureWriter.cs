using System.Buffers;
using System.Text.Json;

namespace Lanyard.Shared.LaserGame.Capture;

/// <summary>
/// Writes a <see cref="LaserCaptureFormat"/> file: the header, then packets as they come, then the
/// footer. Each line goes to the stream whole, so a crash never leaves half a line before a good one.
/// Not thread-safe: one writer task owns it.
/// </summary>
public sealed class LaserCaptureWriter : IAsyncDisposable
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly byte[] NewLine = "\n"u8.ToArray();

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly ArrayBufferWriter<byte> _buffer = new(256);
    private readonly Utf8JsonWriter _json;

    private bool _headerWritten;
    private bool _footerWritten;
    private long _lastOffsetMs;

    public LaserCaptureWriter(Stream stream, bool leaveOpen = false)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        _json = new Utf8JsonWriter(_buffer);
    }

    /// <summary>Bytes written so far, for size limits.</summary>
    public long BytesWritten { get; private set; }

    public async Task WriteHeaderAsync(LaserCaptureHeader header, CancellationToken cancellationToken)
    {
        if (_headerWritten)
        {
            throw new InvalidOperationException("The header has already been written.");
        }

        // The writer decides which format it writes, whatever the caller passed.
        LaserCaptureHeader stamped = header with
        {
            Format = LaserCaptureFormat.FormatName,
            Version = LaserCaptureFormat.CurrentVersion,
            RecordedAtUtc = header.RecordedAtUtc.Kind == DateTimeKind.Local ? header.RecordedAtUtc.ToUniversalTime() : DateTime.SpecifyKind(header.RecordedAtUtc, DateTimeKind.Utc),
        };

        JsonSerializer.Serialize(_json, stamped, JsonOptions);
        await WriteLineAsync(cancellationToken);

        _headerWritten = true;
    }

    public async Task WritePacketAsync(LaserCapturePacket packet, CancellationToken cancellationToken)
    {
        EnsureWritingPackets();

        if (packet.OffsetMs < _lastOffsetMs)
        {
            throw new ArgumentException($"Packets must be written in time order: {packet.OffsetMs} ms came after {_lastOffsetMs} ms.", nameof(packet));
        }

        if (packet.Payload.Length == 0)
        {
            throw new ArgumentException("A packet needs a payload.", nameof(packet));
        }

        _json.WriteStartObject();
        _json.WriteNumber("t", packet.OffsetMs);

        if (packet.SourceIp != null)
        {
            _json.WriteString("s", packet.SourceIp);
        }

        if (packet.DestinationIp != null)
        {
            _json.WriteString("d", packet.DestinationIp);
        }

        _json.WriteString("p", Convert.ToHexStringLower(packet.Payload));
        _json.WriteEndObject();

        await WriteLineAsync(cancellationToken);

        _lastOffsetMs = packet.OffsetMs;
    }

    public async Task WriteFooterAsync(LaserCaptureFooter footer, CancellationToken cancellationToken)
    {
        EnsureWritingPackets();

        _json.WriteStartObject();
        _json.WriteBoolean("end", true);
        _json.WriteNumber("packets", footer.Packets);
        _json.WriteNumber("dropped", footer.Dropped);
        _json.WriteNumber("durationMs", footer.DurationMs);
        _json.WriteNumber("games", footer.Games);
        _json.WriteEndObject();

        await WriteLineAsync(cancellationToken);
        await _stream.FlushAsync(cancellationToken);

        _footerWritten = true;
    }

    public Task FlushAsync(CancellationToken cancellationToken) => _stream.FlushAsync(cancellationToken);

    private void EnsureWritingPackets()
    {
        if (!_headerWritten)
        {
            throw new InvalidOperationException("Write the header first.");
        }

        if (_footerWritten)
        {
            throw new InvalidOperationException("The footer has already been written.");
        }
    }

    private async Task WriteLineAsync(CancellationToken cancellationToken)
    {
        _json.Flush();

        // The line and its newline go out as one write, so a crash can only cut off the last line.
        _buffer.Write(NewLine);

        await _stream.WriteAsync(_buffer.WrittenMemory, cancellationToken);
        BytesWritten += _buffer.WrittenCount;

        _buffer.ResetWrittenCount();
        _json.Reset(_buffer);
    }

    public async ValueTask DisposeAsync()
    {
        await _json.DisposeAsync();

        if (!_leaveOpen)
        {
            await _stream.DisposeAsync();
        }
    }
}
