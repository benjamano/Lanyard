using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Integration;

// Security Issue 9.2: a staff member's right-to-work and ID documents used to sit in the File
// Manager root, where api/files/list and api/files/download handed them to anyone signed in (and
// to any kiosk). Through the real pipeline: colleagues and kiosks get nothing, while the person
// themselves, their Manager and an Admin can open the file from the staff documents endpoint.
[TestClass]
public class StaffDocumentAccessIntegrationTests
{
    private const string Password = "Staff-Document-Test-Pw1!";

    private CustomWebApplicationFactory _factory = null!;
    private Guid _fileId;
    private Guid _staffDocumentId;

    [TestInitialize]
    public async Task Setup()
    {
        _factory = new CustomWebApplicationFactory();
        _factory.CreateClient();

        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationDbContext ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        UserManager<UserProfile> userManager = scope.ServiceProvider.GetRequiredService<UserManager<UserProfile>>();

        UserProfile amy = await CreateUserAsync(userManager, ctx, "amy", role: null);
        await CreateUserAsync(userManager, ctx, "bob", role: null);
        await CreateUserAsync(userManager, ctx, "mo", role: "Manager");

        string path = Path.GetTempFileName();
        File.WriteAllBytes(path, "%PDF-1.7 passport"u8.ToArray());

        FileMetadata file = new()
        {
            Id = Guid.NewGuid(),
            FileName = "passport.pdf",
            FilePath = path,
            FileSize = 17,
            ContentType = "application/pdf",
            UploadedAt = DateTime.UtcNow,
            UploadedBy = "mo",
            IsActive = true,
            IsRestricted = true,
            CompanyId = ApplicationDbContext.SeedPlay2DayCompanyId
        };

        StaffDocumentType type = new() { Id = Guid.NewGuid(), CompanyId = ApplicationDbContext.SeedPlay2DayCompanyId, Name = "Passport", IsActive = true };

        StaffDocument document = new()
        {
            Id = Guid.NewGuid(),
            UserId = amy.Id,
            StaffDocumentTypeId = type.Id,
            FileMetadataId = file.Id,
            UploadedDate = DateTime.UtcNow,
            UploadedByUserId = amy.Id,
            IsActive = true
        };

        ctx.FileMetadata.Add(file);
        ctx.StaffDocumentTypes.Add(type);
        ctx.StaffDocuments.Add(document);
        await ctx.SaveChangesAsync();

        _fileId = file.Id;
        _staffDocumentId = document.Id;
    }

    [TestCleanup]
    public void Cleanup()
    {
        _factory.Dispose();
    }

    private static async Task<UserProfile> CreateUserAsync(UserManager<UserProfile> userManager, ApplicationDbContext ctx, string userName, string? role)
    {
        UserProfile user = new() { UserName = userName, Email = $"{userName}@example.com", FirstName = userName, LastName = "Test", EmailConfirmed = true };
        IdentityResult created = await userManager.CreateAsync(user, Password);
        Assert.IsTrue(created.Succeeded, string.Join(", ", created.Errors.Select(e => e.Description)));

        if (role is not null)
        {
            await userManager.AddToRoleAsync(user, role);
        }

        ctx.UserLocationMemberships.Add(new UserLocationMembership { UserId = user.Id, LocationId = ApplicationDbContext.SeedIpswichLocationId });
        await ctx.SaveChangesAsync();

        return user;
    }

    private async Task<HttpClient> SignedInAsync(string userName, string password = Password)
    {
        HttpClient client = _factory.CreateClient();
        HttpResponseMessage login = await client.PostAsJsonAsync("/api/auth/login",
            new LoginDto { Username = userName, Password = password, LocationId = ApplicationDbContext.SeedIpswichLocationId });
        Assert.IsTrue(login.IsSuccessStatusCode, $"{userName}: {(int)login.StatusCode} {await login.Content.ReadAsStringAsync()}");

        return client;
    }

    [TestMethod]
    public async Task Colleague_CannotListOrDownloadTheFile()
    {
        HttpClient bob = await SignedInAsync("bob");

        Assert.AreEqual(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/files/download/{_fileId}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/staff-documents/{_staffDocumentId}/file")).StatusCode);

        string listed = await bob.GetStringAsync("/api/files/list");
        Assert.IsFalse(listed.Contains(_fileId.ToString(), StringComparison.OrdinalIgnoreCase), listed);
    }

    [TestMethod]
    public async Task Kiosk_CannotDownloadTheFile()
    {
        HttpClient client = _factory.CreateClient();

        using HttpRequestMessage download = new(HttpMethod.Get, $"/api/files/download/{_fileId}");
        download.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await KioskTestSupport.RegisterKioskWithCredentialAsync(_factory, Guid.NewGuid()));
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.SendAsync(download)).StatusCode);

        // The staff documents endpoint is cookie-only: a kiosk's bearer credential isn't a sign-in.
        using HttpRequestMessage direct = new(HttpMethod.Get, $"/api/staff-documents/{_staffDocumentId}/file");
        direct.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await KioskTestSupport.RegisterKioskWithCredentialAsync(_factory, Guid.NewGuid()));
        HttpResponseMessage response = await client.SendAsync(direct);
        Assert.AreNotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    [DataRow("amy", Password, DisplayName = "The person themselves")]
    [DataRow("mo", Password, DisplayName = "Their Manager")]
    [DataRow("admin", CustomWebApplicationFactory.SeedAdminPassword, DisplayName = "An Admin")]
    public async Task OwnerManagerAndAdmin_CanOpenTheFile(string userName, string password)
    {
        HttpClient client = await SignedInAsync(userName, password);

        HttpResponseMessage response = await client.GetAsync($"/api/staff-documents/{_staffDocumentId}/file");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("%PDF-1.7 passport", await response.Content.ReadAsStringAsync());
        Assert.IsTrue(response.Headers.CacheControl!.NoStore);
        Assert.IsTrue(response.Headers.TryGetValues("X-Content-Type-Options", out IEnumerable<string>? nosniff) && nosniff.Contains("nosniff"));
    }
}
