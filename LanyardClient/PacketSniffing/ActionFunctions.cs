using Lanyard.Shared.DTO;
using Lanyard.Shared.Enum;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Lanyard.Client.PacketSniffing;

public class Actions(ILogger<Actions> logger, IGameStateService _gameStateService) : IActionFunctions
{
    private readonly ILogger<Actions> _logger = logger;

    private readonly IGameStateService _gameStateService = _gameStateService;

    public async Task HandleTimingPacketAsync(string[] packetData)
    {
        if (int.TryParse(packetData[3].ToString(), out int timeLeftSeconds))
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

                _logger.LogInformation("Time left updated to {timeleft} seconds", timeLeftSeconds);
            }
        }
        else
        {
            _logger.LogError("Invalid data parsed for the time left figure! Value: {timeleftValue}", packetData[3].ToString());
        }
    }

    public async Task HandlePlayerScorePacketAsync(string[] packetData)
    {
        if (int.TryParse(packetData[1].ToString(), out int GunId) == false)
        {
            _logger.LogError("Invalid data parsed for the Gun ID figure! Value: {gunIdValue}", packetData[1].ToString());

            return;
        }
        
        if (int.TryParse(packetData[3].ToString(), out int Score) == false)
        {
            _logger.LogError("Invalid data parsed for the Score figure! Value: {scoreValue}", packetData[3].ToString());
            return;
        }
        
        if (int.TryParse(packetData[7].ToString(), out int Accuracy) == false)
        {
            _logger.LogError("Invalid data parsed for the Accuracy figure! Value: {accuracyValue}", packetData[2].ToString());
            return;
        }

        _gameStateService.UpdatePlayerScore(new PlayerScoreDTO { GunId = GunId, Score = Score, Accuracy = Accuracy });

        _logger.LogInformation("Player score updated: GunId={gunId}, Score={score}, Accuracy={accuracy}", GunId, Score, Accuracy);
    }

    public async Task HandleGameStatusPacketAsync(string[] packetData)
    {
        if (int.TryParse(packetData[0].ToString().Replace("@", ""), out int gameStatusValue))
        {
            if (gameStatusValue == 4)
            {
                // THIS MEANS THE GAME'S SETTINGS HAVE CHANGED, NOT THE STATUS
                string[] values = packetData.ToString()!.Split("@");

                foreach (string value in values)
                {
                    if (value.StartsWith("016"))
                    {
                        _gameStateService.UpdateTimeRemaining(TimeSpan.FromMinutes(int.Parse(value.Substring(3))));

                        _logger.LogInformation("Game time limit updated to {timelimit} minutes", value.Substring(3));
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
            else
            {
                // THE GAMES STATUS HAS CHANGED
                GameStatus status = (GameStatus)gameStatusValue;
                switch (status)
                {
                    case GameStatus.NotStarted:
                        _logger.LogInformation("Game status updated to Not Started");
                        if (_gameStateService.GetGameStatus() != GameStatus.NotStarted)
                        {
                            _gameStateService.HandleGameEnded();
                        }

                        break;
                    case GameStatus.InGame:
                        _logger.LogInformation("Game status updated to In Progress");
                        if (_gameStateService.GetGameStatus() != GameStatus.InGame && _gameStateService.GetGameStatus() != GameStatus.GetReady)
                        {
                            _gameStateService.HandleGameStarted();
                        }

                        break;
                    default:
                        _logger.LogWarning("Received unknown game status value: {statusValue}", gameStatusValue);
                        break;
                }
            }
        }
        else
        {
            _logger.LogError("Invalid data parsed for the Game Status figure! Value: {gameStatusValue}", gameStatusValue);
        }

    }

    public async Task HandleShotConfirmedPacketAsync(string[] packetData)
    {
        if (int.TryParse(packetData[1], out int shotByGunId) == false)
        {
            _logger.LogError("Invalid data parsed for the Shooter Gun ID figure! Value: {shooterGunIdValue}", packetData[1].ToString());

            return;
        }

        if (int.TryParse(packetData[2], out int shotGunId) == false)
        {
            _logger.LogError("Invalid data parsed for the Target Gun ID figure! Value: {shotGunId}", packetData[2].ToString());
            return;
        }

        _logger.LogInformation("Shot confirmed: ShooterGunId={shotByGunId}, TargetGunId={shotGunId}", shotByGunId, shotGunId);

        _gameStateService.HandlePlayerHit(shotGunId, shotByGunId);
    }
}
