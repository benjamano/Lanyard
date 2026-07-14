using Lanyard.Infrastructure.DTO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace Lanyard.Application.Services.VideoStreaming;

public class VideoStreamSignalingService(IServiceScopeFactory scopeFactory, IVideoStreamTokenService tokenService, ILogger<VideoStreamSignalingService> logger) : IVideoStreamSignalingService
{
    private static readonly TimeSpan PublisherStartTimeout = TimeSpan.FromSeconds(15);

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly IVideoStreamTokenService _tokenService = tokenService;
    private readonly ILogger<VideoStreamSignalingService> _logger = logger;

    private readonly ConcurrentDictionary<Guid, IVideoPublisherHandle> _publishers = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<IVideoPublisherHandle>> _pendingPublishers = new();
    private readonly ConcurrentDictionary<Guid, SessionState> _sessions = new();

    private sealed class SessionState
    {
        public required Guid SessionId { get; init; }
        public required Guid SourceClientId { get; init; }
        public required IVideoViewerHandle Viewer { get; init; }
        public required IVideoPublisherHandle Publisher { get; init; }
    }

    public bool RegisterPublisher(IVideoPublisherHandle publisher, string? publisherToken)
    {
        // Only a page launched off a server StartVideoPublisher command holds a valid token,
        // so an attacker opening /video-publisher/{clientId} directly cannot seize the source.
        if (!_tokenService.ValidatePublisherToken(publisher.ClientId, publisherToken))
        {
            _logger.LogWarning("Rejected video publisher registration for client {ClientId}: invalid or missing token.", publisher.ClientId);
            return false;
        }

        _publishers[publisher.ClientId] = publisher;

        _logger.LogInformation("Video publisher registered for client {ClientId}", publisher.ClientId);

        if (_pendingPublishers.TryRemove(publisher.ClientId, out TaskCompletionSource<IVideoPublisherHandle>? pending))
        {
            pending.TrySetResult(publisher);
        }

        return true;
    }

    public void UnregisterPublisher(IVideoPublisherHandle publisher)
    {
        // Only deregister if this exact instance is still current; a relaunched publisher
        // page must not be evicted by the old circuit's dispose.
        if (_publishers.TryGetValue(publisher.ClientId, out IVideoPublisherHandle? current) && ReferenceEquals(current, publisher))
        {
            _publishers.TryRemove(publisher.ClientId, out _);
        }

        _logger.LogInformation("Video publisher unregistered for client {ClientId}", publisher.ClientId);

        foreach (SessionState session in _sessions.Values.Where(x => ReferenceEquals(x.Publisher, publisher)).ToList())
        {
            if (_sessions.TryRemove(session.SessionId, out _))
            {
                _ = NotifyViewerSessionEndedAsync(session, "Video source disconnected.");
            }
        }
    }

    public async Task<Result<Guid>> CreateSessionAsync(Guid sourceClientId, Guid viewingClientId, string? viewerToken,
        string deviceName, bool enableAudio, int idealWidth, int idealHeight, IVideoViewerHandle viewer,
        CancellationToken cancellationToken)
    {
        try
        {
            // The viewer token proves this request came from a kiosk a client actually launched,
            // not from someone who simply opened the (unguessable) kiosk URL in a browser.
            if (!_tokenService.ValidateViewerToken(viewingClientId, viewerToken))
            {
                _logger.LogWarning("Rejected video session request from client {ViewingClientId}: invalid or missing viewer token.", viewingClientId);
                return Result<Guid>.Fail("This kiosk is not authorised to open a video stream.");
            }

            if (!_publishers.TryGetValue(sourceClientId, out IVideoPublisherHandle? publisher))
            {
                publisher = await WaitForPublisherAsync(sourceClientId, cancellationToken);
            }

            if (publisher == null)
            {
                return Result<Guid>.Fail("Video publisher did not start on the source client.");
            }

            Guid sessionId = Guid.NewGuid();

            SessionState session = new()
            {
                SessionId = sessionId,
                SourceClientId = sourceClientId,
                Viewer = viewer,
                Publisher = publisher
            };

            _sessions[sessionId] = session;

            try
            {
                await publisher.StartSessionAsync(sessionId, deviceName, enableAudio, idealWidth, idealHeight);
            }
            catch (Exception ex)
            {
                _sessions.TryRemove(sessionId, out _);
                UnregisterPublisher(publisher);

                _logger.LogWarning(ex, "Video publisher for client {ClientId} failed to start a session; deregistered.", sourceClientId);

                return Result<Guid>.Fail("The video publisher is no longer reachable.");
            }

            _logger.LogInformation("Video session {SessionId} created: client {ClientId} device '{DeviceName}'", sessionId, sourceClientId, deviceName);

            return Result<Guid>.Ok(sessionId);
        }
        catch (OperationCanceledException)
        {
            return Result<Guid>.Fail("The video session request was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating video session for client {ClientId}", sourceClientId);
            return Result<Guid>.Fail(ex.Message);
        }
    }

    private async Task<IVideoPublisherHandle?> WaitForPublisherAsync(Guid sourceClientId, CancellationToken cancellationToken)
    {
        TaskCompletionSource<IVideoPublisherHandle> pending = _pendingPublishers.GetOrAdd(sourceClientId,
            _ => new TaskCompletionSource<IVideoPublisherHandle>(TaskCreationOptions.RunContinuationsAsynchronously));

        // A publisher may have registered between the dictionary check and the TCS creation.
        if (_publishers.TryGetValue(sourceClientId, out IVideoPublisherHandle? racedPublisher))
        {
            _pendingPublishers.TryRemove(sourceClientId, out _);
            return racedPublisher;
        }

        // The publisher page presents this token to RegisterPublisher; only a client that
        // received the command over its hub connection can produce it.
        string publisherToken = _tokenService.IssuePublisherToken(sourceClientId);

        await using (AsyncServiceScope scope = _scopeFactory.CreateAsyncScope())
        {
            IClientService clientService = scope.ServiceProvider.GetRequiredService<IClientService>();

            Result<bool> startResult = await clientService.StartVideoPublisherOnClientAsync(sourceClientId, publisherToken);

            if (!startResult.IsSuccess)
            {
                _logger.LogWarning("Could not request video publisher on client {ClientId}: {Error}", sourceClientId, startResult.Error);
                return null;
            }
        }

        try
        {
            return await pending.Task.WaitAsync(PublisherStartTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            _pendingPublishers.TryRemove(sourceClientId, out _);
            _logger.LogWarning("Timed out waiting for video publisher on client {ClientId}", sourceClientId);
            return null;
        }
    }

    public async Task PublisherSendOfferAsync(Guid sessionId, string sdpOffer)
    {
        if (!_sessions.TryGetValue(sessionId, out SessionState? session))
        {
            return;
        }

        try
        {
            await session.Viewer.ReceiveOfferAsync(sessionId, sdpOffer);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Viewer for video session {SessionId} is unreachable; ending session.", sessionId);
            await EndSessionAsync(sessionId);
        }
    }

    public async Task PublisherSendIceCandidateAsync(Guid sessionId, string candidateJson)
    {
        if (!_sessions.TryGetValue(sessionId, out SessionState? session))
        {
            return;
        }

        try
        {
            await session.Viewer.ReceiveIceCandidateAsync(sessionId, candidateJson);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Viewer for video session {SessionId} is unreachable; ending session.", sessionId);
            await EndSessionAsync(sessionId);
        }
    }

    public async Task PublisherReportSessionErrorAsync(Guid sessionId, string error)
    {
        if (!_sessions.TryRemove(sessionId, out SessionState? session))
        {
            return;
        }

        _logger.LogWarning("Video session {SessionId} failed on the publisher: {Error}", sessionId, error);

        await NotifyViewerSessionEndedAsync(session, error);
    }

    public int GetActiveSessionCount(Guid clientId)
    {
        return _sessions.Values.Count(x => x.SourceClientId == clientId);
    }

    public async Task ViewerSendAnswerAsync(Guid sessionId, string sdpAnswer)
    {
        if (!_sessions.TryGetValue(sessionId, out SessionState? session))
        {
            return;
        }

        try
        {
            await session.Publisher.ReceiveAnswerAsync(sessionId, sdpAnswer);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Publisher for video session {SessionId} is unreachable; ending session.", sessionId);
            await FailSessionAsync(sessionId, "The video publisher is no longer reachable.");
        }
    }

    public async Task ViewerSendIceCandidateAsync(Guid sessionId, string candidateJson)
    {
        if (!_sessions.TryGetValue(sessionId, out SessionState? session))
        {
            return;
        }

        try
        {
            await session.Publisher.ReceiveIceCandidateAsync(sessionId, candidateJson);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Publisher for video session {SessionId} is unreachable; ending session.", sessionId);
            await FailSessionAsync(sessionId, "The video publisher is no longer reachable.");
        }
    }

    public async Task EndSessionAsync(Guid sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out SessionState? session))
        {
            return;
        }

        _logger.LogInformation("Video session {SessionId} ended", sessionId);

        try
        {
            await session.Publisher.EndSessionAsync(sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Publisher for video session {SessionId} could not be notified of session end.", sessionId);
        }
    }

    private async Task FailSessionAsync(Guid sessionId, string error)
    {
        if (!_sessions.TryRemove(sessionId, out SessionState? session))
        {
            return;
        }

        await NotifyViewerSessionEndedAsync(session, error);
    }

    private async Task NotifyViewerSessionEndedAsync(SessionState session, string? error)
    {
        try
        {
            await session.Viewer.SessionEndedAsync(session.SessionId, error);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Viewer for video session {SessionId} could not be notified of session end.", session.SessionId);
        }
    }
}
