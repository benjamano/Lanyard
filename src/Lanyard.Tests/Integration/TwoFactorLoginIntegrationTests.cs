using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Integration;

// Covers the routing/redirect glue a service-level test can't see: PasswordSignInAsync returning
// RequiresTwoFactor must divert the real login-form POST to /login/verify-2fa instead of signing
// the user in, and the protected app must stay unreachable until the code is verified. There's no
// HTTP endpoint for enabling 2FA (it's UI-only via ISecurityService), so these tests flip
// TwoFactorEnabled directly through UserManager as an arrange step and drive only the login/verify
// flow itself over HTTP.
[TestClass]
public class TwoFactorLoginIntegrationTests
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

    private async Task<UserProfile> EnableEmailTwoFactorForSeedAdminAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        UserManager<UserProfile> userManager = scope.ServiceProvider.GetRequiredService<UserManager<UserProfile>>();
        UserProfile admin = await userManager.FindByNameAsync("admin")
            ?? throw new InvalidOperationException("Seed admin user not found.");

        await userManager.SetTwoFactorEnabledAsync(admin, true);
        return admin;
    }

    private async Task<string> GenerateEmailCodeAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        UserManager<UserProfile> userManager = scope.ServiceProvider.GetRequiredService<UserManager<UserProfile>>();
        UserProfile admin = await userManager.FindByNameAsync("admin")
            ?? throw new InvalidOperationException("Seed admin user not found.");

        return await userManager.GenerateTwoFactorTokenAsync(admin, TokenOptions.DefaultEmailProvider);
    }

    [TestMethod]
    public async Task Login_WithTwoFactorEnabledUser_RedirectsToVerifyPage_NotSignedIn()
    {
        await EnableEmailTwoFactorForSeedAdminAsync();

        HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        HttpResponseMessage loginResponse = await client.PostAsync("/api/auth/login-form", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["username"] = "admin",
                ["password"] = CustomWebApplicationFactory.SeedAdminPassword
            }));

        Assert.IsTrue((int)loginResponse.StatusCode is >= 300 and < 400, $"Expected a redirect, got {(int)loginResponse.StatusCode}.");
        Assert.IsNotNull(loginResponse.Headers.Location);
        StringAssert.StartsWith(loginResponse.Headers.Location!.OriginalString, "/login/verify-2fa");

        HttpResponseMessage pageResponse = await client.GetAsync("/manage/dashboards");
        Assert.IsTrue((int)pageResponse.StatusCode is >= 300 and < 400, "The account should not be signed in until the 2FA code is verified.");
    }

    [TestMethod]
    public async Task VerifyTwoFactorForm_WrongCode_DoesNotSignIn()
    {
        await EnableEmailTwoFactorForSeedAdminAsync();

        HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        await client.PostAsync("/api/auth/login-form", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["username"] = "admin",
                ["password"] = CustomWebApplicationFactory.SeedAdminPassword
            }));

        HttpResponseMessage verifyResponse = await client.PostAsync("/api/auth/verify-2fa-form", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["code"] = "000000",
                ["provider"] = TokenOptions.DefaultEmailProvider
            }));

        Assert.IsTrue((int)verifyResponse.StatusCode is >= 300 and < 400);

        HttpResponseMessage pageResponse = await client.GetAsync("/manage/dashboards");
        Assert.IsTrue((int)pageResponse.StatusCode is >= 300 and < 400, "A wrong 2FA code must not sign the account in.");
    }

    [TestMethod]
    public async Task VerifyTwoFactorForm_RightCode_CompletesSignIn()
    {
        await EnableEmailTwoFactorForSeedAdminAsync();

        HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        await client.PostAsync("/api/auth/login-form", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["username"] = "admin",
                ["password"] = CustomWebApplicationFactory.SeedAdminPassword
            }));

        string code = await GenerateEmailCodeAsync();

        HttpResponseMessage verifyResponse = await client.PostAsync("/api/auth/verify-2fa-form", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["code"] = code,
                ["provider"] = TokenOptions.DefaultEmailProvider
            }));

        Assert.IsTrue((int)verifyResponse.StatusCode is >= 300 and < 400);

        HttpResponseMessage pageResponse = await client.GetAsync("/manage/dashboards");
        Assert.AreEqual(HttpStatusCode.OK, pageResponse.StatusCode);
    }
}
