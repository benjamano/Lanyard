using System.Text;

namespace Lanyard.Application.Services.Projection.Bindings;

/// <summary>
/// Every bound value goes through here before it reaches a screen. The kiosk renders it as text
/// (Razor encodes it online, the offline player uses textContent), so this isn't HTML sanitising -
/// it keeps a value to one tidy line that fits a projected screen, whatever a source returns.
/// </summary>
public static class ProjectionBindingText
{
    public const int MaxLength = 200;

    public static string Normalise(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        StringBuilder builder = new(value.Length);
        bool pendingSpace = false;

        foreach (char c in value)
        {
            // Control characters (newlines, tabs, escape codes) become spaces, then runs of
            // whitespace collapse to one.
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        if (builder.Length > MaxLength)
        {
            builder.Length = MaxLength - 1;
            builder.Append('…');
        }

        return builder.ToString();
    }
}
