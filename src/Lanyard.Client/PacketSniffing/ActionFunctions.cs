using Lanyard.Shared.DTO;
using Lanyard.Shared.Enum;
using Microsoft.Extensions.Logging;

namespace Lanyard.Client.PacketSniffing;

public class Actions(ILogger<Actions> logger, IGameStateService _gameStateService) : IActionFunctions
{
    private readonly ILogger<Actions> _logger = logger;

    private readonly IGameStateService _gameStateService = _gameStateService;

    public void HandleTimingPacket(string[] packetData)
    {
        if (int.TryParse(packetData[3], out int timeLeftSeconds))
        {
            if (timeLeftSeconds <= 0)
            {
                if (_gameStateService.GetGameStatus() == GameStatus.NotStarted)
                {
                    // GAME HAS ALREADY ENDED, NO NEED TO FIRE EVENT AGAIN
                    return;
                }

                _logger.LogInformation("Game end has been detected at {datetime}", DateTime.Now.ToString("g"));

                _gameStateService.HandleGameEnded();
            }
            else
            {
                // CHECK WITH STATE SERVICE TO SEE IF A GAME IS IN PROGRESS
                if (_gameStateService.GetGameStatus() == GameStatus.NotStarted)
                {
                    _logger.LogInformation("Game start has been detected at {datetime}", DateTime.Now.ToString("g"));

                    _gameStateService.HandleGameStarted();
                }

                _gameStateService.UpdateTimeRemaining(TimeSpan.FromSeconds(timeLeftSeconds));

                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.LogTrace("Time left updated to {timeleft} seconds", timeLeftSeconds);
                }
            }
        }
        else
        {
            _logger.LogWarning("Invalid data parsed for the time left figure! Value: {timeleftValue}", packetData[3]);
        }
    }

    public void HandlePlayerScorePacket(string[] packetData)
    {
        if (int.TryParse(packetData[1], out int GunId) == false)
        {
            _logger.LogWarning("Invalid data parsed for the Gun ID figure! Value: {gunIdValue}", packetData[1]);

            return;
        }
        
        if (int.TryParse(packetData[3], out int Score) == false)
        {
            _logger.LogWarning("Invalid data parsed for the Score figure! Value: {scoreValue}", packetData[3]);
            return;
        }
        
        if (int.TryParse(packetData[7], out int Accuracy) == false)
        {
            _logger.LogWarning("Invalid data parsed for the Accuracy figure! Value: {accuracyValue}", packetData[7]);
            return;
        }

        if (int.TryParse(packetData[2], out int TeamId) == false)
        {
            _logger.LogWarning("Invalid data parsed for the Team ID figure! Value: {teamIdValue}", packetData[2]);
            return;
        }

        Team team = Team.Green;

        switch (TeamId)
        {
            case 0:
                team = Team.Red;
                break;
            case 2:
                team = Team.Green;
                break;
                
        }

        _gameStateService.UpdatePlayerScore(new PlayerScoreDTO { GunId = GunId, Score = Score, Accuracy = Accuracy, Team = team });

        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("Player score updated: GunId={gunId}, Score={score}, Accuracy={accuracy}, Team={team}", GunId, Score, Accuracy, team);
        }
    }

    public void HandleGameStatusPacket(string[] packetData)
    {
        string[] parts = packetData[1].Split("@");

        // A settings-changed packet's parts each start with one of these known prefixes. Checking
        // the count alone previously misrouted any real status-change packet that happened to also
        // split into 4 parts, silently skipping HandleGameStarted() - detection then only happened
        // one cycle later via the timing packet's countdown-moving heuristic.
        bool looksLikeSettingsPacket = parts.Length == 4
            && parts.All(p => p.StartsWith("016") || p.StartsWith("017") || p.StartsWith("00"));

        if (parts.Length == 4 && !looksLikeSettingsPacket)
        {
            _logger.LogWarning("Game status packet had 4 '@'-delimited parts but didn't match known settings-packet prefixes; treating as a status packet. Raw: {RawValue}", packetData[1]);
        }

        if (looksLikeSettingsPacket)
        {
            // THIS MEANS THE GAME'S SETTINGS HAVE CHANGED, NOT THE STATUS
            _logger.LogDebug("Game settings packet received. Raw: {RawValue}", packetData[1]);

            foreach (string value in parts)
            {
                if (value.StartsWith("016"))
                {
                    if (int.TryParse(value[3..].Trim(), out int minutes))
                    {
                        _gameStateService.UpdateGameLength(TimeSpan.FromMinutes(minutes));

                        _logger.LogDebug("Game time limit updated to {timelimit} minutes", minutes);
                    }
                }
                else if (value.StartsWith("017"))
                {
                    //TODO: WORKOUT WHICH NUMBER EQUATES TO WHICH SOUND MODE
                }
                else if (value.StartsWith("00"))
                {
                    //TODO: MAKE A LIST OF GAME MODES AND WORKOUT WHICH ID THEY ARE FOR
                }
            }
        }
        else if (int.TryParse(packetData[1].Replace("@", ""), out int gameStatusValue))
        {
            // THE GAMES STATUS HAS CHANGED
            GameStatus status = (GameStatus)gameStatusValue;
            switch (status)
            {
                case GameStatus.NotStarted:
                    if (_gameStateService.GetGameStatus() != GameStatus.NotStarted)
                    {
                        _logger.LogInformation("Game status updated to Not Started");
                        _gameStateService.HandleGameEnded();
                    }

                    break;
                case GameStatus.GetReady:
                    if (_gameStateService.GetGameStatus() != GameStatus.GetReady)
                    {
                        _logger.LogInformation("Game status updated to Get Ready");
                        _gameStateService.HandleGameGetReady();
                    }

                    break;
                case GameStatus.InGame:
                    if (_gameStateService.GetGameStatus() != GameStatus.InGame)
                    {
                        _logger.LogInformation("Game status updated to In Progress");
                        _gameStateService.HandleGameStarted();
                    }

                    break;
                default:
                    _logger.LogWarning("Received unknown game status value: {statusValue}", gameStatusValue);
                    break;
            }
        }
        else
        {
            _logger.LogWarning("Invalid data parsed for the Game Status figure! Value: {rawValue}", packetData[1]);
        }

    }

    public void HandleShotConfirmedPacket(string[] packetData)
    {
        if (int.TryParse(packetData[1], out int shotGunId) == false)
        {
            _logger.LogWarning("Invalid data parsed for the Shot Gun ID figure! Value: {shooterGunIdValue}", packetData[1]);

            return;
        }

        if (int.TryParse(packetData[2], out int shotByGunId) == false)
        {
            _logger.LogWarning("Invalid data parsed for the Shot by Gun ID figure! Value: {shotGunId}", packetData[2]);
            return;
        }

        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("Shot confirmed: ShooterGunId={shotByGunId}, TargetGunId={shotGunId}", shotByGunId, shotGunId);
        }

        _gameStateService.HandlePlayerHit(shotGunId, shotByGunId);
    }
}
