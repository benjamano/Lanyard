using Lanyard.Infrastructure.Models.Dmx;

namespace Lanyard.App.Components.Dmx;

/// <summary>
/// Plays a scene's steps on screen only. It never calls the DMX engine or the scene runner, so it
/// can't touch real lights or a scene running on the kiosk, and any number of people can preview
/// the same scene at once.
/// </summary>
/// <remarks>
/// BPM-synced timing depends on the song playing on the kiosk, which this has no access to, so
/// every step holds for its fixed Duration - a deliberate approximation (see the dmx-scene-engine
/// skill).
/// </remarks>
public sealed class DmxScenePreview(Func<Func<Task>, Task> dispatch) : IDisposable
{
    public const int UniverseSize = 512;

    private static readonly TimeSpan FallbackStep = TimeSpan.FromMilliseconds(200);

    private CancellationTokenSource? _playback;

    public Guid? SceneId { get; private set; }

    public IReadOnlyList<DmxSceneStep> Steps { get; private set; } = [];

    public bool Loop { get; private set; }

    public bool IsActive => SceneId != null && Steps.Count > 0;

    public bool IsPlaying { get; private set; }

    public int StepIndex { get; private set; }

    public byte[] Values { get; private set; } = new byte[UniverseSize + 1];

    public DmxUniverseView ValuesView { get; private set; } = new(new byte[UniverseSize + 1]);

    public event Action? Changed;

    public void Start(Guid sceneId, IReadOnlyList<DmxSceneStep> steps, bool loop)
    {
        Stop();

        if (steps.Count == 0)
        {
            return;
        }

        SceneId = sceneId;
        Steps = steps;
        Loop = loop;
        StepIndex = 0;
        Values = new byte[UniverseSize + 1];
        ValuesView = new DmxUniverseView(Values);

        Apply(Steps[0]);
        Play();
    }

    public void Stop()
    {
        CancelPlayback();

        SceneId = null;
        Steps = [];
        IsPlaying = false;
        StepIndex = 0;
        Changed?.Invoke();
    }

    public void TogglePlay()
    {
        if (IsPlaying)
        {
            CancelPlayback();
            IsPlaying = false;
            Changed?.Invoke();
        }
        else
        {
            Play();
        }
    }

    public void ScrubTo(int index)
    {
        if (index < 0 || index >= Steps.Count)
        {
            return;
        }

        CancelPlayback();
        IsPlaying = false;
        StepIndex = index;

        // Replay from the start so a channel set in an earlier step shows as it would on the rig.
        Values = new byte[UniverseSize + 1];
        ValuesView = new DmxUniverseView(Values);

        for (int i = 0; i <= index; i++)
        {
            Apply(Steps[i]);
        }

        Changed?.Invoke();
    }

    private void Play()
    {
        if (!IsActive)
        {
            return;
        }

        CancelPlayback();
        _playback = new CancellationTokenSource();
        IsPlaying = true;
        Changed?.Invoke();

        _ = RunAsync(_playback.Token);
    }

    private static readonly TimeSpan PreviewFadeTick = TimeSpan.FromMilliseconds(50);

    private async Task RunAsync(CancellationToken token)
    {
        // The step on screen at start was applied instantly; later steps fade in if they have a fade.
        bool fadeIn = false;

        try
        {
            while (!token.IsCancellationRequested)
            {
                DmxSceneStep step = Steps[StepIndex];
                TimeSpan hold = step.Duration > TimeSpan.Zero ? step.Duration : FallbackStep;
                System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
                TimeSpan fade = fadeIn ? DmxFade.Effective(step.FadeTime, hold) : TimeSpan.Zero;

                if (fade > TimeSpan.Zero)
                {
                    byte[] from = (byte[])Values.Clone();
                    double progress = 0;

                    while (progress < 1)
                    {
                        await Task.Delay(PreviewFadeTick, token);
                        progress = Math.Min(elapsed.Elapsed / fade, 1);

                        double p = progress;
                        await dispatch(() =>
                        {
                            if (!token.IsCancellationRequested)
                            {
                                foreach (DmxSceneStepChannelValue value in step.ChannelValues)
                                {
                                    if (value.ChannelNumber is >= 1 and <= UniverseSize)
                                    {
                                        Values[value.ChannelNumber] = DmxFade.Interpolate(from[value.ChannelNumber], value.Value, p);
                                    }
                                }

                                Changed?.Invoke();
                            }

                            return Task.CompletedTask;
                        });
                    }
                }

                TimeSpan remaining = hold - elapsed.Elapsed;

                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, token);
                }

                bool stop = false;

                await dispatch(() =>
                {
                    if (token.IsCancellationRequested)
                    {
                        return Task.CompletedTask;
                    }

                    if (StepIndex + 1 >= Steps.Count)
                    {
                        if (!Loop)
                        {
                            IsPlaying = false;
                            CancelPlayback();
                            Changed?.Invoke();
                            stop = true;
                            return Task.CompletedTask;
                        }

                        StepIndex = 0;
                    }
                    else
                    {
                        StepIndex++;
                    }

                    DmxSceneStep next = Steps[StepIndex];
                    fadeIn = next.FadeTime > TimeSpan.Zero;

                    // A fading step is drawn by the fade itself; others snap.
                    if (!fadeIn)
                    {
                        Apply(next);
                    }

                    Changed?.Invoke();

                    return Task.CompletedTask;
                });

                if (stop)
                {
                    return;
                }
            }
        }
        catch (TaskCanceledException)
        {
            // Paused, scrubbed or stopped.
        }
    }

    private void Apply(DmxSceneStep step)
    {
        foreach (DmxSceneStepChannelValue value in step.ChannelValues)
        {
            if (value.ChannelNumber is >= 1 and <= UniverseSize)
            {
                Values[value.ChannelNumber] = value.Value;
            }
        }
    }

    private void CancelPlayback()
    {
        _playback?.Cancel();
        _playback?.Dispose();
        _playback = null;
    }

    public void Dispose() => CancelPlayback();
}
