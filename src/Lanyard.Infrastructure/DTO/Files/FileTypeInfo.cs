namespace Lanyard.Infrastructure.DTO.Files;

/// <summary>The groups Files filters by (the type chips and Quick access).</summary>
public enum FileTypeGroup
{
    Other = 0,
    Image = 1,
    Video = 2,
    Audio = 3,
    Document = 4,
}

/// <summary>
/// Friendly names and groups for the file types Files accepts (see FileUploadPolicy), worked out
/// from the extension so "image/png" shows as "PNG image". Shared by the service's type filter
/// and the UI.
/// </summary>
public static class FileTypeInfo
{
    private static readonly Dictionary<string, (FileTypeGroup Group, string Name)> _byExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = (FileTypeGroup.Image, "PNG image"),
        [".jpg"] = (FileTypeGroup.Image, "JPEG image"),
        [".jpeg"] = (FileTypeGroup.Image, "JPEG image"),
        [".gif"] = (FileTypeGroup.Image, "GIF image"),
        [".webp"] = (FileTypeGroup.Image, "WebP image"),
        [".bmp"] = (FileTypeGroup.Image, "Bitmap image"),
        [".svg"] = (FileTypeGroup.Image, "SVG image"),
        [".heic"] = (FileTypeGroup.Image, "HEIC photo"),

        [".mp4"] = (FileTypeGroup.Video, "MP4 video"),
        [".m4v"] = (FileTypeGroup.Video, "MP4 video"),
        [".webm"] = (FileTypeGroup.Video, "WebM video"),
        [".mov"] = (FileTypeGroup.Video, "QuickTime video"),
        [".mkv"] = (FileTypeGroup.Video, "MKV video"),
        [".avi"] = (FileTypeGroup.Video, "AVI video"),

        [".mp3"] = (FileTypeGroup.Audio, "MP3 audio"),
        [".wav"] = (FileTypeGroup.Audio, "WAV audio"),
        [".flac"] = (FileTypeGroup.Audio, "FLAC audio"),
        [".ogg"] = (FileTypeGroup.Audio, "Ogg audio"),
        [".aac"] = (FileTypeGroup.Audio, "AAC audio"),
        [".m4a"] = (FileTypeGroup.Audio, "M4A audio"),
        [".wma"] = (FileTypeGroup.Audio, "WMA audio"),

        [".pdf"] = (FileTypeGroup.Document, "PDF document"),
        [".doc"] = (FileTypeGroup.Document, "Word document"),
        [".docx"] = (FileTypeGroup.Document, "Word document"),
        [".xls"] = (FileTypeGroup.Document, "Excel spreadsheet"),
        [".xlsx"] = (FileTypeGroup.Document, "Excel spreadsheet"),
        [".ppt"] = (FileTypeGroup.Document, "PowerPoint presentation"),
        [".pptx"] = (FileTypeGroup.Document, "PowerPoint presentation"),
        [".odt"] = (FileTypeGroup.Document, "OpenDocument text"),
        [".ods"] = (FileTypeGroup.Document, "OpenDocument spreadsheet"),
        [".odp"] = (FileTypeGroup.Document, "OpenDocument presentation"),
        [".rtf"] = (FileTypeGroup.Document, "Rich text document"),
        [".txt"] = (FileTypeGroup.Document, "Text file"),
        [".csv"] = (FileTypeGroup.Document, "CSV spreadsheet"),
        [".json"] = (FileTypeGroup.Document, "JSON file"),

        [".zip"] = (FileTypeGroup.Other, "ZIP archive"),
    };

    public static FileTypeGroup GroupOf(string fileName) =>
        _byExtension.TryGetValue(Path.GetExtension(fileName), out (FileTypeGroup Group, string Name) info) ? info.Group : FileTypeGroup.Other;

    public static string FriendlyName(string fileName)
    {
        if (_byExtension.TryGetValue(Path.GetExtension(fileName), out (FileTypeGroup Group, string Name) info))
            return info.Name;

        string extension = Path.GetExtension(fileName).TrimStart('.');
        return extension.Length == 0 ? "File" : $"{extension.ToUpperInvariant()} file";
    }

    /// <summary>Every extension in a group, lowercase with the dot, for filtering in the database.</summary>
    public static IReadOnlyList<string> ExtensionsIn(FileTypeGroup group) =>
        _byExtension.Where(x => x.Value.Group == group).Select(x => x.Key.ToLowerInvariant()).ToList();

    /// <summary>Whether the browser can show the file itself in a preview (rather than just an icon).</summary>
    public static bool CanPreview(string fileName) =>
        GroupOf(fileName) is FileTypeGroup.Image or FileTypeGroup.Video or FileTypeGroup.Audio
        || Path.GetExtension(fileName).ToLowerInvariant() is ".pdf" or ".txt" or ".csv" or ".json";

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        int unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0 ? $"{size:0} {units[unit]}" : $"{size:0.#} {units[unit]}";
    }
}
