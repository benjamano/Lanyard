namespace Lanyard.Shared.Diagnostics;

/// <summary>
/// The sizes and JPEG qualities a kiosk tries, in order, when fitting a screenshot under the
/// byte limit. Kept apart from the WPF encoder so the steps can be unit tested off Windows.
/// </summary>
public static class ScreenshotSizing
{
    /// <summary>The widest a kiosk will send, whatever the server asks for.</summary>
    public const int MaxWidthLimit = 1920;

    /// <summary>
    /// The largest JPEG a kiosk will send. The reply goes through the hub as JSON, where the
    /// bytes become base64 (a third bigger), and the hub rejects any message over 256 KB.
    /// </summary>
    public const int MaxBytesLimit = 150 * 1024;

    public const int MinWidth = 320;

    private static readonly int[] Qualities = [75, 60];

    private const double ShrinkFactor = 0.75;

    public static int ClampMaxWidth(int requested)
    {
        return requested <= 0 ? MaxWidthLimit : Math.Clamp(requested, MinWidth, MaxWidthLimit);
    }

    public static int ClampMaxBytes(int requested)
    {
        return requested <= 0 ? MaxBytesLimit : Math.Clamp(requested, 16 * 1024, MaxBytesLimit);
    }

    /// <summary>
    /// Every size and quality to try, best first: the source scaled down to <paramref name="maxWidth"/>
    /// (never up), at each quality, then smaller by a quarter each round until <see cref="MinWidth"/>.
    /// The caller keeps the first result that fits, or the last one if none do.
    /// </summary>
    public static IEnumerable<(int Width, int Height, int Quality)> Attempts(int sourceWidth, int sourceHeight, int maxWidth)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            yield break;
        }

        int width = Math.Min(sourceWidth, ClampMaxWidth(maxWidth));

        while (true)
        {
            int height = Math.Max(1, (int)Math.Round((double)sourceHeight * width / sourceWidth));

            foreach (int quality in Qualities)
            {
                yield return (width, height, quality);
            }

            if (width <= MinWidth)
            {
                yield break;
            }

            width = Math.Max(MinWidth, (int)(width * ShrinkFactor));
        }
    }
}
