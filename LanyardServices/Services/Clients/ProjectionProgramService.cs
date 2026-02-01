using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services;

public class ProjectionProgramService(IDbContextFactory<ApplicationDbContext> factory, IClientService clientService) : IProjectionProgramService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly IClientService _clientService = clientService;

    public async Task<Result<IEnumerable<ProjectionProgram>>> GetProjectionProgramsAsync()
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<ProjectionProgram> programs = await ctx.ProjectionPrograms
                .AsNoTracking()
                .Include(x => x.ProjectionProgramSteps.Where(s => s.IsActive))
                    .ThenInclude(x => x.Template)
                        .ThenInclude(x => x!.Parameters)
                .Include(x => x.ProjectionProgramSteps)
                    .ThenInclude(x => x.ParameterValues)
                        .ThenInclude(pv => pv.Parameter)
                .Where(x => x.IsActive)
                .OrderBy(x => x.Name)
                .ToListAsync();

            return Result<IEnumerable<ProjectionProgram>>.Ok(programs);
        }
        catch (Exception ex)
        {
            return Result<IEnumerable<ProjectionProgram>>.Fail(ex.Message);
        }
    }

    public async Task<Result<IEnumerable<ProjectionProgramStepTemplate>>> GetProjectionProgramStepTemplatesAsync()
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<ProjectionProgramStepTemplate> templates = await ctx.ProjectionProgramStepTemplates
                .AsNoTracking()
                .Include(x => x.Parameters)
                .Where(x => x.IsActive)
                .OrderBy(x => x.Name)
                .ToListAsync();

            return Result<IEnumerable<ProjectionProgramStepTemplate>>.Ok(templates);
        }
        catch (Exception ex)
        {
            return Result<IEnumerable<ProjectionProgramStepTemplate>>.Fail(ex.Message);
        }
    }

    public async Task<Result<ProjectionProgramStepTemplate>> CreateProjectionProgramStepTemplateAsync(ProjectionProgramStepTemplate template)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            ctx.Add(template);
            await ctx.SaveChangesAsync();

            return Result<ProjectionProgramStepTemplate>.Ok(template);
        }
        catch (Exception ex)
        {
            return Result<ProjectionProgramStepTemplate>.Fail(ex.Message);
        }
    }

    public async Task<Result<ProjectionProgram>> CreateProjectionProgramAsync(ProjectionProgram projectionProgram)
    {
        try
        {
            if (string.IsNullOrEmpty(projectionProgram.Name))
            {
                return Result<ProjectionProgram>.Fail("Projection program name cannot be empty.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            projectionProgram.IsActive = true;

            ctx.Add(projectionProgram);

            await ctx.SaveChangesAsync();

            return Result<ProjectionProgram>.Ok(projectionProgram);
        }
        catch (Exception ex)
        {
            return Result<ProjectionProgram>.Fail(ex.Message);
        }
    }

    public async Task<Result<bool>> SaveProjectionProgramStepsAsync(IEnumerable<ProjectionProgramStep> projectionProgramSteps)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<ProjectionProgramStep> stepsList = projectionProgramSteps.ToList();

            if (stepsList.Count == 0)
            {
                return Result<bool>.Ok(true);
            }

            Guid programId = stepsList.First().ProjectionProgramId;

            List<ProjectionProgramStep> existingSteps = await ctx.ProjectionProgramSteps
                .Include(x => x.ParameterValues)
                .Where(x => x.ProjectionProgramId == programId && x.IsActive)
                .ToListAsync();

            Dictionary<Guid, ProjectionProgramStep> existingStepsDict = existingSteps.ToDictionary(x => x.Id);

            foreach (ProjectionProgramStep step in stepsList.Where(x => existingStepsDict.ContainsKey(x.Id)))
            {
                ProjectionProgramStep existingStep = existingStepsDict[step.Id];
                existingStep.SortOrder = step.SortOrder;
                existingStep.TemplateId = step.TemplateId;

                await UpdateParameterValues(existingStep, step.ParameterValues);
            }

            foreach (ProjectionProgramStep newStep in stepsList.Where(x => !existingStepsDict.ContainsKey(x.Id)))
            {
                newStep.IsActive = true;
                newStep.Template = null;
                newStep.ProjectionProgram = null;

                foreach (ProjectionProgramParameterValue paramValue in newStep.ParameterValues)
                {
                    paramValue.Parameter = null;
                    paramValue.ProjectionProgramStep = null;
                }

                ctx.ProjectionProgramSteps.Add(newStep);
            }

            await ctx.SaveChangesAsync();

            await _clientService.SendUpdatedProjectionProgramInfoToClientsAsync(projectionProgramSteps.First().ProjectionProgramId);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail(ex.Message);
        }
    }

    private async Task UpdateParameterValues(ProjectionProgramStep existingStep, List<ProjectionProgramParameterValue> newParameterValues)
    {
        await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

        Dictionary<Guid, ProjectionProgramParameterValue> existingValuesDict = existingStep.ParameterValues.ToDictionary(x => x.ParameterId);

        foreach (ProjectionProgramParameterValue newValue in newParameterValues)
        {
            if (existingValuesDict.TryGetValue(newValue.ParameterId, out ProjectionProgramParameterValue? existingValue))
            {
                existingValue.Value = newValue.Value;
            }
            else
            {
                newValue.ProjectionProgramStepId = existingStep.Id;
                newValue.Parameter = null;
                newValue.ProjectionProgramStep = null;

                ctx.Add(newValue);
            }
        }

        HashSet<Guid> newParameterIds = newParameterValues.Select(x => x.ParameterId).ToHashSet();

        List<ProjectionProgramParameterValue> toRemove = [.. existingStep.ParameterValues.Where(x => !newParameterIds.Contains(x.ParameterId))];

        foreach (ProjectionProgramParameterValue valueToRemove in toRemove)
        {
            ctx.Remove(valueToRemove);
        }

        await _clientService.SendUpdatedProjectionProgramInfoToClientsAsync(existingStep.ProjectionProgramId);
    }

    public async Task<Result<bool>> DeleteProjectionProgramStepAsync(Guid id)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            ProjectionProgramStep? projectionProgramStep = await ctx.ProjectionProgramSteps
                .Where(x => x.Id == id)
                .FirstOrDefaultAsync();

            if (projectionProgramStep == null)
            {
                return Result<bool>.Fail("Projection program step not found.");
            }

            projectionProgramStep.IsActive = false;

            await ctx.SaveChangesAsync();

            await _clientService.SendUpdatedProjectionProgramInfoToClientsAsync(projectionProgramStep.ProjectionProgramId);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail(ex.Message);
        }
    }

    public async Task<Result<ProjectionProgram>> GetProjectionProgramAsync(Guid projectionProgramId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            ProjectionProgram? projectionProgram = await ctx.ProjectionPrograms
                .Where(x => x.Id == projectionProgramId)
                .FirstOrDefaultAsync();

            if (projectionProgram == null)
            {
                return Result<ProjectionProgram>.Fail("Projection program not found.");
            }

            return Result<ProjectionProgram>.Ok(projectionProgram);
        }
        catch (Exception ex)
        {
            return Result<ProjectionProgram>.Fail(ex.Message);
        }
    }
}
