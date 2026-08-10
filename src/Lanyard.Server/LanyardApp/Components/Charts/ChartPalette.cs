namespace Lanyard.App.Components.Charts;

// Central place to keep Radzen chart colors visually aligned with the app's
// FluentUI default Web accent, without reading FluentUI's JS-computed CSS
// tokens at runtime (fragile - see CLAUDE.md notes on v5 theming).
public static class ChartPalette
{
    public const string Accent = "#0F6CBD";
    public const string Success = "#0E7A0D";
    public const string Danger = "#C50F1F";
    public const string Warning = "#835C00";
    public const string Neutral = "#605E5C";

    public static readonly string[] Categorical =
    [
        "#0F6CBD", "#8764B8", "#00B7C3", "#498205", "#C239B3",
        "#C50F1F", "#835C00", "#0078D4", "#107C10", "#5C2E91"
    ];
}
