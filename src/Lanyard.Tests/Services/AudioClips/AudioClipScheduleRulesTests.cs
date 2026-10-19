using Lanyard.Application.Services.AudioClips;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.AudioClips;

[TestClass]
public class AudioClipScheduleRulesTests
{
    // Friday 16 October 2026.
    private static readonly DateOnly Friday = new(2026, 10, 16);
    private const string EveryDay = "0,1,2,3,4,5,6";

    private static AudioClipSchedule Schedule(AudioClipScheduleAnchor anchor, int offset = 15, string time = "12:00", string days = EveryDay) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Test",
        Anchor = anchor,
        OffsetMinutes = offset,
        TimeOfDay = TimeOnly.Parse(time),
        DaysOfWeek = days,
        CreateDate = new DateTime(2026, 10, 1)
    };

    private static LocationOpeningHours Hours(DayOfWeek day, string opens, string closes) =>
        new() { LocationId = 1, DayOfWeek = day, OpensAt = TimeOnly.Parse(opens), ClosesAt = TimeOnly.Parse(closes), IsActive = true };

    [TestMethod]
    public void AtTime_PlaysAtThatTimeOnTheChosenDays()
    {
        AudioClipSchedule schedule = Schedule(AudioClipScheduleAnchor.AtTime, time: "12:30", days: "5");

        CollectionAssert.AreEqual(new[] { new DateTime(2026, 10, 16, 12, 30, 0) }, AudioClipScheduleRules.OccurrencesOn(schedule, [], Friday).ToArray());
        Assert.AreEqual(0, AudioClipScheduleRules.OccurrencesOn(schedule, [], Friday.AddDays(1)).Count());
    }

    [TestMethod]
    public void BeforeClosing_PlaysThatLongBeforeTheClose()
    {
        DateTime[] plays = AudioClipScheduleRules.OccurrencesOn(Schedule(AudioClipScheduleAnchor.BeforeClosing), [Hours(DayOfWeek.Friday, "10:00", "22:00")], Friday).ToArray();

        CollectionAssert.AreEqual(new[] { new DateTime(2026, 10, 16, 21, 45, 0) }, plays);
    }

    [TestMethod]
    public void AfterOpening_PlaysThatLongAfterOpening()
    {
        DateTime[] plays = AudioClipScheduleRules.OccurrencesOn(Schedule(AudioClipScheduleAnchor.AfterOpening, offset: 5), [Hours(DayOfWeek.Friday, "10:00", "22:00")], Friday).ToArray();

        CollectionAssert.AreEqual(new[] { new DateTime(2026, 10, 16, 10, 5, 0) }, plays);
    }

    [TestMethod]
    public void BeforeClosing_OnALateNight_PlaysAfterMidnightButBelongsToTheDayItOpened()
    {
        DateTime[] plays = AudioClipScheduleRules.OccurrencesOn(Schedule(AudioClipScheduleAnchor.BeforeClosing, days: "5"), [Hours(DayOfWeek.Friday, "18:00", "02:00")], Friday).ToArray();

        CollectionAssert.AreEqual(new[] { new DateTime(2026, 10, 17, 1, 45, 0) }, plays);
    }

    [TestMethod]
    public void TwoWindowsInADay_PlayTwice()
    {
        LocationOpeningHours[] hours = [Hours(DayOfWeek.Friday, "18:00", "23:00"), Hours(DayOfWeek.Friday, "09:00", "13:00")];

        DateTime[] plays = AudioClipScheduleRules.OccurrencesOn(Schedule(AudioClipScheduleAnchor.BeforeClosing), hours, Friday).ToArray();

        CollectionAssert.AreEqual(new[] { new DateTime(2026, 10, 16, 12, 45, 0), new DateTime(2026, 10, 16, 22, 45, 0) }, plays);
    }

    [TestMethod]
    public void AClosedDay_HasNoClosingCall()
    {
        Assert.AreEqual(0, AudioClipScheduleRules.OccurrencesOn(Schedule(AudioClipScheduleAnchor.BeforeClosing), [Hours(DayOfWeek.Saturday, "10:00", "22:00")], Friday).Count());
    }

    [TestMethod]
    public void InactiveHours_AreIgnored()
    {
        LocationOpeningHours hours = Hours(DayOfWeek.Friday, "10:00", "22:00");
        hours.IsActive = false;

        Assert.AreEqual(0, AudioClipScheduleRules.OccurrencesOn(Schedule(AudioClipScheduleAnchor.BeforeClosing), [hours], Friday).Count());
    }

    [TestMethod]
    public void AnOffsetLongerThanTheWindow_StaysWithinOpeningHours()
    {
        DateTime[] plays = AudioClipScheduleRules.OccurrencesOn(Schedule(AudioClipScheduleAnchor.BeforeClosing, offset: 120), [Hours(DayOfWeek.Friday, "10:00", "11:00")], Friday).ToArray();

        CollectionAssert.AreEqual(new[] { new DateTime(2026, 10, 16, 10, 0, 0) }, plays);
    }

    [TestMethod]
    public void DueAt_IsTrueFromTheTimeForTwoMinutes()
    {
        AudioClipSchedule schedule = Schedule(AudioClipScheduleAnchor.BeforeClosing);
        LocationOpeningHours[] hours = [Hours(DayOfWeek.Friday, "10:00", "22:00")];

        Assert.IsNull(AudioClipScheduleRules.DueAt(schedule, hours, new DateTime(2026, 10, 16, 21, 44, 59)));
        Assert.AreEqual(new DateTime(2026, 10, 16, 21, 45, 0), AudioClipScheduleRules.DueAt(schedule, hours, new DateTime(2026, 10, 16, 21, 45, 10)));
        Assert.AreEqual(new DateTime(2026, 10, 16, 21, 45, 0), AudioClipScheduleRules.DueAt(schedule, hours, new DateTime(2026, 10, 16, 21, 46, 59)));
        Assert.IsNull(AudioClipScheduleRules.DueAt(schedule, hours, new DateTime(2026, 10, 16, 21, 47, 0)));
    }

    [TestMethod]
    public void DueAt_FindsYesterdaysLateClose()
    {
        AudioClipSchedule schedule = Schedule(AudioClipScheduleAnchor.BeforeClosing, days: "5");

        DateTime? due = AudioClipScheduleRules.DueAt(schedule, [Hours(DayOfWeek.Friday, "18:00", "02:00")], new DateTime(2026, 10, 17, 1, 45, 30));

        Assert.AreEqual(new DateTime(2026, 10, 17, 1, 45, 0), due);
    }

    [TestMethod]
    public void Upcoming_ListsTheNextPlaysInOrder()
    {
        AudioClipSchedule schedule = Schedule(AudioClipScheduleAnchor.AtTime, time: "12:00", days: "1,5");

        List<DateTime> next = AudioClipScheduleRules.Upcoming(schedule, [], new DateTime(2026, 10, 16, 13, 0, 0));

        CollectionAssert.AreEqual(new[] { new DateTime(2026, 10, 19, 12, 0, 0), new DateTime(2026, 10, 23, 12, 0, 0), new DateTime(2026, 10, 26, 12, 0, 0) }, next);
    }

    [TestMethod]
    public void OccurrenceKey_ChangesWhenTheScheduleIsEdited()
    {
        AudioClipSchedule schedule = Schedule(AudioClipScheduleAnchor.AtTime);
        DateTime at = new(2026, 10, 16, 12, 0, 0);

        string before = AudioClipScheduleRules.OccurrenceKey(schedule, at);
        schedule.UpdateDate = new DateTime(2026, 10, 16, 11, 0, 0);

        Assert.AreNotEqual(before, AudioClipScheduleRules.OccurrenceKey(schedule, at));
    }

    [TestMethod]
    [DataRow("", AudioClipScheduleAnchor.AtTime, EveryDay, 0)]
    [DataRow("Call", AudioClipScheduleAnchor.AtTime, "", 0)]
    [DataRow("Call", AudioClipScheduleAnchor.BeforeClosing, EveryDay, 241)]
    [DataRow("Call", AudioClipScheduleAnchor.BeforeClosing, EveryDay, -1)]
    public void Validate_RefusesAnIncompleteSchedule(string name, AudioClipScheduleAnchor anchor, string days, int offset)
    {
        AudioClipSchedule schedule = Schedule(anchor, offset: offset, days: days);
        schedule.Name = name;

        Assert.IsNotNull(AudioClipScheduleRules.Validate(schedule));
    }

    [TestMethod]
    public void Describe_ReadsNaturally()
    {
        Assert.AreEqual("15 minutes before closing, every day", AudioClipScheduleRules.Describe(Schedule(AudioClipScheduleAnchor.BeforeClosing)));
        Assert.AreEqual("At 12:30, weekdays", AudioClipScheduleRules.Describe(Schedule(AudioClipScheduleAnchor.AtTime, time: "12:30", days: "1,2,3,4,5")));
        Assert.AreEqual("At opening time, Fri, Sat", AudioClipScheduleRules.Describe(Schedule(AudioClipScheduleAnchor.AfterOpening, offset: 0, days: "5,6")));
    }
}
