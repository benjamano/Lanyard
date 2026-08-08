using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lanyard.Application.Services.Email;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Email
{
    [TestClass]
    public class EmailServiceTests
    {
        private class FakeHttpMessageHandler : HttpMessageHandler
        {
            private readonly HttpStatusCode _statusCode;
            private readonly string _body;

            public FakeHttpMessageHandler(HttpStatusCode statusCode, string body = "")
            {
                _statusCode = statusCode;
                _body = body;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(new HttpResponseMessage(_statusCode)
                {
                    Content = new StringContent(_body)
                });
            }
        }

        private static EmailService BuildService(HttpStatusCode statusCode, EmailOptions options)
        {
            HttpClient httpClient = new(new FakeHttpMessageHandler(statusCode))
            {
                BaseAddress = new Uri("https://api.resend.com/")
            };

            return new EmailService(httpClient, Options.Create(options), NullLogger<EmailService>.Instance);
        }

        private static EmailOptions ValidOptions()
        {
            return new EmailOptions
            {
                ResendApiKey = "test-key",
                FromAddress = "noreply@example.com",
                FromName = "Lanyard"
            };
        }

        [TestMethod]
        public async Task SendSetPasswordEmailAsync_SuccessResponse_ReturnsOk()
        {
            EmailService service = BuildService(HttpStatusCode.OK, ValidOptions());

            Result<bool> result = await service.SendSetPasswordEmailAsync(
                new UserProfile { UserName = "jdoe", Email = "jane@example.com" },
                "https://lanyard.example.com/set-password?userId=1&token=abc");

            Assert.IsTrue(result.IsSuccess, result.Error);
            Assert.IsTrue(result.Data);
        }

        [TestMethod]
        public async Task SendSetPasswordEmailAsync_NonSuccessStatusCode_ReturnsFail()
        {
            EmailService service = BuildService(HttpStatusCode.Unauthorized, ValidOptions());

            Result<bool> result = await service.SendSetPasswordEmailAsync(
                new UserProfile { UserName = "jdoe", Email = "jane@example.com" },
                "https://lanyard.example.com/set-password?userId=1&token=abc");

            Assert.IsFalse(result.IsSuccess);
            Assert.Contains("401", result.Error);
        }

        [TestMethod]
        public async Task SendSetPasswordEmailAsync_MissingApiKey_ReturnsFail()
        {
            EmailService service = BuildService(HttpStatusCode.OK, new EmailOptions
            {
                ResendApiKey = "",
                FromAddress = "noreply@example.com"
            });

            Result<bool> result = await service.SendSetPasswordEmailAsync(
                new UserProfile { UserName = "jdoe", Email = "jane@example.com" },
                "https://lanyard.example.com/set-password");

            Assert.IsFalse(result.IsSuccess);
            Assert.Contains("not configured", result.Error);
        }

        [TestMethod]
        public async Task SendSetPasswordEmailAsync_MissingFromAddress_ReturnsFail()
        {
            EmailService service = BuildService(HttpStatusCode.OK, new EmailOptions
            {
                ResendApiKey = "test-key",
                FromAddress = ""
            });

            Result<bool> result = await service.SendSetPasswordEmailAsync(
                new UserProfile { UserName = "jdoe", Email = "jane@example.com" },
                "https://lanyard.example.com/set-password");

            Assert.IsFalse(result.IsSuccess);
            Assert.Contains("not configured", result.Error);
        }

        [TestMethod]
        public async Task SendSetPasswordEmailAsync_UserHasNoEmail_ReturnsFail()
        {
            EmailService service = BuildService(HttpStatusCode.OK, ValidOptions());

            Result<bool> result = await service.SendSetPasswordEmailAsync(
                new UserProfile { UserName = "jdoe", Email = null },
                "https://lanyard.example.com/set-password");

            Assert.IsFalse(result.IsSuccess);
            Assert.Contains("no email address", result.Error);
        }
    }
}
