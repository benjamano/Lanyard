namespace Lanyard.Infrastructure.Branding;

public static class BrandConstants
{
    public const string PrimaryColorHex = "#167a47";
    public const string PrimaryColorName = "Green";
    public const string? LogoUrl = null; // reserved for future branding work

    // Categorical chart palette - 8 slots, fixed order, colorblind-safe (validated via
    // the dataviz skill's validate_palette.js against both light and dark surfaces).
    // Slot 6 (green) is the brand color; the other 7 hues exist purely to keep series
    // visually distinct and must stay in this order - reordering breaks the safety checks.
    public static readonly string[] ChartCategoricalLight =
    [
        "#2a78d6", // 1 blue
        "#eb6834", // 2 orange
        "#1baf7a", // 3 aqua
        "#eda100", // 4 yellow
        "#e87ba4", // 5 magenta
        PrimaryColorHex, // 6 green (brand)
        "#4a3aa7", // 7 violet
        "#e34948", // 8 red
    ];

    public static readonly string[] ChartCategoricalDark =
    [
        "#3987e5", "#d95926", "#199e70", "#c98500",
        "#d55181", PrimaryColorHex, "#9085e9", "#e66767",
    ];

    // Shared by every call site that resolves a company's accent color (live UI theme,
    // invite emails, recurrence reminder emails) so the fallback rule lives in one place.
    public static string ResolveAccentColor(string? companyThemeColorHex) =>
        string.IsNullOrWhiteSpace(companyThemeColorHex) ? PrimaryColorHex : companyThemeColorHex;

    // A tenant that hasn't chosen a secondary color gets its primary back, so callers can
    // always set both custom properties without branching on null.
    public static string ResolveSecondaryColor(string? companySecondaryColorHex, string? companyThemeColorHex) =>
        string.IsNullOrWhiteSpace(companySecondaryColorHex)
            ? ResolveAccentColor(companyThemeColorHex)
            : companySecondaryColorHex;

    // Text/icon color to place *on* a tenant's brand color. Tenants pick their brand color for
    // print, not for contrast, so hardcoding white here would render pale brands (yellows,
    // light greens) unreadable. Picks whichever of near-black/white scores better under the
    // WCAG 2.1 contrast formula; on an unparseable value it falls back to the brand default's
    // answer rather than guessing.
    public static string ResolveOnPrimaryColor(string? companyThemeColorHex)
    {
        string accent = ResolveAccentColor(companyThemeColorHex);

        if (!TryParseHex(accent, out double r, out double g, out double b))
        {
            return OnDarkTextHex;
        }

        double luminance = 0.2126 * ToLinear(r) + 0.7152 * ToLinear(g) + 0.0722 * ToLinear(b);

        // Contrast against white vs against near-black, per WCAG's (L1+0.05)/(L2+0.05).
        double contrastWithLight = 1.05 / (luminance + 0.05);
        double contrastWithDark = (luminance + 0.05) / 0.05;

        return contrastWithLight >= contrastWithDark ? OnDarkTextHex : OnLightTextHex;
    }

    public const string OnDarkTextHex = "#ffffff";
    public const string OnLightTextHex = "#111111";

    private static bool TryParseHex(string hex, out double r, out double g, out double b)
    {
        r = g = b = 0;

        ReadOnlySpan<char> span = hex.AsSpan().Trim();

        if (span.Length > 0 && span[0] == '#')
        {
            span = span[1..];
        }

        // Accept the 3-digit shorthand as well as full 6-digit values; admins type both.
        if (span.Length == 3)
        {
            Span<char> expanded = stackalloc char[6];
            for (int i = 0; i < 3; i++)
            {
                expanded[i * 2] = span[i];
                expanded[(i * 2) + 1] = span[i];
            }

            return TryParseSixDigit(expanded, out r, out g, out b);
        }

        return span.Length == 6 && TryParseSixDigit(span, out r, out g, out b);
    }

    private static bool TryParseSixDigit(ReadOnlySpan<char> span, out double r, out double g, out double b)
    {
        r = g = b = 0;

        if (!byte.TryParse(span[..2], System.Globalization.NumberStyles.HexNumber, null, out byte rb)
            || !byte.TryParse(span.Slice(2, 2), System.Globalization.NumberStyles.HexNumber, null, out byte gb)
            || !byte.TryParse(span.Slice(4, 2), System.Globalization.NumberStyles.HexNumber, null, out byte bb))
        {
            return false;
        }

        r = rb / 255.0;
        g = gb / 255.0;
        b = bb / 255.0;

        return true;
    }

    private static double ToLinear(double channel) =>
        channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
}
