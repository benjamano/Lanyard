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
}
