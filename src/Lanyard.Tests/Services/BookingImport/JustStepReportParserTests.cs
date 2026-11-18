using Lanyard.Application.Services.BookingImport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.BookingImport;

[TestClass]
public class JustStepReportParserTests
{
    // Mirrors the shape of a Just Step Online "Report" table (day-header rows, twelve-cell booking
    // rows, a totals row and sub-tables) with invented data.
    private const string Fixture = """
        <table>
          <thead>
            <tr><th>Party Ref</th><th>Party Time</th><th>Contact</th><th>Tel No</th><th>Child Name</th><th>Party Type / Comments</th><th>No.Of children</th><th>Total Due</th><th>Discount</th><th>Paid</th><th>Balance Due</th><th>Book Type</th></tr>
          </thead>
          <tbody>
            <tr><td colspan="12">Friday   02-01-2026</td></tr>
            <tr>
              <td>8388</td><td>Play &amp; Inflatable Exclusive hire 6:30PM - 08:30PM</td><td>Alex Carter</td><td>07000000001</td>
              <td>Robin Carter,2023-01-06,</td><td>Play &amp; Inflatable Exclusive Hire</td><td>50</td><td>300.00</td><td>0.00</td><td>100.00</td><td>200.00</td><td>Confirmed</td>
            </tr>
            <tr>
              <td>8322</td><td>Adventure Play and Laser Party10:30AM - 12:30PM</td><td>Jordan Lee</td><td>07000000002</td>
              <td>Sam Lee,2016-01-02,Boy</td><td>Play and Laser Party</td><td>10</td><td>210.00</td><td>0.00</td><td>50.00</td><td>160.00</td><td>Confirmed</td>
            </tr>
            <tr><td colspan="5">Totals for Friday   02-01-2026</td><td>510</td><td>0.00</td><td>150</td><td>360</td><td></td></tr>
            <tr><td colspan="12">Saturday   03-01-2026</td></tr>
            <tr>
              <td>9107</td><td>Adventure Play Party 11:30am - 01:30pm</td><td>test not a party</td><td></td>
              <td></td><td>Party Info</td><td>10</td><td>180.00</td><td>0.00</td><td>0.00</td><td>180.00</td><td>Provisional</td>
            </tr>
            <tr>
              <td>8400</td><td>4.30pm to 6.30pm</td><td>Chris Moss</td><td>07000000003</td>
              <td>Pat Moss,1970-01-01,Boy</td><td>Adventure Play Party</td><td>12</td><td>216.00</td><td>0.00</td><td>50.00</td><td>166.00</td><td>Confirmed</td>
            </tr>
          </tbody>
        </table>
        """;

    [TestMethod]
    public void Parse_ReadsEveryBookingRowAndSkipsHeadersAndTotals()
    {
        IReadOnlyList<ExternalBookingDto> bookings = JustStepReportParser.Parse(Fixture);

        Assert.AreEqual(4, bookings.Count);
        CollectionAssert.AreEquivalent(new[] { "8388", "8322", "9107", "8400" }, bookings.Select(b => b.Ref).ToArray());
    }

    [TestMethod]
    public void Parse_AttachesTheDayHeaderDateToEachBooking()
    {
        IReadOnlyList<ExternalBookingDto> bookings = JustStepReportParser.Parse(Fixture);

        Assert.AreEqual(new DateOnly(2026, 1, 2), bookings.Single(b => b.Ref == "8388").PartyDate);
        Assert.AreEqual(new DateOnly(2026, 1, 3), bookings.Single(b => b.Ref == "9107").PartyDate);
    }

    [TestMethod]
    public void Parse_ReadsStartAndEndTimesAcrossFormats()
    {
        IReadOnlyList<ExternalBookingDto> bookings = JustStepReportParser.Parse(Fixture);

        ExternalBookingDto evening = bookings.Single(b => b.Ref == "8388");
        Assert.AreEqual(new TimeOnly(18, 30), evening.StartTime);
        Assert.AreEqual(new TimeOnly(20, 30), evening.EndTime);

        // No space before the time, AM/PM once.
        ExternalBookingDto morning = bookings.Single(b => b.Ref == "8322");
        Assert.AreEqual(new TimeOnly(10, 30), morning.StartTime);
        Assert.AreEqual(new TimeOnly(12, 30), morning.EndTime);

        // "4.30pm to 6.30pm": dot separator, shared meridiem, "to" instead of "-".
        ExternalBookingDto dotted = bookings.Single(b => b.Ref == "8400");
        Assert.AreEqual(new TimeOnly(16, 30), dotted.StartTime);
        Assert.AreEqual(new TimeOnly(18, 30), dotted.EndTime);
    }

    [TestMethod]
    public void Parse_ReadsChildNameAndDob_TreatingPlaceholderAsMissing()
    {
        IReadOnlyList<ExternalBookingDto> bookings = JustStepReportParser.Parse(Fixture);

        ExternalBookingDto withDob = bookings.Single(b => b.Ref == "8322");
        Assert.AreEqual("Sam Lee", withDob.ChildName);
        Assert.AreEqual(new DateOnly(2016, 1, 2), withDob.ChildDob);

        // 1970-01-01 is Just Step's "not entered" placeholder.
        ExternalBookingDto placeholder = bookings.Single(b => b.Ref == "8400");
        Assert.IsNull(placeholder.ChildDob);
    }

    [TestMethod]
    public void Parse_ReadsMoneyQuantityAndProvisionalFlag()
    {
        IReadOnlyList<ExternalBookingDto> bookings = JustStepReportParser.Parse(Fixture);

        ExternalBookingDto confirmed = bookings.Single(b => b.Ref == "8388");
        Assert.AreEqual(50, confirmed.Quantity);
        Assert.AreEqual(300.00m, confirmed.TotalDue);
        Assert.AreEqual(100.00m, confirmed.Paid);
        Assert.AreEqual(200.00m, confirmed.Balance);
        Assert.IsFalse(confirmed.IsProvisional);

        Assert.IsTrue(bookings.Single(b => b.Ref == "9107").IsProvisional);
    }

    [TestMethod]
    public void Parse_UsesSlotNameWhenTheTypeColumnIsAComment()
    {
        // Column 6 ("Party Type / Comments") carries a long booking comment; the slot column has the
        // clean type, so that should win.
        const string html = """
            <table><tbody>
            <tr><td colspan="12">Friday   02-01-2026</td></tr>
            <tr>
              <td>9001</td><td>Play &amp; Inflatable Exclusive hire 6:30PM - 08:30PM</td><td>Sam</td><td>07000000009</td>
              <td>Child,2019-01-01,</td>
              <td>Play and Inflatable Private Hire Confirmed 2026-11-10 17:12: 50.00 Paid on a Provisional Booking</td>
              <td>50</td><td>350.00</td><td>0.00</td><td>100.00</td><td>250.00</td><td>Confirmed</td>
            </tr>
            </tbody></table>
            """;

        ExternalBookingDto booking = JustStepReportParser.Parse(html).Single();

        // Raw slot text (the import normalises it to the canonical type afterwards).
        StringAssert.Contains(booking.PartyType, "Exclusive hire");
        Assert.IsFalse(booking.PartyType.Contains("Paid"), "The comment should not leak into the party type.");
    }

    [TestMethod]
    public void Parse_EmptyOrJunkHtml_ReturnsNothing()
    {
        Assert.AreEqual(0, JustStepReportParser.Parse(string.Empty).Count);
        Assert.AreEqual(0, JustStepReportParser.Parse("<html><body><p>No report here</p></body></html>").Count);
    }
}
