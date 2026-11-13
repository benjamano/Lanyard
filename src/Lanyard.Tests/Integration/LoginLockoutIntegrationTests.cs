using System.Net;
using System.Net.Http.Json;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Integration;

// Someone who only knows a username must not be able to lock that person out from one machine,
// and the sign-in answer must not say whether a username exists.
[TestClass]
public class LoginLockoutIntegrationTests
{
    private const string TestIpHeader = "X-Test-Remote-Ip";
    private const string WrongPassword = "Not-The-Password-1!";

    private CustomWebApplicationFactory _baseFactory = null!;
    private WebApplicationFactory<Program> _factory = null!;

    [TestInitialize]
    public void Setup()
    {
        _baseFactory = new CustomWebApplicationFactory();

        // TestServer has no remote address; this lets each request say which IP it comes from.
        // Added as the very first middleware, so the rate limiter and the controller both see it.
        _factory = _baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IStartupFilter, TestRemoteIpStartupFilter>()));
        _factory.CreateClient();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _factory.Dispose();
        _baseFactory.Dispose();
    }

    private async Task<HttpResponseMessage> LoginAsync(string ip, string username, string password)
    {
        HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestIpHeader, ip);

        return await client.PostAsJsonAsync("/api/auth/login", new LoginDto
        {
            Username = username,
            Password = password,
            LocationId = ApplicationDbContext.SeedIpswichLocationId
        });
    }

    private async Task<bool> IsLockedOutAsync(string username)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        UserManager<UserProfile> userManager = scope.ServiceProvider.GetRequiredService<UserManager<UserProfile>>();
        return await userManager.IsLockedOutAsync((await userManager.FindByNameAsync(username))!);
    }

    [TestMethod]
    public async Task ManyWrongPasswordsFromOneAddress_DoNotLockTheAccount()
    {
        for (int i = 0; i < 10; i++)
        {
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await LoginAsync("203.0.113.10", "admin", WrongPassword)).StatusCode);
        }

        Assert.IsFalse(await IsLockedOutAsync("admin"), "One address locked the account.");

        HttpResponseMessage fromElsewhere = await LoginAsync("198.51.100.20", "admin", CustomWebApplicationFactory.SeedAdminPassword);
        Assert.IsTrue(fromElsewhere.IsSuccessStatusCode, $"The real user couldn't sign in: {await fromElsewhere.Content.ReadAsStringAsync()}");
    }

    [TestMethod]
    public async Task ThrottledAddress_IsRefusedEvenWithTheRightPassword()
    {
        for (int i = 0; i < 4; i++)
        {
            await LoginAsync("203.0.113.11", "admin", WrongPassword);
        }

        HttpResponseMessage response = await LoginAsync("203.0.113.11", "admin", CustomWebApplicationFactory.SeedAdminPassword);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task FailuresFromSeveralAddresses_StillLockTheAccount()
    {
        // Identity's own lockout still protects against guessing spread over many machines.
        for (int i = 0; i < 4; i++)
        {
            await LoginAsync("203.0.113.12", "admin", WrongPassword);
        }

        await LoginAsync("203.0.113.13", "admin", WrongPassword);

        Assert.IsTrue(await IsLockedOutAsync("admin"));
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await LoginAsync("198.51.100.21", "admin", CustomWebApplicationFactory.SeedAdminPassword)).StatusCode);
    }

    [TestMethod]
    public async Task UnknownUsername_WrongPassword_AndLockedAccount_AllGetTheSameAnswer()
    {
        string unknown = await (await LoginAsync("203.0.113.30", "nobody-by-this-name", WrongPassword)).Content.ReadAsStringAsync();
        string wrong = await (await LoginAsync("203.0.113.31", "admin", WrongPassword)).Content.ReadAsStringAsync();

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            UserManager<UserProfile> userManager = scope.ServiceProvider.GetRequiredService<UserManager<UserProfile>>();
            UserProfile admin = (await userManager.FindByNameAsync("admin"))!;
            await userManager.SetLockoutEndDateAsync(admin, DateTimeOffset.UtcNow.AddMinutes(15));
        }

        string locked = await (await LoginAsync("203.0.113.32", "admin", WrongPassword)).Content.ReadAsStringAsync();

        Assert.AreEqual(unknown, wrong);
        Assert.AreEqual(unknown, locked);
        StringAssert.Contains(unknown, "Invalid username or password");
    }

    private sealed class TestRemoteIpStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue(TestIpHeader, out Microsoft.Extensions.Primitives.StringValues ip)
                    && IPAddress.TryParse(ip.ToString(), out IPAddress? address))
                {
                    context.Connection.RemoteIpAddress = address;
                }

                await nextMiddleware();
            });

            next(app);
        };
    }
}
