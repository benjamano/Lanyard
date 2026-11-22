using Lanyard.Application.Services;
using Lanyard.Application.Services.VideoStreaming;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Dashboards;

[TestClass]
public class KioskTextWidgetServiceTests
{
    private const string GoodToken = "kiosk-token";

    private static readonly Guid KioskId = Guid.NewGuid();

    private static (KioskTextWidgetService Service, DbContextOptions<ApplicationDbContext> Options) Create()
    {
        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        Mock<IDbContextFactory<ApplicationDbContext>> factory = new();
        factory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => new ApplicationDbContext(options));
        Mock<IVideoStreamTokenService> tokens = new();
        tokens.Setup(x => x.ValidateViewerToken(It.IsAny<Guid>(), It.IsAny<string?>()))
            .Returns((Guid clientId, string? token) => clientId == KioskId && token == GoodToken);

        return (new KioskTextWidgetService(factory.Object, tokens.Object, NullLogger<KioskTextWidgetService>.Instance), options);
    }

    private static async Task<TextAreaWidget> SeedAsync(DbContextOptions<ApplicationDbContext> options, bool editable, int dashboardCompany = 1, int kioskCompany = 1)
    {
        await using ApplicationDbContext ctx = new(options);
        Dashboard dashboard = new() { Id = Guid.NewGuid(), Name = "Notices", IsActive = true, CompanyId = dashboardCompany };
        TextAreaWidget widget = new() { DashboardId = dashboard.Id, Content = "<p>Old</p>", EditableOnKiosk = editable };
        ctx.Dashboards.Add(dashboard);
        ctx.DashboardWidgets.Add(widget);
        ctx.Clients.Add(new Client { Id = KioskId, Name = "Front desk", CompanyId = kioskCompany });
        await ctx.SaveChangesAsync();
        return widget;
    }

    private static async Task<string?> StoredContentAsync(DbContextOptions<ApplicationDbContext> options)
    {
        await using ApplicationDbContext ctx = new(options);
        return (await ctx.DashboardWidgets.OfType<TextAreaWidget>().SingleAsync()).Content;
    }

    [TestMethod]
    public async Task SaveText_FromTheKiosk_SavesAndStripsScripts()
    {
        (KioskTextWidgetService service, DbContextOptions<ApplicationDbContext> options) = Create();
        TextAreaWidget widget = await SeedAsync(options, editable: true);

        Result<bool> result = await service.SaveTextFromKioskAsync(widget.Id, KioskId, GoodToken, "<p>Open till 8</p><script>alert(1)</script>");

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual("<p>Open till 8</p>", await StoredContentAsync(options));
    }

    [TestMethod]
    public async Task SaveText_WithoutAValidKioskToken_IsRefused()
    {
        (KioskTextWidgetService service, DbContextOptions<ApplicationDbContext> options) = Create();
        TextAreaWidget widget = await SeedAsync(options, editable: true);

        Result<bool> missing = await service.SaveTextFromKioskAsync(widget.Id, KioskId, null, "<p>Hacked</p>");
        Result<bool> wrong = await service.SaveTextFromKioskAsync(widget.Id, KioskId, "guess", "<p>Hacked</p>");

        Assert.IsFalse(missing.IsSuccess);
        Assert.IsFalse(wrong.IsSuccess);
        Assert.AreEqual(KioskTextWidgetService.NotAKioskMessage, missing.Error);
        Assert.AreEqual("<p>Old</p>", await StoredContentAsync(options));
    }

    [TestMethod]
    public async Task SaveText_WhenTheWidgetIsntKioskEditable_IsRefused()
    {
        (KioskTextWidgetService service, DbContextOptions<ApplicationDbContext> options) = Create();
        TextAreaWidget widget = await SeedAsync(options, editable: false);

        Result<bool> result = await service.SaveTextFromKioskAsync(widget.Id, KioskId, GoodToken, "<p>New</p>");

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("<p>Old</p>", await StoredContentAsync(options));
    }

    [TestMethod]
    public async Task SaveText_FromAnotherCompanysKiosk_IsRefused()
    {
        (KioskTextWidgetService service, DbContextOptions<ApplicationDbContext> options) = Create();
        TextAreaWidget widget = await SeedAsync(options, editable: true, dashboardCompany: 1, kioskCompany: 2);

        Result<bool> result = await service.SaveTextFromKioskAsync(widget.Id, KioskId, GoodToken, "<p>New</p>");

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("<p>Old</p>", await StoredContentAsync(options));
    }

    [TestMethod]
    public async Task SaveText_ThatsTooLong_IsRefused()
    {
        (KioskTextWidgetService service, DbContextOptions<ApplicationDbContext> options) = Create();
        TextAreaWidget widget = await SeedAsync(options, editable: true);

        Result<bool> result = await service.SaveTextFromKioskAsync(widget.Id, KioskId, GoodToken, new string('a', KioskTextWidgetService.MaxContentLength + 1));

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("<p>Old</p>", await StoredContentAsync(options));
    }
}
