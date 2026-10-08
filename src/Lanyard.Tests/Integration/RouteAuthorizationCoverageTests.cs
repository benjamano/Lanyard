using Lanyard.Application.Services.Features;
using Lanyard.Infrastructure.Enum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Integration;

// Reflection-only companion to RouteAuthorizationIntegrationTests.cs: rather than exercising a
// handful of routes through the real HTTP pipeline, this enumerates every @page component in
// Lanyard.App (each compiles to a class carrying [RouteAttribute]) and asserts none of them can
// slip through review without an explicit [Authorize] or [AllowAnonymous]. See the
// route-authorization skill for why a missing attribute is never safe by default.
[TestClass]
public class RouteAuthorizationCoverageTests
{
    [TestMethod]
    public void AllPages_HaveExplicitAuthorizationAttribute()
    {
        List<string?> offenders = typeof(Program).Assembly.GetTypes()
            .Where(t => t.IsDefined(typeof(RouteAttribute), inherit: true))
            .Where(t => !t.IsDefined(typeof(AuthorizeAttribute), inherit: true)
                     && !t.IsDefined(typeof(AllowAnonymousAttribute), inherit: true))
            .Select(t => t.FullName)
            .ToList();

        Assert.IsTrue(offenders.Count == 0,
            "The following @page components have neither [Authorize] nor [AllowAnonymous]:\n"
            + string.Join('\n', offenders));
    }

    // Every page in a paid-for module has to say which one, or RouteAuthorizationGate lets staff at
    // a company without it straight in by URL. Matched by route prefix so a new page in one of
    // these areas fails here until it's tagged. The clock-in terminal and QR scan pages are the
    // exceptions: they're anonymous, so ClockInTerminalService checks the terminal's company.
    [TestMethod]
    public void PaidModulePages_CarryRequiresFeature()
    {
        (string Prefix, CompanyFeature Feature)[] areas =
        [
            ("/training", CompanyFeature.Training),
            ("/manage/training", CompanyFeature.Training),
            ("/rota", CompanyFeature.StaffScheduling),
            ("/manage/rota", CompanyFeature.StaffScheduling),
            ("/chat", CompanyFeature.Chat),
            ("/manage/chat", CompanyFeature.Chat),
            ("/manage/onboarding", CompanyFeature.Onboarding),
            ("/manage/staff-documents", CompanyFeature.Onboarding),
            ("/manage/announcements", CompanyFeature.Announcements),
            ("/manage/parties", CompanyFeature.Parties)
        ];

        string[] anonymousExceptions = ["/rota/terminal", "/rota/scan/"];

        List<string> offenders = [];

        foreach (Type page in typeof(Program).Assembly.GetTypes().Where(t => t.IsDefined(typeof(RouteAttribute), inherit: true)))
        {
            RequiresFeatureAttribute? tagged = page.GetCustomAttributes(typeof(RequiresFeatureAttribute), inherit: true)
                .Cast<RequiresFeatureAttribute>()
                .SingleOrDefault();

            foreach (RouteAttribute route in page.GetCustomAttributes(typeof(RouteAttribute), inherit: true).Cast<RouteAttribute>())
            {
                if (anonymousExceptions.Any(x => route.Template.StartsWith(x, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                // Longest prefix wins, so /manage/rota isn't mistaken for anything shorter.
                (string Prefix, CompanyFeature Feature)? area = areas
                    .Where(a => route.Template.Equals(a.Prefix, StringComparison.OrdinalIgnoreCase)
                        || route.Template.StartsWith(a.Prefix + "/", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(a => a.Prefix.Length)
                    .Cast<(string, CompanyFeature)?>()
                    .FirstOrDefault();

                if (area is not null && tagged?.Feature != area.Value.Feature)
                {
                    offenders.Add($"{route.Template} ({page.FullName}) needs [RequiresFeature(CompanyFeature.{area.Value.Feature})]");
                }
            }
        }

        Assert.IsTrue(offenders.Count == 0, string.Join('\n', offenders));
    }
}
