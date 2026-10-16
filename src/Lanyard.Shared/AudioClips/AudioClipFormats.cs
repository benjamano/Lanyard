namespace Lanyard.Shared.AudioClips;

/// <summary>
/// The audio files a clip can be, shared so the server's upload check, its download endpoint and
/// the kiosk's cache agree. All three play through Media Foundation on the kiosk.
/// </summary>
public static class AudioClipFormats
{
    // Older kiosks ignore the PlayAudioClip command without an error, so the server checks this
    // before sending rather than letting a clip silently not play.
    public const string MinimumClientVersion = "1.0.42";

    public static readonly IReadOnlyDictionary<string, string> ContentTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
        [".m4a"] = "audio/mp4"
    };

    public static IEnumerable<string> Extensions => ContentTypes.Keys;

    public static bool IsSupported(string? extension) =>
        !string.IsNullOrWhiteSpace(extension) && ContentTypes.ContainsKey(extension);

    // Normalised to lower case with the dot, or null when the file isn't a supported clip.
    public static string? ExtensionOf(string? fileName)
    {
        string extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        return IsSupported(extension) ? extension : null;
    }

    public static string ContentTypeFor(string? fileName) =>
        ExtensionOf(fileName) is string extension ? ContentTypes[extension] : "application/octet-stream";

    /// <summary>
    /// The kiosk's file name for one version of a clip. Built only from a Guid, a sanitised
    /// version and a known extension, so nothing the server sends can steer the path elsewhere.
    /// Returns null when the extension isn't one the kiosk plays.
    /// </summary>
    public static string? CacheFileName(Guid clipId, string? contentVersion, string? extension)
    {
        string? normalisedExtension = extension?.ToLowerInvariant();
        if (!IsSupported(normalisedExtension))
        {
            return null;
        }

        string version = new((contentVersion ?? string.Empty).Where(char.IsAsciiLetterOrDigit).Take(32).ToArray());

        return $"{clipId:N}_{(version.Length == 0 ? "0" : version)}{normalisedExtension}";
    }

    // Reads the clip id back out of a cache file name, so the kiosk can drop older versions.
    public static Guid? ClipIdFromCacheFileName(string fileName)
    {
        string name = Path.GetFileNameWithoutExtension(fileName);
        int separator = name.IndexOf('_');

        return separator > 0 && Guid.TryParseExact(name[..separator], "N", out Guid clipId) ? clipId : null;
    }
}
