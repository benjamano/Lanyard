using Lanyard.Infrastructure.Enum;

namespace Lanyard.Application.Services.Features;

// Marks a routable page as part of a paid-for module. RouteAuthorizationGate refuses to render it
// for a signed-in non-admin whose company has the feature switched off, so a bookmarked or typed
// URL is blocked, not just the nav link hidden.
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class RequiresFeatureAttribute(CompanyFeature feature) : Attribute
{
    public CompanyFeature Feature { get; } = feature;
}
