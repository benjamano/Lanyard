using System.Net;
using System.Net.Http.Json;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Contracts;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Integration;

// Policy PDFs through the real pipeline. The point is Security Issue 9.1: api/files/download lets
// in anyone holding the kiosk secret (and, in this unconfigured test host, anyone at all) as the
// unfiltered system tenant, so a policy's PDF must not be reachable there, and its own endpoint
// must only answer a signed-in person it's assigned to.
[TestClass]
public class PolicyFileIntegrationTests
{
    private const string Password = "Policy-Reader-Pw1!";
    private const string SharedSecret = "integration-test-kiosk-secret";

    private static readonly byte[] PdfBytes = "%PDF-1.7\nfire policy\n%%EOF"u8.ToArray();

    private CustomWebApplicationFactory _baseFactory = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private Guid _fileId;
    private Guid _versionId;

    [TestInitialize]
    public async Task Setup()
    {
        _baseFactory = new CustomWebApplicationFactory();
        _factory = _baseFactory.WithWebHostBuilder(builder => builder.UseSetting("Clients:SharedSecret", SharedSecret));
        _factory.CreateClient();

        UserProfile reader = await AddUserAsync("policyreader");
        await AddUserAsync("policyoutsider");

        string path = Path.GetTempFileName();
        File.WriteAllBytes(path, PdfBytes);

        _fileId = Guid.NewGuid();
        _versionId = Guid.NewGuid();
        Guid policyId = Guid.NewGuid();

        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationDbContext ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        ctx.FileMetadata.Add(new FileMetadata
        {
            Id = _fileId,
            CompanyId = ApplicationDbContext.SeedPlay2DayCompanyId,
            FileName = "Fire policy.pdf",
            FilePath = path,
            FileSize = PdfBytes.Length,
            ContentType = "application/pdf",
            IsRestricted = true
        });
        ctx.Policies.Add(new Policy
        {
            Id = policyId,
            CompanyId = ApplicationDbContext.SeedPlay2DayCompanyId,
            Name = "Fire policy",
            LocationId = ApplicationDbContext.SeedIpswichLocationId,
            CurrentVersionNumber = 1,
            RequiredVersionNumber = 1
        });
        ctx.PolicyVersions.Add(new PolicyVersion
        {
            Id = _versionId,
            CompanyId = ApplicationDbContext.SeedPlay2DayCompanyId,
            PolicyId = policyId,
            VersionNumber = 1,
            FileMetadataId = _fileId,
            FileSha256 = "hash",
            FileName = "Fire policy.pdf",
            PublishedDate = DateTime.UtcNow
        });
        // Only the reader is assigned; the outsider works at the same location but isn't.
        ctx.PolicyAssignments.Add(new PolicyAssignment
        {
            Id = Guid.NewGuid(),
            CompanyId = ApplicationDbContext.SeedPlay2DayCompanyId,
            PolicyId = policyId,
            UserId = reader.Id,
            AssignedDate = DateTime.UtcNow,
            IsActive = true
        });
        await ctx.SaveChangesAsync();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _factory.Dispose();
        _baseFactory.Dispose();
    }

    private async Task<UserProfile> AddUserAsync(string userName)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationDbContext ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        UserManager<UserProfile> userManager = scope.ServiceProvider.GetRequiredService<UserManager<UserProfile>>();

        UserProfile user = new() { UserName = userName, Email = $"{userName}@example.com", FirstName = userName, LastName = "Test", EmailConfirmed = true };
        IdentityResult created = await userManager.CreateAsync(user, Password);
        Assert.IsTrue(created.Succeeded, string.Join(", ", created.Errors.Select(e => e.Description)));
        await userManager.AddToRoleAsync(user, "Staff");

        ctx.UserLocationMemberships.Add(new UserLocationMembership { UserId = user.Id, LocationId = ApplicationDbContext.SeedIpswichLocationId });
        await ctx.SaveChangesAsync();

        return user;
    }

    private async Task<HttpClient> SignedInAsAsync(string userName)
    {
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage login = await client.PostAsJsonAsync("/api/auth/login", new LoginDto
        {
            Username = userName,
            Password = Password,
            LocationId = ApplicationDbContext.SeedIpswichLocationId
        });
        Assert.IsTrue(login.IsSuccessStatusCode, $"Login failed: {(int)login.StatusCode} {await login.Content.ReadAsStringAsync()}");

        return client;
    }

    private string FileUrl => $"/api/policies/versions/{_versionId}/file";

    [TestMethod]
    public async Task AssignedStaff_GetThePdfInline_AndUncached()
    {
        HttpClient client = await SignedInAsAsync("policyreader");

        HttpResponseMessage response = await client.GetAsync(FileUrl);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual("inline", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.IsTrue(response.Headers.CacheControl?.NoStore == true);
        CollectionAssert.AreEqual(PdfBytes, await response.Content.ReadAsByteArrayAsync());
    }

    [TestMethod]
    public async Task StaffItIsntAssignedTo_GetNotFound()
    {
        HttpClient client = await SignedInAsAsync("policyoutsider");

        HttpResponseMessage response = await client.GetAsync(FileUrl);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task KioskSecretWithoutASignIn_IsRefused()
    {
        HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using HttpRequestMessage request = new(HttpMethod.Get, FileUrl);
        request.Headers.Add(ClientConnectionQuery.SecretHeader, SharedSecret);

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.AreNotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreNotEqual("application/pdf", response.Content.Headers.ContentType?.MediaType);
    }

    private async Task<HttpResponseMessage> AcknowledgeAsync(HttpClient client, bool withToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, $"/api/policies/versions/{_versionId}/acknowledge");
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 Version/18.0 Mobile/15E148 Safari/604.1");

        if (withToken)
        {
            AntiforgeryTokenResponse? token = await client.GetFromJsonAsync<AntiforgeryTokenResponse>("/api/policies/antiforgery");
            request.Headers.Add(token!.HeaderName, token.Token);
        }

        return await client.SendAsync(request);
    }

    private sealed record AntiforgeryTokenResponse(string HeaderName, string Token);

    [TestMethod]
    public async Task Acknowledge_WithTheToken_RecordsTheBrowserItCameFrom()
    {
        HttpClient client = await SignedInAsAsync("policyreader");

        HttpResponseMessage response = await AcknowledgeAsync(client, withToken: true);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());

        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationDbContext ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        PolicyAcknowledgement record = ctx.PolicyAcknowledgements.Single();

        Assert.AreEqual(_versionId, record.PolicyVersionId);
        StringAssert.Contains(record.UserAgent, "iPhone");
        StringAssert.Contains(record.DeviceLabel, "iPhone");
        // No IP address assertion: the in-memory test server has no remote connection to report.
    }

    [TestMethod]
    public async Task Acknowledge_WithoutTheToken_IsRejected()
    {
        HttpClient client = await SignedInAsAsync("policyreader");

        HttpResponseMessage response = await AcknowledgeAsync(client, withToken: false);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);

        using IServiceScope scope = _factory.Services.CreateScope();
        Assert.AreEqual(0, scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().PolicyAcknowledgements.Count());
    }

    [TestMethod]
    public async Task Acknowledge_ByStaffItIsntAssignedTo_IsRejected()
    {
        HttpClient client = await SignedInAsAsync("policyoutsider");

        HttpResponseMessage response = await AcknowledgeAsync(client, withToken: true);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task FileManagerDownload_WithTheKioskSecret_DoesNotServeTheRestrictedFile()
    {
        HttpClient client = _factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Get, $"/api/files/download/{_fileId}");
        request.Headers.Add(ClientConnectionQuery.SecretHeader, SharedSecret);

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }
}
