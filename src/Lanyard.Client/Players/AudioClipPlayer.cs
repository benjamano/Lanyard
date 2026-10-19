using Lanyard.Shared.DTO;
using Lanyard.Shared.Enum;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Timer = System.Threading.Timer;

public interface IAudioClipPlayer
{
    event Action<AudioClipPlaybackReport>? PlaybackChanged;

    Task PlayAsync(AudioClipPlayCommand command);

    /// <summary>Stops the clip with this play id, or whatever is playing for Guid.Empty.</summary>
    void Stop(Guid playId);
}

/// <summary>
/// Plays audio clips on their own output, alongside the music, ducking the music while they play.
/// </summary>
/// <remarks>
/// A second WaveOutEvent on the same device rather than a mixer in front of the music: Windows
/// mixes the two, the music pipeline doesn't have to convert every song to a common format, and a
/// clip that fails can't stall the music.
///
/// A new clip cuts off the one playing. The music stays ducked across the change and only comes
/// back when the last clip ends, fails, is stopped, or runs past its length (the watchdog), so
/// nothing can leave the music quiet for good. The duck is in memory only, so a restart can't either.
/// </remarks>
public sealed class AudioClipPlayer(ILogger<AudioClipPlayer> logger, IAudioClipCacheService cache, IMusicDucker ducker) : IAudioClipPlayer, IDisposable
{
    // Extra time past the clip's length before the watchdog decides playback is stuck.
    private static readonly TimeSpan WatchdogGrace = TimeSpan.FromSeconds(10);

    private readonly object _lock = new();
    private ActivePlay? _current;

    public event Action<AudioClipPlaybackReport>? PlaybackChanged;

    private sealed class ActivePlay(AudioClipPlayCommand command)
    {
        public AudioClipPlayCommand Command { get; } = command;
        public Guid PlayId => Command.PlayId;
        public CancellationTokenSource Cancellation { get; } = new();
        public WaveOutEvent? Output { get; set; }
        public MediaFoundationReader? Reader { get; set; }
        public Timer? Watchdog { get; set; }
        public bool Done { get; set; }
    }

    public async Task PlayAsync(AudioClipPlayCommand command)
    {
        ActivePlay play = new(command);
        ActivePlay? previous;

        lock (_lock)
        {
            previous = _current;
            _current = play;
        }

        logger.LogInformation("AudioClipPlayer: Playing clip {ClipId} ({Name}) as play {PlayId}", command.ClipId, command.Name, command.PlayId);

        if (previous is not null)
        {
            Finish(previous, AudioClipPlaybackStatus.Stopped, "Cut off by another clip");
        }

        Report(play.PlayId, AudioClipPlaybackStatus.Preparing, null);

        try
        {
            string? source = await cache.GetAudioSourceAsync(command, play.Cancellation.Token);

            if (source is null)
            {
                Finish(play, AudioClipPlaybackStatus.Failed, $"The kiosk can't play {command.FileExtension} files");
                return;
            }

            MediaFoundationReader reader = new(source);
            VolumeSampleProvider clipLevel = new(reader.ToSampleProvider()) { Volume = Math.Clamp(command.Volume / 100f, 0f, 1f) };
            WaveOutEvent output = new();
            output.Init(clipLevel);
            output.PlaybackStopped += (_, e) => OnPlaybackStopped(play, e);

            bool alreadyDucked;
            TimeSpan fadeDown = TimeSpan.FromMilliseconds(Math.Max(0, command.FadeDownMs));

            // The duck happens under the lock, after checking the play is still wanted, so a Stop
            // racing it always restores after it, never before.
            lock (_lock)
            {
                play.Output = output;
                play.Reader = reader;

                if (play.Done)
                {
                    // Stopped or cut off while it was downloading; Finish already ran.
                    Release(play);
                    return;
                }

                alreadyDucked = ducker.IsDucked;
                ducker.Duck(command.DuckToPercent / 100f, fadeDown);
            }

            // Let the music get out of the way before the clip starts, unless a clip it's
            // replacing already had it down.
            if (!alreadyDucked && fadeDown > TimeSpan.Zero)
            {
                await Task.Delay(fadeDown, play.Cancellation.Token);
            }

            TimeSpan length = TimeSpan.FromSeconds(Math.Max(command.DurationSeconds, reader.TotalTime.TotalSeconds));

            lock (_lock)
            {
                if (play.Done)
                {
                    return;
                }

                output.Play();
                play.Watchdog = new Timer(_ => OnWatchdog(play), null, length + WatchdogGrace, Timeout.InfiniteTimeSpan);
            }

            Report(play.PlayId, AudioClipPlaybackStatus.Playing, null);
        }
        catch (OperationCanceledException)
        {
            // Stopped or cut off before it began; Finish has already reported it.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AudioClipPlayer: Failed to play clip {ClipId}", command.ClipId);
            Finish(play, AudioClipPlaybackStatus.Failed, ex.Message);
        }
    }

    public void Stop(Guid playId)
    {
        ActivePlay? play;

        lock (_lock)
        {
            play = _current;
        }

        if (play is null || (playId != Guid.Empty && play.PlayId != playId))
        {
            logger.LogInformation("AudioClipPlayer: Stop for play {PlayId} ignored, it isn't playing", playId);
            return;
        }

        logger.LogInformation("AudioClipPlayer: Stopping play {PlayId}", play.PlayId);
        Finish(play, AudioClipPlaybackStatus.Stopped, null);
    }

    private void OnPlaybackStopped(ActivePlay play, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            logger.LogError(e.Exception, "AudioClipPlayer: Clip {ClipId} stopped with an error", play.Command.ClipId);
            Finish(play, AudioClipPlaybackStatus.Failed, e.Exception.Message);
            return;
        }

        Finish(play, AudioClipPlaybackStatus.Finished, null);
    }

    private void OnWatchdog(ActivePlay play)
    {
        logger.LogWarning("AudioClipPlayer: Clip {ClipId} ran past its length, stopping it and restoring the music", play.Command.ClipId);
        Finish(play, AudioClipPlaybackStatus.Failed, "Ran past its length and was stopped");
    }

    // Runs once per play, whichever of end, failure, stop, cut-off or watchdog gets there first.
    private void Finish(ActivePlay play, AudioClipPlaybackStatus status, string? error)
    {
        bool wasCurrent;

        lock (_lock)
        {
            if (play.Done)
            {
                return;
            }

            play.Done = true;
            wasCurrent = ReferenceEquals(_current, play);

            if (wasCurrent)
            {
                _current = null;
            }
        }

        play.Cancellation.Cancel();

        // Off this thread: Finish can run inside the output's own PlaybackStopped handler, and
        // disposing a WaveOutEvent from there risks waiting on itself.
        _ = Task.Run(() => Release(play));

        // A clip that was cut off leaves the duck to the clip that replaced it.
        if (wasCurrent)
        {
            ducker.Restore(TimeSpan.FromMilliseconds(Math.Max(0, play.Command.FadeUpMs)));
        }

        logger.LogInformation("AudioClipPlayer: Play {PlayId} ended: {Status} {Error}", play.PlayId, status, error);
        Report(play.PlayId, status, error);
    }

    private void Release(ActivePlay play)
    {
        try
        {
            play.Watchdog?.Dispose();
            play.Output?.Stop();
            play.Output?.Dispose();
            play.Reader?.Dispose();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AudioClipPlayer: Failed to release play {PlayId}", play.PlayId);
        }
    }

    private void Report(Guid playId, AudioClipPlaybackStatus status, string? error)
    {
        try
        {
            PlaybackChanged?.Invoke(new AudioClipPlaybackReport { PlayId = playId, Status = status, Error = error });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AudioClipPlayer: Failed to report play {PlayId}", playId);
        }
    }

    public void Dispose()
    {
        Stop(Guid.Empty);
    }
}
