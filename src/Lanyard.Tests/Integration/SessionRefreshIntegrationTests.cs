using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using Lanyard.Application.Services.ApplicationRoles;
using Lanyard.Application.Services.Authentication;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Lanyard.Infrastructure.DTO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Integration;

// POST api/auth/refresh-session is what an open tab calls when an admin changes the user's roles,
// before handing the new cookie to its circuit (lanyardAuth.refresh in blazorStart.js). The new
// cookie has to carry the new roles, keep the location/company picked at sign-in, and keep
// "Keep me signed in" - otherwise the refresh would quietly break the session it's updating.
[TestClass]
public class SessionRefreshIntegrationTests
{
    private const string AuthCookieName = ".AspNetCore.Identity.Application";
    private const string AddedRole = "CanPlayAudioClips";

    private CustomWebApplicationFactory _factory = null!;

    [TestInitialize]
    public void Setup() => _factory = new CustomWebApplicationFactory();

    [TestCleanup]
    public void Cleanup() => _factory.Dispose();

    private async Task<HttpClient> SignInAsAdminAsync()
    {
        HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        HttpResponseMessage login = await client.PostAsync("/api/auth/login-form", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = "admin",
            ["password"] = CustomWebApplicationFactory.SeedAdminPassword,
            ["locationId"] = ApplicationDbContext.SeedIpswichLocationId.ToString(),
            ["rememberMe"] = "true"
        }));

        Assert.AreEqual(HttpStatusCode.Redirect, login.StatusCode, "Signing in should succeed.");
        return client;
    }

    private static StringContent JsonBody() => new("{}", Encoding.UTF8, "application/json");

    private AuthenticationTicket ReadAuthCookie(HttpResponseMessage response, out SetCookieHeaderValue cookie)
    {
        Assert.IsTrue(response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? setCookies), "The refresh should re-issue the auth cookie.");

        cookie = SetCookieHeaderValue.ParseList(setCookies!.ToList()).Single(c => c.Name.Value == AuthCookieName);

        CookieAuthenticationOptions options = _factory.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);

        AuthenticationTicket? ticket = options.TicketDataFormat.Unprotect(cookie.Value.Value);
        Assert.IsNotNull(ticket, "The re-issued cookie should be readable by the app.");
        return ticket;
    }

    [TestMethod]
    public async Task RefreshSession_ReissuesCookieWithNewRolesAndKeepsSignInChoices()
    {
        HttpClient client = await SignInAsAdminAsync();

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            UserManager<UserProfile> users = scope.ServiceProvider.GetRequiredService<UserManager<UserProfile>>();
            UserProfile admin = (await users.FindByNameAsync("admin"))!;
            Assert.IsFalse(await users.IsInRoleAsync(admin, AddedRole), "Test setup: admin shouldn't have the role yet.");
            Assert.IsTrue((await users.AddToRoleAsync(admin, AddedRole)).Succeeded);
        }

        HttpResponseMessage response = await client.PostAsync("/api/auth/refresh-session", JsonBody());

        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);

        AuthenticationTicket ticket = ReadAuthCookie(response, out SetCookieHeaderValue cookie);
        ClaimsPrincipal principal = ticket.Principal;

        Assert.IsTrue(principal.IsInRole(AddedRole), "The new cookie should carry the role added after sign-in.");
        Assert.IsTrue(principal.IsInRole("Admin"), "Existing roles stay.");
        Assert.AreEqual(ApplicationDbContext.SeedIpswichLocationId.ToString(), principal.FindFirstValue(LocationClaimTypes.LocationId), "The location picked at sign-in must survive the refresh.");
        Assert.IsNotNull(principal.FindFirstValue(LocationClaimTypes.CompanyId), "The company claim must survive the refresh.");
        Assert.IsTrue(ticket.Properties.IsPersistent, "Keep me signed in must survive the refresh.");
        Assert.IsTrue(cookie.Expires.HasValue && cookie.Expires.Value > DateTimeOffset.UtcNow.AddDays(60), $"Expected the long-lived cookie to be kept, got expiry {cookie.Expires}.");
    }

    [TestMethod]
    public async Task RefreshSession_AfterSecurityStampChange_EndsTheSession()
    {
        HttpClient client = await SignInAsAdminAsync();

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            UserManager<UserProfile> users = scope.ServiceProvider.GetRequiredService<UserManager<UserProfile>>();
            await users.UpdateSecurityStampAsync((await users.FindByNameAsync("admin"))!);
        }

        HttpResponseMessage response = await client.PostAsync("/api/auth/refresh-session", JsonBody());

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode, "A session whose stamp changed (password reset, account disabled) must not be renewed.");
    }

    [TestMethod]
    public async Task RefreshSession_RejectsAFormPost()
    {
        HttpClient client = await SignInAsAdminAsync();

        // What a cross-site page could send without a CORS preflight.
        HttpResponseMessage response = await client.PostAsync("/api/auth/refresh-session", new FormUrlEncodedContent(new Dictionary<string, string>()));

        Assert.AreEqual(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [TestMethod]
    public async Task RefreshSession_SignedOut_IsRefused()
    {
        HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        HttpResponseMessage response = await client.PostAsync("/api/auth/refresh-session", JsonBody());

        Assert.AreNotEqual(HttpStatusCode.NoContent, response.StatusCode);
        Assert.IsFalse(response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? setCookies)
            && setCookies.Any(c => c.StartsWith(AuthCookieName + "=")), "No session should be issued to a signed-out caller.");
    }

    // The trigger side: a role edited on the Users page must tell that user's open tabs.
    [TestMethod]
    public async Task AssigningAndRemovingARole_AsksThatUsersTabsToRefresh()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        IServiceProvider services = scope.ServiceProvider;

        UserManager<UserProfile> users = services.GetRequiredService<UserManager<UserProfile>>();
        SignInManager<UserProfile> signIn = services.GetRequiredService<SignInManager<UserProfile>>();
        UserProfile admin = (await users.FindByNameAsync("admin"))!;

        // Act as the admin, signed in to Ipswich, the way a circuit would.
        ClaimsPrincipal principal = await signIn.CreateUserPrincipalAsync(admin);
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(LocationClaimTypes.LocationId, ApplicationDbContext.SeedIpswichLocationId.ToString()));
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(LocationClaimTypes.CompanyId, ApplicationDbContext.SeedPlay2DayCompanyId.ToString()));
        ((IHostEnvironmentAuthenticationStateProvider)services.GetRequiredService<AuthenticationStateProvider>())
            .SetAuthenticationState(Task.FromResult(new AuthenticationState(principal)));

        List<string> refreshed = [];
        IUserSessionRefreshBus bus = services.GetRequiredService<IUserSessionRefreshBus>();
        bus.RefreshRequested += refreshed.Add;

        try
        {
            ApplicationRolesService roles = services.GetRequiredService<ApplicationRolesService>();

            Result<string> assigned = await roles.AssignRoleToUserAsync(admin.Id, ApplicationDbContext.SeedCanPlayAudioClipsRoleId);
            Assert.IsTrue(assigned.IsSuccess, assigned.Error);
            CollectionAssert.AreEqual(new[] { admin.Id }, refreshed);

            // Already has it: nothing changed, so no refresh.
            await roles.AssignRoleToUserAsync(admin.Id, ApplicationDbContext.SeedCanPlayAudioClipsRoleId);
            Assert.AreEqual(1, refreshed.Count);

            Result<string> removed = await roles.RemoveRoleFromUserAsync(admin.Id, ApplicationDbContext.SeedCanPlayAudioClipsRoleId);
            Assert.IsTrue(removed.IsSuccess, removed.Error);
            CollectionAssert.AreEqual(new[] { admin.Id, admin.Id }, refreshed);
        }
        finally
        {
            bus.RefreshRequested -= refreshed.Add;
        }
    }
}
