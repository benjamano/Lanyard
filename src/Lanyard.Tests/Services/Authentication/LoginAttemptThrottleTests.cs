using System.Net;
using Lanyard.Application.Services.Authentication;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Authentication;

[TestClass]
public class LoginAttemptThrottleTests
{
    private static readonly IPAddress Attacker = IPAddress.Parse("203.0.113.5");
    private static readonly IPAddress Elsewhere = IPAddress.Parse("198.51.100.5");

    private static LoginAttemptThrottle Create() =>
        new(new MemoryCache(new MemoryCacheOptions()), NullLogger<LoginAttemptThrottle>.Instance);

    [TestMethod]
    public void ThrottlesAUsername_FromOneAddress_BeforeTheAccountLockoutCouldBeReached()
    {
        LoginAttemptThrottle throttle = Create();

        for (int i = 0; i < LoginAttemptThrottle.PerUsernameLimit; i++)
        {
            Assert.IsFalse(throttle.IsThrottled(Attacker, "admin"));
            throttle.RecordFailure(Attacker, "admin");
        }

        Assert.IsTrue(throttle.IsThrottled(Attacker, "admin"));
        Assert.IsTrue(throttle.IsThrottled(Attacker, " ADMIN "), "Case and spacing must not reset the count.");
        Assert.IsFalse(throttle.IsThrottled(Elsewhere, "admin"), "Another address must be unaffected.");
        Assert.IsFalse(throttle.IsThrottled(Attacker, "someone-else"));
        Assert.IsTrue(LoginAttemptThrottle.PerUsernameLimit < 5, "Must stay below Identity's MaxFailedAccessAttempts.");
    }

    [TestMethod]
    public void ThrottlesAnAddress_AfterTooManyFailuresAcrossUsernames()
    {
        LoginAttemptThrottle throttle = Create();

        for (int i = 0; i < LoginAttemptThrottle.PerIpLimit; i++)
        {
            throttle.RecordFailure(Attacker, $"user{i}");
        }

        Assert.IsTrue(throttle.IsThrottled(Attacker, "brand-new-name"));
        Assert.IsFalse(throttle.IsThrottled(Elsewhere, "brand-new-name"));
    }

    [TestMethod]
    public void Success_ClearsThatUsernamesCount()
    {
        LoginAttemptThrottle throttle = Create();

        for (int i = 0; i < LoginAttemptThrottle.PerUsernameLimit - 1; i++)
        {
            throttle.RecordFailure(Attacker, "admin");
        }

        throttle.RecordSuccess(Attacker, "admin");
        throttle.RecordFailure(Attacker, "admin");

        Assert.IsFalse(throttle.IsThrottled(Attacker, "admin"));
    }
}
