using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Shared.DTO;

namespace Lanyard.Application.Services.AudioClips;

/// <param name="CompanyId">The caller's company: the clip and every kiosk must belong to it.</param>
/// <param name="TriggeredByUserId">The person who pressed Play, or null for a schedule or rule.</param>
public sealed record AudioClipPlayRequest(
    Guid ClipId,
    IReadOnlyCollection<Guid> ClientIds,
    int CompanyId,
    AudioClipPlaySource Source,
    string? TriggeredByUserId);

/// <summary>What happened on one kiosk when a clip was played. PlayId is null if nothing was recorded.</summary>
public sealed record AudioClipKioskOutcome(Guid ClientId, string ClientName, AudioClipPlayStatus Status, string? Error, Guid? PlayId);

/// <summary>The clip a kiosk is playing (or getting ready to), as far as the server knows.</summary>
public sealed record AudioClipNowPlaying(Guid PlayId, Guid ClipId, string ClipName, AudioClipPlayStatus Status, DateTime StartedUtc);

/// <summary>
/// Sends audio clips to kiosks and keeps track of them. The kiosk ducks its own music, so this
/// never touches the music player's volume and never counts as a change by hand to the music
/// schedule. A singleton: "now playing" lives here.
/// </summary>
public interface IAudioClipPlaybackService
{
    /// <summary>Raised with a kiosk's id whenever what it's playing changes.</summary>
    event Action<Guid>? OnClipPlaybackChanged;

    Task<Result<List<AudioClipKioskOutcome>>> PlayAsync(AudioClipPlayRequest request, CancellationToken ct = default);

    /// <summary>Stops whatever clip is playing on the kiosk.</summary>
    Task<Result<bool>> StopAsync(Guid clientId, int companyId);

    /// <summary>
    /// Records a kiosk's report about one of its plays. A report for a play that wasn't sent to
    /// that kiosk is ignored, since any kiosk can claim to be any other (Security Issue 9.1).
    /// </summary>
    Task<Result<bool>> ReportFromClientAsync(Guid clientId, AudioClipPlaybackReport report);

    AudioClipNowPlaying? GetNowPlaying(Guid clientId);
}
