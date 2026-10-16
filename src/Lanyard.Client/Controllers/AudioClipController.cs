using Lanyard.Shared.DTO;
using Lanyard.Shared.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace Lanyard.Client.Controllers;

/// <summary>
/// Receives audio clip commands from the server and reports each clip's progress back.
/// </summary>
public class AudioClipController
{
    private readonly IAudioClipPlayer _player;
    private readonly IAudioClipCacheService _cache;
    private readonly ILogger<AudioClipController> _logger;
    private HubConnection? _connection;

    public AudioClipController(IAudioClipPlayer player, IAudioClipCacheService cache, ILogger<AudioClipController> logger)
    {
        _player = player;
        _cache = cache;
        _logger = logger;

        _player.PlaybackChanged += OnPlaybackChanged;
    }

    public void Register(HubConnection connection)
    {
        _connection = connection;

        connection.On<AudioClipPlayCommand>(ClientHubMethods.PlayAudioClip, command =>
        {
            _logger.LogInformation("Received PLAYAUDIOCLIP command for clip {ClipId} (play {PlayId})", command.ClipId, command.PlayId);

            // Not awaited: the hub runs a connection's handlers one at a time, and a clip that is
            // still downloading mustn't hold up a Stop, or the music, behind it.
            _ = Task.Run(() => _player.PlayAsync(command));

            return Task.CompletedTask;
        });

        connection.On<List<AudioClipPlayCommand>>(ClientHubMethods.PrefetchAudioClips, commands =>
        {
            _logger.LogInformation("Received PREFETCHAUDIOCLIPS with {Count} scheduled clips", commands.Count);

            // In the background, for the same reason as PlayAudioClip.
            _ = Task.Run(async () =>
            {
                try
                {
                    await _cache.PrefetchAsync(commands, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to download scheduled clips ahead");
                }
            });

            return Task.CompletedTask;
        });

        connection.On<Guid>(ClientHubMethods.StopAudioClip, playId =>
        {
            _logger.LogInformation("Received STOPAUDIOCLIP command for play {PlayId}", playId);

            _player.Stop(playId);
        });
    }

    private async void OnPlaybackChanged(AudioClipPlaybackReport report)
    {
        HubConnection? connection = _connection;

        if (connection is null || connection.State != HubConnectionState.Connected)
        {
            _logger.LogWarning("Cannot report clip play {PlayId} as {Status} - connection not established", report.PlayId, report.Status);
            return;
        }

        try
        {
            await connection.InvokeAsync(ClientHubMethods.AudioClipPlaybackChanged, report);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to report clip play {PlayId} as {Status}", report.PlayId, report.Status);
        }
    }
}
