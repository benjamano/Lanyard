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

public enum GameMode
{
    StandardSolo = 0,
    StandardTeam = 1,
    FastSolo = 2,
    FastTeam = 3,
    SoloElimination = 4,
    TeamElimination = 5,
    FastTeamElimination = 6,
    TeamReload = 7,
    BirthdaySolo = 8,
    BirthdayTeam = 9,
    Vampire = 10,
    Targets1 = 11,
    Targets2 = 12,
    ContinuousSolo = 13,
    ContinuousTeam = 14,
    Targets3 = 15,
    Targets4 = 16,
    Zombie = 17
}

public enum SoundSet
{
    Male = 1,
    Female = 2,
}