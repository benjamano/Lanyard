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
    public void PinFailures_LockAfterFiveWithinAMinuteThenUnlockAfterThirtySeconds()
    {
        (TerminalEphemeralTokenService service, TestClock clock) = Create();
        Guid terminalId = Guid.NewGuid();

        for (int i = 0; i < 4; i++)
        {
            Assert.IsFalse(service.RegisterPinFailure(terminalId, "ben"));
            clock.Advance(TimeSpan.FromSeconds(5));
        }

        Assert.IsTrue(service.RegisterPinFailure(terminalId, "ben"));
        Assert.IsTrue(service.IsPinLocked(terminalId, "ben"));
        Assert.IsFalse(service.IsPinLocked(terminalId, "amy"));
        Assert.IsFalse(service.IsPinLocked(Guid.NewGuid(), "ben"));

        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.IsFalse(service.IsPinLocked(terminalId, "ben"));
    }

    [TestMethod]
    public void PinFailures_SpreadOverMoreThanAMinuteDoNotLock()
    {
        (TerminalEphemeralTokenService service, TestClock clock) = Create();
        Guid terminalId = Guid.NewGuid();

        for (int i = 0; i < 8; i++)
        {
            Assert.IsFalse(service.RegisterPinFailure(terminalId, "ben"));
            clock.Advance(TimeSpan.FromSeconds(20));
        }
    }

    [TestMethod]
    public void ClearPinFailures_ResetsTheCount()
    {
        (TerminalEphemeralTokenService service, _) = Create();
        Guid terminalId = Guid.NewGuid();

        for (int i = 0; i < 4; i++)
        {
            service.RegisterPinFailure(terminalId, "ben");
        }

        service.ClearPinFailures(terminalId, "ben");

        Assert.IsFalse(service.RegisterPinFailure(terminalId, "ben"));
    }
}
