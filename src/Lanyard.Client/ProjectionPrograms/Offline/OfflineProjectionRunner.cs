using Lanyard.Shared.DTO;
using Lanyard.Shared.Kiosk;
using Lanyard.Shared.Projection;
using Microsoft.Extensions.Logging;

namespace Lanyard.Client.ProjectionPrograms.Offline;

/// <summary>
/// Plays one screen's cached program while the kiosk can't reach the server. The kiosk-side
/// counterpart of the server's ProjectionProgramRunnerService: same step order, holds and repeat
/// rules (both use ProjectionSequence), but no pause or skip, since nobody can send those while
/// offline. It always starts at the first step; when the server comes back it starts its own run
/// from the first step too, so there's no position to keep in step.
/// </summary>
public sealed class OfflineProjectionRunner(ProjectionWindow window, KioskOfflineDisplayDTO display, IReadOnlySet<Guid> cachedVideoIds, ILogger logger) : IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    public void Start()
    {
        _ = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts.Cancel();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            window.PostOfflineMessage(new { type = "theme", dark = display.IsDarkTheme }, isTheme: true);

            List<KioskOfflineStepDTO> steps = OfflineCachePlanner.PlayableSteps(display, cachedVideoIds);

            if (steps.Count == 0)
            {
                logger.LogInformation("Offline: nothing on display {DisplayIndex} can play offline, showing the idle screen", display.DisplayIndex);
                window.PostOfflineMessage(new { type = "idle" });
                return;
            }

            logger.LogInformation("Offline: playing {ProgramName} on display {DisplayIndex} ({StepCount} steps)", display.ProgramName, display.DisplayIndex, steps.Count);

            int index = 0;
            int completedPasses = 0;

            while (!token.IsCancellationRequested)
            {
                KioskOfflineStepDTO step = steps[index];

                object? message = ToMessage(step);

                // A Delay step sends nothing, so the previous step stays on screen, as online.
                if (message != null)
                {
                    window.PostOfflineMessage(message);
                }

                await Task.Delay(ProjectionSequence.EffectiveHold(step.HoldForMilliseconds), token);

                ProjectionStepPosition next = ProjectionSequence.Advance(index, steps.Count, completedPasses, display.RepeatInfinitely, display.RepeatNumberOfTimes);

                if (next.Finished)
                {
                    // An ambient program that runs out of repeats stays on its last step, as online.
                    logger.LogInformation("Offline: {ProgramName} on display {DisplayIndex} finished its repeats", display.ProgramName, display.DisplayIndex);
                    return;
                }

                index = next.Index;
                completedPasses = next.CompletedPasses;
            }
        }
        catch (OperationCanceledException)
        {
            // Handing back to the server.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Offline: playback failed on display {DisplayIndex}", display.DisplayIndex);
        }
    }

    private static object? ToMessage(KioskOfflineStepDTO step)
    {
        return step.TemplateName switch
        {
            ProjectionTemplateNames.ShowText => new { type = "text", text = step.Text ?? string.Empty },
            ProjectionTemplateNames.PlayVideo => new
            {
                type = "video",
                src = $"https://{ProjectionWindow.OfflineMediaHost}/{IKioskOfflineCache.VideoFileName(step.VideoFileId!.Value)}",
                loop = step.VideoShouldRepeat,
                volume = step.VideoVolume,
            },
            _ => null,
        };
    }
}
