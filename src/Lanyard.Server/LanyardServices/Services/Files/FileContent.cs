using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services;

/// <summary>An inclusive byte range requested by a client; a null <see cref="End"/> means "to the end of the file".</summary>
public readonly record struct FileByteRange(long Start, long? End);

/// <summary>
/// A file opened for streaming straight into an HTTP response. Dispose it (or hand its
/// <see cref="Stream"/> to a FileStreamResult) once the response has been written.
/// </summary>
public sealed class FileContent : IAsyncDisposable
{
    public required Stream Stream { get; init; }

    public required FileMetadata Metadata { get; init; }

    /// <summary>Size of the whole file in bytes, even when only a range of it is being served.</summary>
    public required long TotalLength { get; init; }

    /// <summary>Set when the storage backend honoured a byte range: the inclusive range <see cref="Stream"/> covers.</summary>
    public long? RangeStart { get; init; }

    public long? RangeEnd { get; init; }

    /// <summary>Anything that owns <see cref="Stream"/> and must be disposed with it (e.g. the S3 response).</summary>
    public IDisposable? Owner { get; init; }

    public bool IsPartial => RangeStart.HasValue && RangeEnd.HasValue;

    public long ContentLength => IsPartial ? RangeEnd!.Value - RangeStart!.Value + 1 : TotalLength;

    public async ValueTask DisposeAsync()
    {
        await Stream.DisposeAsync();
        Owner?.Dispose();
    }
}
