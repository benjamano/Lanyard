using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lanyard.Application.Services.Email;
using Lanyard.Infrastructure.Branding;
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

            public string? LastRequestBody { get; private set; }

            public FakeHttpMessageHandler(HttpStatusCode statusCode, string body = "")
            {
                _statusCode = statusCode;
                _body = body;
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                LastRequestBody = request.Content is not null ? await request.Content.ReadAsStringAsync(cancellationToken) : null;

                return new HttpResponseMessage(_statusCode)
                {
                    Content = new StringContent(_body)
                };
            }
        }

        private static (EmailService service, FakeHttpMessageHandler handler) BuildService(HttpStatusCode statusCode, EmailOptions options)
        {
            FakeHttpMessageHandler handler = new(statusCode);
            HttpClient httpClient = new(handler)
            {
                BaseAddress = new Uri("https://api.resend.com/")
            };

            return (new EmailService(httpClient, Options.Create(options), NullLogger<EmailService>.Instance), handler);
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
            (EmailService service, _) = BuildService(HttpStatusCode.OK, ValidOptions());

            Result<bool> result = await service.SendSetPasswordEmailAsync(
                new UserProfile { UserName = "jdoe", Email = "jane@example.com" },
                "https://lanyard.example.com/set-password?userId=1&token=abc",
                logoUrl: null,
                accentColorHex: BrandConstants.PrimaryColorHex,
                locationName: null);

            Assert.IsTrue(result.IsSuccess, result.Error);
            Assert.IsTrue(result.Data);
        }

        [TestMethod]
        public async Task SendSetPasswordEmailAsync_NonSuccessStatusCode_ReturnsFail()
        {
            (EmailService service, _) = BuildService(HttpStatusCode.Unauthorized, ValidOptions());

            Result<bool> result = await service.SendSetPasswordEmailAsync(
                new UserProfile { UserName = "jdoe", Email = "jane@example.com" },
                "https://lanyard.example.com/set-password?userId=1&token=abc",
                logoUrl: null,
                accentColorHex: BrandConstants.PrimaryColorHex,
                locationName: null);

            Assert.IsFalse(result.IsSuccess);
            Assert.Contains("401", result.Error);
        }

        [TestMethod]
        public async Task SendSetPasswordEmailAsync_MissingApiKey_ReturnsFail()
        {
            (EmailService service, _) = BuildService(HttpStatusCode.OK, new EmailOptions
            {
                ResendApiKey = "",
                FromAddress = "noreply@example.com"
            });

            Result<bool> result = await service.SendSetPasswordEmailAsync(
                new UserProfile { UserName = "jdoe", Email = "jane@example.com" },
                "https://lanyard.example.com/set-password",
                logoUrl: null,
                accentColorHex: BrandConstants.PrimaryColorHex,
                locationName: null);

            Assert.IsFalse(result.IsSuccess);
            Assert.Contains("not configured", result.Error);
        }

        [TestMethod]
        public async Task SendSetPasswordEmailAsync_MissingFromAddress_ReturnsFail()
        {
            (EmailService service, _) = BuildService(HttpStatusCode.OK, new EmailOptions
            {
                ResendApiKey = "test-key",
                FromAddress = ""
            });

            Result<bool> result = await service.SendSetPasswordEmailAsync(
                new UserProfile { UserName = "jdoe", Email = "jane@example.com" },
                "https://lanyard.example.com/set-password",
                logoUrl: null,
                accentColorHex: BrandConstants.PrimaryColorHex,
                locationName: null);

            Assert.IsFalse(result.IsSuccess);
            Assert.Contains("not configured", result.Error);
        }

        [TestMethod]
        public async Task SendSetPasswordEmailAsync_UserHasNoEmail_ReturnsFail()
        {
            (EmailService service, _) = BuildService(HttpStatusCode.OK, ValidOptions());

            Result<bool> result = await service.SendSetPasswordEmailAsync(
                new UserProfile { UserName = "jdoe", Email = null },
                "https://lanyard.example.com/set-password",
                logoUrl: null,
                accentColorHex: BrandConstants.PrimaryColorHex,
                locationName: null);

            Assert.IsFalse(result.IsSuccess);
            Assert.Contains("no email address", result.Error);
        }

        [TestMethod]
        public async Task SendSetPasswordEmailAsync_WithLogoAndColor_IncludesBothInHtmlBody()
        {
            (EmailService service, FakeHttpMessageHandler handler) = BuildService(HttpStatusCode.OK, ValidOptions());

            Result<bool> result = await service.SendSetPasswordEmailAsync(
                new UserProfile { UserName = "jdoe", Email = "jane@example.com" },
                "https://lanyard.example.com/set-password?userId=1&token=abc",
                logoUrl: "https://lanyard.example.com/api/companies/1/logo",
                accentColorHex: "#C8102E",
                locationName: null);

            Assert.IsTrue(result.IsSuccess, result.Error);
            Assert.Contains("https://lanyard.example.com/api/companies/1/logo", handler.LastRequestBody);
            Assert.Contains("#C8102E", handler.LastRequestBody);
            Assert.Contains("Lanyard", handler.LastRequestBody);
        }

        [TestMethod]
        public async Task SendSetPasswordEmailAsync_NoLogo_OmitsImageTag()
        {
            (EmailService service, FakeHttpMessageHandler handler) = BuildService(HttpStatusCode.OK, ValidOptions());

            Result<bool> result = await service.SendSetPasswordEmailAsync(
                new UserProfile { UserName = "jdoe", Email = "jane@example.com" },
                "https://lanyard.example.com/set-password",
                logoUrl: null,
                accentColorHex: "#167a47",
                locationName: null);

            Assert.IsTrue(result.IsSuccess, result.Error);
            Assert.DoesNotContain("<img", handler.LastRequestBody);
        }

        [TestMethod]
        public async Task SendSetPasswordEmailAsync_WithLocationName_IncludesLocationInHtmlBody()
        {
            (EmailService service, FakeHttpMessageHandler handler) = BuildService(HttpStatusCode.OK, ValidOptions());

            Result<bool> result = await service.SendSetPasswordEmailAsync(
                new UserProfile { UserName = "jdoe", Email = "jane@example.com" },
                "https://lanyard.example.com/set-password",
                logoUrl: null,
                accentColorHex: BrandConstants.PrimaryColorHex,
                locationName: "Acme Corp Manchester");

            Assert.IsTrue(result.IsSuccess, result.Error);
            Assert.Contains("Acme Corp Manchester", handler.LastRequestBody);
        }

        [TestMethod]
        public async Task SendSetPasswordEmailAsync_NoLocationName_OmitsLocationLine()
        {
            (EmailService service, FakeHttpMessageHandler handler) = BuildService(HttpStatusCode.OK, ValidOptions());

            Result<bool> result = await service.SendSetPasswordEmailAsync(
                new UserProfile { UserName = "jdoe", Email = "jane@example.com" },
                "https://lanyard.example.com/set-password",
                logoUrl: null,
                accentColorHex: BrandConstants.PrimaryColorHex,
                locationName: null);

            Assert.IsTrue(result.IsSuccess, result.Error);
            Assert.DoesNotContain("Log in at:", handler.LastRequestBody);
        }

        [TestMethod]
        public async Task SendCourseRecurrenceReminderEmailAsync_WithLogoAndColor_IncludesBothInHtmlBody()
        {
            (EmailService service, FakeHttpMessageHandler handler) = BuildService(HttpStatusCode.OK, ValidOptions());

            Result<bool> result = await service.SendCourseRecurrenceReminderEmailAsync(
                new UserProfile { UserName = "jdoe", Email = "jane@example.com" },
                "Fire Safety",
                "https://lanyard.example.com/training/123",
                logoUrl: "https://lanyard.example.com/api/companies/1/logo",
                accentColorHex: "#C8102E");

            Assert.IsTrue(result.IsSuccess, result.Error);
            Assert.Contains("https://lanyard.example.com/api/companies/1/logo", handler.LastRequestBody);
            Assert.Contains("#C8102E", handler.LastRequestBody);
            Assert.Contains("Lanyard", handler.LastRequestBody);
        }

        [TestMethod]
        public async Task SendTwoFactorCodeEmailAsync_SuccessResponse_IncludesCodeInHtmlBody()
        {
            (EmailService service, FakeHttpMessageHandler handler) = BuildService(HttpStatusCode.OK, ValidOptions());

            Result<bool> result = await service.SendTwoFactorCodeEmailAsync(
                new UserProfile { UserName = "jdoe", Email = "jane@example.com" },
                "123456");

            Assert.IsTrue(result.IsSuccess, result.Error);
            Assert.Contains("123456", handler.LastRequestBody);
        }

        [TestMethod]
        public async Task SendTwoFactorCodeEmailAsync_UserHasNoEmail_ReturnsFail()
        {
            (EmailService service, _) = BuildService(HttpStatusCode.OK, ValidOptions());

            Result<bool> result = await service.SendTwoFactorCodeEmailAsync(
                new UserProfile { UserName = "jdoe", Email = null },
                "123456");

            Assert.IsFalse(result.IsSuccess);
            Assert.Contains("no email address", result.Error);
        }

        [TestMethod]
        public async Task SendTwoFactorCodeEmailAsync_NonSuccessStatusCode_ReturnsFail()
        {
            (EmailService service, _) = BuildService(HttpStatusCode.Unauthorized, ValidOptions());

            Result<bool> result = await service.SendTwoFactorCodeEmailAsync(
                new UserProfile { UserName = "jdoe", Email = "jane@example.com" },
                "123456");

            Assert.IsFalse(result.IsSuccess);
            Assert.Contains("401", result.Error);
        }

        [TestMethod]
        public async Task SendTrainingAssignedEmailAsync_WithDueDate_IncludesDueDateInHtmlBody()
        {
            (EmailService service, FakeHttpMessageHandler handler) = BuildService(HttpStatusCode.OK, ValidOptions());

            Result<bool> result = await service.SendTrainingAssignedEmailAsync(
                new UserProfile { UserName = "jdoe", Email = "jane@example.com" },
                "Fire Safety",
                new DateTime(2026, 12, 25, 0, 0, 0, DateTimeKind.Utc),
                "https://lanyard.example.com/training/123",
                logoUrl: "https://lanyard.example.com/api/companies/1/logo",
                accentColorHex: "#C8102E");

            Assert.IsTrue(result.IsSuccess, result.Error);
            Assert.Contains("25 December 2026", handler.LastRequestBody);
            Assert.Contains("https://lanyard.example.com/api/companies/1/logo", handler.LastRequestBody);
            Assert.Contains("#C8102E", handler.LastRequestBody);
        }

        [TestMethod]
        public async Task SendTrainingAssignedEmailAsync_NoDueDate_OmitsDueDateLine()
        {
            (EmailService service, FakeHttpMessageHandler handler) = BuildService(HttpStatusCode.OK, ValidOptions());

            Result<bool> result = await service.SendTrainingAssignedEmailAsync(
                new UserProfile { UserName = "jdoe", Email = "jane@example.com" },
                "Fire Safety",
                null,
                "https://lanyard.example.com/training/123",
                logoUrl: null,
                accentColorHex: BrandConstants.PrimaryColorHex);

            Assert.IsTrue(result.IsSuccess, result.Error);
            Assert.DoesNotContain("due by", handler.LastRequestBody);
        }

        [TestMethod]
        public async Task SendTrainingAssignedEmailAsync_UserHasNoEmail_ReturnsFail()
        {
            (EmailService service, _) = BuildService(HttpStatusCode.OK, ValidOptions());

            Result<bool> result = await service.SendTrainingAssignedEmailAsync(
                new UserProfile { UserName = "jdoe", Email = null },
                "Fire Safety",
                null,
                "https://lanyard.example.com/training/123",
                logoUrl: null,
                accentColorHex: BrandConstants.PrimaryColorHex);

            Assert.IsFalse(result.IsSuccess);
            Assert.Contains("no email address", result.Error);
        }

        [TestMethod]
        public async Task SendTrainingAssignedEmailAsync_NonSuccessStatusCode_ReturnsFail()
        {
            (EmailService service, _) = BuildService(HttpStatusCode.Unauthorized, ValidOptions());

            Result<bool> result = await service.SendTrainingAssignedEmailAsync(
                new UserProfile { UserName = "jdoe", Email = "jane@example.com" },
                "Fire Safety",
                null,
                "https://lanyard.example.com/training/123",
                logoUrl: null,
                accentColorHex: BrandConstants.PrimaryColorHex);

            Assert.IsFalse(result.IsSuccess);
            Assert.Contains("401", result.Error);
        }

        [TestMethod]
        public async Task SendTrainingDueSoonEmailAsync_SuccessResponse_IncludesCourseNameAndDueDateInHtmlBody()
        {
            (EmailService service, FakeHttpMessageHandler handler) = BuildService(HttpStatusCode.OK, ValidOptions());

            Result<bool> result = await service.SendTrainingDueSoonEmailAsync(
                new UserProfile { UserName = "jdoe", Email = "jane@example.com" },
                "Fire Safety",
                new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc),
                "https://lanyard.example.com/training/123",
                logoUrl: "https://lanyard.example.com/api/companies/1/logo",
                accentColorHex: "#C8102E");

            Assert.IsTrue(result.IsSuccess, result.Error);
            Assert.Contains("Fire Safety", handler.LastRequestBody);
            Assert.Contains("2 September 2026", handler.LastRequestBody);
            Assert.Contains("https://lanyard.example.com/api/companies/1/logo", handler.LastRequestBody);
            Assert.Contains("#C8102E", handler.LastRequestBody);
        }

        [TestMethod]
        public async Task SendTrainingDueSoonEmailAsync_UserHasNoEmail_ReturnsFail()
        {
            (EmailService service, _) = BuildService(HttpStatusCode.OK, ValidOptions());

            Result<bool> result = await service.SendTrainingDueSoonEmailAsync(
                new UserProfile { UserName = "jdoe", Email = null },
                "Fire Safety",
                DateTime.UtcNow.AddDays(3),
                "https://lanyard.example.com/training/123",
                logoUrl: null,
                accentColorHex: BrandConstants.PrimaryColorHex);

            Assert.IsFalse(result.IsSuccess);
            Assert.Contains("no email address", result.Error);
        }

        [TestMethod]
        public async Task SendTrainingDueSoonEmailAsync_NonSuccessStatusCode_ReturnsFail()
        {
            (EmailService service, _) = BuildService(HttpStatusCode.Unauthorized, ValidOptions());

            Result<bool> result = await service.SendTrainingDueSoonEmailAsync(
                new UserProfile { UserName = "jdoe", Email = "jane@example.com" },
                "Fire Safety",
                DateTime.UtcNow.AddDays(3),
                "https://lanyard.example.com/training/123",
                logoUrl: null,
                accentColorHex: BrandConstants.PrimaryColorHex);

            Assert.IsFalse(result.IsSuccess);
            Assert.Contains("401", result.Error);
        }
    }
}
