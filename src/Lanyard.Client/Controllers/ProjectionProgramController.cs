using Lanyard.Client.ProjectionPrograms;
using Lanyard.Shared.DTO;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text;

namespace Lanyard.Client.Controllers;

public class ProjectionProgramController(IProjectionProgramsService projectionProgramsService, ILogger<ProjectionProgramController> logger)
{
    private HubConnection? _connection;
    private readonly IProjectionProgramsService _projectionProgramsService = projectionProgramsService;
    private readonly ILogger<ProjectionProgramController> _logger = logger;

    public void Register(HubConnection connection)
    {
        _connection = connection;

        connection.On<IEnumerable<ClientProjectionSettingsDTO>>("ReceiveProjectionPrograms", async (programs) =>
        {
            _logger.LogInformation("Received command to start projection programs");

            await _projectionProgramsService.StartProjectingAsync(programs);
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
                    await _connection!.InvokeAsync("ProjectionProgramCompleted");
                });
            });

            return Task.CompletedTask;
        });
    }
}