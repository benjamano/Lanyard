namespace Lanyard.App.Components.Rota.Staff;

internal static class BulkMessages
{
    // "Updated positions for 4 people." / "... 3 of 5 people already had these positions."
    public static string Changed(int changed, int selected, string what)
    {
        string people = changed == 1 ? "1 person" : $"{changed} people";

        if (changed == 0)
        {
            return $"Nothing to change - everyone selected already had these {what}.";
        }

        return changed == selected
            ? $"Updated {what} for {people}."
            : $"Updated {what} for {people}. The other {selected - changed} already had them.";
    }
}
