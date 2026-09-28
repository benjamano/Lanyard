using System.Net;
using Lanyard.Application.Services.Demo;
using Lanyard.Application.Services.Email;
using Lanyard.Application.Services.Notifications;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Demo;

// The public demo is shared by strangers, so it mustn't email anyone or push notifications to
// anyone's phone. (Password/2FA lockdowns are covered in SecurityServiceTenancyTests.)
[TestClass]
public class DemoLockdownTests
{
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }

    private static Mock<IDemoDirectory> Directory(bool isDemoUser)
    {
        Mock<IDemoDirectory> directory = new();
        directory.Setup(x => x.IsDemoUserAsync(It.IsAny<string?>())).ReturnsAsync(isDemoUser);
        return directory;
    }

    private static (EmailService Service, CountingHandler Handler) BuildEmailService(bool isDemoUser)
    {
        CountingHandler handler = new();
        HttpClient http = new(handler) { BaseAddress = new Uri("https://api.resend.com/") };
        EmailOptions options = new() { ResendApiKey = "re_test", FromAddress = "hello@example.com", FromName = "Lanyard", PublicBaseUrl = "https://lanyard.example" };

        return (new EmailService(http, Options.Create(options), NullLogger<EmailService>.Instance, Directory(isDemoUser).Object), handler);
    }

    [TestMethod]
    public async Task EmailToADemoAccount_IsNeverSent_ButReportsSuccess()
    {
        (EmailService service, CountingHandler handler) = BuildEmailService(isDemoUser: true);

        Result<bool> result = await service.SendSetPasswordEmailAsync(
            new UserProfile { Id = "demo-invitee", Email = "someone-real@example.com", FirstName = "Real" },
            "https://lanyard.example/set-password", null, "#123456", "Demo Town");

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(0, handler.Calls, "Nothing addressed to a demo account may reach Resend.");
    }

    [TestMethod]
    public async Task EmailToARealAccount_IsStillSent()
    {
        (EmailService service, CountingHandler handler) = BuildEmailService(isDemoUser: false);

        await service.SendSetPasswordEmailAsync(
            new UserProfile { Id = "real-user", Email = "real@example.com", FirstName = "Real" },
            "https://lanyard.example/set-password", null, "#123456", "Ipswich");

        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    public async Task PushSubscriptionForADemoAccount_IsRefused()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        PushSubscriptionService service = new(SchedulingTestHelpers.GetFactory(options), TimeProvider.System,
            NullLogger<PushSubscriptionService>.Instance, Directory(isDemoUser: true).Object);

        Result<bool> result = await service.SaveAsync(DemoAccounts.StaffUserId,
            new PushSubscriptionInput("https://push.example.com/abc", "p256dh-key", "auth-key"),
            new DeviceReport("Mozilla/5.0", 0, false));

        Assert.IsFalse(result.IsSuccess);

        await using ApplicationDbContext ctx = new(options);
        Assert.IsFalse(await ctx.PushSubscriptions.AnyAsync());
    }
}
