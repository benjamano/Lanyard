using System.Text;
using System.Text.RegularExpressions;
using Lanyard.Infrastructure.Enum;

namespace Lanyard.Application.Services.Parties;

/// <summary>
/// Which party a venue screen shows, and the words it shows for it. Pure, so the choice and the
/// wording can be tested at fixed instants without a kiosk, a program or a database.
/// </summary>
public static partial class PartyScreen
{
    /// <summary>A party shows from this long before its start, so the welcome is up as families arrive.</summary>
    public static readonly TimeSpan ShowBeforeStart = TimeSpan.FromMinutes(15);

    public const int MaxNameLength = 40;

    /// <summary>
    /// The party for a screen: among parties showing now (from 15 minutes before their start until
    /// their end), those in the kiosk's room if it has one and any match, otherwise every one at the
    /// location. Of those, the one that started most recently; if none has started, the next to
    /// start. A tie goes to the party booked first.
    /// </summary>
    public static PartySlot? Choose(IEnumerable<PartySlot> parties, string? kioskRoom, DateTime nowUtc)
    {
        List<PartySlot> showing = [.. parties.Where(p => p.StartUtc - ShowBeforeStart <= nowUtc && nowUtc < p.EndUtc)];

        if (showing.Count == 0)
        {
            return null;
        }

        if (PartyRooms.Normalise(kioskRoom) is not null)
        {
            List<PartySlot> inRoom = [.. showing.Where(p => PartyRooms.Matches(kioskRoom, p.Room))];

            if (inRoom.Count > 0)
            {
                showing = inRoom;
            }
        }

        List<PartySlot> started = [.. showing.Where(p => p.StartUtc <= nowUtc)];

        return started.Count > 0
            ? started.OrderByDescending(p => p.StartUtc).ThenBy(p => p.CreateDate).First()
            : showing.OrderBy(p => p.StartUtc).ThenBy(p => p.CreateDate).First();
    }

    /// <summary>
    /// The child's first name only: "Ava Smith" gives "Ava", "Ava and Leo Smith" gives "Ava &amp; Leo"
    /// (twins share a party). Null when there's nothing to show.
    /// </summary>
    public static string? FirstName(string? childName)
    {
        if (string.IsNullOrWhiteSpace(childName))
        {
            return null;
        }

        StringBuilder clean = new(childName.Length);

        foreach (char c in childName)
        {
            clean.Append(char.IsControl(c) ? ' ' : c);
        }

        List<string> firstNames = [.. AndSplitter().Split(clean.ToString())
            .Select(part => part.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
            .OfType<string>()];

        if (firstNames.Count == 0)
        {
            return null;
        }

        string joined = string.Join(" & ", firstNames);

        return joined.Length > MaxNameLength ? joined[..MaxNameLength].TrimEnd() : joined;
    }

    /// <summary>"Happy 7th birthday, Ava!", or "Happy birthday, Ava!" without an age.</summary>
    public static string? Welcome(PartySlot party)
    {
        if (FirstName(party.ChildName) is not string name)
        {
            return null;
        }

        return party.ChildAgeTurning is int age and > 0 and < 120
            ? $"Happy {Ordinal(age)} birthday, {name}!"
            : $"Happy birthday, {name}!";
    }

    /// <summary>
    /// The party's next laser tag or food time still to come, as "Laser tag at 14:30" or "Food at
    /// 15:00" (venue time). Null once both have passed, or the party has neither.
    /// </summary>
    public static string? NextUp(PartySlot party, DateTime nowUtc)
    {
        (string Label, DateTime At)? next = new (string Label, DateTime? At)[]
            {
                ("Laser tag", party.LaserTagTimeUtc),
                ("Food", party.EatTimeUtc)
            }
            .Where(x => x.At is DateTime at && at > nowUtc)
            .Select(x => ((string Label, DateTime At)?)(x.Label, x.At!.Value))
            .OrderBy(x => x!.Value.At)
            .FirstOrDefault();

        return next is (string label, DateTime when) ? $"{label} at {RotaTime.LocalTime(when):HH\\:mm}" : null;
    }

    public static string Ordinal(int n)
    {
        string suffix = (n % 100) is 11 or 12 or 13 ? "th" : (n % 10) switch
        {
            1 => "st",
            2 => "nd",
            3 => "rd",
            _ => "th"
        };

        return $"{n}{suffix}";
    }

    [GeneratedRegex(@"\s*(?:&|\+|\band\b)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex AndSplitter();
}
