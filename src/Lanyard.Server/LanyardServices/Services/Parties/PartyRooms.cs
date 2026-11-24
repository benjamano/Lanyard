namespace Lanyard.Application.Services.Parties;

/// <summary>
/// PartyBooking.Room is free text ("Room 2", "room 2 ", "Table 4"), so every comparison against
/// it goes through here: trimmed, inner whitespace collapsed, case ignored.
/// </summary>
public static class PartyRooms
{
    public const int MaxLength = 100;

    /// <summary>The tidied room, or null when it's blank.</summary>
    public static string? Normalise(string? room)
    {
        if (string.IsNullOrWhiteSpace(room))
        {
            return null;
        }

        string tidy = string.Join(' ', room.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return tidy.Length > MaxLength ? tidy[..MaxLength] : tidy;
    }

    /// <summary>True when there's no filter, or the party's room is the same room.</summary>
    public static bool Matches(string? filter, string? room)
    {
        string? wanted = Normalise(filter);

        return wanted is null || string.Equals(wanted, Normalise(room), StringComparison.OrdinalIgnoreCase);
    }
}
