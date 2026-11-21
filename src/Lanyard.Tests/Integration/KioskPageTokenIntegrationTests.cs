using System.Net;
using Lanyard.Application.Services.VideoStreaming;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Integration;

// Security Issue 9.3: /kiosk/{clientId}/{programId} is anonymous (it runs in the kiosk's WebView2)
// and used to start or replace a run on a venue's screens for anyone who knew the two ids. Through
// the real pipeline: without the kiosk's own viewer token, or for another company's program, the
// page refuses before loading anything; with it, the program loads. The ids are no longer printed.
[TestClass]
public class KioskPageTokenIntegrationTests
{
    private const string InvalidLink = "This display link isn&#x27;t valid";

    private CustomWebApplicationFactory _factory = null!;
    private Guid _clientId;
    private Guid _ownProgramId;
    private Guid _otherCompanyProgramId;

    [TestInitialize]
    public async Task Setup()
    {
        _factory = new CustomWebApplicationFactory();
        _factory.CreateClient();

        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationDbContext ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        Company other = new() { Name = "Other Co", IsActive = true };
        ctx.Companies.Add(other);
        await ctx.SaveChangesAsync();

        _clientId = Guid.NewGuid();
        _ownProgramId = Guid.NewGuid();
        _otherCompanyProgramId = Guid.NewGuid();

        ctx.Clients.Add(new Client { Id = _clientId, CompanyId = ApplicationDbContext.SeedPlay2DayCompanyId, Name = "Arena kiosk", CreateDate = DateTime.UtcNow });
        ctx.ProjectionPrograms.Add(new ProjectionProgram { Id = _ownProgramId, CompanyId = ApplicationDbContext.SeedPlay2DayCompanyId, Name = "Arena loop", IsActive = true });
        ctx.ProjectionPrograms.Add(new ProjectionProgram { Id = _otherCompanyProgramId, CompanyId = other.Id, Name = "Their loop", IsActive = true });
        await ctx.SaveChangesAsync();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _factory.Dispose();
    }

    private string IssueToken(Guid clientId) =>
        _factory.Services.GetRequiredService<IVideoStreamTokenService>().IssueViewerToken(clientId);

    private async Task<string> GetPageAsync(Guid programId, string? token)
    {
        HttpClient client = _factory.CreateClient();
        string url = $"/kiosk/{_clientId}/{programId}?display=0" + (token is null ? string.Empty : $"&token={Uri.EscapeDataString(token)}");

        HttpResponseMessage response = await client.GetAsync(url);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadAsStringAsync();
    }

    [TestMethod]
    public async Task WithoutAToken_ThePageRefuses()
    {
        string html = await GetPageAsync(_ownProgramId, token: null);

        StringAssert.Contains(html, InvalidLink);
    }

    [TestMethod]
    public async Task WithAnotherKiosksToken_ThePageRefuses()
    {
        string html = await GetPageAsync(_ownProgramId, IssueToken(Guid.NewGuid()));

        StringAssert.Contains(html, InvalidLink);
    }

    [TestMethod]
    public async Task WithAMadeUpToken_ThePageRefuses()
    {
        string html = await GetPageAsync(_ownProgramId, "not-a-real-token");

        StringAssert.Contains(html, InvalidLink);
    }

    [TestMethod]
    public async Task ForAnotherCompanysProgram_ThePageRefuses()
    {
        string html = await GetPageAsync(_otherCompanyProgramId, IssueToken(_clientId));

        StringAssert.Contains(html, InvalidLink);
    }

    [TestMethod]
    public async Task WithTheKiosksOwnToken_TheProgramLoads_AndNoIdsArePrinted()
    {
        string html = await GetPageAsync(_ownProgramId, IssueToken(_clientId));

        Assert.IsFalse(html.Contains(InvalidLink), "The kiosk's own token must be accepted.");
        Assert.IsFalse(html.Contains($"Client Id: {_clientId}"), "The page must not print the kiosk's id.");
    }
}
