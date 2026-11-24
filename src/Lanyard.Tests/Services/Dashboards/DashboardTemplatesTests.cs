using Lanyard.App.Components.Dashboards;
using Lanyard.Application.Services;
using Lanyard.Application.Services.Authentication;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Dashboards;

[TestClass]
public class DashboardTemplatesTests
{
    [TestMethod]
    public void EveryTemplate_HasAValidLayout()
    {
        foreach (DashboardTemplate template in DashboardTemplates.All)
        {
            List<WidgetRect> layout = [.. template.Build().Select(w => new WidgetRect(w.Id, w.GridX, w.GridY, w.GridW, w.GridH))];

            // Normalize only moves widgets that overlap or overflow, so a valid layout comes back unchanged.
            CollectionAssert.AreEqual(layout, DashboardLayoutEngine.Normalize(layout), $"{template.Name} has overlapping or overflowing widgets");
        }
    }

    [TestMethod]
    public void TemplateKeys_AreUnique()
    {
        Assert.HasCount(DashboardTemplates.All.Count, DashboardTemplates.All.Select(x => x.Key).Distinct());
    }

    [TestMethod]
    public async Task EveryTemplate_CanBeCreated()
    {
        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        Mock<IDbContextFactory<ApplicationDbContext>> factory = new();
        factory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => new ApplicationDbContext(options));
        Mock<ISecurityService> security = new();
        security.Setup(x => x.IsCurrentUserInRoleAsync("Admin")).ReturnsAsync(true);
        DashboardService service = new(factory.Object, security.Object);

        foreach (DashboardTemplate template in DashboardTemplates.All)
        {
            Result<Guid> result = await service.CreateDashboardAsync(new Dashboard
            {
                Name = template.Name,
                FitToScreen = template.FitToScreen,
                RowHeight = template.RowHeight,
                Widgets = template.Build()
            });

            Assert.IsTrue(result.IsSuccess, $"{template.Name}: {result.Error}");

            await using ApplicationDbContext ctx = new(options);
            Assert.AreEqual(template.Build().Count, await ctx.DashboardWidgets.CountAsync(x => x.DashboardId == result.Data), template.Name);
        }
    }
}
