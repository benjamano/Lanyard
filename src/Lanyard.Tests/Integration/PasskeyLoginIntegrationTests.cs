using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Integration;

// Face ID / fingerprint sign-in, end to end over HTTP. The WebAuthn ceremonies keep their state
// in a cookie between two requests and the sign-in has to go through the same location checks
// as a password, so this is the wiring a PasskeyService unit test can't see.
// SoftwarePasskeyAuthenticator plays the phone.
[TestClass]
public class PasskeyLoginIntegrationTests
{
    private const string Origin = "http://localhost";
    private const string IPhoneUserAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1";

    private CustomWebApplicationFactory _factory = null!;
    private WebApplicationFactory<Program> _app = null!;

    [TestInitialize]
    public void Setup()
    {
        _factory = new CustomWebApplicationFactory();

        // See InMemoryPasskeyUserStore: EF InMemory can't store Identity's passkey rows.
        _app = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<InMemoryPasskeyUserStore.PasskeyTable>();
            services.AddScoped<IUserStore<UserProfile>, InMemoryPasskeyUserStore>();
        }));
    }

    [TestCleanup]
    public void Cleanup()
    {
        _app.Dispose();
        _factory.Dispose();
    }

    // Browsers send an Origin header on every POST, and Identity checks the origin the phone signed
    // against it - so the test client has to send one too, the same as the page would.
    private HttpClient CreateClient()
    {
        HttpClient client = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Origin", Origin);
        return client;
    }

    private static async Task SignInWithPasswordAsync(HttpClient client)
    {
        HttpResponseMessage response = await client.PostAsync("/api/auth/login-form", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["username"] = "admin",
                ["password"] = CustomWebApplicationFactory.SeedAdminPassword,
                ["locationId"] = ApplicationDbContext.SeedIpswichLocationId.ToString()
            }));

        Assert.AreEqual("/", response.Headers.Location?.OriginalString, "Password sign-in should succeed as the arrange step.");
    }

    // Adds a passkey to the seed admin the way Account Management does, from a signed-in session.
    private async Task<HttpResponseMessage> RegisterPasskeyAsync(SoftwarePasskeyAuthenticator authenticator)
    {
        HttpClient client = CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(IPhoneUserAgent);
        await SignInWithPasswordAsync(client);

        HttpResponseMessage optionsResponse = await client.PostAsync("/api/auth/passkey/creation-options", null);
        Assert.AreEqual(HttpStatusCode.OK, optionsResponse.StatusCode, await optionsResponse.Content.ReadAsStringAsync());

        string credentialJson = authenticator.CreateCredentialJson(await optionsResponse.Content.ReadAsStringAsync());

        return await client.PostAsync("/api/auth/passkey/register", new StringContent(
            JsonSerializer.Serialize(new { credentialJson }), Encoding.UTF8, "application/json"));
    }

    private async Task RegisterPasskeyOrFailAsync(SoftwarePasskeyAuthenticator authenticator)
    {
        HttpResponseMessage response = await RegisterPasskeyAsync(authenticator);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"Arrange step: adding the passkey should succeed. {await response.Content.ReadAsStringAsync()}");
    }

    // The login page's "Sign in with Face ID or fingerprint", from a browser with no session.
    private static async Task<HttpResponseMessage> SignInWithPasskeyAsync(HttpClient client, SoftwarePasskeyAuthenticator authenticator, int? locationId)
    {
        HttpResponseMessage optionsResponse = await client.PostAsync("/api/auth/passkey/request-options", null);
        Assert.AreEqual(HttpStatusCode.OK, optionsResponse.StatusCode);

        Dictionary<string, string> form = new()
        {
            ["credentialJson"] = authenticator.GetAssertionJson(await optionsResponse.Content.ReadAsStringAsync()),
            ["rememberMe"] = "true",
        };

        if (locationId is not null)
        {
            form["locationId"] = locationId.Value.ToString();
        }

        return await client.PostAsync("/api/auth/passkey-login-form", new FormUrlEncodedContent(form));
    }

    private static async Task<bool> IsSignedInAsync(HttpClient client)
    {
        HttpResponseMessage response = await client.GetAsync("/manage/dashboards");
        return response.StatusCode == HttpStatusCode.OK;
    }

    [TestMethod]
    public async Task RequestOptions_Anonymous_ReturnsChallengeWithoutNamingAccounts()
    {
        HttpClient client = CreateClient();

        HttpResponseMessage response = await client.PostAsync("/api/auth/passkey/request-options", null);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        JsonNode options = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.IsFalse(string.IsNullOrEmpty(options["challenge"]?.GetValue<string>()), "The options must carry a challenge to sign.");
        Assert.AreEqual("localhost", options["rpId"]?.GetValue<string>());
        Assert.AreEqual("required", options["userVerification"]?.GetValue<string>(), "The phone must check it's the owner.");

        JsonArray? allowCredentials = options["allowCredentials"] as JsonArray;
        Assert.IsTrue(allowCredentials is null || allowCredentials.Count == 0, "An anonymous caller mustn't be sent anyone's credential ids.");
    }

    [TestMethod]
    public async Task CreationOptions_NotSignedIn_IsRejected()
    {
        HttpClient client = CreateClient();

        HttpResponseMessage response = await client.PostAsync("/api/auth/passkey/creation-options", null);

        Assert.AreNotEqual(HttpStatusCode.OK, response.StatusCode, "Only a signed-in user can add a passkey.");
    }

    [TestMethod]
    public async Task CreationOptions_SignedIn_AsksForDiscoverableVerifiedCredential()
    {
        HttpClient client = CreateClient();
        await SignInWithPasswordAsync(client);

        HttpResponseMessage response = await client.PostAsync("/api/auth/passkey/creation-options", null);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        JsonNode options = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.AreEqual("admin", options["user"]?["name"]?.GetValue<string>());
        Assert.AreEqual("required", options["authenticatorSelection"]?["residentKey"]?.GetValue<string>(), "Needed to sign in without typing a username.");
        Assert.AreEqual("required", options["authenticatorSelection"]?["userVerification"]?.GetValue<string>());
    }

    [TestMethod]
    public async Task Register_ThenSignInWithPasskey_SignsInWithoutPassword()
    {
        using SoftwarePasskeyAuthenticator authenticator = new(Origin);

        HttpResponseMessage registerResponse = await RegisterPasskeyAsync(authenticator);

        Assert.AreEqual(HttpStatusCode.OK, registerResponse.StatusCode, await registerResponse.Content.ReadAsStringAsync());
        StringAssert.Contains(await registerResponse.Content.ReadAsStringAsync(), "iPhone", "The passkey should be named after the device it was made on.");

        HttpClient phone = CreateClient();
        HttpResponseMessage signInResponse = await SignInWithPasskeyAsync(phone, authenticator, ApplicationDbContext.SeedIpswichLocationId);

        Assert.AreEqual("/", signInResponse.Headers.Location?.OriginalString);
        Assert.IsTrue(await IsSignedInAsync(phone), "A verified passkey should sign the account in.");
    }

    [TestMethod]
    public async Task SignInWithPasskey_TwoFactorEnabled_SkipsTheCodeStep()
    {
        using SoftwarePasskeyAuthenticator authenticator = new(Origin);
        await RegisterPasskeyOrFailAsync(authenticator);

        using (IServiceScope scope = _app.Services.CreateScope())
        {
            UserManager<UserProfile> userManager = scope.ServiceProvider.GetRequiredService<UserManager<UserProfile>>();
            UserProfile admin = (await userManager.FindByNameAsync("admin"))!;
            await userManager.SetTwoFactorEnabledAsync(admin, true);
        }

        HttpClient phone = CreateClient();
        HttpResponseMessage signInResponse = await SignInWithPasskeyAsync(phone, authenticator, ApplicationDbContext.SeedIpswichLocationId);

        Assert.AreEqual("/", signInResponse.Headers.Location?.OriginalString, "A user-verified passkey already covers both factors.");
        Assert.IsTrue(await IsSignedInAsync(phone));
    }

    [TestMethod]
    public async Task SignInWithPasskey_NoLocation_DoesNotSignIn()
    {
        using SoftwarePasskeyAuthenticator authenticator = new(Origin);
        await RegisterPasskeyOrFailAsync(authenticator);

        HttpClient phone = CreateClient();
        HttpResponseMessage signInResponse = await SignInWithPasskeyAsync(phone, authenticator, locationId: null);

        StringAssert.StartsWith(signInResponse.Headers.Location?.OriginalString, "/login?error=");
        Assert.IsFalse(await IsSignedInAsync(phone), "The location checks apply to a passkey just as to a password.");
    }

    [TestMethod]
    public async Task SignInWithPasskey_UnregisteredPasskey_DoesNotSignIn()
    {
        using SoftwarePasskeyAuthenticator registered = new(Origin);
        await RegisterPasskeyOrFailAsync(registered);

        // Same account handle, but a key the server has never seen.
        using SoftwarePasskeyAuthenticator stranger = new(Origin);
        stranger.CreateCredentialJson(await CreationOptionsForUserHandleAsync(registered.UserHandle!));

        HttpClient phone = CreateClient();
        HttpResponseMessage signInResponse = await SignInWithPasskeyAsync(phone, stranger, ApplicationDbContext.SeedIpswichLocationId);

        StringAssert.StartsWith(signInResponse.Headers.Location?.OriginalString, "/login?error=");
        Assert.IsFalse(await IsSignedInAsync(phone));
    }

    [TestMethod]
    public async Task SignInWithPasskey_WithoutRequestingOptionsFirst_DoesNotSignIn()
    {
        using SoftwarePasskeyAuthenticator authenticator = new(Origin);
        await RegisterPasskeyOrFailAsync(authenticator);

        // A challenge from another browser's session: this one never asked for one, so it has no
        // state cookie to check the signature against.
        HttpResponseMessage elsewhere = await CreateClient().PostAsync("/api/auth/passkey/request-options", null);
        string assertion = authenticator.GetAssertionJson(await elsewhere.Content.ReadAsStringAsync());

        HttpClient phone = CreateClient();
        HttpResponseMessage signInResponse = await phone.PostAsync("/api/auth/passkey-login-form", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["credentialJson"] = assertion,
                ["locationId"] = ApplicationDbContext.SeedIpswichLocationId.ToString()
            }));

        StringAssert.StartsWith(signInResponse.Headers.Location?.OriginalString, "/login?error=");
        Assert.IsFalse(await IsSignedInAsync(phone), "A replayed assertion must not sign anyone in.");
    }

    [TestMethod]
    public async Task Register_WithoutUserVerification_IsRejected()
    {
        // A passkey stands in for the password and the 2FA code only because the phone checked
        // the face/fingerprint/PIN. One made without that check must not be stored.
        using SoftwarePasskeyAuthenticator authenticator = new(Origin) { UserVerified = false };

        HttpResponseMessage registerResponse = await RegisterPasskeyAsync(authenticator);

        Assert.AreEqual(HttpStatusCode.BadRequest, registerResponse.StatusCode);
    }

    private async Task<string> CreationOptionsForUserHandleAsync(string userHandle)
    {
        HttpClient client = CreateClient();
        await SignInWithPasswordAsync(client);

        HttpResponseMessage response = await client.PostAsync("/api/auth/passkey/creation-options", null);
        JsonNode options = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        options["user"]!["id"] = userHandle;
        return options.ToJsonString();
    }
}
