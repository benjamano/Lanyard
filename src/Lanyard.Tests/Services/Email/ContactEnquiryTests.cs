using System.Net;
using System.Text.Json;
using Lanyard.Application.Services.Email;
using Lanyard.Infrastructure.DTO;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Email;

// The homepage contact form: what counts as a sendable enquiry, and the email it becomes.
[TestClass]
public class ContactEnquiryTests
{
    [TestMethod]
    public void AGoodEnquiry_IsTrimmed()
    {
        (ContactEnquiry? enquiry, string? error) = ContactEnquiry.Create("  Sam Taylor ", " sam@example.com ", "  ", "  Tell me more about the rota, please.  ");

        Assert.IsNull(error);
        Assert.AreEqual("Sam Taylor", enquiry!.Name);
        Assert.AreEqual("sam@example.com", enquiry.Email);
        Assert.IsNull(enquiry.Venue, "A blank venue is left out.");
        Assert.AreEqual("Tell me more about the rota, please.", enquiry.Message);
    }

    [TestMethod]
    [DataRow("", "sam@example.com", "A long enough message", DisplayName = "No name")]
    [DataRow("Sam\nBcc: x@y.com", "sam@example.com", "A long enough message", DisplayName = "Name over two lines")]
    [DataRow("Sam", "sam@example", "A long enough message", DisplayName = "Email without a domain")]
    [DataRow("Sam", "Sam <sam@example.com>", "A long enough message", DisplayName = "Email with a display name")]
    [DataRow("Sam", "sam@example.com", "Hi", DisplayName = "Message too short")]
    public void ABadEnquiry_SaysWhatsWrong(string name, string email, string message)
    {
        (ContactEnquiry? enquiry, string? error) = ContactEnquiry.Create(name, email, null, message);

        Assert.IsNull(enquiry);
        Assert.IsFalse(string.IsNullOrWhiteSpace(error));
    }

    [TestMethod]
    public void AnOverlongMessage_IsRefused()
    {
        (ContactEnquiry? enquiry, _) = ContactEnquiry.Create("Sam", "sam@example.com", null, new string('a', ContactEnquiry.MessageMaxLength + 1));

        Assert.IsNull(enquiry);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }

    [TestMethod]
    public async Task TheEmail_GoesToTheContactAddress_RepliesToTheVisitor_AndEncodesWhatTheyTyped()
    {
        CapturingHandler handler = new();
        EmailService service = new(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.resend.com/") },
            Options.Create(new EmailOptions { ResendApiKey = "test-key", FromAddress = "noreply@example.com", FromName = "Lanyard" }),
            NullLogger<EmailService>.Instance);

        ContactEnquiry enquiry = new("Sam <b>Taylor</b>", "sam@example.com", "Riverside Arena", "Line one\n<script>alert(1)</script>");

        Result<bool> result = await service.SendContactEnquiryEmailAsync("owner@example.com", enquiry);

        Assert.IsTrue(result.IsSuccess, result.Error);
        using JsonDocument json = JsonDocument.Parse(handler.Body!);
        Assert.AreEqual("owner@example.com", json.RootElement.GetProperty("to")[0].GetString());
        Assert.AreEqual("sam@example.com", json.RootElement.GetProperty("reply_to").GetString());
        StringAssert.Contains(json.RootElement.GetProperty("subject").GetString(), "Riverside Arena");

        string html = json.RootElement.GetProperty("html").GetString()!;
        Assert.IsFalse(html.Contains("<script>"), "Nothing the visitor typed is sent as markup.");
        Assert.IsFalse(html.Contains("<b>Taylor</b>"));
        StringAssert.Contains(html, "Line one<br>");
    }
}
