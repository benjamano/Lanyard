using SkiaSharp;

namespace Lanyard.Application.Services;

/// <summary>A small WebP for Files' grid, and the size of the picture it came from.</summary>
public sealed record RenderedThumbnail(byte[] WebP, int SourceWidth, int SourceHeight);

/// <summary>
/// Turns an image (or a video's poster frame) into the thumbnail Files shows in its grid, so a
/// folder of 8 MB phone photos doesn't download 8 MB per tile. Pure: no storage, no database.
/// </summary>
public static class ThumbnailRenderer
{
    /// <summary>Longest side of a thumbnail. Grid tiles are about 170px wide, so this is sharp at 2x.</summary>
    public const int MaxSide = 480;

    // Refuse to decode anything absurd (a decompression bomb), whatever its file size.
    private const long MaxSourcePixels = 80_000_000;

    // What may actually be decoded into memory: 16 MP is about 64 MB. JPEG decodes straight to a
    // fraction of its size, so a huge photo is fine; PNG, WebP and GIF can't, so above this they
    // get no thumbnail (Files shows the picture itself) rather than a 300 MB bitmap.
    private const long MaxDecodedPixels = 16_000_000;

    // Uploads arrive three at a time per person; never decode more than two pictures at once.
    private static readonly SemaphoreSlim Decoding = new(2, 2);

    /// <summary>Null when the bytes aren't an image SkiaSharp can read (an SVG, HEIC on some builds, a broken file).</summary>
    public static RenderedThumbnail? Render(Stream source)
    {
        Decoding.Wait();

        try
        {
            using SKCodec? codec = SKCodec.Create(source);

            if (codec is null)
                return null;

            SKImageInfo info = codec.Info;

            if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > MaxSourcePixels)
                return null;

            // Decode at a reduced size straight away where the codec can (JPEG can, cheaply).
            float scale = Math.Min(1f, (float)MaxSide / Math.Max(info.Width, info.Height));
            SKSizeI decodeSize = codec.GetScaledDimensions(Math.Max(scale, 0.125f));

            if ((long)decodeSize.Width * decodeSize.Height > MaxDecodedPixels)
                return null;
            SKImageInfo decodeInfo = new(decodeSize.Width, decodeSize.Height, SKColorType.Rgba8888, SKAlphaType.Premul);

            using SKBitmap decoded = new(decodeInfo);

            if (codec.GetPixels(decodeInfo, decoded.GetPixels()) is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                return null;

            using SKBitmap oriented = Orient(decoded, codec.EncodedOrigin);

            (int width, int height) = Fit(oriented.Width, oriented.Height);
            using SKBitmap resized = new(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
            using (SKCanvas canvas = new(resized))
            {
                canvas.Clear(SKColors.Transparent);
                using SKImage image = SKImage.FromBitmap(oriented);
                using SKPaint paint = new() { IsAntialias = true };
                canvas.DrawImage(image, SKRect.Create(width, height), new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
            }

            using SKData data = resized.Encode(SKEncodedImageFormat.Webp, 78);

            if (data is null)
                return null;

            bool sideways = codec.EncodedOrigin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

            return new RenderedThumbnail(data.ToArray(), sideways ? info.Height : info.Width, sideways ? info.Width : info.Height);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            Decoding.Release();
        }
    }

    private static (int Width, int Height) Fit(int width, int height)
    {
        if (Math.Max(width, height) <= MaxSide)
            return (width, height);

        double scale = (double)MaxSide / Math.Max(width, height);
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    // Phone photos are stored sideways with an EXIF orientation; turn them the right way up.
    private static SKBitmap Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft)
            return source.Copy();

        bool sideways = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        SKBitmap rotated = new(sideways ? source.Height : source.Width, sideways ? source.Width : source.Height);

        using SKCanvas canvas = new(rotated);

        switch (origin)
        {
            case SKEncodedOrigin.TopRight:
                canvas.Scale(-1, 1, source.Width / 2f, 0);
                break;
            case SKEncodedOrigin.BottomRight:
                canvas.RotateDegrees(180, source.Width / 2f, source.Height / 2f);
                break;
            case SKEncodedOrigin.BottomLeft:
                canvas.Scale(1, -1, 0, source.Height / 2f);
                break;
            case SKEncodedOrigin.LeftTop:
                canvas.Translate(rotated.Width, 0);
                canvas.RotateDegrees(90);
                canvas.Scale(1, -1, 0, source.Height / 2f);
                break;
            case SKEncodedOrigin.RightTop:
                canvas.Translate(rotated.Width, 0);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.RightBottom:
                canvas.Translate(rotated.Width, 0);
                canvas.RotateDegrees(90);
                canvas.Scale(-1, 1, source.Width / 2f, 0);
                break;
            case SKEncodedOrigin.LeftBottom:
                canvas.Translate(0, rotated.Height);
                canvas.RotateDegrees(-90);
                break;
        }

        canvas.DrawBitmap(source, 0, 0);
        return rotated;
    }
}
