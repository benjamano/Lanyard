using Lanyard.Infrastructure.DTO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services;

/// <summary>
/// Bridges the runner's natural-completion event to the kiosk client that opened the window.
/// A triggered projection program used to have no way to hand control back on its own - the
/// kiosk page's loop simply ran out and left the last step on screen forever - because nothing
/// outside that page's circuit knew the show had finished. Now the server owns the loop, so it
/// can tell the client to close the window, which resumes the client's ambient projection.
/// </summary>
public class ProjectionProgramCompletionListener(
    IProjectionProgramRunnerService runnerService,
    IServiceScopeFactory scopeFactory,
    ILogger<ProjectionProgramCompletionListener> logger) : IHostedService
{
    private readonly IProjectionProgramRunnerService _runnerService = runnerService;
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly ILogger<ProjectionProgramCompletionListener> _logger = logger;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _runnerService.OnProgramCompletedNaturally += HandleProgramCompletedNaturally;

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _runnerService.OnProgramCompletedNaturally -= HandleProgramCompletedNaturally;

        return Task.CompletedTask;
    }

    private void HandleProgramCompletedNaturally(Guid clientId, int displayIndex, Guid programId)
    {
        // The event is raised from the runner's playback loop; detach so a slow or failing
        // hub send can never stall the loop's teardown.
        _ = Task.Run(async () =>
        {
            try
            {
                using IServiceScope scope = _scopeFactory.CreateScope();

                IClientService clientService = scope.ServiceProvider.GetRequiredService<IClientService>();

                Result<bool> closeResult = await clientService.CloseTemporaryProjectionWindowOnClientAsync(clientId, displayIndex);

                if (!closeResult.IsSuccess)
                {
                    _logger.LogWarning("Failed to close temporary projection window for client {ClientId} display {DisplayIndex} after program {ProgramId} finished: {Error}", clientId, displayIndex, programId, closeResult.Error);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error closing temporary projection window for client {ClientId} display {DisplayIndex} after program {ProgramId} finished", clientId, displayIndex, programId);
            }
        });
    }
}
