using Lanyard.App.Components.Kiosk.Templates;
using Lanyard.Application.Services;
using Lanyard.Application.Services.Projection.Bindings;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Projection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Integration;

// Data-bound projection parameters through the real DI container and the real kiosk component.
[TestClass]
public class ProjectionBindingIntegrationTests
{
    private CustomWebApplicationFactory _factory = null!;

    [TestInitialize]
    public void Setup()
    {
        _factory = new CustomWebApplicationFactory();
        _factory.CreateClient();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _factory.Dispose();
    }

    // A key in the catalogue with no registered source saves fine and silently shows its fallback
    // for ever - the same silent-dispatch trap as dashboard widgets and automation actions.
    [TestMethod]
    public void EveryCatalogueKey_HasExactlyOneRegisteredSource()
    {
        List<string> registered = [.. _factory.Services.GetServices<IProjectionBindingSource>().Select(s => s.Key)];

        CollectionAssert.AreEquivalent(ProjectionBindingCatalogue.All.Select(x => x.Key).ToList(), registered);
    }

    [TestMethod]
    public void RunnerAndResolver_ResolveFromTheContainer()
    {
        Assert.IsNotNull(_factory.Services.GetRequiredService<IProjectionBindingResolver>());
        Assert.IsNotNull(_factory.Services.GetRequiredService<IProjectionProgramRunnerService>());
    }

    [TestMethod]
    public async Task ShowText_RendersABoundValueAsEncodedText()
    {
        Guid valueId = Guid.NewGuid();

        ProjectionProgramStep step = new()
        {
            Template = new ProjectionProgramStepTemplate { Name = ProjectionTemplateNames.ShowText },
            ParameterValues =
            [
                new ProjectionProgramParameterValue
                {
                    Id = valueId,
                    Parameter = new ProjectionProgramStepTemplateParameter { TemplateId = Guid.Empty, Name = ProjectionParameterNames.Text, DataType = "String" },
                    BindingKey = ProjectionBindingKeys.TopScoreToday,
                    Value = "Fallback"
                }
            ]
        };

        Dictionary<Guid, string> boundValues = new() { [valueId] = "<script>alert('x')</script> Gun 7" };

        using IServiceScope scope = _factory.Services.CreateScope();
        await using HtmlRenderer renderer = new(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<ILoggerFactory>());

        string html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            ParameterView parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(KioskShowTextTemplate.Step)] = step,
                [nameof(KioskShowTextTemplate.BoundValues)] = boundValues
            });

            return (await renderer.RenderComponentAsync<KioskShowTextTemplate>(parameters)).ToHtmlString();
        });

        Assert.IsFalse(html.Contains("<script>"), html);
        StringAssert.Contains(html, "&lt;script&gt;");
        StringAssert.Contains(html, "Gun 7");
    }
}
