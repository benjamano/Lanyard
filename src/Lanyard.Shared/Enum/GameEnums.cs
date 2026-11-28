using System;
using System.Collections.Generic;
using System.Text;

namespace Lanyard.Shared.Enum;

public enum Team
{
    Red = 0,
    Green = 2
}

public enum GameStatus
{
    NotStarted = 14,
    InGame = 15,
    GetReady,
}

/// <summary>
/// Game modes as the Begeara control box numbers them: the box sends the number as a three-digit
/// field ("@001" for Standard solo, up to "@018" for Zombies), counting from 1.
/// </summary>
public enum GameMode
{
    StandardSolo = 1,
    StandardTeam = 2,
    FastSolo = 3,
    FastTeam = 4,
    SoloElimination = 5,
    TeamElimination = 6,
    FastTeamElimination = 7,
    /// <summary>Standard team, but each gun has 30 ammo. A player who runs out is out of the game.</summary>
    TeamReload = 8,
    /// <summary>The birthday gun plays music, sounds different and fires much faster. Hand the flashing gun to the birthday player and press start again.</summary>
    BirthdaySolo = 9,
    /// <summary>As BirthdaySolo, but the birthday player's whole team gets the fast-firing guns.</summary>
    BirthdayTeam = 10,
    /// <summary>One gun starts as the vampire and infects the others it hits. Plays random sounds.</summary>
    Vampire = 11,
    /// <summary>Targets modes: some guns may be invulnerable to others (unconfirmed).</summary>
    Targets1 = 12,
    Targets2 = 13,
    ContinuousSolo = 14,
    ContinuousTeam = 15,
    Targets3 = 16,
    Targets4 = 17,
    Zombie = 18
}

/// <summary>
/// Sound set from the settings packet: "0171" is sound 1, "0172" is sound 2. Which of these is
/// the male or female voice hasn't been confirmed.
/// </summary>
public enum SoundSet
{
    Male = 1,
    Female = 2,
}