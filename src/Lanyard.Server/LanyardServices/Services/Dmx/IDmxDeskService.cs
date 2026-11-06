using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models.Dmx;

/// <summary>
/// What a signed-in user's DMX desk may do. Scoped, and checks every kiosk and scene against the
/// caller's company before handing off to the system-level <see cref="IDmxService"/> and
/// <see cref="IDmxSceneRunnerService"/> singletons, which trust whoever calls them. Components
/// use this, never the singletons directly.
/// </summary>
public interface IDmxDeskService
{
    Task<Result<IReadOnlyList<DmxChannel>>> GetChannelsAsync(Guid clientId);

    Task<Result<bool>> SetChannelsAsync(Guid clientId, IReadOnlyList<DmxChannel> channels);

    /// <summary>
    /// Channel changes for one kiosk, on the writer's thread (marshal with InvokeAsync).
    /// Dispose the result to stop.
    /// </summary>
    Task<Result<IDisposable>> SubscribeToChannelsAsync(Guid clientId, Action<IReadOnlyList<DmxChannel>> handler, Action<DmxOutputState>? onOutputChanged = null);

    Task<Result<DmxOutputState>> GetOutputStateAsync(Guid clientId);

    Task<Result<bool>> SetGrandMasterAsync(Guid clientId, byte level);

    Task<Result<bool>> SetBlackoutAsync(Guid clientId, bool isBlackout);

    Task<Result<bool>> StartSceneAsync(Guid clientId, Guid sceneId, TimeSpan? holdFor = null);

    Task<Result<bool>> StopSceneAsync(Guid clientId, Guid sceneId);

    Task<Result<bool>> StopAllScenesAsync(Guid clientId);

    Task<Result<IReadOnlyList<Guid>>> GetRunningSceneIdsAsync(Guid clientId);

    /// <summary>Scene playback events for one kiosk, on the runner's thread. Dispose to stop.</summary>
    Task<Result<IDisposable>> SubscribeToScenesAsync(Guid clientId, DmxSceneEventHandlers handlers);
}

/// <summary>Callbacks for one kiosk's scene playback. Each receives the scene id.</summary>
public sealed record DmxSceneEventHandlers(
    Action<Guid> Started,
    Action<Guid> Stopped,
    Action<Guid, int> StepAdvanced,
    Action<Guid, string> Failed);
