using System.Text.RegularExpressions;
using Lanyard.Application.Services.Demo;
using Microsoft.JSInterop;

namespace Lanyard.App.Components.Demo;

// The branding a demo visitor is trying out ("see Lanyard in your own colours"). Scoped, so one per
// circuit; the source of truth is the visitor's own browser storage (demoBranding.js), because the
// demo company is shared and this must never change what anyone else sees.
public sealed partial class DemoBrandingState(IJSRuntime js)
{
    public sealed record Branding(string? Name, string? ColorHex, string? LogoUrl)
    {
        public bool IsEmpty => string.IsNullOrWhiteSpace(Name) && ColorHex is null && LogoUrl is null;
    }

    private sealed record StoredBranding(string? Name, string? Color, string? LogoUrl);

    public Branding? Current { get; private set; }

    public bool Loaded { get; private set; }

    public event Action? Changed;

    public async Task LoadAsync()
    {
        if (Loaded)
        {
            return;
        }

        StoredBranding? stored = await js.InvokeAsync<StoredBranding?>("lanyardDemoBranding.load");
        Current = stored is null ? null : Normalise(new Branding(stored.Name, stored.Color, stored.LogoUrl));
        Loaded = true;
        Changed?.Invoke();
    }

    public async Task SaveAsync(Branding branding)
    {
        Branding? normalised = Normalise(branding);

        if (normalised is null)
        {
            await ClearAsync();
            return;
        }

        // The logo is whatever the dialog is previewing (held in the browser); save hands back its URL.
        string? logoUrl = await js.InvokeAsync<string?>("lanyardDemoBranding.save", normalised.Name, normalised.ColorHex);
        Current = normalised with { LogoUrl = logoUrl };
        Changed?.Invoke();
    }

    public async Task ClearAsync()
    {
        await js.InvokeVoidAsync("lanyardDemoBranding.clear");
        Current = null;
        Changed?.Invoke();
    }

    // The signed-in location's name with the demo company's name swapped for the visitor's, e.g.
    // "Starlight Leisure Riverside" -> "Acme Leisure Riverside".
    public string? RebrandLocationName(string? locationDisplayName)
    {
        if (locationDisplayName is null || string.IsNullOrWhiteSpace(Current?.Name))
        {
            return locationDisplayName;
        }

        return locationDisplayName.StartsWith(DemoSeeder.CompanyName, StringComparison.Ordinal)
            ? Current.Name + locationDisplayName[DemoSeeder.CompanyName.Length..]
            : locationDisplayName;
    }

    public static bool IsValidColor(string? value) => value is not null && HexColor().IsMatch(value);

    private static Branding? Normalise(Branding branding)
    {
        Branding clean = new(
            string.IsNullOrWhiteSpace(branding.Name) ? null : branding.Name.Trim()[..Math.Min(branding.Name.Trim().Length, 60)],
            IsValidColor(branding.ColorHex) ? branding.ColorHex!.ToUpperInvariant() : null,
            string.IsNullOrEmpty(branding.LogoUrl) ? null : branding.LogoUrl);

        return clean.IsEmpty ? null : clean;
    }

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColor();
}
