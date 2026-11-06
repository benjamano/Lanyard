using System.Collections.Concurrent;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models.Dmx;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services;

public class DmxDeskService(
    IDbContextFactory<ApplicationDbContext> factory,
    IDmxService dmxService,
    IDmxSceneRunnerService sceneRunner,
    ILogger<DmxDeskService> logger) : IDmxDeskService
{
    private const string KioskNotFound = "Kiosk not found.";

    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly IDmxService _dmxService = dmxService;
    private readonly IDmxSceneRunnerService _sceneRunner = sceneRunner;
    private readonly ILogger<DmxDeskService> _logger = logger;

    // Per scope (a circuit, for the desk). A kiosk or scene never changes company, so a "yes" can
    // be remembered; a "no" is asked again in case the kiosk was only just paired.
    private readonly ConcurrentDictionary<Guid, byte> _visibleClients = new();
    private readonly ConcurrentDictionary<Guid, Guid> _clientIdBySceneId = new();

    private async Task<bool> CanUseClientAsync(Guid clientId)
    {
        if (clientId == Guid.Empty)
        {
            return false;
        }

        if (_visibleClients.ContainsKey(clientId))
        {
            return true;
        }

        // Tenant-filtered factory: another company's kiosk doesn't exist as far as this query knows.
        await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

        bool visible = await context.Clients
            .AsNoTracking()
            .TagWithCallSite()
            .AnyAsync(c => c.Id == clientId);

        if (visible)
        {
            _visibleClients[clientId] = 0;
        }
        else
        {
            _logger.LogWarning("Refused DMX desk access to client {ClientId}: not visible to the caller", clientId);
        }

        return visible;
    }

    private async Task<bool> SceneBelongsToClientAsync(Guid clientId, Guid sceneId)
    {
        if (!await CanUseClientAsync(clientId))
        {
            return false;
        }

        if (_clientIdBySceneId.TryGetValue(sceneId, out Guid knownClientId))
        {
            return knownClientId == clientId;
        }

        await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

        Guid? sceneClientId = await context.DmxScenes
            .AsNoTracking()
            .TagWithCallSite()
            .Where(s => s.Id == sceneId)
            .Select(s => (Guid?)s.ClientId)
            .FirstOrDefaultAsync();

        if (sceneClientId == null)
        {
            return false;
        }

        _clientIdBySceneId[sceneId] = sceneClientId.Value;

        return sceneClientId == clientId;
    }

    public async Task<Result<IReadOnlyList<DmxChannel>>> GetChannelsAsync(Guid clientId)
    {
        try
        {
            if (!await CanUseClientAsync(clientId))
            {
                return Result<IReadOnlyList<DmxChannel>>.Fail(KioskNotFound);
            }

            Result<IEnumerable<DmxChannel>> result = await _dmxService.GetDmxChannelsAsync(clientId);

            return result.IsSuccess && result.Data != null
                ? Result<IReadOnlyList<DmxChannel>>.Ok(result.Data.ToList())
                : Result<IReadOnlyList<DmxChannel>>.Fail(result.Error ?? "Couldn't read the kiosk's channels.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading DMX channels for client {ClientId}", clientId);
            return Result<IReadOnlyList<DmxChannel>>.Fail("An error occurred while reading DMX channels.");
        }
    }

    public async Task<Result<bool>> SetChannelsAsync(Guid clientId, IReadOnlyList<DmxChannel> channels)
    {
        try
        {
            if (!await CanUseClientAsync(clientId))
            {
                return Result<bool>.Fail(KioskNotFound);
            }

            await _dmxService.UpdateChannelValuesAsync(clientId, channels);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting {ChannelCount} DMX channels for client {ClientId}", channels.Count, clientId);
            return Result<bool>.Fail("An error occurred while setting DMX channels.");
        }
    }

    public async Task<Result<DmxOutputState>> GetOutputStateAsync(Guid clientId)
    {
        try
        {
            if (!await CanUseClientAsync(clientId))
            {
                return Result<DmxOutputState>.Fail(KioskNotFound);
            }

            return Result<DmxOutputState>.Ok(await _dmxService.GetOutputStateAsync(clientId));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading DMX output state for client {ClientId}", clientId);
            return Result<DmxOutputState>.Fail("An error occurred while reading the master and blackout.");
        }
    }

    public async Task<Result<bool>> SetGrandMasterAsync(Guid clientId, byte level)
    {
        try
        {
            if (!await CanUseClientAsync(clientId))
            {
                return Result<bool>.Fail(KioskNotFound);
            }

            await _dmxService.SetGrandMasterAsync(clientId, level);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting the DMX grand master for client {ClientId}", clientId);
            return Result<bool>.Fail("An error occurred while setting the master.");
        }
    }

    public async Task<Result<bool>> SetBlackoutAsync(Guid clientId, bool isBlackout)
    {
        try
        {
            if (!await CanUseClientAsync(clientId))
            {
                return Result<bool>.Fail(KioskNotFound);
            }

            await _dmxService.SetBlackoutAsync(clientId, isBlackout);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting DMX blackout for client {ClientId}", clientId);
            return Result<bool>.Fail("An error occurred while setting blackout.");
        }
    }

    public async Task<Result<IDisposable>> SubscribeToChannelsAsync(Guid clientId, Action<IReadOnlyList<DmxChannel>> handler, Action<DmxOutputState>? onOutputChanged = null)
    {
        try
        {
            if (!await CanUseClientAsync(clientId))
            {
                return Result<IDisposable>.Fail(KioskNotFound);
            }

            return Result<IDisposable>.Ok(_dmxService.SubscribeToClient(clientId, handler, onOutputChanged));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error subscribing to DMX channels for client {ClientId}", clientId);
            return Result<IDisposable>.Fail("An error occurred while subscribing to DMX channels.");
        }
    }

    public async Task<Result<bool>> StartSceneAsync(Guid clientId, Guid sceneId, TimeSpan? holdFor = null)
    {
        try
        {
            if (!await SceneBelongsToClientAsync(clientId, sceneId))
            {
                return Result<bool>.Fail("Scene not found.");
            }

            return await _sceneRunner.StartSceneAsync(clientId, sceneId, holdFor);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting DMX scene {SceneId} for client {ClientId}", sceneId, clientId);
            return Result<bool>.Fail("An error occurred while starting the DMX scene.");
        }
    }

    public async Task<Result<bool>> StopSceneAsync(Guid clientId, Guid sceneId)
    {
        try
        {
            if (!await SceneBelongsToClientAsync(clientId, sceneId))
            {
                return Result<bool>.Fail("Scene not found.");
            }

            return _sceneRunner.StopScene(sceneId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping DMX scene {SceneId} for client {ClientId}", sceneId, clientId);
            return Result<bool>.Fail("An error occurred while stopping the DMX scene.");
        }
    }

    public async Task<Result<bool>> StopAllScenesAsync(Guid clientId)
    {
        try
        {
            if (!await CanUseClientAsync(clientId))
            {
                return Result<bool>.Fail(KioskNotFound);
            }

            return _sceneRunner.StopAllScenesForClient(clientId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping all DMX scenes for client {ClientId}", clientId);
            return Result<bool>.Fail("An error occurred while stopping DMX scenes.");
        }
    }

    public async Task<Result<IReadOnlyList<Guid>>> GetRunningSceneIdsAsync(Guid clientId)
    {
        try
        {
            if (!await CanUseClientAsync(clientId))
            {
                return Result<IReadOnlyList<Guid>>.Fail(KioskNotFound);
            }

            return Result<IReadOnlyList<Guid>>.Ok(_sceneRunner.GetRunningSceneIds(clientId));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading running DMX scenes for client {ClientId}", clientId);
            return Result<IReadOnlyList<Guid>>.Fail("An error occurred while reading running DMX scenes.");
        }
    }

    public async Task<Result<IDisposable>> SubscribeToScenesAsync(Guid clientId, DmxSceneEventHandlers handlers)
    {
        try
        {
            if (!await CanUseClientAsync(clientId))
            {
                return Result<IDisposable>.Fail(KioskNotFound);
            }

            Action<Guid, Guid> started = (c, s) => { if (c == clientId) handlers.Started(s); };
            Action<Guid, Guid> stopped = (c, s) => { if (c == clientId) handlers.Stopped(s); };
            Action<Guid, Guid, int> step = (c, s, n) => { if (c == clientId) handlers.StepAdvanced(s, n); };
            Action<Guid, Guid, string> failed = (c, s, reason) => { if (c == clientId) handlers.Failed(s, reason); };

            _sceneRunner.OnSceneStarted += started;
            _sceneRunner.OnSceneStopped += stopped;
            _sceneRunner.OnSceneStepAdvanced += step;
            _sceneRunner.OnSceneFailed += failed;

            return Result<IDisposable>.Ok(new Unsubscriber(() =>
            {
                _sceneRunner.OnSceneStarted -= started;
                _sceneRunner.OnSceneStopped -= stopped;
                _sceneRunner.OnSceneStepAdvanced -= step;
                _sceneRunner.OnSceneFailed -= failed;
            }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error subscribing to DMX scenes for client {ClientId}", clientId);
            return Result<IDisposable>.Fail("An error occurred while subscribing to DMX scenes.");
        }
    }

    private sealed class Unsubscriber(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;

        public void Dispose() => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
    }
}
