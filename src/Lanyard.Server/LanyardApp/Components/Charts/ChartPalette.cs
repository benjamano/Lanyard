using Lanyard.Infrastructure.Branding;

namespace Lanyard.App.Components.Charts;

// Sources every chart color from BrandConstants (the app's single validated,
// colorblind-safe palette) instead of inventing separate hex values here, so
// chart colors stay in sync with the rest of the brand system by construction.
public static class ChartPalette
{
    public const string Accent = BrandConstants.PrimaryColorHex;
    public const string Success = BrandConstants.PrimaryColorHex;

    // Indices into ChartCategoricalLight per its own documented, fixed slot
    // order (1 blue, 2 orange, 3 aqua, 4 yellow, 5 magenta, 6 green/brand,
    // 7 violet, 8 red) - slot 4 (yellow) reads as "caution", slot 8 (red) as
    // "negative", matching Warning/Danger semantics without new colors.
    public static readonly string Warning = BrandConstants.ChartCategoricalLight[3];
    public static readonly string Danger = BrandConstants.ChartCategoricalLight[7];

    public static readonly string[] Categorical = BrandConstants.ChartCategoricalLight;
}
