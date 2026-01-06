using Microsoft.Build.Framework;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text;

namespace Lanyard.Client.PacketSniffing;

public class Actions(ILogger<Actions> logger, IGameStateService _gameStateService) : IActionFunctions
{
    private readonly ILogger<Actions> _logger = logger;

    private readonly IGameStateService _gameStateService = _gameStateService;

    public async Task HandleTimingPacketAsync(string[] PacketData)
    {
        if (int.TryParse(PacketData[3].ToString(), out int TimeLeftSeconds))
        {
            if (TimeLeftSeconds <= 0)
            {
                if (_gameStateService.IsGameInProgress() == false)
                {
                    // GAME HAS ALREADY BEEN ENDED, NO NEED TO FIRE EVENT AGAIN
                    return;
                }

                _logger.LogInformation("Game end has been detected at {datetime}", DateTime.Now.ToString("g"));

                _gameStateService.HandleGameEnded();
            }
            else
            {
                // CHECK WITH STATE SERVICE TO SEE IF A GAME IS IN PROGRESS
                if (_gameStateService.IsGameInProgress() == false)
                {
                    _gameStateService.HandleGameStarted();
                }

                _gameStateService.UpdateTimeRemaining(TimeLeftSeconds);
            }
        }
        else
        {
            _logger.LogError("Invalid data parsed for the time left figure! Value: {timeleftValue}", PacketData[3].ToString());
        }
    }
}
