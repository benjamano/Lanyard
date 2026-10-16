using System.Reflection;
using Lanyard.Application.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Integration;

// An action type with no registered executor saves fine and then silently does nothing: the engine
// only records "Action type not supported" (see the automation-engine skill). This boots the real
// container and checks every AutomationActionTypes constant has an executor, and the rule dialog
// offers it, so a new type can't half-ship.
[TestClass]
public class AutomationActionDispatchCoverageTests
{
    private static IEnumerable<string> ActionTypes => typeof(AutomationActionTypes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(x => x.IsLiteral && x.FieldType == typeof(string))
        .Select(x => (string)x.GetRawConstantValue()!);

    [TestMethod]
    public void EveryActionType_HasARegisteredExecutor()
    {
        using CustomWebApplicationFactory factory = new();
        factory.CreateClient();

        List<IActionExecutor> executors = factory.Services.GetServices<IActionExecutor>().ToList();

        List<string> missing = ActionTypes.Where(type => !executors.Any(e => e.CanHandle(type))).ToList();

        Assert.AreEqual(0, missing.Count, $"No IActionExecutor handles: {string.Join(", ", missing)}");
    }

    [TestMethod]
    public void EveryActionType_IsOfferedInTheRuleDialog()
    {
        string dialog = File.ReadAllText(FindRepoFile("src/Lanyard.Server/LanyardApp/Components/Manager/AddEditAutomationRuleDialog.razor"));

        List<string> fields = typeof(AutomationActionTypes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(x => x.IsLiteral)
            .Select(x => x.Name)
            .Where(name => !dialog.Contains($"<FluentOption Value=\"@AutomationActionTypes.{name}\">"))
            .ToList();

        Assert.AreEqual(0, fields.Count, $"Not offered in the rule dialog: {string.Join(", ", fields)}");
    }

    private static string FindRepoFile(string relativePath)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, relativePath)))
        {
            dir = dir.Parent;
        }

        Assert.IsNotNull(dir, $"Couldn't find {relativePath} above {AppContext.BaseDirectory}");
        return Path.Combine(dir.FullName, relativePath);
    }
}
