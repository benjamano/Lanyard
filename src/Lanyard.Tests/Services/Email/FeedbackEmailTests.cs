using System.Net;
using System.Text.Json;
using Lanyard.Application.Services.Email;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Feedback;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Email;

// The "new feedback" email to platform admins.
[TestClass]
public class FeedbackEmailTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }

    private static EmailService GetService(CapturingHandler handler) => new(
        new HttpClient(handler) { BaseAddress = new Uri("https://api.resend.com/") },
        Options.Create(new EmailOptions { ResendApiKey = "test-key", FromAddress = "noreply@example.com", FromName = "Lanyard" }),
        NullLogger<EmailService>.Instance);

    [TestMethod]
    public async Task TheEmail_GoesToTheAdmin_RepliesToTheSender_AndEncodesTheComment()
    {
        CapturingHandler handler = new();
        UserProfile admin = new() { Id = "ops", Email = "ops@lanyard.example" };
        FeedbackEmail feedback = new(FeedbackKind.Thumbs, -1, "Line one\n<script>alert(1)</script>", "https://lanyard.example/rota",
            "Sam <b>Taylor</b>", "sam@example.com", "Play2Day", "Ipswich", "https://lanyard.example/platform/feedback");

        Result<bool> result = await GetService(handler).SendFeedbackReceivedEmailAsync(admin, feedback);

        Assert.IsTrue(result.IsSuccess, result.Error);
        using JsonDocument json = JsonDocument.Parse(handler.Body!);
        Assert.AreEqual("ops@lanyard.example", json.RootElement.GetProperty("to")[0].GetString());
        Assert.AreEqual("sam@example.com", json.RootElement.GetProperty("reply_to").GetString());
        StringAssert.Contains(json.RootElement.GetProperty("subject").GetString(), "Thumbs down");
        StringAssert.Contains(json.RootElement.GetProperty("subject").GetString(), "Play2Day");

        string html = json.RootElement.GetProperty("html").GetString()!;
        Assert.IsFalse(html.Contains("<script>"), "Nothing the sender typed is sent as markup.");
        Assert.IsFalse(html.Contains("<b>Taylor</b>"));
        StringAssert.Contains(html, "Line one<br>");
        StringAssert.Contains(html, "https://lanyard.example/platform/feedback");
    }

    [TestMethod]
    public async Task AStarRating_ShowsTheScoreOutOfFive()
    {
        CapturingHandler handler = new();
        UserProfile admin = new() { Id = "ops", Email = "ops@lanyard.example" };
        FeedbackEmail feedback = new(FeedbackKind.StarRating, 4, null, "https://lanyard.example/", "Sam", null, null, null, "https://lanyard.example/platform/feedback");

        await GetService(handler).SendFeedbackReceivedEmailAsync(admin, feedback);

        using JsonDocument json = JsonDocument.Parse(handler.Body!);
        StringAssert.Contains(json.RootElement.GetProperty("subject").GetString(), "4/5 stars");
        Assert.IsFalse(json.RootElement.TryGetProperty("reply_to", out _), "No sender email, no reply-to.");
        StringAssert.Contains(json.RootElement.GetProperty("html").GetString(), "No comment left.");
    }
}
