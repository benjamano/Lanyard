using Lanyard.App.Components.Demo;
using Microsoft.JSInterop;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Components;

// The demo's "see Lanyard in your own branding": the palette it shows and how it relabels the
// signed-in location. The browser side (storage, logo, favicon) is exercised end to end in the UI.
[TestClass]
public class DemoBrandingTests
{
    private static async Task<DemoBrandingState> StateWithAsync(string? name, string? color)
    {
        Mock<IJSRuntime> js = new();
        js.Setup(x => x.InvokeAsync<string?>("lanyardDemoBranding.save", It.IsAny<object?[]?>()))
            .ReturnsAsync((string?)null);

        DemoBrandingState state = new(js.Object);
        await state.SaveAsync(new DemoBrandingState.Branding(name, color, null));
        return state;
    }

    [TestMethod]
    public void Ramp_RunsLightToDarkAndContainsTheChosenColour()
    {
        IReadOnlyList<string> ramp = ColorPalette.Ramp("#0E7C86");

        Assert.AreEqual(8, ramp.Count);
        CollectionAssert.Contains(ramp.ToList(), "#0E7C86");
        Assert.IsTrue(ramp.All(x => DemoBrandingState.IsValidColor(x)));
    }

    [TestMethod]
    public void TextOn_PicksAReadableColour()
    {
        Assert.AreEqual("#FFFFFF", ColorPalette.TextOn("#0E7C86"));
        Assert.AreEqual("#1F1F1F", ColorPalette.TextOn("#FFE600"));
    }

    [TestMethod]
    public async Task LocationName_UsesTheVisitorsCompanyName()
    {
        DemoBrandingState state = await StateWithAsync("Acme Leisure", "#0E7C86");

        Assert.AreEqual("Acme Leisure Riverside", state.RebrandLocationName("Starlight Leisure Riverside"));
    }

    [TestMethod]
    public async Task LocationName_IsUnchangedWithoutACompanyName()
    {
        DemoBrandingState state = await StateWithAsync(null, "#0E7C86");

        Assert.AreEqual("Starlight Leisure Riverside", state.RebrandLocationName("Starlight Leisure Riverside"));
        Assert.AreEqual("#0E7C86", state.Current!.ColorHex);
    }

    [TestMethod]
    public async Task InvalidColoursAndBlankNamesAreDropped()
    {
        DemoBrandingState state = await StateWithAsync("   ", "not-a-colour");

        Assert.IsNull(state.Current, "Nothing valid to apply, so the demo's own branding stays.");
    }
}
