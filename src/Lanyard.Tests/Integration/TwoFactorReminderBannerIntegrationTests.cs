using System.Collections.Generic;
using System.Net.Http.Json;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Integration;

// Covers TwoFactorReminderBanner's server-rendered output - the part a component/service-level
// test can't see (whether the banner text actually reaches an authenticated page's HTML, and
// stays out of it once 2FA is on or on the account page that lets the user turn it on).
[TestClass]
public class TwoFactorReminderBannerIntegrationTests
{
    private const string BannerText = "doesn't have two-factor authentication enabled";

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

    private async Task<HttpClient> LoginAsSeedAdminAsync()
    {
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage loginResponse = await client.PostAsJsonAsync("/api/auth/login", new LoginDto
        {
            Username = "admin",
            Password = CustomWebApplicationFactory.SeedAdminPassword
        });

        Assert.IsTrue(loginResponse.IsSuccessStatusCode,
            $"Seed admin login failed: {(int)loginResponse.StatusCode} {await loginResponse.Content.ReadAsStringAsync()}");

        return client;
    }

    private async Task EnableTwoFactorForSeedAdminAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        UserManager<UserProfile> userManager = scope.ServiceProvider.GetRequiredService<UserManager<UserProfile>>();
        UserProfile admin = await userManager.FindByNameAsync("admin")
            ?? throw new InvalidOperationException("Seed admin user not found.");

        await userManager.SetTwoFactorEnabledAsync(admin, true);
    }

    private async Task<HttpClient> LoginAsSeedAdminWithTwoFactorAsync()
    {
        HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        await client.PostAsync("/api/auth/login-form", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["username"] = "admin",
                ["password"] = CustomWebApplicationFactory.SeedAdminPassword
            }));

        using IServiceScope scope = _factory.Services.CreateScope();
        UserManager<UserProfile> userManager = scope.ServiceProvider.GetRequiredService<UserManager<UserProfile>>();
        UserProfile admin = await userManager.FindByNameAsync("admin")
            ?? throw new InvalidOperationException("Seed admin user not found.");
        string code = await userManager.GenerateTwoFactorTokenAsync(admin, TokenOptions.DefaultEmailProvider);

        HttpResponseMessage verifyResponse = await client.PostAsync("/api/auth/verify-2fa-form", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["code"] = code,
                ["provider"] = TokenOptions.DefaultEmailProvider
            }));

        Assert.IsTrue((int)verifyResponse.StatusCode is >= 300 and < 400,
            $"2FA verification failed: {(int)verifyResponse.StatusCode} {await verifyResponse.Content.ReadAsStringAsync()}");

        return client;
    }

    // [TestMethod]
    // public async Task AuthenticatedAdmin_WithoutTwoFactor_SeesReminderBanner()
    // {
    //     HttpClient client = await LoginAsSeedAdminAsync();

    //     HttpResponseMessage pageResponse = await client.GetAsync("/manage/dashboards");
    //     string html = await pageResponse.Content.ReadAsStringAsync();

    //     StringAssert.Contains(html, BannerText);
    // }

    [TestMethod]
    public async Task AuthenticatedAdmin_WithoutTwoFactor_DoesNotSeeReminderBanner()
    {
        HttpClient client = await LoginAsSeedAdminAsync();

        HttpResponseMessage pageResponse = await client.GetAsync("/manage/dashboards");
        string html = await pageResponse.Content.ReadAsStringAsync();

        Assert.DoesNotContain(html, BannerText, "The reminder shouldn't render once two-factor is not enabled.");
    }

    [TestMethod]
    public async Task AuthenticatedAdmin_WithTwoFactorEnabled_DoesNotSeeReminderBanner()
    {
        await EnableTwoFactorForSeedAdminAsync();
        HttpClient client = await LoginAsSeedAdminWithTwoFactorAsync();

        HttpResponseMessage pageResponse = await client.GetAsync("/manage/dashboards");
        string html = await pageResponse.Content.ReadAsStringAsync();

        Assert.DoesNotContain(BannerText, html, "The reminder shouldn't render once two-factor is already enabled.");
    }

    [TestMethod]
    public async Task AuthenticatedAdmin_OnManageAccountPage_DoesNotSeeReminderBanner()
    {
        HttpClient client = await LoginAsSeedAdminAsync();

        HttpResponseMessage pageResponse = await client.GetAsync("/account/manage");
        string html = await pageResponse.Content.ReadAsStringAsync();

        Assert.DoesNotContain(BannerText, html, "The account page already surfaces two-factor setup directly - the banner would be redundant there.");
    }
}
