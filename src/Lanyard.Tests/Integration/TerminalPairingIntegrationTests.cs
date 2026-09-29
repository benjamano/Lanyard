using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Integration;

// The clock-in terminal's device authentication only works if several pieces line up across the
// real pipeline: the controller's role gate and hand-rolled antiforgery check, the HttpOnly cookie
// it sets, the anonymous /rota/terminal route, and the page reading that cookie during its
// prerender (a Blazor Server circuit has no HTTP request of its own). None of that is visible to a
// service-level test, so it's exercised end to end here.
[TestClass]
public class TerminalPairingIntegrationTests
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

    private async Task<HttpClient> SignedInAdminClientAsync()
    {
        HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        HttpResponseMessage login = await client.PostAsJsonAsync("/api/auth/login", new LoginDto
        {
            Username = "admin",
            Password = CustomWebApplicationFactory.SeedAdminPassword,
            LocationId = ApplicationDbContext.SeedIpswichLocationId
        });

        Assert.IsTrue(login.IsSuccessStatusCode, $"Seed admin login failed: {(int)login.StatusCode}");

        return client;
    }

    private string IssueCode(string issuedBy = ApplicationDbContext.SeedAdminUserId, bool signOut = false) =>
        _factory.Services.GetRequiredService<ITerminalEphemeralTokenService>()
            .IssuePairingCode(ApplicationDbContext.SeedIpswichLocationId, "Integration tablet", issuedBy, signOut);

    private static (string Name, string Value) ReadAntiforgeryField(string html)
    {
        Match match = Regex.Match(html, "<input type=\"hidden\" name=\"(?<name>[^\"]+)\" value=\"(?<value>[^\"]*)\" />");
        Assert.IsTrue(match.Success, "The pairing page didn't render an antiforgery field.");

        return (WebUtility.HtmlDecode(match.Groups["name"].Value), WebUtility.HtmlDecode(match.Groups["value"].Value));
    }

    [TestMethod]
    public async Task PairGet_WhenSignedOut_RedirectsToLogin()
    {
        HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        HttpResponseMessage response = await client.GetAsync($"/api/terminal/pair/{IssueCode()}");

        Assert.IsTrue((int)response.StatusCode is >= 300 and < 400);
        StringAssert.StartsWith(response.Headers.Location!.AbsolutePath, "/HandleLogin");
    }

    [TestMethod]
    public async Task Pairing_SetsAnHttpOnlyCookie_AndTheTerminalPageRecognisesTheDevice()
    {
        HttpClient client = await SignedInAdminClientAsync();
        string code = IssueCode();

        HttpResponseMessage form = await client.GetAsync($"/api/terminal/pair/{code}");
        Assert.AreEqual(HttpStatusCode.OK, form.StatusCode);
        (string tokenName, string tokenValue) = ReadAntiforgeryField(await form.Content.ReadAsStringAsync());

        HttpResponseMessage paired = await client.PostAsync("/api/terminal/pair", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            [tokenName] = tokenValue,
            ["code"] = code
        }));

        Assert.AreEqual(HttpStatusCode.Redirect, paired.StatusCode, await paired.Content.ReadAsStringAsync());
        Assert.AreEqual("/rota/terminal", paired.Headers.Location!.OriginalString);

        string setCookie = string.Join("\n", paired.Headers.GetValues("Set-Cookie"));
        StringAssert.Contains(setCookie, $"{TerminalCookie.Name}=");
        StringAssert.Contains(setCookie.ToLowerInvariant(), "httponly");

        // The terminal page is anonymous and reads the device cookie in its prerender - the served
        // HTML should already name the location and terminal.
        HttpResponseMessage terminal = await client.GetAsync("/rota/terminal");
        string html = await terminal.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, terminal.StatusCode);
        StringAssert.Contains(html, "Integration tablet");
        StringAssert.Contains(html, "Ipswich");
        StringAssert.Contains(html, "Tap your name");
    }

    [TestMethod]
    public async Task PairPost_WithoutAntiforgeryToken_IsRejected()
    {
        HttpClient client = await SignedInAdminClientAsync();

        HttpResponseMessage response = await client.PostAsync("/api/terminal/pair", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = IssueCode()
        }));

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.IsFalse(response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies)
            && cookies.Any(c => c.StartsWith(TerminalCookie.Name, StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task PairGet_WithACodeSomeoneElseStarted_GoesNowhere()
    {
        HttpClient client = await SignedInAdminClientAsync();

        HttpResponseMessage response = await client.GetAsync($"/api/terminal/pair/{IssueCode(issuedBy: "someone-else")}");

        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        StringAssert.Contains(response.Headers.Location!.OriginalString, "pairing=expired");
    }

    [TestMethod]
    public async Task TerminalPage_WithoutACookie_IsReachableAnonymouslyAndSaysItIsNotPaired()
    {
        HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        HttpResponseMessage response = await client.GetAsync("/rota/terminal");
        string html = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(html, "isn&#x27;t a clock-in terminal yet");
        StringAssert.Contains(html, "_framework/blazor.web");
    }

    [TestMethod]
    public async Task ScanPage_WhenSignedOut_RedirectsToLogin()
    {
        HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        HttpResponseMessage response = await client.GetAsync("/rota/scan/some-code");

        Assert.IsTrue((int)response.StatusCode is >= 300 and < 400);
        StringAssert.StartsWith(response.Headers.Location!.AbsolutePath, "/HandleLogin");
    }

    // Regression: the terminal's full-screen layout was chosen by a substring match on
    // "/rota/terminal", which also caught this page and left it with no nav or dialog host.
    [TestMethod]
    public async Task TerminalsManagementPage_KeepsTheNormalAppLayout()
    {
        HttpClient client = await SignedInAdminClientAsync();

        HttpResponseMessage response = await client.GetAsync("/manage/rota/terminals");
        string html = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(html, "Rota Builder");
        StringAssert.Contains(html, "Pair this device as a terminal");
    }
}
