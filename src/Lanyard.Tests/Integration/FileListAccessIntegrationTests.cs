using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Integration;

// api/files/list and api/files/folders/list are the File Manager's browse endpoints. Only people
// who manage files may call them, and the response never carries the server's storage path.
[TestClass]
public class FileListAccessIntegrationTests
{
    private const string Password = "File-List-Test-Pw1!";

    private CustomWebApplicationFactory _factory = null!;
    private Guid _fileId;

    [TestInitialize]
    public async Task Setup()
    {
        _factory = new CustomWebApplicationFactory();
        _factory.CreateClient();

        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationDbContext ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        UserManager<UserProfile> userManager = scope.ServiceProvider.GetRequiredService<UserManager<UserProfile>>();

        _fileId = Guid.NewGuid();
        ctx.FileMetadata.Add(new FileMetadata
        {
            Id = _fileId,
            FileName = "rota.pdf",
            FilePath = "/srv/lanyard/uploads/secret-storage-name.pdf",
            CompanyId = ApplicationDbContext.SeedPlay2DayCompanyId
        });
        ctx.Folders.Add(new Folder { Id = Guid.NewGuid(), Name = "Posters", CompanyId = ApplicationDbContext.SeedPlay2DayCompanyId });
        await ctx.SaveChangesAsync();

        await CreateUserAsync(ctx, userManager, "plainstaff", "Staff");
        await CreateUserAsync(ctx, userManager, "filemanager", "Staff", "CanManageFiles");
    }

    [TestCleanup]
    public void Cleanup() => _factory.Dispose();

    private static async Task CreateUserAsync(ApplicationDbContext ctx, UserManager<UserProfile> userManager, string userName, params string[] roles)
    {
        UserProfile user = new() { UserName = userName, Email = $"{userName}@example.com", FirstName = userName, LastName = "Test", EmailConfirmed = true };
        IdentityResult created = await userManager.CreateAsync(user, Password);
        Assert.IsTrue(created.Succeeded, string.Join(", ", created.Errors.Select(e => e.Description)));

        foreach (string role in roles)
        {
            await userManager.AddToRoleAsync(user, role);
        }

        ctx.UserLocationMemberships.Add(new UserLocationMembership { UserId = user.Id, LocationId = ApplicationDbContext.SeedIpswichLocationId });
        await ctx.SaveChangesAsync();
    }

    private async Task<HttpClient> SignInAsync(string userName, string password)
    {
        HttpClient client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        HttpResponseMessage login = await client.PostAsJsonAsync("/api/auth/login", new LoginDto
        {
            Username = userName,
            Password = password,
            LocationId = ApplicationDbContext.SeedIpswichLocationId
        });
        Assert.IsTrue(login.IsSuccessStatusCode, $"{userName}: {(int)login.StatusCode} {await login.Content.ReadAsStringAsync()}");
        return client;
    }

    [TestMethod]
    public async Task PlainStaff_CannotListFilesOrFolders()
    {
        HttpClient client = await SignInAsync("plainstaff", Password);

        Assert.AreEqual(HttpStatusCode.Forbidden, (await client.GetAsync("/api/files/list")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await client.GetAsync("/api/files/folders/list")).StatusCode);
    }

    [TestMethod]
    public async Task Anonymous_ListIsUnauthorized()
    {
        HttpClient client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/files/list")).StatusCode);
    }

    [TestMethod]
    public async Task FileManager_ListsFiles_WithoutTheStoragePath()
    {
        HttpClient client = await SignInAsync("filemanager", Password);

        HttpResponseMessage response = await client.GetAsync("/api/files/list");
        string body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
        using JsonDocument json = JsonDocument.Parse(body);
        JsonElement file = json.RootElement.GetProperty("data").EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == _fileId);

        Assert.AreEqual("rota.pdf", file.GetProperty("fileName").GetString());
        Assert.IsFalse(file.TryGetProperty("filePath", out _), "The list must not expose where the file is stored.");
        Assert.IsFalse(body.Contains("secret-storage-name"), "The storage name leaked into the response.");
    }

    [TestMethod]
    public async Task Admin_ListsFolders()
    {
        HttpClient client = await SignInAsync("admin", CustomWebApplicationFactory.SeedAdminPassword);

        HttpResponseMessage response = await client.GetAsync("/api/files/folders/list");
        string body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
        StringAssert.Contains(body, "Posters");
    }

    [TestMethod]
    public async Task PlainStaff_CanStillDownloadAFileByItsId()
    {
        // Course images, party artwork and logos are embedded by id on pages every staff member
        // sees, so download stays open to signed-in users (InMemory has no stored bytes, so this
        // only proves the request isn't refused by authorization).
        HttpClient client = await SignInAsync("plainstaff", Password);

        HttpResponseMessage response = await client.GetAsync($"/api/files/download/{_fileId}");

        Assert.AreNotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.AreNotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
