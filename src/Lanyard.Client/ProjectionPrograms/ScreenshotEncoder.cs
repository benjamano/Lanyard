using Lanyard.Shared.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lanyard.Client.ProjectionPrograms;

/// <summary>
/// Turns a window capture into a JPEG small enough to send through the hub, following the steps in
/// <see cref="ScreenshotSizing"/>. Uses only WPF imaging, and freezes the bitmaps so it can run off
/// the UI thread.
/// </summary>
public static class ScreenshotEncoder
{
    public static (byte[] Jpeg, int Width, int Height) Encode(byte[] png, int maxWidth, int maxBytes)
    {
        using MemoryStream input = new(png);

        BitmapDecoder decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource source = decoder.Frames[0];
        source.Freeze();

        byte[]? best = null;
        int bestWidth = 0;
        int bestHeight = 0;

        foreach ((int width, int height, int quality) in ScreenshotSizing.Attempts(source.PixelWidth, source.PixelHeight, maxWidth))
        {
            BitmapSource scaled = width == source.PixelWidth
                ? source
                : new TransformedBitmap(source, new ScaleTransform((double)width / source.PixelWidth, (double)height / source.PixelHeight));

            if (!scaled.IsFrozen)
            {
                scaled.Freeze();
            }

            JpegBitmapEncoder encoder = new() { QualityLevel = quality };
            encoder.Frames.Add(BitmapFrame.Create(scaled));

            using MemoryStream output = new();
            encoder.Save(output);

            best = output.ToArray();
            bestWidth = width;
            bestHeight = height;

            if (best.Length <= maxBytes)
            {
                break;
            }
        }

        if (best == null)
        {
            throw new InvalidOperationException("The capture was empty.");
        }

        return (best, bestWidth, bestHeight);
    }
}
