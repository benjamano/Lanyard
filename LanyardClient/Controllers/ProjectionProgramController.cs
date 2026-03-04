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
    }
}