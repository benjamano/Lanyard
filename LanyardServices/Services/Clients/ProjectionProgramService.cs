using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services.Clients;

public class ProjectionProgramService(IDbContextFactory<ApplicationDbContext> factory) : IProjectionProgramService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;

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

            // Load existing steps with parameter values
            List<ProjectionProgramStep> existingSteps = await ctx.ProjectionProgramSteps
                .Include(x => x.ParameterValues)
                .Where(x => x.ProjectionProgramId == programId && x.IsActive)
                .ToListAsync();

            // Create lookup for efficient updates
            Dictionary<Guid, ProjectionProgramStep> existingStepsDict = existingSteps.ToDictionary(x => x.Id);

            // Update existing steps
            foreach (ProjectionProgramStep step in stepsList.Where(x => existingStepsDict.ContainsKey(x.Id)))
            {
                ProjectionProgramStep existingStep = existingStepsDict[step.Id];
                existingStep.SortOrder = step.SortOrder;
                existingStep.Source = step.Source;
                existingStep.TemplateId = step.TemplateId;

                // Update parameter values efficiently
                UpdateParameterValues(ctx, existingStep, step.ParameterValues);
            }

            // Add new steps
            foreach (ProjectionProgramStep newStep in stepsList.Where(x => !existingStepsDict.ContainsKey(x.Id)))
            {
                newStep.IsActive = true;
                // Clear navigation properties but keep TemplateId - this is the critical fix
                newStep.Template = null;
                newStep.ProjectionProgram = null;

                // Prepare parameter values for insertion
                foreach (ProjectionProgramParameterValue paramValue in newStep.ParameterValues)
                {
                    paramValue.Parameter = null;
                    paramValue.ProjectionProgramStep = null;
                }

                ctx.ProjectionProgramSteps.Add(newStep);
            }

            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail(ex.Message);
        }
    }

    private void UpdateParameterValues(ApplicationDbContext ctx, ProjectionProgramStep existingStep, List<ProjectionProgramParameterValue> newParameterValues)
    {
        // Create lookup for existing parameter values
        Dictionary<Guid, ProjectionProgramParameterValue> existingValuesDict = existingStep.ParameterValues.ToDictionary(x => x.ParameterId);

        foreach (ProjectionProgramParameterValue newValue in newParameterValues)
        {
            if (existingValuesDict.TryGetValue(newValue.ParameterId, out ProjectionProgramParameterValue? existingValue))
            {
                // Update existing value
                existingValue.Value = newValue.Value;
            }
            else
            {
                // Add new parameter value
                newValue.ProjectionProgramStepId = existingStep.Id;
                newValue.Parameter = null;
                newValue.ProjectionProgramStep = null;
                ctx.Add(newValue);
            }
        }

        // Remove parameter values that are no longer present
        HashSet<Guid> newParameterIds = newParameterValues.Select(x => x.ParameterId).ToHashSet();
        List<ProjectionProgramParameterValue> toRemove = existingStep.ParameterValues
            .Where(x => !newParameterIds.Contains(x.ParameterId))
            .ToList();

        foreach (ProjectionProgramParameterValue valueToRemove in toRemove)
        {
            ctx.Remove(valueToRemove);
        }
    }

    public async Task<Result<bool>> DeleteProjectionProgramStepAsync(Guid id)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            await ctx.ProjectionProgramSteps
                .Where(x => x.Id == id)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.IsActive, false));
 
            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail(ex.Message);
        }
    }
}
