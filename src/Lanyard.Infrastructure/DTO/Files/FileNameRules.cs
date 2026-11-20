namespace Lanyard.Infrastructure.DTO.Files;

/// <summary>
/// What a file or folder may be called in Files. Names are unique (ignoring case) among the live
/// items of one folder. The database doesn't enforce that because older rows already have
/// duplicates, so every create, rename, upload, move and restore goes through these rules.
/// </summary>
public static class FileNameRules
{
    public const int MaxLength = 255;

    private static readonly char[] _forbidden = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>Trims the name and returns null when it is usable, otherwise the reason to show the person.</summary>
    public static string? Validate(string? name, string what = "name")
    {
        string trimmed = name?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
            return $"Enter a {what}.";

        if (trimmed.Length > MaxLength)
            return $"A {what} can be up to {MaxLength} characters.";

        if (trimmed.IndexOfAny(_forbidden) >= 0 || trimmed.Any(char.IsControl))
            return $"A {what} can't contain any of these: / \\ : * ? \" < > |";

        if (trimmed is "." or "..")
            return $"That {what} isn't allowed.";

        return null;
    }

    public static string Clean(string name) => name.Trim();

    public static bool SameName(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns <paramref name="name"/>, or the first "name (2).ext", "name (3).ext" ... that isn't
    /// in <paramref name="taken"/>. Used for uploads and restores, which should never fail just
    /// because the name is in use.
    /// </summary>
    public static string MakeUnique(string name, IEnumerable<string> taken)
    {
        HashSet<string> used = new(taken.Select(x => x.Trim()), StringComparer.OrdinalIgnoreCase);
        string cleaned = Clean(name);

        if (!used.Contains(cleaned))
            return cleaned;

        string extension = Path.GetExtension(cleaned);
        string stem = Path.GetFileNameWithoutExtension(cleaned);

        // "poster (2)" -> keep counting from "poster" rather than making "poster (2) (2)".
        System.Text.RegularExpressions.Match numbered = System.Text.RegularExpressions.Regex.Match(stem, @"^(.*) \((\d+)\)$");
        int start = 2;

        if (numbered.Success)
        {
            stem = numbered.Groups[1].Value;
            start = int.Parse(numbered.Groups[2].Value) + 1;
        }

        for (int n = start; ; n++)
        {
            string candidate = $"{stem} ({n}){extension}";

            if (candidate.Length > MaxLength)
                candidate = $"{stem[..Math.Max(1, stem.Length - (candidate.Length - MaxLength))]} ({n}){extension}";

            if (!used.Contains(candidate))
                return candidate;
        }
    }
}
