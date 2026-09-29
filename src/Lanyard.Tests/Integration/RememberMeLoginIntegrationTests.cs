using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using Lanyard.Infrastructure.DataAccess;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Net.Http.Headers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Integration;

// "Keep me signed in" only works if the login-form POST turns rememberMe into a persistent auth
// cookie (one with an expiry). Without it the browser gets a session cookie, which a phone drops
// every time it kills the installed app - the bug this checkbox was added to fix.
[TestClass]
public class RememberMeLoginIntegrationTests
{
    private const string AuthCookieName = ".AspNetCore.Identity.Application";

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

    private async Task<SetCookieHeaderValue> LoginAndGetAuthCookieAsync(string? rememberMe)
    {
        HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        Dictionary<string, string> form = new()
        {
            ["username"] = "admin",
            ["password"] = CustomWebApplicationFactory.SeedAdminPassword,
            ["locationId"] = ApplicationDbContext.SeedIpswichLocationId.ToString()
        };

        if (rememberMe is not null)
        {
            form["rememberMe"] = rememberMe;
        }

        HttpResponseMessage response = await client.PostAsync("/api/auth/login-form", new FormUrlEncodedContent(form));

        Assert.IsTrue(response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? setCookies), "Login should set the auth cookie.");

        SetCookieHeaderValue? authCookie = SetCookieHeaderValue.ParseList(setCookies!.ToList())
            .FirstOrDefault(c => c.Name.Value!.StartsWith(AuthCookieName));

        Assert.IsNotNull(authCookie, "Login should set the auth cookie.");
        return authCookie;
    }

    [TestMethod]
    public async Task LoginForm_RememberMeTrue_IssuesLongLivedPersistentCookie()
    {
        SetCookieHeaderValue authCookie = await LoginAndGetAuthCookieAsync("true");

        Assert.IsTrue(authCookie.Expires.HasValue, "Keep me signed in should issue a persistent cookie.");
        Assert.IsTrue(authCookie.Expires.Value > DateTimeOffset.UtcNow.AddDays(60), $"Expected a long-lived cookie, got expiry {authCookie.Expires}.");
    }

    [TestMethod]
    public async Task LoginForm_RememberMeFalse_IssuesSessionCookie()
    {
        SetCookieHeaderValue authCookie = await LoginAndGetAuthCookieAsync("false");

        Assert.IsFalse(authCookie.Expires.HasValue, "An unticked box should give a session cookie that ends when the browser closes.");
    }
}
