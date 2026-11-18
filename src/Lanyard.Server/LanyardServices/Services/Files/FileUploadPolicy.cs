namespace Lanyard.Application.Services;

// What FileService accepts, checked on the server for every upload (File Manager, staff
// documents, onboarding attachments, audio clips): the uploaders' Accept and size settings are
// only browser hints (Security Issue 9.14). The stored content type comes from the extension,
// never from what the browser claimed, so a file named .png can't be served back as text/html.
public static class FileUploadPolicy
{
    // Matches FileUploader's default; uploaders that want less still set their own lower limit.
    public const long MaxBytes = 100L * 1024 * 1024;

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Images
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".svg"] = "image/svg+xml",
        [".heic"] = "image/heic",

        // Video
        [".mp4"] = "video/mp4",
        [".m4v"] = "video/mp4",
        [".webm"] = "video/webm",
        [".mov"] = "video/quicktime",
        [".mkv"] = "video/x-matroska",
        [".avi"] = "video/x-msvideo",

        // Audio
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
        [".flac"] = "audio/flac",
        [".ogg"] = "audio/ogg",
        [".aac"] = "audio/aac",
        [".m4a"] = "audio/mp4",
        [".wma"] = "audio/x-ms-wma",

        // Documents
        [".pdf"] = "application/pdf",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".odt"] = "application/vnd.oasis.opendocument.text",
        [".ods"] = "application/vnd.oasis.opendocument.spreadsheet",
        [".odp"] = "application/vnd.oasis.opendocument.presentation",
        [".rtf"] = "application/rtf",
        [".txt"] = "text/plain",
        [".csv"] = "text/csv",
        [".json"] = "application/json",
        [".zip"] = "application/zip",
    };

    /// <summary>Null when the upload is allowed, otherwise the reason to show the person.</summary>
    public static string? Check(string fileName, long length)
    {
        if (length <= 0)
        {
            return "The file is empty.";
        }

        if (length > MaxBytes)
        {
            return $"Files can be up to {MaxBytes / (1024 * 1024)} MB.";
        }

        if (!ContentTypes.ContainsKey(Path.GetExtension(fileName)))
        {
            return "That type of file can't be uploaded. Use an image, video, audio file, PDF or office document.";
        }

        return null;
    }

    public static string ContentTypeFor(string fileName) =>
        ContentTypes.TryGetValue(Path.GetExtension(fileName), out string? contentType) ? contentType : "application/octet-stream";
}
