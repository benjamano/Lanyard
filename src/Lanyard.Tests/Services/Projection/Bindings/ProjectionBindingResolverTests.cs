using Lanyard.Application.Services.Projection.Bindings;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Projection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Projection.Bindings;

[TestClass]
public class ProjectionBindingResolverTests
{
    private static readonly ProjectionBindingContext Context = new(1, 10, Guid.NewGuid(), DateTime.UtcNow);

    private sealed class StubSource(string key, Func<CancellationToken, Task<Result<string?>>> resolve) : IProjectionBindingSource
    {
        public string Key { get; } = key;

        public int Calls;

        public Task<Result<string?>> ResolveAsync(ProjectionBindingContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return resolve(cancellationToken);
        }
    }

    private static StubSource Returning(string key, string? value) => new(key, _ => Task.FromResult(Result<string?>.Ok(value)));

    private static ProjectionBindingResolver CreateResolver(params IProjectionBindingSource[] sources) =>
        new(sources, Mock.Of<ILogger<ProjectionBindingResolver>>());

    private static (ProjectionProgramStep Step, Guid ValueId) ShowTextStep(string? bindingKey, string? value, string templateName = ProjectionTemplateNames.ShowText, string parameterName = ProjectionParameterNames.Text)
    {
        Guid valueId = Guid.NewGuid();

        ProjectionProgramStep step = new()
        {
            Id = Guid.NewGuid(),
            Template = new ProjectionProgramStepTemplate { Name = templateName },
            ParameterValues =
            [
                new ProjectionProgramParameterValue
                {
                    Id = valueId,
                    Parameter = new ProjectionProgramStepTemplateParameter { TemplateId = Guid.Empty, Name = parameterName, DataType = "String" },
                    BindingKey = bindingKey,
                    Value = value
                }
            ]
        };

        return (step, valueId);
    }

    [TestMethod]
    public async Task ResolveStep_UsesTheSourcesValue_Normalised()
    {
        (ProjectionProgramStep step, Guid valueId) = ShowTextStep(ProjectionBindingKeys.TopScoreToday, "Fallback");
        ProjectionBindingResolver resolver = CreateResolver(Returning(ProjectionBindingKeys.TopScoreToday, "  Top\nscore:\t 900  "));

        IReadOnlyDictionary<Guid, string> values = await resolver.ResolveStepAsync(Context, step, CancellationToken.None);

        Assert.AreEqual("Top score: 900", values[valueId]);
    }

    [TestMethod]
    public async Task ResolveStep_FallsBackToTheAdminsText_ThenTheSourceDefault()
    {
        (ProjectionProgramStep withFallback, Guid withFallbackId) = ShowTextStep(ProjectionBindingKeys.LastGameWinner, "Play now!");
        (ProjectionProgramStep blankFallback, Guid blankFallbackId) = ShowTextStep(ProjectionBindingKeys.LastGameWinner, "   ");
        ProjectionBindingResolver resolver = CreateResolver(Returning(ProjectionBindingKeys.LastGameWinner, null));

        IReadOnlyDictionary<Guid, string> first = await resolver.ResolveStepAsync(Context, withFallback, CancellationToken.None);
        IReadOnlyDictionary<Guid, string> second = await resolver.ResolveStepAsync(Context, blankFallback, CancellationToken.None);

        Assert.AreEqual("Play now!", first[withFallbackId]);
        Assert.AreEqual(ProjectionBindingCatalogue.Find(ProjectionBindingKeys.LastGameWinner)!.DefaultFallback, second[blankFallbackId]);
    }

    [TestMethod]
    public async Task ResolveStep_FallsBack_WhenTheSourceFailsThrowsOrIsUnknown()
    {
        StubSource failing = new(ProjectionBindingKeys.TopScoreToday, _ => Task.FromResult(Result<string?>.Fail("db down")));
        StubSource throwing = new(ProjectionBindingKeys.LastGameWinner, _ => throw new InvalidOperationException("boom"));
        ProjectionBindingResolver resolver = CreateResolver(failing, throwing);

        (ProjectionProgramStep failStep, Guid failId) = ShowTextStep(ProjectionBindingKeys.TopScoreToday, "F1");
        (ProjectionProgramStep throwStep, Guid throwId) = ShowTextStep(ProjectionBindingKeys.LastGameWinner, "F2");
        (ProjectionProgramStep unknownStep, Guid unknownId) = ShowTextStep(ProjectionBindingKeys.OpeningHoursToday, "F3");

        Assert.AreEqual("F1", (await resolver.ResolveStepAsync(Context, failStep, CancellationToken.None))[failId]);
        Assert.AreEqual("F2", (await resolver.ResolveStepAsync(Context, throwStep, CancellationToken.None))[throwId]);
        Assert.AreEqual("F3", (await resolver.ResolveStepAsync(Context, unknownStep, CancellationToken.None))[unknownId]);
    }

    [TestMethod]
    public async Task ResolveStep_FallsBack_WhenASourceIgnoresCancellationAndHangs()
    {
        StubSource hanging = new(ProjectionBindingKeys.TopScoreToday, async _ =>
        {
            await Task.Delay(Timeout.Infinite, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            return Result<string?>.Ok("too late");
        });

        ProjectionBindingResolver resolver = new([hanging], Mock.Of<ILogger<ProjectionBindingResolver>>()) { SourceTimeout = TimeSpan.FromMilliseconds(100) };
        (ProjectionProgramStep step, Guid valueId) = ShowTextStep(ProjectionBindingKeys.TopScoreToday, "Fallback");

        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
        IReadOnlyDictionary<Guid, string> values = await resolver.ResolveStepAsync(Context, step, CancellationToken.None);

        Assert.AreEqual("Fallback", values[valueId]);
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(5), "A hung source must not hold the step up.");
    }

    [TestMethod]
    public async Task ResolveStep_IgnoresFixedValues_AndBindingsOnParametersThatCantShowThem()
    {
        StubSource source = Returning(ProjectionBindingKeys.TopScoreToday, "900");
        ProjectionBindingResolver resolver = CreateResolver(source);

        (ProjectionProgramStep fixedStep, _) = ShowTextStep(null, "Hello");
        (ProjectionProgramStep volumeStep, _) = ShowTextStep(ProjectionBindingKeys.TopScoreToday, "60", ProjectionTemplateNames.PlayVideo, ProjectionParameterNames.Volume);

        Assert.AreEqual(0, (await resolver.ResolveStepAsync(Context, fixedStep, CancellationToken.None)).Count);
        Assert.AreEqual(0, (await resolver.ResolveStepAsync(Context, volumeStep, CancellationToken.None)).Count);
        Assert.AreEqual(0, source.Calls);
    }

    [TestMethod]
    public async Task ResolveStep_ReusesAValueWithinTheCacheWindow_PerCompanyAndLocation()
    {
        StubSource source = Returning(ProjectionBindingKeys.TopScoreToday, "900");
        ProjectionBindingResolver resolver = CreateResolver(source);
        (ProjectionProgramStep step, _) = ShowTextStep(ProjectionBindingKeys.TopScoreToday, null);

        await resolver.ResolveStepAsync(Context, step, CancellationToken.None);
        await resolver.ResolveStepAsync(Context, step, CancellationToken.None);
        Assert.AreEqual(1, source.Calls);

        await resolver.ResolveStepAsync(Context with { LocationId = 11 }, step, CancellationToken.None);
        await resolver.ResolveStepAsync(Context with { CompanyId = 2 }, step, CancellationToken.None);
        Assert.AreEqual(3, source.Calls);
    }

    [TestMethod]
    public async Task Preview_ReturnsNull_WhenTheSourceHasNoData_AndFails_ForAnUnknownKey()
    {
        ProjectionBindingResolver resolver = CreateResolver(Returning(ProjectionBindingKeys.TopScoreToday, null));

        Result<string?> preview = await resolver.PreviewAsync(ProjectionBindingKeys.TopScoreToday, 1, null);
        Result<string?> unknown = await resolver.PreviewAsync("nope", 1, null);

        Assert.IsTrue(preview.IsSuccess);
        Assert.IsNull(preview.Data);
        Assert.IsFalse(unknown.IsSuccess);
    }

    [TestMethod]
    public void Normalise_StripsControlCharacters_CollapsesWhitespace_AndCapsTheLength()
    {
        Assert.AreEqual(string.Empty, ProjectionBindingText.Normalise(null));
        Assert.AreEqual(string.Empty, ProjectionBindingText.Normalise(" \r\n\t "));
        Assert.AreEqual("a b c", ProjectionBindingText.Normalise("a\u0007b\u001b\n\nc"));

        string capped = ProjectionBindingText.Normalise(new string('x', 500));
        Assert.AreEqual(ProjectionBindingText.MaxLength, capped.Length);
        Assert.IsTrue(capped.EndsWith('…'));
    }

    [TestMethod]
    public void Catalogue_OnlyShowTextsTextIsBindable()
    {
        Assert.IsTrue(ProjectionBindingCatalogue.IsBindable(ProjectionTemplateNames.ShowText, ProjectionParameterNames.Text));
        Assert.IsFalse(ProjectionBindingCatalogue.IsBindable(ProjectionTemplateNames.PlayVideo, ProjectionParameterNames.Volume));
        Assert.IsFalse(ProjectionBindingCatalogue.IsBindable(ProjectionTemplateNames.Dashboard, ProjectionParameterNames.Dashboard));
    }

    [TestMethod]
    public void Catalogue_FallbackFor_LeavesFixedValuesAlone()
    {
        Assert.AreEqual(" as typed ", ProjectionBindingCatalogue.FallbackFor(null, " as typed "));
        Assert.AreEqual("Custom", ProjectionBindingCatalogue.FallbackFor(ProjectionBindingKeys.TopScoreToday, "Custom"));
        Assert.AreEqual(string.Empty, ProjectionBindingCatalogue.FallbackFor("retired.key", null));
    }
}
