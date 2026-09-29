using Lanyard.Application.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Lanyard.API.Extensions;

/// <summary>
/// Shared HTTP plumbing for endpoints that serve an uploaded file's bytes (downloads, music,
/// kiosk video). Handles Range requests for both storage backends: a seekable local stream is
/// handed to ASP.NET's own range processing, while a bucket stream (which can't seek) is served
/// as the 206 the bucket already sliced for us.
/// </summary>
public static class FileContentStreamingExtensions
{
    /// <summary>The single byte range a request asks for, or null when it wants the whole file (or asks for something this app doesn't slice, such as multiple or suffix ranges).</summary>
    public static FileByteRange? ParseRequestedRange(HttpRequest request)
    {
        if (!RangeHeaderValue.TryParse(request.Headers.Range.ToString(), out RangeHeaderValue? header)
            || !header.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase)
            || header.Ranges.Count != 1)
        {
            return null;
        }

        RangeItemHeaderValue range = header.Ranges.First();

        if (range.From is null)
        {
            return null;
        }

        return new FileByteRange(range.From.Value, range.To);
    }

    /// <summary>
    /// Writes <paramref name="content"/> to the response. The content (and anything it owns) is
    /// disposed once the body has been written.
    /// </summary>
    public static async Task<IActionResult> StreamFileContentAsync(
        this ControllerBase controller,
        FileContent content,
        string? contentType = null,
        string? downloadFileName = null,
        TimeSpan? cacheFor = null)
    {
        HttpResponse response = controller.Response;
        string resolvedContentType = contentType ?? content.Metadata.ContentType ?? "application/octet-stream";

        if (cacheFor is TimeSpan ttl)
        {
            // File ids are immutable (a replaced file gets a new id), so callers can cache freely.
            response.Headers.CacheControl = $"private, max-age={(long)ttl.TotalSeconds}";
        }

        if (content.Stream.CanSeek)
        {
            // FileStreamResult handles Range/If-Range, 206 and 416 itself and disposes the stream.
            return controller.File(content.Stream, resolvedContentType, downloadFileName, enableRangeProcessing: true);
        }

        await using (content)
        {
            response.Headers.AcceptRanges = "bytes";
            response.ContentType = resolvedContentType;
            response.ContentLength = content.ContentLength;

            if (downloadFileName is not null)
            {
                ContentDispositionHeaderValue disposition = new("attachment");
                disposition.SetHttpFileName(downloadFileName);
                response.Headers.ContentDisposition = disposition.ToString();
            }

            if (content.IsPartial)
            {
                response.StatusCode = StatusCodes.Status206PartialContent;
                response.Headers.ContentRange = $"bytes {content.RangeStart}-{content.RangeEnd}/{content.TotalLength}";
            }

            await content.Stream.CopyToAsync(response.Body, controller.HttpContext.RequestAborted);
        }

        return new EmptyResult();
    }

    /// <summary>Maps a failed <see cref="IFileService.OpenFileContentAsync"/> to the right status code.</summary>
    public static IActionResult FileContentFailure(this ControllerBase controller, string? error)
    {
        return error == FileService.RangeNotSatisfiableError
            ? controller.StatusCode(StatusCodes.Status416RangeNotSatisfiable)
            : controller.NotFound();
    }
}
