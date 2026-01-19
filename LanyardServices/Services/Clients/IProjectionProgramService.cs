using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace Lanyard.Application.Services.Clients;

public interface IProjectionProgramService
{
    Task<Result<IEnumerable<ProjectionProgram>>> GetProjectionProgramsAsync();
    Task<Result<IEnumerable<ProjectionProgramStepTemplate>>> GetProjectionProgramStepTemplatesAsync();
    Task<Result<ProjectionProgramStepTemplate>> CreateProjectionProgramStepTemplateAsync(ProjectionProgramStepTemplate template);
    Task<Result<ProjectionProgram>> CreateProjectionProgramAsync(ProjectionProgram projectionProgram);
    Task<Result<bool>> SaveProjectionProgramStepsAsync(IEnumerable<ProjectionProgramStep> projectionProgramSteps);
}
