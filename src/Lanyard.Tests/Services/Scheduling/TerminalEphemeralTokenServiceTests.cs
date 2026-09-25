using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DTO.Scheduling;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Scheduling;

[TestClass]
public class TerminalEphemeralTokenServiceTests
{
    private static (TerminalEphemeralTokenService Service, TestClock Clock) Create()
    {
        TestClock clock = new(new DateTime(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc));
        return (new TerminalEphemeralTokenService(clock), clock);
    }

    [TestMethod]
    public void PairingCode_CanBePeekedButOnlyConsumedOnce()
    {
        (TerminalEphemeralTokenService service, _) = Create();

        string code = service.IssuePairingCode(3, "Front desk", "manager", signOutAfterPairing: true);

        PendingPairing? peeked = service.PeekPairingCode(code);
        PendingPairing? first = service.ConsumePairingCode(code);
        PendingPairing? second = service.ConsumePairingCode(code);

        Assert.IsNotNull(peeked);
        Assert.AreEqual(3, first!.LocationId);
        Assert.AreEqual("Front desk", first.TerminalName);
        Assert.IsTrue(first.SignOutAfterPairing);
        Assert.IsNull(second);
    }

    [TestMethod]
    public void PairingCode_ExpiresAfterFiveMinutes()
    {
        (TerminalEphemeralTokenService service, TestClock clock) = Create();
        string code = service.IssuePairingCode(3, "Front desk", "manager", false);

        clock.Advance(TimeSpan.FromMinutes(5).Add(TimeSpan.FromSeconds(1)));

        Assert.IsNull(service.PeekPairingCode(code));
        Assert.IsNull(service.ConsumePairingCode(code));
    }

    [TestMethod]
    public void QrNonce_IsSingleUseAndPeekDoesNotConsume()
    {
        (TerminalEphemeralTokenService service, _) = Create();
        Guid terminalId = Guid.NewGuid();

        string nonce = service.IssueQrNonce(terminalId);

        Assert.AreEqual(terminalId, service.PeekQrNonce(nonce));
        Assert.AreEqual(terminalId, service.ConsumeQrNonce(nonce));
        Assert.IsNull(service.ConsumeQrNonce(nonce));
    }

    [TestMethod]
    public void QrNonce_HeldForSignInOnce_ThenStillSingleUse()
    {
        (TerminalEphemeralTokenService service, TestClock clock) = Create();
        Guid terminalId = Guid.NewGuid();
        string nonce = service.IssueQrNonce(terminalId);

        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.IsTrue(service.HoldQrNonceForSignIn(nonce));
        Assert.IsFalse(service.HoldQrNonceForSignIn(nonce), "Only once");

        // Well past the usual minute, while they sign in.
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.AreEqual(terminalId, service.ConsumeQrNonce(nonce));
        Assert.IsNull(service.ConsumeQrNonce(nonce));
    }

    [TestMethod]
    public void QrNonce_CannotBeHeldOnceExpired()
    {
        (TerminalEphemeralTokenService service, TestClock clock) = Create();
        string nonce = service.IssueQrNonce(Guid.NewGuid());

        clock.Advance(TimeSpan.FromSeconds(61));

        Assert.IsFalse(service.HoldQrNonceForSignIn(nonce));
        Assert.IsNull(service.ConsumeQrNonce(nonce));
    }

    [TestMethod]
    public void QrNonce_StillValidJustAfterRotationButExpiresAfterAMinute()
    {
        (TerminalEphemeralTokenService service, TestClock clock) = Create();
        string nonce = service.IssueQrNonce(Guid.NewGuid());

        clock.Advance(TimeSpan.FromSeconds(40));
        Assert.IsNotNull(service.PeekQrNonce(nonce));

        clock.Advance(TimeSpan.FromSeconds(21));
        Assert.IsNull(service.ConsumeQrNonce(nonce));
    }

    [TestMethod]
    public void PinFailures_LockAfterFive_ForThirtySeconds_AcrossEveryTerminal()
    {
        (TerminalEphemeralTokenService service, TestClock clock) = Create();

        for (int i = 0; i < 4; i++)
        {
            Assert.IsNull(service.RegisterPinFailure("ben"));
            clock.Advance(TimeSpan.FromSeconds(5));
        }

        DateTime? until = service.RegisterPinFailure("ben");
        Assert.IsNotNull(until);
        Assert.AreEqual(TerminalEphemeralTokenService.PinLockout, until.Value - clock.GetUtcNow().UtcDateTime);
        Assert.IsNotNull(service.PinLockedUntil("ben"), "Locked whichever tablet they try next");
        Assert.IsNull(service.PinLockedUntil("amy"));

        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.IsNull(service.PinLockedUntil("ben"));
    }

    [TestMethod]
    public void PinFailures_SlowGuessingStillLocks()
    {
        (TerminalEphemeralTokenService service, TestClock clock) = Create();

        for (int i = 0; i < 4; i++)
        {
            Assert.IsNull(service.RegisterPinFailure("ben"));
            clock.Advance(TimeSpan.FromMinutes(10));
        }

        Assert.IsNotNull(service.RegisterPinFailure("ben"));
    }

    [TestMethod]
    public void PinFailures_EachLockoutIsTwiceTheLast_UpToAnHour()
    {
        (TerminalEphemeralTokenService service, TestClock clock) = Create();
        List<TimeSpan> lockouts = [];

        for (int round = 0; round < 10; round++)
        {
            DateTime? until = null;

            for (int i = 0; i < TerminalEphemeralTokenService.MaxPinFailures; i++)
            {
                until = service.RegisterPinFailure("ben");
            }

            TimeSpan lockout = until!.Value - clock.GetUtcNow().UtcDateTime;
            lockouts.Add(lockout);
            clock.Advance(lockout + TimeSpan.FromSeconds(1));
        }

        Assert.AreEqual(TimeSpan.FromSeconds(30), lockouts[0]);
        Assert.AreEqual(TimeSpan.FromSeconds(60), lockouts[1]);
        Assert.AreEqual(TimeSpan.FromSeconds(120), lockouts[2]);
        Assert.AreEqual(TerminalEphemeralTokenService.MaxPinLockout, lockouts[^1]);
    }

    [TestMethod]
    public void PinFailures_AreForgottenAfterADayWithoutAWrongPin()
    {
        (TerminalEphemeralTokenService service, TestClock clock) = Create();

        for (int round = 0; round < 3; round++)
        {
            for (int i = 0; i < TerminalEphemeralTokenService.MaxPinFailures; i++)
            {
                service.RegisterPinFailure("ben");
            }

            clock.Advance(TimeSpan.FromHours(1));
        }

        clock.Advance(TimeSpan.FromHours(25));

        for (int i = 0; i < TerminalEphemeralTokenService.MaxPinFailures - 1; i++)
        {
            Assert.IsNull(service.RegisterPinFailure("ben"));
        }

        DateTime? until = service.RegisterPinFailure("ben");
        Assert.AreEqual(TerminalEphemeralTokenService.PinLockout, until!.Value - clock.GetUtcNow().UtcDateTime, "Back to the first, shortest lockout");
    }

    [TestMethod]
    public void ClearPinFailures_ResetsTheCount()
    {
        (TerminalEphemeralTokenService service, _) = Create();

        for (int i = 0; i < 4; i++)
        {
            service.RegisterPinFailure("ben");
        }

        service.ClearPinFailures("ben");

        Assert.IsNull(service.RegisterPinFailure("ben"));
    }
}
