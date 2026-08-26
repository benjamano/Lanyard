using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Integration;

// RouteAuthorizationIntegrationTests next door proves the gate *behaves* correctly, but only for
// the handful of pages it names. This class covers the other half of the rule: that every routable
// component actually opts in to one side of it. The two are complementary - a page with no
// attribute at all doesn't fail any behavioural test, it just silently redirects to login (or, if
// the gate ever regresses again, silently doesn't), which is exactly the failure mode
// CLAUDE.md calls "the one rule that must never get missed".
//
// This is a pure reflection test over the Lanyard.App assembly - it boots nothing, so it stays
// fast enough to sit alongside the integration tests without needing a CustomWebApplicationFactory.
[TestClass]
public class RoutableComponentAuthorizationTests
{
    // Routes.razor passes AppAssembly="@typeof(Program).Assembly" to the Router, so enumerating
    // that same assembly is exactly the set of pages the Router can reach.
    private static readonly Assembly AppAssembly = typeof(Program).Assembly;

    private static List<Type> GetRoutableComponents()
    {
        return AppAssembly
            .GetTypes()
            .Where(type => typeof(IComponent).IsAssignableFrom(type))
            .Where(type => type.GetCustomAttributes<RouteAttribute>(inherit: true).Any())
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToList();
    }

    private static string DescribeRoutes(Type component)
    {
        IEnumerable<string> templates = component
            .GetCustomAttributes<RouteAttribute>(inherit: true)
            .Select(route => route.Template);

        return $"{component.FullName} ({string.Join(", ", templates)})";
    }

    [TestMethod]
    public void EveryRoutableComponent_DeclaresAuthorizeOrAllowAnonymous()
    {
        List<Type> offenders = GetRoutableComponents()
            // inherit: true mirrors RouteAuthorizationGate's own
            // Attribute.IsDefined(..., inherit: true) check, so a page that inherits its
            // attribute from a base component counts as covered here too.
            .Where(type => !type.GetCustomAttributes<AuthorizeAttribute>(inherit: true).Any()
                        && !type.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Any())
            .ToList();

        Assert.IsEmpty(
            offenders,
            "Every @page must carry either [Authorize] or [AllowAnonymous] - there is no third "
            + "option. A page with neither is not left public: RouteAuthorizationGate denies it by "
            + "default and redirects to login, which looks like a broken link rather than a "
            + "missing attribute. Add the attribute the page actually wants:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, offenders.Select(o => "  - " + DescribeRoutes(o))));
    }

    [TestMethod]
    public void NoRoutableComponent_DeclaresBothAuthorizeAndAllowAnonymous()
    {
        List<Type> offenders = GetRoutableComponents()
            .Where(type => type.GetCustomAttributes<AuthorizeAttribute>(inherit: true).Any()
                        && type.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Any())
            .ToList();

        Assert.IsEmpty(
            offenders,
            "These pages declare both [Authorize] and [AllowAnonymous]. RouteAuthorizationGate "
            + "checks AllowAnonymous first, so the [Authorize] - including any Roles on it - is "
            + "silently ignored and the page is public. That is almost never what was intended:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, offenders.Select(o => "  - " + DescribeRoutes(o))));
    }

    // Guards the guard: if the reflection above ever stops matching components (a Blazor change to
    // how RouteAttribute is emitted, a namespace move, a trimming setting), both tests above would
    // pass vacuously over an empty set and quietly stop protecting anything.
    [TestMethod]
    public void ReflectionActuallyFindsTheApplicationsPages()
    {
        List<Type> routable = GetRoutableComponents();

        Assert.IsGreaterThanOrEqualTo(
            30,
            routable.Count,
            $"Expected to find the application's routable pages, but only found {routable.Count}. "
            + "Either the app really has that few pages, or this test's reflection no longer "
            + "matches how routes are declared - in which case the two checks above are passing "
            + "over an empty set and protecting nothing.");

        Assert.IsTrue(
            routable.Any(type => type.Name == "Login"),
            "Expected the Login page among the routable components found by reflection.");
    }
}
