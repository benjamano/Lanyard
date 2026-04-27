using Lanyard.Shared.DTO;
using System;
using System.Collections.Generic;
using System.Text;

namespace Lanyard.Client.ProjectionPrograms;

public interface IProjectionProgramsService
{
    Task StartProjectingAsync(IEnumerable<ClientProjectionSettingsDTO> projectionPrograms);
    Task TriggerTemporaryProjectionProgramAsync(Guid projectionProgramId, Func<Task> onCompleted);
}
