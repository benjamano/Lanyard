using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using NLayer.NAudioSupport;

namespace Lanyard.Application.Services;

public class SongAnalysisService(
    IDbContextFactory<ApplicationDbContext> factory,
    IFileService fileService,
    ILogger<SongAnalysisService> logger) : ISongAnalysisService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly IFileService _fileService = fileService;
    private readonly ILogger<SongAnalysisService> _logger = logger;

    // Formats decodable everywhere via managed readers. Everything else needs
    // MediaFoundation, which only exists on Windows.
    private static readonly string[] _managedDecodeExtensions = [".mp3", ".wav"];

    public async Task<Result<bool>> AnalyzeSongAsync(Guid songId, CancellationToken cancellationToken)
    {
        string? tempPath = null;

        try
        {
            await using ApplicationDbContext context = await _factory.CreateDbContextAsync(cancellationToken);

            Song? song = await context.Songs
                .TagWithCallSite()
                .Where(s => s.Id == songId)
                .FirstOrDefaultAsync(cancellationToken);

            if (song == null)
            {
                return Result<bool>.Fail("Song not found.");
            }

            if (!song.IsActive || song.BpmAnalysisStatus != BpmAnalysisStatus.NotAnalyzed)
            {
                return Result<bool>.Ok(true);
            }

            tempPath = await MaterializeAudioAsync(song, cancellationToken);

            if (tempPath == null)
            {
                song.BpmAnalysisStatus = BpmAnalysisStatus.Failed;
                song.BpmAnalysisDate = DateTime.UtcNow;
                await context.SaveChangesAsync(cancellationToken);

                return Result<bool>.Fail("Audio file could not be retrieved for analysis.");
            }

            double? tagBpm = TryReadTagBpm(tempPath);
            string extension = Path.GetExtension(song.FilePath).ToLowerInvariant();
            (float[] samples, int sampleRate)? audio = TryDecodeToMono(tempPath, extension);

            if (audio == null)
            {
                // Can't inspect the waveform on this platform/format: fall back to
                // the tag BPM when present (rate-only sync), otherwise mark terminal.
                song.Bpm = tagBpm;
                song.FirstBeatOffsetSeconds = null;
                song.BpmAnalysisStatus = tagBpm.HasValue ? BpmAnalysisStatus.TagOnly : BpmAnalysisStatus.Unsupported;
                song.BpmAnalysisDate = DateTime.UtcNow;
                await context.SaveChangesAsync(cancellationToken);

                _logger.LogInformation("Song {SongId} ({Name}) analysis: no decoder for {Extension}; status {Status}, tag BPM {Bpm}",
                    song.Id, song.Name, extension, song.BpmAnalysisStatus, tagBpm);

                return Result<bool>.Ok(true);
            }

            BpmAnalysisResult? analysis = SongBpmAnalyzer.Analyze(audio.Value.samples, audio.Value.sampleRate, tagBpm);

            if (analysis == null)
            {
                song.Bpm = tagBpm;
                song.FirstBeatOffsetSeconds = null;
                song.BpmAnalysisStatus = tagBpm.HasValue ? BpmAnalysisStatus.TagOnly : BpmAnalysisStatus.Failed;
                song.BpmAnalysisDate = DateTime.UtcNow;
                await context.SaveChangesAsync(cancellationToken);

                _logger.LogInformation("Song {SongId} ({Name}) analysis: no confident tempo found; status {Status}",
                    song.Id, song.Name, song.BpmAnalysisStatus);

                return Result<bool>.Ok(true);
            }

            song.Bpm = analysis.Bpm;
            song.FirstBeatOffsetSeconds = analysis.FirstBeatOffsetSeconds;
            song.BpmAnalysisStatus = BpmAnalysisStatus.Analyzed;
            song.BpmAnalysisDate = DateTime.UtcNow;
            await context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Song {SongId} ({Name}) analyzed: {Bpm:F1} BPM, first beat at {Offset:F3}s (confidence {Confidence:F1})",
                song.Id, song.Name, analysis.Bpm, analysis.FirstBeatOffsetSeconds, analysis.Confidence);

            return Result<bool>.Ok(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error analyzing song {SongId}", songId);

            await TryMarkFailedAsync(songId);

            return Result<bool>.Fail($"An error occurred while analyzing the song: {ex.Message}");
        }
        finally
        {
            if (tempPath != null)
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException)
                {
                    // Temp cleanup is best-effort.
                }
            }
        }
    }

    /// <summary>
    /// Copies the song's audio to a local temp file (NAudio readers need seekable,
    /// path-addressable input). Uploaded songs come through IFileService (disk in
    /// dev, bucket in prod); songs without file metadata are tried by path.
    /// </summary>
    private async Task<string?> MaterializeAudioAsync(Song song, CancellationToken cancellationToken)
    {
        string extension = Path.GetExtension(song.FilePath);
        string tempPath = Path.Combine(Path.GetTempPath(), $"lanyard-bpm-{song.Id}{extension}");

        if (song.FileMetadataId.HasValue)
        {
            Result<Stream> downloadResult = await _fileService.DownloadFileAsync(song.FileMetadataId.Value, cancellationToken);

            if (!downloadResult.IsSuccess || downloadResult.Data == null)
            {
                _logger.LogWarning("Song {SongId}: could not download audio for analysis: {Error}", song.Id, downloadResult.Error);
                return null;
            }

            await using Stream source = downloadResult.Data;
            await using FileStream target = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(target, cancellationToken);

            return tempPath;
        }

        if (File.Exists(song.FilePath))
        {
            File.Copy(song.FilePath, tempPath, overwrite: true);
            return tempPath;
        }

        return null;
    }

    private double? TryReadTagBpm(string path)
    {
        try
        {
            using TagLib.File tagFile = TagLib.File.Create(path);
            uint tagBpm = tagFile.Tag.BeatsPerMinute;

            return tagBpm > 0 ? tagBpm : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read tags from {Path}", path);
            return null;
        }
    }

    private (float[] samples, int sampleRate)? TryDecodeToMono(string path, string extension)
    {
        try
        {
            using WaveStream reader = CreateReader(path, extension) ?? throw new NotSupportedException($"No decoder available for {extension}.");

            ISampleProvider provider = reader.ToSampleProvider();
            int channels = provider.WaveFormat.Channels;
            int sampleRate = provider.WaveFormat.SampleRate;

            // Only the analyzer's window is needed; cap the read so a long file
            // doesn't balloon memory. +1s of slack keeps the last frame intact.
            int maxFrames = (int)(91L * sampleRate);
            List<float> mono = new(capacity: Math.Min(maxFrames, 16 * 1024 * 1024 / sizeof(float)));

            float[] buffer = new float[sampleRate * channels];
            int read;

            while ((read = provider.Read(buffer, 0, buffer.Length)) > 0 && mono.Count < maxFrames)
            {
                for (int i = 0; i + channels <= read; i += channels)
                {
                    float sum = 0;

                    for (int channel = 0; channel < channels; channel++)
                    {
                        sum += buffer[i + channel];
                    }

                    mono.Add(sum / channels);
                }
            }

            return mono.Count == 0 ? null : (mono.ToArray(), sampleRate);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not decode {Path} for BPM analysis", path);
            return null;
        }
    }

    private static WaveStream? CreateReader(string path, string extension)
    {
        if (extension == ".mp3")
        {
            // NLayer: fully managed MP3 decode, works on Linux (production) too.
            return new Mp3FileReaderBase(path, waveFormat => new Mp3FrameDecompressor(waveFormat));
        }

        if (extension == ".wav")
        {
            return new WaveFileReader(path);
        }

        // Everything else needs an OS codec; MediaFoundation is Windows-only.
        return OperatingSystem.IsWindows() ? new MediaFoundationReader(path) : null;
    }

    private async Task TryMarkFailedAsync(Guid songId)
    {
        try
        {
            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            Song? song = await context.Songs
                .TagWithCallSite()
                .Where(s => s.Id == songId)
                .FirstOrDefaultAsync();

            if (song != null && song.BpmAnalysisStatus == BpmAnalysisStatus.NotAnalyzed)
            {
                song.BpmAnalysisStatus = BpmAnalysisStatus.Failed;
                song.BpmAnalysisDate = DateTime.UtcNow;
                await context.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not mark song {SongId} analysis as failed", songId);
        }
    }
}
