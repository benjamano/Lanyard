using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace Lanyard.Application.Services;

public interface IProjectionProgramService
{
    Task<Result<IEnumerable<ProjectionProgram>>> GetProjectionProgramsAsync();
    Task<Result<IEnumerable<ProjectionProgramStepTemplate>>> GetProjectionProgramStepTemplatesAsync();
    Task<Result<ProjectionProgramStepTemplate>> CreateProjectionProgramStepTemplateAsync(ProjectionProgramStepTemplate template);
    Task<Result<ProjectionProgramStepTemplate>> SaveProjectionProgramStepTemplateAsync(ProjectionProgramStepTemplate template);
    Task<Result<bool>> DeleteProjectionProgramStepTemplateAsync(Guid templateId);
    Task<Result<ProjectionProgram>> CreateProjectionProgramAsync(ProjectionProgram projectionProgram);
    Task<Result<bool>> SaveProjectionProgramStepsAsync(IEnumerable<ProjectionProgramStep> projectionProgramSteps);
    Task<Result<bool>> DeleteProjectionProgramStepAsync(Guid Id);
    Task<Result<ProjectionProgram>> GetProjectionProgramAsync(Guid projectionProgramId);
    Task<Result<bool>> DeleteProjectionProgramAsync(Guid projectionProgramId);
}
