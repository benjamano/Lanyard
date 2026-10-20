using System.Security.Claims;
using Lanyard.App.Help;
using Lanyard.Application.Services.Features;
using Lanyard.Infrastructure.Enum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Components;

// Keeps the help system honest. Every routed page has to be explained by a help area in
// HelpRegistry (or be listed below as having nothing to explain), and every help area has to point
// at a real page whose access rules match the registry - otherwise the /help hub would offer cards
// that lead to access-denied pages. See the help-pages skill.
[TestClass]
public class HelpCoverageTests
{
    // Pages with no feature to explain. Give a reason with each one; anything a user actually works
    // in belongs in a help area instead.
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.OrdinalIgnoreCase)
    {
        ["/"] = "Home page - links out to everything else",
        ["/help"] = "The hub and the help pages themselves (EveryHelpPage_IsRegistered checks those)",
        ["/login"] = "Sign-in flow",
        ["/login/verify-2fa"] = "Sign-in flow",
        ["/logout"] = "Sign-in flow",
        ["/HandleLogin"] = "Sign-in flow",
        ["/HandleLogout"] = "Sign-in flow",
        ["/set-password"] = "Reached from an invite email, explains itself",
        ["/privacy"] = "Legal text",
        ["/not-found"] = "Error page",
        ["/whats-new"] = "Release notes",
        ["/kiosk"] = "Rendered on kiosk screens, not by people",
        ["/video-publisher"] = "Rendered on kiosk screens, not by people"
    };

    private static List<(Type Page, string Route)> AllRoutes() =>
        typeof(Program).Assembly.GetTypes()
            .Where(t => t.IsDefined(typeof(RouteAttribute), inherit: true))
            .SelectMany(t => t.GetCustomAttributes(typeof(RouteAttribute), inherit: true)
                .Cast<RouteAttribute>()
                .Select(r => (t, r.Template)))
            .ToList();

    private static bool Matches(string route, string prefix) =>
        prefix == "/"
            ? route == "/"
            : route.Equals(prefix, StringComparison.OrdinalIgnoreCase)
              || route.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);

    [TestMethod]
    public void EveryPage_IsCoveredByAHelpArea()
    {
        HashSet<string> helpRoutes = HelpRegistry.Areas.Select(a => a.Route).ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<string> offenders = AllRoutes()
            .Select(x => x.Route)
            .Where(route => !helpRoutes.Contains(route))
            .Where(route => !HelpRegistry.Areas.Any(a => a.Covers.Any(prefix => Matches(route, prefix))))
            .Where(route => !Exempt.Keys.Any(prefix => Matches(route, prefix)))
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        Assert.IsTrue(offenders.Count == 0,
            "These pages aren't explained by any help area. Add or extend a HelpRegistry entry (see the help-pages skill):\n"
            + string.Join('\n', offenders));
    }

    // The "/help" exemption above would otherwise hide a help page nobody registered - one the hub
    // and Help buttons can never reach.
    [TestMethod]
    public void EveryHelpPage_IsRegistered()
    {
        HashSet<string> registered = HelpRegistry.Areas.Select(a => a.Route).ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<string> orphans = AllRoutes()
            .Select(x => x.Route)
            .Where(route => route.StartsWith("/help/", StringComparison.OrdinalIgnoreCase) && !registered.Contains(route))
            .ToList();

        Assert.IsTrue(orphans.Count == 0, "Help pages missing from HelpRegistry:\n" + string.Join('\n', orphans));
    }

    [TestMethod]
    public void EveryHelpArea_PointsAtAPageWithMatchingAccessRules()
    {
        List<(Type Page, string Route)> routes = AllRoutes();
        List<string> problems = [];

        foreach (HelpArea area in HelpRegistry.Areas)
        {
            (Type Page, string Route) match = routes.FirstOrDefault(r => r.Route.Equals(area.Route, StringComparison.OrdinalIgnoreCase));

            if (match.Page is null)
            {
                problems.Add($"{area.Key}: no page has @page \"{area.Route}\"");
                continue;
            }

            AuthorizeAttribute? authorize = match.Page.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
                .Cast<AuthorizeAttribute>()
                .FirstOrDefault();

            if (authorize is null)
            {
                problems.Add($"{area.Key}: {area.Route} must be [Authorize] - help pages are for signed-in users");
            }
            else if (!SameRoles(authorize.Roles, area.Roles))
            {
                problems.Add($"{area.Key}: page roles '{authorize.Roles}' don't match registry roles '{area.Roles}'");
            }

            RequiresFeatureAttribute? feature = match.Page.GetCustomAttributes(typeof(RequiresFeatureAttribute), inherit: true)
                .Cast<RequiresFeatureAttribute>()
                .SingleOrDefault();

            if (feature?.Feature != area.Feature)
            {
                problems.Add($"{area.Key}: page feature '{feature?.Feature}' doesn't match registry feature '{area.Feature}'");
            }
        }

        Assert.IsTrue(problems.Count == 0, string.Join('\n', problems));
    }

    [TestMethod]
    public void HelpAreas_HaveUniqueKeysRoutesAndTopics()
    {
        Assert.AreEqual(HelpRegistry.Areas.Count, HelpRegistry.Areas.Select(a => a.Key).Distinct().Count(), "Duplicate help area key");
        Assert.AreEqual(HelpRegistry.Areas.Count, HelpRegistry.Areas.Select(a => a.Route).Distinct(StringComparer.OrdinalIgnoreCase).Count(), "Duplicate help route");

        foreach (HelpArea area in HelpRegistry.Areas)
        {
            Assert.IsTrue(area.Topics.Count > 0, $"{area.Key} has no topics");
            Assert.AreEqual(area.Topics.Count, area.Topics.Select(t => t.Key).Distinct().Count(), $"{area.Key} has duplicate topic keys");
        }
    }

    [TestMethod]
    public void UrlFor_IgnoresUnknownTopics()
    {
        HelpArea area = HelpRegistry.Get(HelpRegistry.ManageTraining);

        Assert.AreEqual("/help/training?topic=courses", area.UrlFor("courses"));
        Assert.AreEqual("/help/training", area.UrlFor("nonsense"));
        Assert.AreEqual("/help/training", area.UrlFor(null));
    }

    [TestMethod]
    public void IsVisibleTo_FiltersByRoleAndCompanyFeature()
    {
        HelpArea manage = HelpRegistry.Get(HelpRegistry.ManageTraining);
        HelpArea mine = HelpRegistry.Get(HelpRegistry.MyTraining);
        HashSet<CompanyFeature> none = [];
        HashSet<CompanyFeature> noTraining = [CompanyFeature.Training];

        ClaimsPrincipal staff = User();
        ClaimsPrincipal manager = User("Manager");

        Assert.IsTrue(mine.IsVisibleTo(staff, none));
        Assert.IsFalse(manage.IsVisibleTo(staff, none));
        Assert.IsTrue(manage.IsVisibleTo(manager, none));
        Assert.IsFalse(manage.IsVisibleTo(manager, noTraining));
        Assert.IsFalse(mine.IsVisibleTo(new ClaimsPrincipal(new ClaimsIdentity()), none));
    }

    private static ClaimsPrincipal User(params string[] roles) =>
        new(new ClaimsIdentity(roles.Select(r => new Claim(ClaimTypes.Role, r)).Append(new Claim(ClaimTypes.Name, "test")), "Test"));

    private static bool SameRoles(string? a, string? b)
    {
        static HashSet<string> Split(string? roles) =>
            (roles ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return Split(a).SetEquals(Split(b));
    }
}
