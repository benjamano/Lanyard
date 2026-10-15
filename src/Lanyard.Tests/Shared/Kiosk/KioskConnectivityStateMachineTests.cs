using Lanyard.Shared.Kiosk;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Shared.Kiosk;

[TestClass]
public class KioskConnectivityStateMachineTests
{
    private static readonly DateTime Start = new(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc);

    private static KioskConnectivityStateMachine Connected()
    {
        KioskConnectivityStateMachine machine = new(Start);
        machine.Configure(true, TimeSpan.FromSeconds(30), Start);
        machine.SetConnected(true, Start);
        return machine;
    }

    [TestMethod]
    public void AShortDrop_StaysOnline()
    {
        KioskConnectivityStateMachine machine = Connected();

        machine.SetConnected(false, Start.AddSeconds(10));
        Assert.AreEqual(KioskConnectivityTransition.None, machine.Tick(Start.AddSeconds(39), canPlayOffline: true));

        machine.SetConnected(true, Start.AddSeconds(39));
        Assert.AreEqual(KioskConnectivityState.Online, machine.State);
        Assert.AreEqual(KioskConnectivityTransition.None, machine.Tick(Start.AddSeconds(120), canPlayOffline: true));
    }

    [TestMethod]
    public void DisconnectedForTheConfiguredTime_GoesOffline()
    {
        KioskConnectivityStateMachine machine = Connected();

        machine.SetConnected(false, Start.AddSeconds(10));

        Assert.AreEqual(KioskConnectivityTransition.EnteredOffline, machine.Tick(Start.AddSeconds(40), canPlayOffline: true));
        Assert.AreEqual(KioskConnectivityState.Offline, machine.State);
        Assert.AreEqual(KioskConnectivityTransition.None, machine.Tick(Start.AddSeconds(41), canPlayOffline: true), "Entering offline is reported once.");
    }

    [TestMethod]
    public void BootingWhileTheServerIsDown_GoesOfflineAfterTheSameDelay()
    {
        KioskConnectivityStateMachine machine = new(Start);
        machine.Configure(true, TimeSpan.FromSeconds(30), Start);

        Assert.AreEqual(KioskConnectivityTransition.None, machine.Tick(Start.AddSeconds(29), canPlayOffline: true));
        Assert.AreEqual(KioskConnectivityTransition.EnteredOffline, machine.Tick(Start.AddSeconds(30), canPlayOffline: true));
    }

    [TestMethod]
    public void NothingCached_KeepsWaitingInsteadOfGoingOffline()
    {
        KioskConnectivityStateMachine machine = new(Start);

        Assert.AreEqual(KioskConnectivityTransition.None, machine.Tick(Start.AddMinutes(5), canPlayOffline: false));
        Assert.AreEqual(KioskConnectivityState.Grace, machine.State);

        // Once a cache turns up, it switches straight over.
        Assert.AreEqual(KioskConnectivityTransition.EnteredOffline, machine.Tick(Start.AddMinutes(5), canPlayOffline: true));
    }

    [TestMethod]
    public void Disabled_NeverGoesOffline()
    {
        KioskConnectivityStateMachine machine = new(Start);
        machine.Configure(false, TimeSpan.FromSeconds(30), Start);

        Assert.AreEqual(KioskConnectivityTransition.None, machine.Tick(Start.AddHours(1), canPlayOffline: true));
    }

    [TestMethod]
    public void Reconnecting_HandsBackOnlyOnceTheConnectionHasStayedUp()
    {
        KioskConnectivityStateMachine machine = Connected();
        machine.SetConnected(false, Start);
        machine.Tick(Start.AddSeconds(30), canPlayOffline: true);

        machine.SetConnected(true, Start.AddSeconds(100));
        Assert.AreEqual(KioskConnectivityState.Recovering, machine.State);
        Assert.AreEqual(KioskConnectivityTransition.None, machine.Tick(Start.AddSeconds(109), canPlayOffline: true));

        Assert.AreEqual(KioskConnectivityTransition.ReturnedOnline, machine.Tick(Start.AddSeconds(110), canPlayOffline: true));
        Assert.AreEqual(KioskConnectivityState.Online, machine.State);
    }

    [TestMethod]
    public void AFlappingConnection_KeepsPlayingOffline()
    {
        KioskConnectivityStateMachine machine = Connected();
        machine.SetConnected(false, Start);
        machine.Tick(Start.AddSeconds(30), canPlayOffline: true);

        for (int i = 0; i < 5; i++)
        {
            DateTime up = Start.AddSeconds(100 + i * 8);
            machine.SetConnected(true, up);
            Assert.AreEqual(KioskConnectivityTransition.None, machine.Tick(up.AddSeconds(5), canPlayOffline: true));
            machine.SetConnected(false, up.AddSeconds(6));
            Assert.AreEqual(KioskConnectivityState.Offline, machine.State);
        }
    }

    [TestMethod]
    public void TurningTheFallbackOffWhileOffline_HandsBackStraightAway()
    {
        KioskConnectivityStateMachine machine = Connected();
        machine.SetConnected(false, Start);
        machine.Tick(Start.AddSeconds(30), canPlayOffline: true);

        Assert.AreEqual(KioskConnectivityTransition.ReturnedOnline, machine.Configure(false, TimeSpan.FromSeconds(30), Start.AddSeconds(60)));
        Assert.AreEqual(KioskConnectivityState.Grace, machine.State);
        Assert.AreEqual(KioskConnectivityTransition.None, machine.Tick(Start.AddHours(1), canPlayOffline: true));
    }
}
