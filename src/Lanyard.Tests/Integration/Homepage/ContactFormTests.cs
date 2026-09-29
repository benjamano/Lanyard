using System.Net;
using System.Net.Http.Json;
using Lanyard.API.Contact;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Integration.Homepage;

[TestClass]
public class ContactFormTests
{
    private static HttpClient AnonymousClient(CustomWebApplicationFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    // A stamp from a form shown `ago` - what the real form sends with its message.
    private static string Stamp(CustomWebApplicationFactory factory, TimeSpan ago) =>
        ContactFormToken.Create(factory.Services.GetRequiredService<IDataProtectionProvider>(), DateTimeOffset.UtcNow - ago);

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
        string stamp = Stamp(factory, TimeSpan.FromSeconds(30));

        HttpResponseMessage invalid = await client.PostAsJsonAsync("/api/contact", new { name = "Sam", email = "not-an-email", message = "Tell me more about Lanyard.", token = stamp });
        Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);

        HttpResponseMessage honeypot = await client.PostAsJsonAsync("/api/contact", new { name = "Sam", email = "sam@example.com", message = "Tell me more about Lanyard.", website = "http://spam.example", token = stamp });
        Assert.AreEqual(HttpStatusCode.OK, honeypot.StatusCode, "A bot is told it worked, so it doesn't retry.");

        HttpResponseMessage tooFast = await client.PostAsJsonAsync("/api/contact", new { name = "Sam", email = "sam@example.com", message = "Tell me more about Lanyard.", token = Stamp(factory, TimeSpan.Zero) });
        Assert.AreEqual(HttpStatusCode.OK, tooFast.StatusCode, "Filled in faster than a person could: dropped, but looks sent.");
    }

    [TestMethod]
    public async Task ContactEndpoint_WithoutTheFormsStamp_IsRefused()
    {
        using CustomWebApplicationFactory factory = new();
        HttpClient client = AnonymousClient(factory);

        HttpResponseMessage missing = await client.PostAsJsonAsync("/api/contact", new { name = "Sam", email = "sam@example.com", message = "Tell me more about Lanyard." });
        Assert.AreEqual(HttpStatusCode.BadRequest, missing.StatusCode, "Posting straight to the endpoint, without loading the form, doesn't send anything.");

        HttpResponseMessage forged = await client.PostAsJsonAsync("/api/contact", new { name = "Sam", email = "sam@example.com", message = "Tell me more about Lanyard.", token = "not-a-real-stamp" });
        Assert.AreEqual(HttpStatusCode.BadRequest, forged.StatusCode);

        HttpResponseMessage stale = await client.PostAsJsonAsync("/api/contact", new { name = "Sam", email = "sam@example.com", message = "Tell me more about Lanyard.", token = Stamp(factory, TimeSpan.FromDays(2)) });
        Assert.AreEqual(HttpStatusCode.BadRequest, stale.StatusCode);
    }

    [TestMethod]
    public async Task ContactEndpoint_OnlyTakesJson()
    {
        using CustomWebApplicationFactory factory = new();

        HttpResponseMessage response = await AnonymousClient(factory).PostAsync("/api/contact",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["name"] = "Sam", ["email"] = "sam@example.com", ["message"] = "Tell me more about Lanyard." }));

        Assert.AreEqual(HttpStatusCode.UnsupportedMediaType, response.StatusCode, "A cross-site form post can't reach it.");
    }

    [TestMethod]
    public async Task ContactEndpoint_IsRateLimitedPerVisitor()
    {
        using CustomWebApplicationFactory factory = new();
        HttpClient client = AnonymousClient(factory);

        List<HttpStatusCode> codes = [];
        for (int i = 0; i < 6; i++)
        {
            HttpResponseMessage response = await client.PostAsJsonAsync("/api/contact", new { name = "Sam", email = "bad", message = "Tell me more about Lanyard." });
            codes.Add(response.StatusCode);
        }

        Assert.AreEqual(HttpStatusCode.TooManyRequests, codes[^1], string.Join(", ", codes));
    }
}
