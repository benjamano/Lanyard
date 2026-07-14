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
    }
}
