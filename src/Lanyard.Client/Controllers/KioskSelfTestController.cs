using Lanyard.Client.SelfTest;
using Lanyard.Shared.DTO;
using Lanyard.Shared.Enum;
using Lanyard.Shared.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace Lanyard.Client.Controllers;

public class KioskSelfTestController(KioskSelfTestRunner runner, ILogger<KioskSelfTestController> logger)
{
    private readonly KioskSelfTestRunner _runner = runner;
    private readonly ILogger<KioskSelfTestController> _logger = logger;

    public void Register(HubConnection connection)
    {
        // A client result, like the other diagnostics: the server waits for the checklist, and a
        // kiosk too old to have this handler comes back to it as a HubException.
        connection.On<KioskSelfTestRequestDTO, KioskSelfTestResultDTO>(ClientHubMethods.RunSelfTest, async request =>
        {
            request ??= new KioskSelfTestRequestDTO();

            _logger.LogInformation("Running the hardware self-test {RunId} for the server (flash DMX: {FlashDmx})", request.RunId, request.FlashDmx);

            KioskSelfTestResultDTO result = await _runner.RunAsync(request, ClientDiagnosticsController.GetClientVersion());

            _logger.LogInformation("Hardware self-test {RunId} finished in {Milliseconds} ms: {Passed} passed, {Problems} need attention",
                request.RunId,
                (int)(result.FinishedUtc - result.StartedUtc).TotalMilliseconds,
                result.Checks.Count(x => x.Outcome == SelfTestOutcome.Pass),
                result.Checks.Count(x => x.Outcome is SelfTestOutcome.Fail or SelfTestOutcome.Warn));

            return result;
        });
    }
}
