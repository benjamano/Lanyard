using Lanyard.Infrastructure.Branding;

namespace Lanyard.App.Components.Rota;

// Positions are colour-coded on the rota by indexing into the validated categorical chart
// palette rather than storing hex values - every position stays distinguishable in both themes
// and no company can pick a colour that fails the palette's contrast checks. Both theme values
// are emitted as CSS custom properties so a scoped stylesheet can switch on the active theme.
internal static class PositionColours
{
    public static int Count => BrandConstants.ChartCategoricalLight.Length;

    public static string Light(int colorIndex) =>
        BrandConstants.ChartCategoricalLight[Mod(colorIndex)];

    public static string Dark(int colorIndex) =>
        BrandConstants.ChartCategoricalDark[Mod(colorIndex)];

    public static string CssVariables(int colorIndex) =>
        $"--swatch-light: {Light(colorIndex)}; --swatch-dark: {Dark(colorIndex)};";

    private static int Mod(int colorIndex) =>
        ((colorIndex % Count) + Count) % Count;
}
