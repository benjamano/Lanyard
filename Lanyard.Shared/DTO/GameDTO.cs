using System;
using System.Collections.Generic;
using System.Text;
using Lanyard.Shared.Enum;

namespace Lanyard.Shared.DTO;

public class PlayerScoreDTO
{
    public int GunId { get; set; }
    public string? GunName { get; set; }

    public Team? Team { get; set; }

    public int Score { get; set; }
    public int Accuracy { get; set; }
}

public class PlayerHitDTO
{
    public int ShotByGunId { get; set; }
    public int ShotGunId { get; set; }

}

public class LaserGameStatusDTO
{
    public Guid ClientId { get; set; }

    public GameStatus Status { get; set; } = GameStatus.NotStarted;
    public int TimeRemainingSeconds { get; set; }
    public int PlayerCount { get; set; }

    public DateTime LastUpdateUtc { get; set; } = DateTime.UtcNow;
}
