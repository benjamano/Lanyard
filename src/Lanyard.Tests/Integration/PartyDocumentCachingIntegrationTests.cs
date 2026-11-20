using System.Net;
using System.Net.Http.Json;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Integration;

// The party printout PDFs are built fresh from the saved template on every request. Without a
// no-store header Firefox kept showing a preview from before the artwork was replaced, even on
// reload, so the response must tell the browser never to keep it.
[TestClass]
public class PartyDocumentCachingIntegrationTests
{
    private const string Password = "Printout-Cache-Test-Pw1!";

    private CustomWebApplicationFactory _factory = null!;

    [TestInitialize]
    public async Task Setup()
    {
        _factory = new CustomWebApplicationFactory();
        _factory.CreateClient();

        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationDbContext ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        UserManager<UserProfile> userManager = scope.ServiceProvider.GetRequiredService<UserManager<UserProfile>>();

        UserProfile user = new() { UserName = "printoutadmin", Email = "printoutadmin@example.com", FirstName = "Printout", LastName = "Admin", EmailConfirmed = true };
        IdentityResult created = await userManager.CreateAsync(user, Password);
        Assert.IsTrue(created.Succeeded, string.Join(", ", created.Errors.Select(e => e.Description)));
        await userManager.AddToRoleAsync(user, "Admin");

        ctx.UserLocationMemberships.Add(new UserLocationMembership { UserId = user.Id, LocationId = ApplicationDbContext.SeedIpswichLocationId });
        await ctx.SaveChangesAsync();
    }

    [TestCleanup]
    public void Cleanup() => _factory.Dispose();

    [TestMethod]
    [DataRow("reserved-card")]
    [DataRow("voucher")]
    [DataRow("feedback-form")]
    public async Task Preview_IsNeverStoredByTheBrowser(string document)
    {
        HttpClient client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        HttpResponseMessage login = await client.PostAsJsonAsync("/api/auth/login", new LoginDto
        {
            Username = "printoutadmin",
            Password = Password,
            LocationId = ApplicationDbContext.SeedIpswichLocationId
        });
        Assert.IsTrue(login.IsSuccessStatusCode, $"{(int)login.StatusCode} {await login.Content.ReadAsStringAsync()}");

        HttpResponseMessage response = await client.GetAsync($"/api/parties/printouts/{document}/preview");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.IsTrue(response.Headers.CacheControl?.NoStore == true, $"Cache-Control was '{response.Headers.CacheControl}'");
    }
}
