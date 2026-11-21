using Lanyard.Shared.Audio;
using Lanyard.Shared.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace Lanyard.Client.Controllers;

public class EqualiserController(MusicEqualiser equaliser, ILogger<EqualiserController> logger)
{
    public void Register(HubConnection connection)
    {
        connection.On<MusicEqualiserSettings>(ClientHubMethods.ReceiveEqualiserSettings, settings =>
        {
            logger.LogInformation("Received music equaliser: bass {BassDb} dB, mid {MidDb} dB, treble {TrebleDb} dB", settings.BassDb, settings.MidDb, settings.TrebleDb);

            equaliser.Apply(settings);
        });
    }
}
