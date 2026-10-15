using Lanyard.Application.Services.MusicScheduling;
using Lanyard.Infrastructure.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.MusicScheduling;

[TestClass]
public class MusicDaypartRulesTests
{
    private static MusicDaypart Daypart(string name, string start, string end, params DayOfWeek[] days) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        DaysOfWeek = AutomationScheduleDays.Serialize(days),
        StartTime = TimeOnly.Parse(start),
        EndTime = TimeOnly.Parse(end),
        PlaylistId = Guid.NewGuid()
    };

    [TestMethod]
    public void Validate_AcceptsAnOrdinaryDaypart()
    {
        Assert.IsNull(MusicDaypartRules.Validate(Daypart("Morning", "08:00", "10:00", DayOfWeek.Monday)));
    }

    [TestMethod]
    public void Validate_RejectsAStartAndEndAtTheSameTime()
    {
        Assert.IsNotNull(MusicDaypartRules.Validate(Daypart("Morning", "08:00", "08:00", DayOfWeek.Monday)));
    }

    [TestMethod]
    public void Validate_RejectsNoDays()
    {
        MusicDaypart daypart = Daypart("Morning", "08:00", "10:00");
        daypart.DaysOfWeek = "";

        // Blank means "every day" for automation rules; a daypart must say which days.
        Assert.IsNotNull(MusicDaypartRules.Validate(daypart));
    }

    [TestMethod]
    public void Validate_RejectsABlankName()
    {
        Assert.IsNotNull(MusicDaypartRules.Validate(Daypart("  ", "08:00", "10:00", DayOfWeek.Monday)));
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(101)]
    public void Validate_RejectsAVolumeOutsideZeroToOneHundred(int volume)
    {
        MusicDaypart daypart = Daypart("Morning", "08:00", "10:00", DayOfWeek.Monday);
        daypart.Volume = volume;

        Assert.IsNotNull(MusicDaypartRules.Validate(daypart));
    }

    [TestMethod]
    public void FindOverlap_FindsTwoDaypartsSharingTimeOnTheSameDay()
    {
        MusicDaypart morning = Daypart("Morning", "08:00", "10:00", DayOfWeek.Monday);
        MusicDaypart brunch = Daypart("Brunch", "09:30", "11:00", DayOfWeek.Monday);

        string? overlap = MusicDaypartRules.FindOverlap(brunch, [morning]);

        Assert.IsNotNull(overlap);
        StringAssert.Contains(overlap, "Morning");
        StringAssert.Contains(overlap, "Monday at 09:30");
    }

    [TestMethod]
    public void FindOverlap_AllowsOneToStartWhenTheOtherEnds()
    {
        MusicDaypart morning = Daypart("Morning", "08:00", "10:00", DayOfWeek.Monday);
        MusicDaypart late = Daypart("Late morning", "10:00", "12:00", DayOfWeek.Monday);

        Assert.IsNull(MusicDaypartRules.FindOverlap(late, [morning]));
    }

    [TestMethod]
    public void FindOverlap_IgnoresTheSameTimesOnDifferentDays()
    {
        MusicDaypart monday = Daypart("Morning", "08:00", "10:00", DayOfWeek.Monday);
        MusicDaypart tuesday = Daypart("Morning", "08:00", "10:00", DayOfWeek.Tuesday);

        Assert.IsNull(MusicDaypartRules.FindOverlap(tuesday, [monday]));
    }

    [TestMethod]
    public void FindOverlap_SeesADaypartThatRunsPastMidnightIntoTheNextDay()
    {
        // Friday night 22:00-02:00 covers early Saturday.
        MusicDaypart lateNight = Daypart("Late night", "22:00", "02:00", DayOfWeek.Friday);
        MusicDaypart earlySaturday = Daypart("Early", "01:00", "03:00", DayOfWeek.Saturday);

        string? overlap = MusicDaypartRules.FindOverlap(earlySaturday, [lateNight]);

        Assert.IsNotNull(overlap);
        StringAssert.Contains(overlap, "Saturday at 01:00");
    }

    [TestMethod]
    public void FindOverlap_WrapsFromSaturdayNightIntoSundayMorning()
    {
        MusicDaypart saturdayNight = Daypart("Late night", "23:00", "01:00", DayOfWeek.Saturday);
        MusicDaypart sundayEarly = Daypart("Early", "00:30", "02:00", DayOfWeek.Sunday);

        string? overlap = MusicDaypartRules.FindOverlap(sundayEarly, [saturdayNight]);

        Assert.IsNotNull(overlap);
        StringAssert.Contains(overlap, "Sunday at 00:30");
    }

    [TestMethod]
    public void FindOverlap_EndingAtMidnightDoesNotTouchTheNextMorning()
    {
        MusicDaypart evening = Daypart("Evening", "18:00", "00:00", DayOfWeek.Friday);
        MusicDaypart saturdayFirstThing = Daypart("Overnight", "00:00", "06:00", DayOfWeek.Saturday);

        Assert.IsNull(MusicDaypartRules.FindOverlap(saturdayFirstThing, [evening]));
    }

    [TestMethod]
    public void FindOverlap_IgnoresTheDaypartBeingEdited()
    {
        MusicDaypart morning = Daypart("Morning", "08:00", "10:00", DayOfWeek.Monday);
        MusicDaypart edited = Daypart("Morning", "08:30", "10:30", DayOfWeek.Monday);
        edited.Id = morning.Id;

        Assert.IsNull(MusicDaypartRules.FindOverlap(edited, [morning]));
    }

    [TestMethod]
    public void DescribeTimes_MarksADaypartThatEndsTheNextDay()
    {
        Assert.AreEqual("22:00–02:00 (next day)", MusicDaypartRules.DescribeTimes(Daypart("Late", "22:00", "02:00", DayOfWeek.Friday)));
        Assert.AreEqual("08:00–10:00", MusicDaypartRules.DescribeTimes(Daypart("Morning", "08:00", "10:00", DayOfWeek.Friday)));
    }

    [TestMethod]
    public void DescribeDays_ListsDaysMondayFirst()
    {
        Assert.AreEqual("Mon, Sat, Sun", MusicDaypartRules.DescribeDays("0,1,6"));
        Assert.AreEqual("Every day", MusicDaypartRules.DescribeDays("0,1,2,3,4,5,6"));
    }
}
