using Lanyard.Shared.LaserGame;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Shared.LaserGame;

[TestClass]
public class CaptureFilterBuilderTests
{
    [TestMethod]
    public void Build_SourceAndBroadcastDestination()
    {
        string? filter = CaptureFilterBuilder.Build(["192.168.0.10"], "192.168.0.255");

        Assert.AreEqual(
            "(udp and (src host 192.168.0.10) and dst host 192.168.0.255) or (vlan and udp and (src host 192.168.0.10) and dst host 192.168.0.255)",
            filter);
    }

    [TestMethod]
    public void Build_SeveralSources_AreAlternatives()
    {
        string? filter = CaptureFilterBuilder.Build(["192.168.0.10", "192.168.0.11"], "192.168.0.255");

        StringAssert.StartsWith(filter, "(udp and (src host 192.168.0.10 or src host 192.168.0.11) and dst host 192.168.0.255)");
    }

    [TestMethod]
    public void Build_NoDestination_LeavesOutTheDestinationClause()
    {
        Assert.AreEqual(
            "(udp and (src host 192.168.0.10)) or (vlan and udp and (src host 192.168.0.10))",
            CaptureFilterBuilder.Build(["192.168.0.10"], null));
        Assert.AreEqual(
            "(udp and (src host 192.168.0.10)) or (vlan and udp and (src host 192.168.0.10))",
            CaptureFilterBuilder.Build(["192.168.0.10"], "  "));
    }

    [TestMethod]
    public void Build_TrimsWhatTheAdminTyped()
    {
        StringAssert.StartsWith(CaptureFilterBuilder.Build([" 192.168.0.10 "], " 192.168.0.255"), "(udp and (src host 192.168.0.10) and dst host 192.168.0.255)");
    }

    [TestMethod]
    [DataRow("not an ip", "192.168.0.255")]
    [DataRow("192.168.0.10", "192.168.0.999")]
    [DataRow("192.168.0.10 or tcp", "192.168.0.255")]
    [DataRow("192.168.0.10", "192.168.0.255) or (tcp")]
    public void Build_InvalidAddress_GivesNoFilter(string source, string destination)
    {
        Assert.IsNull(CaptureFilterBuilder.Build([source], destination));
    }

    [TestMethod]
    public void Build_NoSource_GivesNoFilter()
    {
        Assert.IsNull(CaptureFilterBuilder.Build([], "192.168.0.255"));
        Assert.IsNull(CaptureFilterBuilder.Build([null, ""], "192.168.0.255"));
    }

    [TestMethod]
    public void Build_KeepsTaggedVlanTraffic()
    {
        StringAssert.Contains(CaptureFilterBuilder.Build(["10.0.0.5"], "10.0.0.255"), "or (vlan and udp and (src host 10.0.0.5) and dst host 10.0.0.255)");
    }
}
