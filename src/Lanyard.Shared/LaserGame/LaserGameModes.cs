using Lanyard.Shared.Enum;

namespace Lanyard.Shared.LaserGame;

/// <summary>
/// Reads the game mode the control box reports, and says whether a mode is played as solos.
/// The box sends the mode as a three-digit field: <c>"@004"</c> in a timing packet
/// (<c>1,&lt;game&gt;,"@&lt;mode&gt;",&lt;secs&gt;,...</c>) and <c>004</c> in a settings packet.
/// </summary>
public static class LaserGameModes
{
    /// <summary>
    /// Parses a mode field such as <c>"@004"</c>, <c>@004</c> or <c>004</c>. Returns false for
    /// anything that isn't a number the <see cref="GameMode"/> enum knows, so an unknown mode
    /// leaves the last known one in place rather than guessing.
    /// </summary>
    public static bool TryParse(string? raw, out GameMode mode)
    {
        mode = default;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string digits = raw.Trim().Trim('"').TrimStart('@');

        if (!int.TryParse(digits, out int value) || !System.Enum.IsDefined(typeof(GameMode), value))
        {
            return false;
        }

        mode = (GameMode)value;
        return true;
    }

    /// <summary>
    /// True for the modes where everyone plays for themselves. In these the box still tags every
    /// player with a team (all the same one), so team-based views would put everyone on one side.
    /// </summary>
    public static bool IsSolo(this GameMode mode) => mode is
        GameMode.StandardSolo or
        GameMode.FastSolo or
        GameMode.SoloElimination or
        GameMode.BirthdaySolo or
        GameMode.ContinuousSolo;
}
