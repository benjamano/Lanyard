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
    TeamReload = 8,
    BirthdaySolo = 9,
    BirthdayTeam = 10,
    Vampire = 11,
    Targets1 = 12,
    Targets2 = 13,
    ContinuousSolo = 14,
    ContinuousTeam = 15,
    Targets3 = 16,
    Targets4 = 17,
    Zombie = 18
}

public enum SoundSet
{
    Male = 1,
    Female = 2,
}