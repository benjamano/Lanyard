using Lanyard.Infrastructure.DTO;

namespace Lanyard.Application.Services.VideoStreaming;

/// <summary>
/// Implemented by the publisher Blazor page's circuit on the video source machine.
/// All methods may be invoked from other circuits' threads; implementations must
/// marshal onto their own circuit via ComponentBase.InvokeAsync.
/// </summary>
public interface IVideoPublisherHandle
{
    Guid ClientId { get; }
    Task StartSessionAsync(Guid sessionId, string deviceName, bool enableAudio, int idealWidth, int idealHeight);
    Task ReceiveAnswerAsync(Guid sessionId, string sdpAnswer);
    Task ReceiveIceCandidateAsync(Guid sessionId, string candidateJson);
    Task EndSessionAsync(Guid sessionId);
}

/// <summary>
/// Implemented by the kiosk viewer component's circuit. Same threading rules as
/// <see cref="IVideoPublisherHandle"/>.
/// </summary>
public interface IVideoViewerHandle
{
    Task ReceiveOfferAsync(Guid sessionId, string sdpOffer);
    Task ReceiveIceCandidateAsync(Guid sessionId, string candidateJson);
    Task SessionEndedAsync(Guid sessionId, string? error);
}

/// <summary>
/// Server-side relay for WebRTC signaling between a publisher page (video source client)
/// and kiosk viewer components, each connected via their own Blazor circuit.
/// </summary>
public interface IVideoStreamSignalingService
{
    // Publisher circuit
    void RegisterPublisher(IVideoPublisherHandle publisher);
    void UnregisterPublisher(IVideoPublisherHandle publisher);
    Task PublisherSendOfferAsync(Guid sessionId, string sdpOffer);
    Task PublisherSendIceCandidateAsync(Guid sessionId, string candidateJson);
    Task PublisherReportSessionErrorAsync(Guid sessionId, string error);
    int GetActiveSessionCount(Guid clientId);

    // Viewer circuit
    Task<Result<Guid>> CreateSessionAsync(Guid sourceClientId, string deviceName, bool enableAudio,
        int idealWidth, int idealHeight, IVideoViewerHandle viewer, CancellationToken cancellationToken);
    Task ViewerSendAnswerAsync(Guid sessionId, string sdpAnswer);
    Task ViewerSendIceCandidateAsync(Guid sessionId, string candidateJson);
    Task EndSessionAsync(Guid sessionId);
}
