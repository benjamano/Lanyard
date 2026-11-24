using System;
using Lanyard.Application.Services.VideoStreaming;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.VideoStreaming
{
    [TestClass]
    public class VideoStreamTokenServiceTests
    {
        [TestMethod]
        public void PublisherToken_ValidatesForIssuingClientOnly()
        {
            VideoStreamTokenService service = new();
            Guid clientId = Guid.NewGuid();

            string token = service.IssuePublisherToken(clientId);

            Assert.IsTrue(service.ValidatePublisherToken(clientId, token));
            Assert.IsFalse(service.ValidatePublisherToken(Guid.NewGuid(), token), "A token must not validate for a different client.");
        }

        [TestMethod]
        public void ViewerToken_ValidatesForIssuingClientOnly()
        {
            VideoStreamTokenService service = new();
            Guid clientId = Guid.NewGuid();

            string token = service.IssueViewerToken(clientId);

            Assert.IsTrue(service.ValidateViewerToken(clientId, token));
            Assert.IsFalse(service.ValidateViewerToken(Guid.NewGuid(), token));
        }

        [TestMethod]
        public void PublisherAndViewerTokens_AreNotInterchangeable()
        {
            VideoStreamTokenService service = new();
            Guid clientId = Guid.NewGuid();

            string publisherToken = service.IssuePublisherToken(clientId);
            string viewerToken = service.IssueViewerToken(clientId);

            Assert.IsFalse(service.ValidateViewerToken(clientId, publisherToken), "A publisher token must not authorise a viewer.");
            Assert.IsFalse(service.ValidatePublisherToken(clientId, viewerToken), "A viewer token must not authorise a publisher.");
        }

        [TestMethod]
        public void Validate_RejectsNullEmptyAndUnknownTokens()
        {
            VideoStreamTokenService service = new();
            Guid clientId = Guid.NewGuid();

            Assert.IsFalse(service.ValidatePublisherToken(clientId, null));
            Assert.IsFalse(service.ValidatePublisherToken(clientId, string.Empty));
            Assert.IsFalse(service.ValidatePublisherToken(clientId, "never-issued"));
            Assert.IsFalse(service.ValidateViewerToken(clientId, null));
            Assert.IsFalse(service.ValidateViewerToken(clientId, "never-issued"));
        }

        [TestMethod]
        public void IssuedTokens_AreUnique()
        {
            VideoStreamTokenService service = new();
            Guid clientId = Guid.NewGuid();

            string first = service.IssuePublisherToken(clientId);
            string second = service.IssuePublisherToken(clientId);

            Assert.AreNotEqual(first, second);
            Assert.IsTrue(service.ValidatePublisherToken(clientId, first), "Issuing a second token must not invalidate the first.");
            Assert.IsTrue(service.ValidatePublisherToken(clientId, second));
        }
        // A kiosk window keeps its token in its URL across a server restart; the new server must
        // take it back (same Data Protection keys), for that kiosk only.
        [TestMethod]
        public void ViewerToken_StillWorksAfterARestart_ForThatKioskOnly()
        {
            Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider keys = new();
            Lanyard.Tests.Services.Scheduling.TestClock clock = new(DateTime.UtcNow);
            Guid clientId = Guid.NewGuid();

            string token = new VideoStreamTokenService(keys, clock).IssueViewerToken(clientId);
            VideoStreamTokenService restarted = new(keys, clock);

            Assert.IsFalse(restarted.ValidateViewerToken(Guid.NewGuid(), token), "Another kiosk can't use it.");
            Assert.IsTrue(restarted.ValidateViewerToken(clientId, token));
            Assert.IsTrue(restarted.ValidateViewerToken(clientId, token), "And it slides as before.");
            Assert.IsFalse(restarted.ValidatePublisherToken(clientId, token));
            Assert.IsFalse(restarted.ValidateViewerToken(clientId, token[..^2] + "xx"), "A tampered token is refused.");
            Assert.IsFalse(new VideoStreamTokenService().ValidateViewerToken(clientId, token), "Other keys don't accept it.");
        }

        [TestMethod]
        public void ViewerToken_TooOldToComeBackAfterARestart()
        {
            Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider keys = new();
            Lanyard.Tests.Services.Scheduling.TestClock clock = new(DateTime.UtcNow);
            Guid clientId = Guid.NewGuid();

            string token = new VideoStreamTokenService(keys, clock).IssueViewerToken(clientId);
            clock.Advance(VideoStreamTokenService.MaxViewerTokenAge + TimeSpan.FromDays(1));

            Assert.IsFalse(new VideoStreamTokenService(keys, clock).ValidateViewerToken(clientId, token));
        }

        [TestMethod]
        public void ViewerToken_LeftIdlePastItsLifetime_DoesNotComeBack()
        {
            Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider keys = new();
            Lanyard.Tests.Services.Scheduling.TestClock clock = new(DateTime.UtcNow);
            VideoStreamTokenService service = new(keys, clock);
            Guid clientId = Guid.NewGuid();

            string token = service.IssueViewerToken(clientId);
            clock.Advance(TimeSpan.FromHours(13));

            Assert.IsFalse(service.ValidateViewerToken(clientId, token), "Idle for 13 hours on the same server: lapsed, signed or not.");
            Assert.IsFalse(service.ValidateViewerToken(clientId, token));
        }
    }
}
