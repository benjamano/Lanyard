using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
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
            Assert.DoesNotContain("/logo.png", handler.LastRequestBody);
        }

        [TestMethod]
        public async Task SendSetPasswordEmailAsync_NoCompanyLogoOrPublicBaseUrl_FallsBackToLanyardLogoOnLinkOrigin()
        {
            (EmailService service, FakeHttpMessageHandler handler) = BuildService(HttpStatusCode.OK, ValidOptions());

            Result<bool> result = await service.SendSetPasswordEmailAsync(
                new UserProfile { UserName = "jdoe", Email = "jane@example.com" },
                "https://lanyard.example.com/set-password",
                logoUrl: null,
                accentColorHex: "#167a47",
                locationName: null);

            Assert.IsTrue(result.IsSuccess, result.Error);
            Assert.Contains("https://lanyard.example.com/logo.png", handler.LastRequestBody);
        }

        [TestMethod]
        public async Task SendCourseRecurrenceReminderEmailAsync_NoCompanyLogo_FallsBackToLanyardLogoOnPublicBaseUrl()
        {
            EmailOptions options = ValidOptions();
            options.PublicBaseUrl = "https://public.lanyard.example/";
            (EmailService service, FakeHttpMessageHandler handler) = BuildService(HttpStatusCode.OK, options);

            Result<bool> result = await service.SendCourseRecurrenceReminderEmailAsync(
                new UserProfile { UserName = "jdoe", Email = "jane@example.com" },
                "Fire Safety",
                "https://lanyard.example.com/training/123",
                logoUrl: null,
                accentColorHex: "#C8102E");

            Assert.IsTrue(result.IsSuccess, result.Error);
            Assert.Contains("https://public.lanyard.example/logo.png", handler.LastRequestBody);
        }

        [TestMethod]
        public async Task SendTwoFactorCodeEmailAsync_NoPublicBaseUrl_OmitsLogo()
        {
            (EmailService service, FakeHttpMessageHandler handler) = BuildService(HttpStatusCode.OK, ValidOptions());

            Result<bool> result = await service.SendTwoFactorCodeEmailAsync(
                new UserProfile { UserName = "jdoe", Email = "jane@example.com" },
                "123456");

            Assert.IsTrue(result.IsSuccess, result.Error);
            // The request body is JSON, which escapes "<" - so assert on the logo path, not the tag.
            Assert.DoesNotContain("logo.png", handler.LastRequestBody);
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
            Assert.DoesNotContain("/logo.png", handler.LastRequestBody);
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
    
        [TestMethod]
        public async Task SendCourseCompletionCertificateEmailAsync_AttachesPdfAsBase64()
        {
            (EmailService service, FakeHttpMessageHandler handler) = BuildService(HttpStatusCode.OK, ValidOptions());
            byte[] pdfBytes = [0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37];

            Result<bool> result = await service.SendCourseCompletionCertificateEmailAsync(
                new UserProfile { UserName = "jdoe", Email = "jane@example.com" },
                "Fire Safety",
                pdfBytes,
                logoUrl: null,
                accentColorHex: BrandConstants.PrimaryColorHex);

            Assert.IsTrue(result.IsSuccess, result.Error);
            Assert.IsNotNull(handler.LastRequestBody);

            using JsonDocument body = JsonDocument.Parse(handler.LastRequestBody!);
            JsonElement attachments = body.RootElement.GetProperty("attachments");

            Assert.AreEqual(1, attachments.GetArrayLength());

            JsonElement attachment = attachments[0];
            Assert.AreEqual("Fire Safety Certificate.pdf", attachment.GetProperty("filename").GetString());
            CollectionAssert.AreEqual(pdfBytes, Convert.FromBase64String(attachment.GetProperty("content").GetString()!));
        }

        [TestMethod]
        public async Task SendCourseCompletionCertificateEmailAsync_NoEmailAddress_Fails()
        {
            (EmailService service, _) = BuildService(HttpStatusCode.OK, ValidOptions());

            Result<bool> result = await service.SendCourseCompletionCertificateEmailAsync(
                new UserProfile { UserName = "jdoe", Email = null },
                "Fire Safety",
                [1, 2, 3],
                logoUrl: null,
                accentColorHex: BrandConstants.PrimaryColorHex);

            Assert.IsFalse(result.IsSuccess);
        }

        [TestMethod]
        public async Task SendCourseCompletionCertificateEmailAsync_StripsUnsafeCharactersFromFileName()
        {
            (EmailService service, FakeHttpMessageHandler handler) = BuildService(HttpStatusCode.OK, ValidOptions());

            await service.SendCourseCompletionCertificateEmailAsync(
                new UserProfile { UserName = "jdoe", Email = "jane@example.com" },
                "Health \"&\" Safety / Level 2",
                [1, 2, 3],
                logoUrl: null,
                accentColorHex: BrandConstants.PrimaryColorHex);

            using JsonDocument body = JsonDocument.Parse(handler.LastRequestBody!);
            string? fileName = body.RootElement.GetProperty("attachments")[0].GetProperty("filename").GetString();

            Assert.AreEqual("Health  Safety  Level 2 Certificate.pdf", fileName);
        }

        // Regression guard: adding attachment support must not change the request body of
        // the five send methods that predate it - Resend rejects a null "attachments" key.
        [TestMethod]
        public async Task SendTrainingAssignedEmailAsync_OmitsAttachmentsPropertyEntirely()
        {
            (EmailService service, FakeHttpMessageHandler handler) = BuildService(HttpStatusCode.OK, ValidOptions());

            await service.SendTrainingAssignedEmailAsync(
                new UserProfile { UserName = "jdoe", Email = "jane@example.com" },
                "Fire Safety",
                dueDate: null,
                "https://lanyard.example.com/training/1",
                logoUrl: null,
                accentColorHex: BrandConstants.PrimaryColorHex);

            using JsonDocument body = JsonDocument.Parse(handler.LastRequestBody!);

            Assert.IsFalse(body.RootElement.TryGetProperty("attachments", out _));
        }
    
        [TestMethod]
        public async Task SendCourseCompletionCertificateEmailAsync_GreetsByFirstName_NotUsername()
        {
            (EmailService service, FakeHttpMessageHandler handler) = BuildService(HttpStatusCode.OK, ValidOptions());

            await service.SendCourseCompletionCertificateEmailAsync(
                new UserProfile { UserName = "jdoe", Email = "jane@example.com", FirstName = "Jane", LastName = "Doe" },
                "Fire Safety",
                [1, 2, 3],
                logoUrl: null,
                accentColorHex: BrandConstants.PrimaryColorHex);

            using JsonDocument body = JsonDocument.Parse(handler.LastRequestBody!);
            string html = body.RootElement.GetProperty("html").GetString()!;

            StringAssert.Contains(html, "Hi Jane,");
            Assert.IsFalse(html.Contains("Hi jdoe,"), "greeting should not fall back to the username when a first name exists");
        }

        [TestMethod]
        public async Task SendTrainingAssignedEmailAsync_GreetsByFirstName_NotUsername()
        {
            (EmailService service, FakeHttpMessageHandler handler) = BuildService(HttpStatusCode.OK, ValidOptions());

            await service.SendTrainingAssignedEmailAsync(
                new UserProfile { UserName = "jdoe", Email = "jane@example.com", FirstName = "Jane", LastName = "Doe" },
                "Fire Safety",
                dueDate: null,
                "https://lanyard.example.com/training/1",
                logoUrl: null,
                accentColorHex: BrandConstants.PrimaryColorHex);

            using JsonDocument body = JsonDocument.Parse(handler.LastRequestBody!);
            StringAssert.Contains(body.RootElement.GetProperty("html").GetString()!, "Hi Jane,");
        }

        [TestMethod]
        public async Task SendCourseCompletionCertificateEmailAsync_NoFirstName_FallsBackRatherThanGreetingNobody()
        {
            (EmailService service, FakeHttpMessageHandler handler) = BuildService(HttpStatusCode.OK, ValidOptions());

            await service.SendCourseCompletionCertificateEmailAsync(
                new UserProfile { UserName = "jdoe", Email = "jane@example.com" },
                "Fire Safety",
                [1, 2, 3],
                logoUrl: null,
                accentColorHex: BrandConstants.PrimaryColorHex);

            using JsonDocument body = JsonDocument.Parse(handler.LastRequestBody!);
            StringAssert.Contains(body.RootElement.GetProperty("html").GetString()!, "Hi jdoe,");
        }

        [TestMethod]
        public async Task SendStaffDocumentExpiryReminderEmailAsync_SuccessResponse_IncludesDocumentTypeAndExpiryDateInHtmlBody()
        {
            (EmailService service, FakeHttpMessageHandler handler) = BuildService(HttpStatusCode.OK, ValidOptions());

            Result<bool> result = await service.SendStaffDocumentExpiryReminderEmailAsync(
                new UserProfile { UserName = "jdoe", Email = "jane@example.com" },
                "First Aid Certificate",
                new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
                daysBeforeExpiry: 7,
                logoUrl: "https://lanyard.example.com/api/companies/1/logo",
                accentColorHex: "#C8102E");

            Assert.IsTrue(result.IsSuccess, result.Error);
            Assert.Contains("First Aid Certificate", handler.LastRequestBody);
            Assert.Contains("1 October 2026", handler.LastRequestBody);
            Assert.Contains("https://lanyard.example.com/api/companies/1/logo", handler.LastRequestBody);
            Assert.Contains("#C8102E", handler.LastRequestBody);
            Assert.Contains("7 days", handler.LastRequestBody);
        }

        [TestMethod]
        public async Task SendStaffDocumentExpiryReminderEmailAsync_SingularDayCount_ReadsAsOneDayNotOneDays()
        {
            (EmailService service, FakeHttpMessageHandler handler) = BuildService(HttpStatusCode.OK, ValidOptions());

            await service.SendStaffDocumentExpiryReminderEmailAsync(
                new UserProfile { UserName = "jdoe", Email = "jane@example.com" },
                "First Aid Certificate",
                DateTime.UtcNow.AddDays(1),
                daysBeforeExpiry: 1,
                logoUrl: null,
                accentColorHex: BrandConstants.PrimaryColorHex);

            using JsonDocument body = JsonDocument.Parse(handler.LastRequestBody!);
            string html = body.RootElement.GetProperty("html").GetString()!;

            StringAssert.Contains(html, "1 day<");
            Assert.IsFalse(html.Contains("1 days"), "singular day count should read \"1 day\", not \"1 days\"");
        }

        [TestMethod]
        public async Task SendStaffDocumentExpiryReminderEmailAsync_UserHasNoEmail_ReturnsFail()
        {
            (EmailService service, _) = BuildService(HttpStatusCode.OK, ValidOptions());

            Result<bool> result = await service.SendStaffDocumentExpiryReminderEmailAsync(
                new UserProfile { UserName = "jdoe", Email = null },
                "First Aid Certificate",
                DateTime.UtcNow.AddDays(7),
                daysBeforeExpiry: 7,
                logoUrl: null,
                accentColorHex: BrandConstants.PrimaryColorHex);

            Assert.IsFalse(result.IsSuccess);
            Assert.Contains("no email address", result.Error);
        }

        [TestMethod]
        public async Task SendStaffDocumentExpiryReminderEmailAsync_NonSuccessStatusCode_ReturnsFail()
        {
            (EmailService service, _) = BuildService(HttpStatusCode.Unauthorized, ValidOptions());

            Result<bool> result = await service.SendStaffDocumentExpiryReminderEmailAsync(
                new UserProfile { UserName = "jdoe", Email = "jane@example.com" },
                "First Aid Certificate",
                DateTime.UtcNow.AddDays(7),
                daysBeforeExpiry: 7,
                logoUrl: null,
                accentColorHex: BrandConstants.PrimaryColorHex);

            Assert.IsFalse(result.IsSuccess);
            Assert.Contains("401", result.Error);
        }

        // The set-password email deliberately still shows the username - it IS the credential
        // being communicated, so switching it to a first name would break the email's purpose.
        [TestMethod]
        public async Task SendSetPasswordEmailAsync_StillShowsTheUsername()
        {
            (EmailService service, FakeHttpMessageHandler handler) = BuildService(HttpStatusCode.OK, ValidOptions());

            await service.SendSetPasswordEmailAsync(
                new UserProfile { UserName = "jdoe", Email = "jane@example.com", FirstName = "Jane", LastName = "Doe" },
                "https://lanyard.example.com/set-password?userId=1&token=abc",
                logoUrl: null,
                accentColorHex: BrandConstants.PrimaryColorHex,
                locationName: null);

            using JsonDocument body = JsonDocument.Parse(handler.LastRequestBody!);
            StringAssert.Contains(body.RootElement.GetProperty("html").GetString()!, "jdoe");
        }

        [TestMethod]
        public async Task SendOnboardingWelcomeEmailAsync_SuccessResponse_IncludesSubjectAndBodyHtml()
        {
            (EmailService service, FakeHttpMessageHandler handler) = BuildService(HttpStatusCode.OK, ValidOptions());

            Result<bool> result = await service.SendOnboardingWelcomeEmailAsync(
                new UserProfile { UserName = "jdoe", Email = "jane@example.com", FirstName = "Jane", LastName = "Doe" },
                "Welcome to Acme",
                "<p>Glad to have you.</p>",
                logoUrl: "https://lanyard.example.com/api/companies/1/logo",
                accentColorHex: "#C8102E",
                attachments: []);

            Assert.IsTrue(result.IsSuccess, result.Error);
            Assert.Contains("Welcome to Acme", handler.LastRequestBody);
            Assert.Contains("Glad to have you.", handler.LastRequestBody);
            Assert.Contains("https://lanyard.example.com/api/companies/1/logo", handler.LastRequestBody);
            Assert.Contains("#C8102E", handler.LastRequestBody);
            Assert.Contains("Hi Jane,", handler.LastRequestBody);
        }

        [TestMethod]
        public async Task SendOnboardingWelcomeEmailAsync_StripsScriptTagsFromBodyHtml()
        {
            (EmailService service, FakeHttpMessageHandler handler) = BuildService(HttpStatusCode.OK, ValidOptions());

            await service.SendOnboardingWelcomeEmailAsync(
                new UserProfile { UserName = "jdoe", Email = "jane@example.com" },
                "Welcome",
                "<p>Hello</p><script>alert('xss')</script>",
                logoUrl: null,
                accentColorHex: BrandConstants.PrimaryColorHex,
                attachments: []);

            using JsonDocument body = JsonDocument.Parse(handler.LastRequestBody!);
            string html = body.RootElement.GetProperty("html").GetString()!;

            StringAssert.Contains(html, "Hello");
            Assert.IsFalse(html.Contains("<script>"), "sanitizer should strip script tags from admin-authored body HTML");
            Assert.IsFalse(html.Contains("alert("), "sanitizer should strip script tags from admin-authored body HTML");
        }

        [TestMethod]
        public async Task SendOnboardingWelcomeEmailAsync_StripsEventHandlerAttributesFromBodyHtml()
        {
            (EmailService service, FakeHttpMessageHandler handler) = BuildService(HttpStatusCode.OK, ValidOptions());

            await service.SendOnboardingWelcomeEmailAsync(
                new UserProfile { UserName = "jdoe", Email = "jane@example.com" },
                "Welcome",
                "<p>Hello</p><img src=\"x\" onerror=\"alert('xss')\" />",
                logoUrl: null,
                accentColorHex: BrandConstants.PrimaryColorHex,
                attachments: []);

            using JsonDocument body = JsonDocument.Parse(handler.LastRequestBody!);
            string html = body.RootElement.GetProperty("html").GetString()!;

            StringAssert.Contains(html, "Hello");
            Assert.IsFalse(html.Contains("onerror"), "sanitizer should strip event-handler attributes from admin-authored body HTML");
        }

        [TestMethod]
        public async Task SendOnboardingWelcomeEmailAsync_StripsJavascriptHrefFromBodyHtml()
        {
            (EmailService service, FakeHttpMessageHandler handler) = BuildService(HttpStatusCode.OK, ValidOptions());

            await service.SendOnboardingWelcomeEmailAsync(
                new UserProfile { UserName = "jdoe", Email = "jane@example.com" },
                "Welcome",
                "<p><a href=\"javascript:alert('xss')\">Click here</a></p>",
                logoUrl: null,
                accentColorHex: BrandConstants.PrimaryColorHex,
                attachments: []);

            using JsonDocument body = JsonDocument.Parse(handler.LastRequestBody!);
            string html = body.RootElement.GetProperty("html").GetString()!;

            StringAssert.Contains(html, "Click here");
            Assert.IsFalse(html.Contains("javascript:"), "sanitizer should strip javascript: hrefs from admin-authored body HTML");
        }

        [TestMethod]
        public async Task SendOnboardingWelcomeEmailAsync_WithAttachments_AttachesThemAsBase64()
        {
            (EmailService service, FakeHttpMessageHandler handler) = BuildService(HttpStatusCode.OK, ValidOptions());
            byte[] handbookBytes = [1, 2, 3, 4];

            await service.SendOnboardingWelcomeEmailAsync(
                new UserProfile { UserName = "jdoe", Email = "jane@example.com" },
                "Welcome",
                "<p>Hello</p>",
                logoUrl: null,
                accentColorHex: BrandConstants.PrimaryColorHex,
                attachments: [new EmailAttachment("Handbook.pdf", handbookBytes)]);

            using JsonDocument body = JsonDocument.Parse(handler.LastRequestBody!);
            JsonElement attachments = body.RootElement.GetProperty("attachments");

            Assert.AreEqual(1, attachments.GetArrayLength());
            Assert.AreEqual("Handbook.pdf", attachments[0].GetProperty("filename").GetString());
            CollectionAssert.AreEqual(handbookBytes, Convert.FromBase64String(attachments[0].GetProperty("content").GetString()!));
        }

        [TestMethod]
        public async Task SendOnboardingWelcomeEmailAsync_NoAttachments_OmitsAttachmentsPropertyEntirely()
        {
            (EmailService service, FakeHttpMessageHandler handler) = BuildService(HttpStatusCode.OK, ValidOptions());

            await service.SendOnboardingWelcomeEmailAsync(
                new UserProfile { UserName = "jdoe", Email = "jane@example.com" },
                "Welcome",
                "<p>Hello</p>",
                logoUrl: null,
                accentColorHex: BrandConstants.PrimaryColorHex,
                attachments: []);

            using JsonDocument body = JsonDocument.Parse(handler.LastRequestBody!);

            Assert.IsFalse(body.RootElement.TryGetProperty("attachments", out _));
        }

        [TestMethod]
        public async Task SendOnboardingWelcomeEmailAsync_UserHasNoEmail_ReturnsFail()
        {
            (EmailService service, _) = BuildService(HttpStatusCode.OK, ValidOptions());

            Result<bool> result = await service.SendOnboardingWelcomeEmailAsync(
                new UserProfile { UserName = "jdoe", Email = null },
                "Welcome",
                "<p>Hello</p>",
                logoUrl: null,
                accentColorHex: BrandConstants.PrimaryColorHex,
                attachments: []);

            Assert.IsFalse(result.IsSuccess);
            Assert.Contains("no email address", result.Error);
        }

        [TestMethod]
        public async Task SendOnboardingWelcomeEmailAsync_NonSuccessStatusCode_ReturnsFail()
        {
            (EmailService service, _) = BuildService(HttpStatusCode.Unauthorized, ValidOptions());

            Result<bool> result = await service.SendOnboardingWelcomeEmailAsync(
                new UserProfile { UserName = "jdoe", Email = "jane@example.com" },
                "Welcome",
                "<p>Hello</p>",
                logoUrl: null,
                accentColorHex: BrandConstants.PrimaryColorHex,
                attachments: []);

            Assert.IsFalse(result.IsSuccess);
            Assert.Contains("401", result.Error);
        }
    }
}
