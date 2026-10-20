using Lanyard.Application.Services.Clients;
using Lanyard.Infrastructure.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Clients;

[TestClass]
public class ClientVersionReportTests
{
    private static readonly DateTime EarlierReport = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);

    private static Client ClientThatReported(string? version) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Kiosk",
        ReportedClientVersion = version,
        ClientVersionReportedAt = version is null ? null : EarlierReport
    };

    [TestMethod]
    public void Apply_RecordsValidVersionAndTime()
    {
        Client client = ClientThatReported("1.0.40");

        ClientVersionReportOutcome outcome = ClientVersionReport.Apply(client, "1.0.41", Now);

        Assert.AreEqual(ClientVersionReportOutcome.Recorded, outcome);
        Assert.AreEqual("1.0.41", client.ReportedClientVersion);
        Assert.AreEqual(Now, client.ClientVersionReportedAt);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    public void Apply_KeepsStoredVersion_WhenAnOldClientSendsNone(string? raw)
    {
        // An old build reconnecting (or a rollback to one) must not blank the last known version.
        Client client = ClientThatReported("1.0.41");

        ClientVersionReportOutcome outcome = ClientVersionReport.Apply(client, raw, Now);

        Assert.AreEqual(ClientVersionReportOutcome.NotReported, outcome);
        Assert.AreEqual("1.0.41", client.ReportedClientVersion);
        Assert.AreEqual(EarlierReport, client.ClientVersionReportedAt);
    }

    [TestMethod]
    public void Apply_IgnoresInvalidVersion()
    {
        Client client = ClientThatReported("1.0.41");

        ClientVersionReportOutcome outcome = ClientVersionReport.Apply(client, new string('9', 500), Now);

        Assert.AreEqual(ClientVersionReportOutcome.Rejected, outcome);
        Assert.AreEqual("1.0.41", client.ReportedClientVersion);
        Assert.AreEqual(EarlierReport, client.ClientVersionReportedAt);
    }

    [TestMethod]
    public void Apply_LeavesNewClientUnknown_WhenNothingReported()
    {
        Client client = ClientThatReported(null);

        ClientVersionReport.Apply(client, null, Now);

        Assert.IsNull(client.ReportedClientVersion);
        Assert.IsNull(client.ClientVersionReportedAt);
    }
}
