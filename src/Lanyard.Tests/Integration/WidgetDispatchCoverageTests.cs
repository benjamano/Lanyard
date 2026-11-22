using System.Reflection;
using System.Text.RegularExpressions;
using Lanyard.App.Components.Dashboards;
using Lanyard.Application.Services;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Components;
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
        Mock<Lanyard.Application.Services.Authentication.ISecurityService> security = new();
        security.Setup(x => x.IsCurrentUserInRoleAsync("Admin")).ReturnsAsync(true);
        DashboardService service = new(factory.Object, security.Object);

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
    public void EveryWidgetType_HasACatalogEntryThatBuildsAndRendersIt()
    {
        Dictionary<WidgetType, Type> mapped = MappedWidgetTypes();
        List<string> problems = [];

        foreach (WidgetType type in RealWidgetTypes)
        {
            WidgetDescriptor? descriptor = DashboardWidgetCatalog.Get(type);

            if (descriptor is null)
            {
                problems.Add($"{type}: no entry in DashboardWidgetCatalog.All");
                continue;
            }

            DashboardWidget created = descriptor.Create();

            if (created.Type != type)
            {
                problems.Add($"{type}: the catalog's Create() makes a {created.Type} widget");
            }

            if (mapped.TryGetValue(type, out Type? clrType) && created.GetType() != clrType)
            {
                problems.Add($"{type}: the catalog's Create() makes a {created.GetType().Name}, but the discriminator maps it to {clrType.Name}");
            }

            if (created.GridW < 1 || created.GridH < 1)
            {
                problems.Add($"{type}: {created.GetType().Name}'s constructor sets no default size");
            }

            // DynamicComponent hands the renderer a "Widget" parameter, so it must take one the
            // widget can be assigned to, or the widget renders nothing at runtime.
            PropertyInfo? widgetParameter = descriptor.Component.GetProperty("Widget");

            if (!typeof(IComponent).IsAssignableFrom(descriptor.Component)
                || widgetParameter?.GetCustomAttribute<ParameterAttribute>() is null
                || !widgetParameter.PropertyType.IsAssignableFrom(created.GetType()))
            {
                problems.Add($"{type}: {descriptor.Component.Name} has no [Parameter] Widget that takes a {created.GetType().Name}");
            }
        }

        List<WidgetType> duplicates = [.. DashboardWidgetCatalog.All.GroupBy(x => x.Type).Where(g => g.Count() > 1).Select(g => g.Key)];

        if (duplicates.Count > 0)
        {
            problems.Add("Listed more than once in DashboardWidgetCatalog.All: " + string.Join(", ", duplicates));
        }

        Assert.IsEmpty(problems, string.Join('\n', problems));
    }

    [TestMethod]
    public void EveryConfigurableWidgetType_OpensASettingsDialog()
    {
        string configurator = ReadSource("src/Lanyard.Server/LanyardApp/Components/Dashboards/DashboardWidgetConfigurator.cs");
        Dictionary<WidgetType, Type> mapped = MappedWidgetTypes();
        List<string> problems = [];

        foreach (WidgetDescriptor descriptor in DashboardWidgetCatalog.All.Where(x => x.HasSettings))
        {
            if (mapped.TryGetValue(descriptor.Type, out Type? clrType) && !Regex.IsMatch(configurator, $@"case {clrType.Name} \w+:"))
            {
                problems.Add($"{descriptor.Type}: marked HasSettings in the catalog but has no case in DashboardWidgetConfigurator.ConfigureAsync");
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
