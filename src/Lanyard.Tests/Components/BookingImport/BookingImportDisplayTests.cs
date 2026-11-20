using Lanyard.App.Components.BookingImport;
using Lanyard.Application.Services.BookingImport;
using Lanyard.Infrastructure.Enum;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Components.BookingImport;

[TestClass]
public class BookingImportDisplayTests
{
    private static BookingImportRunView Run(int imported = 0, int updated = 0, int failed = 0, int seen = 0) =>
        new(Guid.NewGuid(), 1, DateTime.UtcNow, DateTime.UtcNow, BookingImportRunStatus.Success,
            BookingImportTrigger.Scheduled, seen, imported, updated, 0, failed, null);

    [TestMethod]
    public void StatusTone_MapsFailuresToNegativeAndSuccessToPositive()
    {
        Assert.AreEqual(BookingImportTone.Positive, BookingImportDisplay.StatusTone(BookingImportRunStatus.Success));
        Assert.AreEqual(BookingImportTone.Positive, BookingImportDisplay.StatusTone(BookingImportRunStatus.NoChanges));
        Assert.AreEqual(BookingImportTone.Caution, BookingImportDisplay.StatusTone(BookingImportRunStatus.PartialSuccess));
        Assert.AreEqual(BookingImportTone.Negative, BookingImportDisplay.StatusTone(BookingImportRunStatus.LoginFailed));
        Assert.AreEqual(BookingImportTone.Negative, BookingImportDisplay.StatusTone(BookingImportRunStatus.Failed));
        Assert.AreEqual(BookingImportTone.Neutral, BookingImportDisplay.StatusTone(BookingImportRunStatus.Skipped));
    }

    [TestMethod]
    public void Counts_DescribesNewUpdatedAndFailed()
    {
        Assert.AreEqual("12 new, 3 updated", BookingImportDisplay.Counts(Run(imported: 12, updated: 3)));
        Assert.AreEqual("1 couldn't be imported", BookingImportDisplay.Counts(Run(failed: 1)));
        Assert.AreEqual("5 new, 2 couldn't be imported", BookingImportDisplay.Counts(Run(imported: 5, failed: 2)));
    }

    [TestMethod]
    public void Counts_NothingFoundVsNothingChanged()
    {
        Assert.AreEqual("No bookings found", BookingImportDisplay.Counts(Run(seen: 0)));
        Assert.AreEqual("Nothing changed", BookingImportDisplay.Counts(Run(seen: 10)));
    }

    [TestMethod]
    public void Ago_ReadsAsRelativeTime()
    {
        Assert.AreEqual("just now", BookingImportDisplay.Ago(TimeSpan.FromSeconds(10)));
        Assert.AreEqual("1 minute ago", BookingImportDisplay.Ago(TimeSpan.FromMinutes(1)));
        Assert.AreEqual("5 minutes ago", BookingImportDisplay.Ago(TimeSpan.FromMinutes(5)));
        Assert.AreEqual("2 hours ago", BookingImportDisplay.Ago(TimeSpan.FromHours(2)));
        Assert.AreEqual("3 days ago", BookingImportDisplay.Ago(TimeSpan.FromDays(3)));
    }
}
