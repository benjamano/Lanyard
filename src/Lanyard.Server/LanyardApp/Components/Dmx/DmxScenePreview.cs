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

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                DmxSceneStep step = Steps[StepIndex];
                TimeSpan hold = step.Duration > TimeSpan.Zero ? step.Duration : FallbackStep;

                await Task.Delay(hold, token);

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
                            return Task.CompletedTask;
                        }

                        StepIndex = 0;
                    }
                    else
                    {
                        StepIndex++;
                    }

                    Apply(Steps[StepIndex]);
                    Changed?.Invoke();

                    return Task.CompletedTask;
                });
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
