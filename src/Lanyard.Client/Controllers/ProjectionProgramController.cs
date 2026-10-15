using Lanyard.Client.ProjectionPrograms;
using Lanyard.Client.ProjectionPrograms.Offline;
using Lanyard.Shared.DTO;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text;

namespace Lanyard.Client.Controllers;

public class ProjectionProgramController(IProjectionProgramsService projectionProgramsService, IKioskOfflineCache offlineCache, ILogger<ProjectionProgramController> logger)
{
    private HubConnection? _connection;
    private readonly IProjectionProgramsService _projectionProgramsService = projectionProgramsService;
    private readonly IKioskOfflineCache _offlineCache = offlineCache;
    private readonly ILogger<ProjectionProgramController> _logger = logger;

    public void Register(HubConnection connection)
    {
        _connection = connection;

        connection.On<IEnumerable<ClientProjectionSettingsDTO>>("ReceiveProjectionPrograms", async (programs) =>
        {
            _logger.LogInformation("Received command to start projection programs");

            // The server sends this on connect and whenever a program or this kiosk's settings
            // change, so it's also the cue to refresh the offline copy.
            _offlineCache.RequestSync();

            await _projectionProgramsService.StartProjectingAsync(programs);
        });

        // The server's projection program runner owns the step loop, so it - not this process -
        // is what knows a triggered program has finished. Closing the window here completes the
        // TriggerTemporaryProjectionProgramAsync await, which reports back and restores ambient.
        connection.On<int>("CloseTemporaryProjectionWindow", async (displayIndex) =>
        {
            _logger.LogInformation("Received command to close the temporary projection window on display {DisplayIndex}", displayIndex);

            await _projectionProgramsService.CloseWindowForDisplayAsync(displayIndex);
        });

        connection.On<Guid, int?>("TriggerProjectionProgram", (projectionProgramId, displayIndex) =>
        {
            _logger.LogInformation("Received command to trigger projection program {ProgramId} on display {DisplayIndex}", projectionProgramId, displayIndex);

            // Detached from the hub's message pump: the triggered program can run for as long as the
            // configured video (e.g. a 30s briefing), and awaiting it here would block delivery of every
            // other SignalR message (DMX, music, automation) to this client until it finished.
            _ = Task.Run(async () =>
            {
                await _projectionProgramsService.TriggerTemporaryProjectionProgramAsync(projectionProgramId, displayIndex, async () =>
                {
                    _logger.LogInformation("Triggered projection program {ProgramId} completed, notifying server", projectionProgramId);

                    try
                    {
                        await _connection!.InvokeAsync("ProjectionProgramCompleted");
                    }
                    catch (Exception ex)
                    {
                        // The connection dropped while it ran (the window is closed when the kiosk
                        // goes offline). The server restores the ambient program when it reconnects.
                        _logger.LogWarning("Couldn't report that triggered program {ProgramId} completed: {Message}", projectionProgramId, ex.Message);
                    }
                });
            });

            return Task.CompletedTask;
        });
    }
}