using Lanyard.Infrastructure.DTO;
using Lanyard.Shared.AudioClips;

namespace Lanyard.Application.Services.AudioClips;

/// <summary>
/// Checks an uploaded file really is a short clip a kiosk can play. The file manager itself
/// checks nothing, and a clip goes straight to every kiosk, so this is stricter: a known
/// extension, a size limit, and audio that actually parses with a sensible length. A renamed
/// document or executable fails the parse.
/// </summary>
public static class AudioClipFileCheck
{
    public const long MaxBytes = 25L * 1024 * 1024;
    public static readonly TimeSpan MaxLength = TimeSpan.FromMinutes(5);

    /// <summary>The clip's length in seconds, or why it can't be a clip.</summary>
    public static Result<double> Inspect(string fileName, long length, Func<Stream> openRead)
    {
        if (AudioClipFormats.ExtensionOf(fileName) is null)
        {
            return Result<double>.Fail($"Clips must be {string.Join(", ", AudioClipFormats.Extensions.Select(x => x.TrimStart('.').ToUpperInvariant()))} files.");
        }

        if (length <= 0)
        {
            return Result<double>.Fail("That file is empty.");
        }

        if (length > MaxBytes)
        {
            return Result<double>.Fail($"Clips can be at most {MaxBytes / (1024 * 1024)} MB.");
        }

        double seconds;

        try
        {
            using Stream stream = openRead();
            using TagLib.File tag = TagLib.File.Create(new StreamAbstraction(Path.GetFileName(fileName), stream));

            if ((tag.Properties.MediaTypes & TagLib.MediaTypes.Audio) == 0)
            {
                return Result<double>.Fail("That file doesn't contain any audio.");
            }

            seconds = tag.Properties.Duration.TotalSeconds;
        }
        catch (Exception)
        {
            return Result<double>.Fail("That file isn't audio a kiosk can play, or it's damaged.");
        }

        if (seconds <= 0)
        {
            return Result<double>.Fail("Couldn't work out how long that clip is, so it may be damaged.");
        }

        if (seconds > MaxLength.TotalSeconds)
        {
            return Result<double>.Fail($"Clips can be at most {MaxLength.TotalMinutes:0} minutes long. For longer audio, add it to a playlist.");
        }

        return Result<double>.Ok(Math.Round(seconds, 1));
    }

    private sealed class StreamAbstraction(string name, Stream stream) : TagLib.File.IFileAbstraction
    {
        public string Name { get; } = name;
        public Stream ReadStream => stream;
        public Stream WriteStream => stream;

        public void CloseStream(Stream toClose)
        {
            // The caller owns the stream.
        }
    }
}
