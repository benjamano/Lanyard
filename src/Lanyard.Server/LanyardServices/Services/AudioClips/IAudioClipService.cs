using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Speech;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Http;

namespace Lanyard.Application.Services.AudioClips;

/// <summary>Who may do what with audio clips; also used by the page's [Authorize] attribute.</summary>
public static class AudioClipRoles
{
    public const string CanPlayAudioClips = "CanPlayAudioClips";

    // Anyone who may play a clip; managing clips is Admin, or a Manager for their own location.
    public const string Play = "Admin,Manager," + CanPlayAudioClips;
}

/// <summary>What a person can set on a clip, on upload or edit.</summary>
/// <param name="LocationId">Null plays at every location (Admin only); otherwise only there.</param>
/// <param name="DuckToPercent">Null takes the company default (AudioClipDefaults).</param>
public sealed record AudioClipDetails(
    string Name,
    int? LocationId,
    int Volume = 100,
    int? DuckToPercent = null,
    int? FadeDownMs = null,
    int? FadeUpMs = null);

/// <summary>What a speech clip says, and the voice it says it in.</summary>
public sealed record AudioClipSpeech(string Text, SpeechVoiceSettings Voice);

/// <summary>A kiosk a clip could be played on, for the Play dialog.</summary>
public sealed record AudioClipKiosk(Guid Id, string Name, int? LocationId, string? LocationName, bool IsOnline, bool CanPlayClips, string? Version);

/// <summary>An audio file already in the file manager that could become a clip.</summary>
public sealed record AudioClipFileOption(Guid Id, string FileName);

/// <summary>One line of the recent plays list.</summary>
public sealed record AudioClipPlaySummary(
    Guid Id,
    string ClipName,
    string KioskName,
    Infrastructure.Enum.AudioClipPlaySource Source,
    Infrastructure.Enum.AudioClipPlayStatus Status,
    string? Error,
    string? TriggeredBy,
    DateTime RequestedAtUtc);

/// <summary>
/// The audio clip library: upload, pick, edit and delete clips, and play or stop them on kiosks.
/// Every call takes the caller's LocationScope. An Admin can do anything in their company; a
/// Manager manages and plays clips for their own location (and plays company-wide ones there);
/// someone holding only CanPlayAudioClips plays clips at their own location.
/// </summary>
public interface IAudioClipService
{
    Task<Result<List<AudioClip>>> ListAsync(LocationScope scope);

    Task<Result<AudioClip>> UploadAsync(LocationScope scope, IFormFile file, AudioClipDetails details, string userId, CancellationToken ct = default);

    Task<Result<AudioClip>> CreateFromFileAsync(LocationScope scope, Guid fileId, AudioClipDetails details, string userId, CancellationToken ct = default);

    Task<Result<AudioClip>> UpdateAsync(LocationScope scope, Guid clipId, AudioClipDetails details);

    /// <summary>
    /// Adds a clip that speaks typed text. The speech is rendered now and stored like an uploaded
    /// file, so it plays, schedules and automates exactly like any other clip.
    /// </summary>
    Task<Result<AudioClip>> CreateSpeechAsync(LocationScope scope, AudioClipSpeech speech, AudioClipDetails details, string userId, CancellationToken ct = default);

    /// <summary>
    /// Saves a speech clip's details and words. When the words or the voice changed it's rendered
    /// again; the new audio has a new file, so kiosks fetch it fresh, and the old one is deleted.
    /// </summary>
    Task<Result<AudioClip>> UpdateSpeechAsync(LocationScope scope, Guid clipId, AudioClipSpeech speech, AudioClipDetails details, string userId, CancellationToken ct = default);

    /// <summary>
    /// Renders the speech for listening to before saving. Returns the id to fetch it with from
    /// /api/speech/preview/{id}, which only this user can do, for ten minutes.
    /// </summary>
    Task<Result<Guid>> PreviewSpeechAsync(LocationScope scope, AudioClipSpeech speech, string userId, CancellationToken ct = default);

    IReadOnlyList<SpeechVoiceOption> ListVoices();

    Task<Result<bool>> DeleteAsync(LocationScope scope, Guid clipId);

    Task<Result<List<AudioClipFileOption>>> ListAudioFilesAsync(LocationScope scope);

    Task<Result<List<AudioClipKiosk>>> ListKiosksAsync(LocationScope scope);

    Task<Result<List<AudioClipKioskOutcome>>> PlayAsync(LocationScope scope, Guid clipId, IReadOnlyCollection<Guid> clientIds, string userId);

    Task<Result<bool>> StopAsync(LocationScope scope, Guid clientId);

    Task<Result<List<AudioClipPlaySummary>>> ListRecentPlaysAsync(LocationScope scope, int take = 20);

    bool CanManage(LocationScope scope, int? locationId);
}
