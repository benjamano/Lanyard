using System.Globalization;

namespace Lanyard.App.Components.Demo;

// A light-to-dark ramp around a brand colour, for showing a visitor the shades their theme colour
// produces. Fluent computes its real brand ramp in JavaScript; this approximates it well enough to
// show "these are your colours" without a round trip.
public static class ColorPalette
{
    public static IReadOnlyList<string> Ramp(string hex)
    {
        (double h, double s, double l) = ToHsl(hex);
        double[] lightness = [0.95, 0.88, 0.78, 0.66, l, Math.Max(0.08, l * 0.8), Math.Max(0.06, l * 0.62), Math.Max(0.04, l * 0.45)];

        return [.. lightness.Select(x => ToHex(h, s, x))];
    }

    // Black or white, whichever reads better on the given background.
    public static string TextOn(string hex)
    {
        (double r, double g, double b) = Rgb(hex);
        double luminance = (0.2126 * Channel(r)) + (0.7152 * Channel(g)) + (0.0722 * Channel(b));

        return luminance > 0.45 ? "#1F1F1F" : "#FFFFFF";

        static double Channel(double c) => c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static (double R, double G, double B) Rgb(string hex) => (
        int.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber) / 255.0,
        int.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber) / 255.0,
        int.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber) / 255.0);

    private static (double H, double S, double L) ToHsl(string hex)
    {
        (double r, double g, double b) = Rgb(hex);
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2;

        if (max == min)
        {
            return (0, 0, l);
        }

        double d = max - min;
        double s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h = max == r ? ((g - b) / d) + (g < b ? 6 : 0)
            : max == g ? ((b - r) / d) + 2
            : ((r - g) / d) + 4;

        return (h / 6, s, l);
    }

    private static string ToHex(double h, double s, double l)
    {
        double r, g, b;

        if (s == 0)
        {
            r = g = b = l;
        }
        else
        {
            double q = l < 0.5 ? l * (1 + s) : l + s - (l * s);
            double p = (2 * l) - q;
            r = Hue(p, q, h + (1.0 / 3));
            g = Hue(p, q, h);
            b = Hue(p, q, h - (1.0 / 3));
        }

        return $"#{(int)Math.Round(r * 255):X2}{(int)Math.Round(g * 255):X2}{(int)Math.Round(b * 255):X2}";

        static double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + ((q - p) * 6 * t);
            if (t < 1.0 / 2) return q;
            if (t < 2.0 / 3) return p + ((q - p) * ((2.0 / 3) - t) * 6);
            return p;
        }
    }
}
