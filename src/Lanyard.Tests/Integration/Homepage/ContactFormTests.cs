using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Integration.Homepage;

[TestClass]
public class ContactFormTests
{
    private static HttpClient AnonymousClient(CustomWebApplicationFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [TestMethod]
    public async Task Homepage_HasTheContactForm()
    {
        using CustomWebApplicationFactory factory = new();
        string html = await AnonymousClient(factory).GetStringAsync("/");

        StringAssert.Contains(html, "Send a message");
        StringAssert.Contains(html, "name=\"website\"", "The honeypot field is on the form.");
    }

    [TestMethod]
    public async Task ContactEndpoint_IsAnonymous_ChecksWhatItsSent_AndQuietlyDropsBots()
    {
        using CustomWebApplicationFactory factory = new();
        HttpClient client = AnonymousClient(factory);

        HttpResponseMessage invalid = await client.PostAsJsonAsync("/api/contact", new { name = "Sam", email = "not-an-email", message = "Tell me more about Lanyard." });
        Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);

        HttpResponseMessage bot = await client.PostAsJsonAsync("/api/contact", new { name = "Sam", email = "sam@example.com", message = "Tell me more about Lanyard.", website = "http://spam.example" });
        Assert.AreEqual(HttpStatusCode.OK, bot.StatusCode, "A bot is told it worked, so it doesn't retry.");
    }

    [TestMethod]
    public async Task ContactEndpoint_OnlyTakesJson()
    {
        using CustomWebApplicationFactory factory = new();

        HttpResponseMessage response = await AnonymousClient(factory).PostAsync("/api/contact",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["name"] = "Sam", ["email"] = "sam@example.com", ["message"] = "Tell me more about Lanyard." }));

        Assert.AreEqual(HttpStatusCode.UnsupportedMediaType, response.StatusCode, "A cross-site form post can't reach it.");
    }
}
