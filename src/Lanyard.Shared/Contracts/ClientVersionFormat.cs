using System.Text.RegularExpressions;

namespace Lanyard.Shared.Contracts;

// The kiosk reports its own version, and the client ID it connects with is self-asserted, so the
// server treats the value as untrusted input: only a short SemVer string is ever stored.
public static partial class ClientVersionFormat
{
    public const int MaxLength = 32;

    [GeneratedRegex(@"^\d{1,9}\.\d{1,9}\.\d{1,9}(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$")]
    private static partial Regex SemVerPattern();

    public static bool TryNormalize(string? rawVersion, out string version)
    {
        version = string.Empty;

        if (string.IsNullOrWhiteSpace(rawVersion))
        {
            return false;
        }

        string trimmed = rawVersion.Trim();

        if (trimmed.Length > MaxLength || !SemVerPattern().IsMatch(trimmed))
        {
            return false;
        }

        version = trimmed;
        return true;
    }

    // SemVer precedence for the subset TryNormalize accepts: numeric major.minor.patch first
    // (so 1.0.10 sorts above 1.0.9, which a string sort gets wrong), then a release above any
    // prerelease of the same number. Build metadata is ignored, as SemVer specifies.
    public static int Compare(string left, string right)
    {
        (Version leftCore, string? leftPre) = Split(left);
        (Version rightCore, string? rightPre) = Split(right);

        int coreComparison = leftCore.CompareTo(rightCore);

        if (coreComparison != 0)
        {
            return coreComparison;
        }

        if (leftPre is null || rightPre is null)
        {
            return (leftPre is null ? 1 : 0) - (rightPre is null ? 1 : 0);
        }

        return string.CompareOrdinal(leftPre, rightPre);
    }

    private static (Version Core, string? PreRelease) Split(string version)
    {
        string withoutBuild = version.Split('+', 2)[0];
        string[] parts = withoutBuild.Split('-', 2);

        Version core = Version.TryParse(parts[0], out Version? parsed) ? parsed : new Version(0, 0, 0);

        return (core, parts.Length > 1 ? parts[1] : null);
    }
}
