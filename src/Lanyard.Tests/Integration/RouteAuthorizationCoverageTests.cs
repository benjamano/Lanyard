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
}
