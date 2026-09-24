using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Infrastructure.DTO.Scheduling;

// One unmet contract requirement for one person in one Monday-starting week.
public record ContractWarning(string UserId, DateOnly WeekStart, ContractWarningKind Kind, string Message);

// One line of the rota grid. Shifts are this location's only (including removed-but-not-yet-told
// ones, for the manager's benefit); HoursByWeek counts active shifts at every location in the
// company, because a contract is with the company, not a site.
public record RotaStaffRow(
    UserProfile User,
    UserPosition? PrimaryPosition,
    List<Shift> Shifts,
    Dictionary<DateOnly, decimal> HoursByWeek,
    List<ContractWarning> Warnings,
    bool IsLocationMember)
{
    public string DisplayName => RotaNames.For(User);
}

public record RotaRangeView(
    int LocationId,
    int CompanyId,
    string LocationName,
    DateOnly From,
    DateOnly To,
    List<RotaStaffRow> Rows,
    List<StaffPosition> Positions,
    int UnpublishedChangeCount,
    int PeopleWithUnpublishedChanges);

public record ShiftSaveResult(Shift Shift, List<string> Warnings);

public record PublishedChange(string UserId, List<Shift> New, List<Shift> Changed, List<Shift> Removed);

public record PublishResult(List<PublishedChange> Changes)
{
    public int PeopleAffected => Changes.Count;
    public int ShiftCount => Changes.Sum(x => x.New.Count + x.Changed.Count + x.Removed.Count);
}

public record CopyRangeResult(int Copied, List<string> Skipped);

public static class RotaNames
{
    public static string For(UserProfile? user)
    {
        if (user is null)
        {
            return "Unknown";
        }

        string name = user.GetName().Trim();

        return string.IsNullOrWhiteSpace(name) ? user.UserName ?? "Unknown" : name;
    }
}

// The one place rota numbers are formatted, shared by contract-warning messages (service layer)
// and the grid/My Shifts (UI) so the two can't drift apart.
public static class RotaFormat
{
    public static readonly System.Globalization.CultureInfo Uk = System.Globalization.CultureInfo.GetCultureInfo("en-GB");

    public static string Hours(decimal hours) => $"{hours.ToString("0.##", Uk)} h";

    // "09:00–17:00", with "(+1)" when it runs past midnight - in venue-local time.
    public static string TimeRange(DateTime startUtc, DateTime endUtc)
    {
        bool nextDay = Enum.RotaTime.LocalDate(endUtc) > Enum.RotaTime.LocalDate(startUtc);

        return $"{Enum.RotaTime.LocalTime(startUtc).ToString("HH:mm", Uk)}–{Enum.RotaTime.LocalTime(endUtc).ToString("HH:mm", Uk)}{(nextDay ? " (+1)" : string.Empty)}";
    }

    public static string LocalTime(DateTime utc) => Enum.RotaTime.LocalTime(utc).ToString("HH:mm", Uk);

    public static string DayAndTime(DateTime utc) => Enum.RotaTime.ToVenueLocal(utc).ToString("ddd d MMM 'at' HH:mm", Uk);
}
