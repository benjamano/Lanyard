using System.Text.RegularExpressions;
using Lanyard.Application.Services;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Integration;

// Widget types are dispatched by hand-written switches spread over several files, with nothing
// tying them together at compile time (see the dashboard-widgets skill). Missing one doesn't throw
// - the widget saves but renders blank, can't be configured, or fails the whole dashboard save -
// so these tests check every WidgetType against every switch instead of trusting review to.
[TestClass]
public class WidgetDispatchCoverageTests
{
    private static readonly HashSet<WidgetType> WidgetsWithoutConfigDialog = [WidgetType.Greeting, WidgetType.TextArea];

    private static IEnumerable<WidgetType> RealWidgetTypes =>
        Enum.GetValues<WidgetType>().Where(x => x != WidgetType.Unknown);

    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions() =>
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private static Dictionary<WidgetType, Type> MappedWidgetTypes()
    {
        using ApplicationDbContext ctx = new(GetInMemoryOptions());

        return ctx.Model.GetEntityTypes()
            .Where(x => typeof(DashboardWidget).IsAssignableFrom(x.ClrType) && x.ClrType != typeof(DashboardWidget))
            .ToDictionary(x => (WidgetType)x.GetDiscriminatorValue()!, x => x.ClrType);
    }

    [TestMethod]
    public void EveryWidgetType_HasADiscriminatorMapping()
    {
        Dictionary<WidgetType, Type> mapped = MappedWidgetTypes();

        List<WidgetType> missing = [.. RealWidgetTypes.Where(x => !mapped.ContainsKey(x))];

        Assert.IsEmpty(missing,
            "WidgetType values with no HasValue<...>() in ApplicationDbContext's discriminator chain: " + string.Join(", ", missing));
    }

    [TestMethod]
    public async Task EveryWidgetType_CanBeAddedToADashboardAndSaved()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Mock<IDbContextFactory<ApplicationDbContext>> factory = new();
        factory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => new ApplicationDbContext(options));
        DashboardService service = new(factory.Object);

        List<string> failures = [];

        foreach ((WidgetType type, Type clrType) in MappedWidgetTypes())
        {
            Dashboard dashboard = new() { Id = Guid.NewGuid(), Name = type.ToString(), IsActive = true, CreateDate = DateTime.UtcNow };

            await using (ApplicationDbContext ctx = new(options))
            {
                ctx.Dashboards.Add(dashboard);
                await ctx.SaveChangesAsync();
            }

            // Guid.Empty takes the new-widget path through CreateWidgetCopy, whose fallback arm
            // fails the whole save for a type it doesn't know.
            DashboardWidget widget = (DashboardWidget)Activator.CreateInstance(clrType)!;
            widget.Id = Guid.Empty;
            widget.IsActive = true;
            dashboard.Widgets = [widget];

            Result<bool> result = await service.SaveDashboardAsync(dashboard);

            if (!result.IsSuccess)
            {
                failures.Add($"{type}: {result.Error}");
            }
        }

        Assert.IsEmpty(failures, "Widget types DashboardService can't save:\n" + string.Join('\n', failures));
    }

    [TestMethod]
    public void EveryWidgetType_IsRenderedConfiguredAndOfferedInTheEditor()
    {
        string grid = ReadSource("src/Lanyard.Server/LanyardApp/Components/Dashboards/DashboardGrid.razor");
        string editor = ReadSource("src/Lanyard.Server/LanyardApp/Components/Pages/Staff/Dashboards/EditDashboard.razor");
        Dictionary<WidgetType, Type> mapped = MappedWidgetTypes();

        List<string> problems = [];

        foreach (WidgetType type in RealWidgetTypes)
        {
            if (!grid.Contains($"case WidgetType.{type}:"))
            {
                problems.Add($"{type}: no render case in DashboardGrid.razor");
            }

            if (!mapped.TryGetValue(type, out Type? clrType))
            {
                continue;
            }

            if (!Regex.IsMatch(editor, $@"new {clrType.Name}\(\)"))
            {
                problems.Add($"{type}: no menu item creating a {clrType.Name} in EditDashboard.razor");
            }

            // Greeting has nothing to configure and TextArea is edited in place, so neither has a
            // config-dialog case.
            if (!WidgetsWithoutConfigDialog.Contains(type) && !Regex.IsMatch(grid, $@"case {clrType.Name} \w+:"))
            {
                problems.Add($"{type}: no config-dialog case in DashboardGrid.ShowWidgetConfigDialog");
            }
        }

        Assert.IsEmpty(problems, string.Join('\n', problems));
    }

    private static string ReadSource(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LanyardApp.sln")))
        {
            directory = directory.Parent;
        }

        Assert.IsNotNull(directory, "Couldn't find the repository root (LanyardApp.sln) above the test output folder.");

        return File.ReadAllText(Path.Combine(directory.FullName, relativePath));
    }
}
