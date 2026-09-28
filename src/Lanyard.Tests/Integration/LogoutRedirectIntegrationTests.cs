using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Integration;

// Where signing out lands you: the Log out button goes to the public homepage; the idle
// auto-logout (which passes the page it was on) goes to the login page so you can pick up again.
[TestClass]
public class LogoutRedirectIntegrationTests
{
    private CustomWebApplicationFactory _factory = null!;

    [TestInitialize]
    public void Setup()
    {
        _factory = new CustomWebApplicationFactory();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _factory.Dispose();
    }

    private async Task<HttpClient> SignedInClientAsync()
    {
        HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        HttpResponseMessage login = await client.PostAsJsonAsync("/api/auth/login", new LoginDto
        {
            Username = "admin",
            Password = CustomWebApplicationFactory.SeedAdminPassword,
            LocationId = ApplicationDbContext.SeedIpswichLocationId
        });
        Assert.IsTrue(login.IsSuccessStatusCode);

        return client;
    }

    // Signing out is a POST behind an antiforgery token; the GET renders the form that carries it.
    private static async Task<HttpResponseMessage> SignOutAsync(HttpClient client, string? returnUrl)
    {
        string getUrl = returnUrl is null ? "/api/auth/logout" : $"/api/auth/logout?returnUrl={Uri.EscapeDataString(returnUrl)}";
        string html = await client.GetStringAsync(getUrl);

        Dictionary<string, string> fields = Regex.Matches(html, "<input type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]*)\"")
            .ToDictionary(m => WebUtility.HtmlDecode(m.Groups[1].Value), m => WebUtility.HtmlDecode(m.Groups[2].Value));

        return await client.PostAsync("/api/auth/logout", new FormUrlEncodedContent(fields));
    }

    [TestMethod]
    public async Task LogOutButton_TakesYouToTheHomepage()
    {
        HttpClient client = await SignedInClientAsync();

        HttpResponseMessage response = await SignOutAsync(client, returnUrl: null);

        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.AreEqual("/", response.Headers.Location?.OriginalString);
    }

    [TestMethod]
    public async Task IdleAutoLogout_StillOffersToTakeYouBackAfterSigningIn()
    {
        HttpClient client = await SignedInClientAsync();

        HttpResponseMessage response = await SignOutAsync(client, returnUrl: "/manage/users");

        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.AreEqual("/login?returnUrl=%2Fmanage%2Fusers", response.Headers.Location?.OriginalString);
    }
}
