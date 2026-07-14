using System;
using System.Threading;
using System.Threading.Tasks;
using Lanyard.Application.Services;
using Lanyard.Application.Services.VideoStreaming;
using Lanyard.Infrastructure.DTO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.VideoStreaming
{
    [TestClass]
    public class VideoStreamSignalingServiceTests
    {
        private Mock<IClientService> _clientServiceMock = default!;
        private VideoStreamTokenService _tokenService = default!;
        private VideoStreamSignalingService _service = default!;

        [TestInitialize]
        public void Setup()
        {
            _clientServiceMock = new Mock<IClientService>();

            Mock<IServiceProvider> providerMock = new();
            providerMock.Setup(p => p.GetService(typeof(IClientService))).Returns(_clientServiceMock.Object);

            Mock<IServiceScope> scopeMock = new();
            scopeMock.Setup(s => s.ServiceProvider).Returns(providerMock.Object);

            Mock<IServiceScopeFactory> scopeFactoryMock = new();
            scopeFactoryMock.Setup(f => f.CreateScope()).Returns(scopeMock.Object);

            _tokenService = new VideoStreamTokenService();
            _service = new VideoStreamSignalingService(scopeFactoryMock.Object, _tokenService, Mock.Of<ILogger<VideoStreamSignalingService>>());
        }

        private static Mock<IVideoPublisherHandle> CreatePublisherMock(Guid clientId)
        {
            Mock<IVideoPublisherHandle> publisherMock = new();
            publisherMock.SetupGet(p => p.ClientId).Returns(clientId);
            return publisherMock;
        }

        private void RegisterPublisherWithToken(IVideoPublisherHandle publisher)
        {
            string token = _tokenService.IssuePublisherToken(publisher.ClientId);
            Assert.IsTrue(_service.RegisterPublisher(publisher, token));
        }

        private Task<Result<Guid>> CreateSessionAsync(Guid sourceClientId, Guid viewingClientId, IVideoViewerHandle viewer)
        {
            string viewerToken = _tokenService.IssueViewerToken(viewingClientId);
            return _service.CreateSessionAsync(sourceClientId, viewingClientId, viewerToken, "Webcam 1", false, 1920, 1080, viewer, CancellationToken.None);
        }

        [TestMethod]
        public async Task CreateSessionAsync_WithRegisteredPublisher_StartsSession()
        {
            Guid clientId = Guid.NewGuid();
            Guid viewingClientId = Guid.NewGuid();

            Mock<IVideoPublisherHandle> publisherMock = CreatePublisherMock(clientId);
            Mock<IVideoViewerHandle> viewerMock = new();

            RegisterPublisherWithToken(publisherMock.Object);

            string viewerToken = _tokenService.IssueViewerToken(viewingClientId);
            Result<Guid> result = await _service.CreateSessionAsync(clientId, viewingClientId, viewerToken, "Webcam 1", true, 1920, 1080, viewerMock.Object, CancellationToken.None);

            Assert.IsTrue(result.Success, result.Error);
            publisherMock.Verify(p => p.StartSessionAsync(result.Data, "Webcam 1", true, 1920, 1080), Times.Once);
            Assert.AreEqual(1, _service.GetActiveSessionCount(clientId));
            _clientServiceMock.Verify(c => c.StartVideoPublisherOnClientAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never,
                "No launch command should be sent when a publisher is already registered.");
        }

        [TestMethod]
        public async Task CreateSessionAsync_InvalidViewerToken_FailsWithoutLaunchingPublisher()
        {
            Guid clientId = Guid.NewGuid();
            Guid viewingClientId = Guid.NewGuid();

            Result<Guid> result = await _service.CreateSessionAsync(clientId, viewingClientId, "forged-token", "Webcam 1", false, 1920, 1080, new Mock<IVideoViewerHandle>().Object, CancellationToken.None);

            Assert.IsFalse(result.Success);
            _clientServiceMock.Verify(c => c.StartVideoPublisherOnClientAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never,
                "An unauthorised viewer must never cause a source camera to be activated.");
        }

        [TestMethod]
        public void RegisterPublisher_InvalidToken_IsRejected()
        {
            Guid clientId = Guid.NewGuid();

            Mock<IVideoPublisherHandle> publisherMock = CreatePublisherMock(clientId);

            bool registered = _service.RegisterPublisher(publisherMock.Object, "forged-token");

            Assert.IsFalse(registered);
        }

        [TestMethod]
        public async Task RegisterPublisher_TokenForDifferentClient_IsRejected()
        {
            Guid clientId = Guid.NewGuid();
            Guid otherClientId = Guid.NewGuid();

            Mock<IVideoPublisherHandle> publisherMock = CreatePublisherMock(clientId);

            // A token issued for another client must not authorise this publisher.
            string otherToken = _tokenService.IssuePublisherToken(otherClientId);

            bool registered = _service.RegisterPublisher(publisherMock.Object, otherToken);

            Assert.IsFalse(registered);
            Assert.AreEqual(0, _service.GetActiveSessionCount(clientId));
            await Task.CompletedTask;
        }

        [TestMethod]
        public async Task CreateSessionAsync_PublisherRegistersAfterStartCommand_CompletesSession()
        {
            Guid clientId = Guid.NewGuid();
            Guid viewingClientId = Guid.NewGuid();

            Mock<IVideoPublisherHandle> publisherMock = CreatePublisherMock(clientId);
            Mock<IVideoViewerHandle> viewerMock = new();

            // The service issues the publisher token and passes it to the client; the publisher
            // registers with exactly that token.
            _clientServiceMock
                .Setup(c => c.StartVideoPublisherOnClientAsync(clientId, It.IsAny<string>()))
                .Callback<Guid, string>((_, token) => _service.RegisterPublisher(publisherMock.Object, token))
                .ReturnsAsync(Result<bool>.Ok(true));

            Result<Guid> result = await CreateSessionAsync(clientId, viewingClientId, viewerMock.Object);

            Assert.IsTrue(result.Success, result.Error);
            publisherMock.Verify(p => p.StartSessionAsync(result.Data, "Webcam 1", false, 1920, 1080), Times.Once);
        }

        [TestMethod]
        public async Task CreateSessionAsync_OfflineClient_FailsFast()
        {
            Guid clientId = Guid.NewGuid();
            Guid viewingClientId = Guid.NewGuid();

            _clientServiceMock
                .Setup(c => c.StartVideoPublisherOnClientAsync(clientId, It.IsAny<string>()))
                .ReturnsAsync(Result<bool>.Fail("Client has no active connection."));

            Result<Guid> result = await CreateSessionAsync(clientId, viewingClientId, new Mock<IVideoViewerHandle>().Object);

            Assert.IsFalse(result.Success);
            Assert.AreEqual(0, _service.GetActiveSessionCount(clientId), "No session should exist after a failed launch request.");
        }

        [TestMethod]
        public async Task CreateSessionAsync_CancelledWhileWaiting_FailsWithoutLeakingSession()
        {
            Guid clientId = Guid.NewGuid();
            Guid viewingClientId = Guid.NewGuid();

            _clientServiceMock
                .Setup(c => c.StartVideoPublisherOnClientAsync(clientId, It.IsAny<string>()))
                .ReturnsAsync(Result<bool>.Ok(true));

            using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(100));

            string viewerToken = _tokenService.IssueViewerToken(viewingClientId);
            Result<Guid> result = await _service.CreateSessionAsync(clientId, viewingClientId, viewerToken, "Webcam 1", false, 1920, 1080, new Mock<IVideoViewerHandle>().Object, cts.Token);

            Assert.IsFalse(result.Success);
            Assert.AreEqual(0, _service.GetActiveSessionCount(clientId));
        }

        [TestMethod]
        public async Task CreateSessionAsync_PublisherThrowsOnStart_FailsAndDeregistersPublisher()
        {
            Guid clientId = Guid.NewGuid();
            Guid viewingClientId = Guid.NewGuid();

            Mock<IVideoPublisherHandle> publisherMock = CreatePublisherMock(clientId);
            publisherMock
                .Setup(p => p.StartSessionAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<int>()))
                .ThrowsAsync(new InvalidOperationException("Circuit gone"));

            RegisterPublisherWithToken(publisherMock.Object);

            Result<Guid> result = await CreateSessionAsync(clientId, viewingClientId, new Mock<IVideoViewerHandle>().Object);

            Assert.IsFalse(result.Success);
            Assert.AreEqual(0, _service.GetActiveSessionCount(clientId));
        }

        [TestMethod]
        public async Task SignalingForwards_OfferAnswerAndIce_ToTheCorrectPeer()
        {
            Guid clientId = Guid.NewGuid();
            Guid viewingClientId = Guid.NewGuid();

            Mock<IVideoPublisherHandle> publisherMock = CreatePublisherMock(clientId);
            Mock<IVideoViewerHandle> viewerMock = new();

            RegisterPublisherWithToken(publisherMock.Object);

            Result<Guid> sessionResult = await CreateSessionAsync(clientId, viewingClientId, viewerMock.Object);
            Guid sessionId = sessionResult.Data;

            await _service.PublisherSendOfferAsync(sessionId, "offer-sdp");
            viewerMock.Verify(v => v.ReceiveOfferAsync(sessionId, "offer-sdp"), Times.Once);

            await _service.ViewerSendAnswerAsync(sessionId, "answer-sdp");
            publisherMock.Verify(p => p.ReceiveAnswerAsync(sessionId, "answer-sdp"), Times.Once);

            await _service.PublisherSendIceCandidateAsync(sessionId, "pub-candidate");
            viewerMock.Verify(v => v.ReceiveIceCandidateAsync(sessionId, "pub-candidate"), Times.Once);

            await _service.ViewerSendIceCandidateAsync(sessionId, "viewer-candidate");
            publisherMock.Verify(p => p.ReceiveIceCandidateAsync(sessionId, "viewer-candidate"), Times.Once);
        }

        [TestMethod]
        public async Task EndSessionAsync_IsIdempotent_AndNotifiesPublisherOnce()
        {
            Guid clientId = Guid.NewGuid();
            Guid viewingClientId = Guid.NewGuid();

            Mock<IVideoPublisherHandle> publisherMock = CreatePublisherMock(clientId);

            RegisterPublisherWithToken(publisherMock.Object);

            Result<Guid> sessionResult = await CreateSessionAsync(clientId, viewingClientId, new Mock<IVideoViewerHandle>().Object);
            Guid sessionId = sessionResult.Data;

            await _service.EndSessionAsync(sessionId);
            await _service.EndSessionAsync(sessionId);

            publisherMock.Verify(p => p.EndSessionAsync(sessionId), Times.Once);
            Assert.AreEqual(0, _service.GetActiveSessionCount(clientId));
        }

        [TestMethod]
        public async Task UnregisterPublisher_NotifiesViewersAndClearsSessions()
        {
            Guid clientId = Guid.NewGuid();
            Guid viewingClientId = Guid.NewGuid();

            Mock<IVideoPublisherHandle> publisherMock = CreatePublisherMock(clientId);
            Mock<IVideoViewerHandle> viewerMock = new();

            RegisterPublisherWithToken(publisherMock.Object);

            Result<Guid> sessionResult = await CreateSessionAsync(clientId, viewingClientId, viewerMock.Object);
            Guid sessionId = sessionResult.Data;

            _service.UnregisterPublisher(publisherMock.Object);

            viewerMock.Verify(v => v.SessionEndedAsync(sessionId, "Video source disconnected."), Times.Once);
            Assert.AreEqual(0, _service.GetActiveSessionCount(clientId));
        }

        [TestMethod]
        public async Task UnregisterPublisher_StaleInstance_DoesNotEvictNewerRegistration()
        {
            Guid clientId = Guid.NewGuid();
            Guid viewingClientId = Guid.NewGuid();

            Mock<IVideoPublisherHandle> oldPublisherMock = CreatePublisherMock(clientId);
            Mock<IVideoPublisherHandle> newPublisherMock = CreatePublisherMock(clientId);

            RegisterPublisherWithToken(oldPublisherMock.Object);
            RegisterPublisherWithToken(newPublisherMock.Object);

            // The old page's circuit disposes after the relaunched page registered.
            _service.UnregisterPublisher(oldPublisherMock.Object);

            Result<Guid> result = await CreateSessionAsync(clientId, viewingClientId, new Mock<IVideoViewerHandle>().Object);

            Assert.IsTrue(result.Success, "The newer publisher registration should still serve sessions.");
            newPublisherMock.Verify(p => p.StartSessionAsync(It.IsAny<Guid>(), "Webcam 1", false, 1920, 1080), Times.Once);
        }

        [TestMethod]
        public async Task PublisherReportSessionErrorAsync_NotifiesViewerAndRemovesSession()
        {
            Guid clientId = Guid.NewGuid();
            Guid viewingClientId = Guid.NewGuid();

            Mock<IVideoPublisherHandle> publisherMock = CreatePublisherMock(clientId);
            Mock<IVideoViewerHandle> viewerMock = new();

            RegisterPublisherWithToken(publisherMock.Object);

            Result<Guid> sessionResult = await CreateSessionAsync(clientId, viewingClientId, viewerMock.Object);
            Guid sessionId = sessionResult.Data;

            await _service.PublisherReportSessionErrorAsync(sessionId, "Camera in use.");

            viewerMock.Verify(v => v.SessionEndedAsync(sessionId, "Camera in use."), Times.Once);
            Assert.AreEqual(0, _service.GetActiveSessionCount(clientId));
        }

        [TestMethod]
        public async Task ThrowingViewer_EndsItsSessionWithoutBreakingOthers()
        {
            Guid clientId = Guid.NewGuid();
            Guid viewingClientId = Guid.NewGuid();

            Mock<IVideoPublisherHandle> publisherMock = CreatePublisherMock(clientId);

            Mock<IVideoViewerHandle> throwingViewerMock = new();
            throwingViewerMock
                .Setup(v => v.ReceiveOfferAsync(It.IsAny<Guid>(), It.IsAny<string>()))
                .ThrowsAsync(new InvalidOperationException("Circuit gone"));

            Mock<IVideoViewerHandle> healthyViewerMock = new();

            RegisterPublisherWithToken(publisherMock.Object);

            Result<Guid> throwingSession = await CreateSessionAsync(clientId, viewingClientId, throwingViewerMock.Object);
            Result<Guid> healthySession = await CreateSessionAsync(clientId, viewingClientId, healthyViewerMock.Object);

            await _service.PublisherSendOfferAsync(throwingSession.Data, "offer-1");
            await _service.PublisherSendOfferAsync(healthySession.Data, "offer-2");

            healthyViewerMock.Verify(v => v.ReceiveOfferAsync(healthySession.Data, "offer-2"), Times.Once);
            Assert.AreEqual(1, _service.GetActiveSessionCount(clientId), "Only the healthy session should remain.");
        }
    }
}
