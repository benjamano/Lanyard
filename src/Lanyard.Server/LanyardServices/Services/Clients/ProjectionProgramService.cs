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
                        .ThenInclude(x => x!.Parameters.Where(p => p.IsActive))
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
                .Include(x => x.Parameters
                    .Where(x=> x.IsActive))
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
        return await SaveProjectionProgramStepTemplateAsync(template);
    }

    public async Task<Result<ProjectionProgramStepTemplate>> SaveProjectionProgramStepTemplateAsync(ProjectionProgramStepTemplate template)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(template.Name))
            {
                return Result<ProjectionProgramStepTemplate>.Fail("Template name is required.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            ProjectionProgramStepTemplate? existingTemplate = template.Id == Guid.Empty
                ? null
                : await ctx.ProjectionProgramStepTemplates
                    .Include(x => x.Parameters)
                    .FirstOrDefaultAsync(x => x.Id == template.Id);

            ProjectionProgramStepTemplate targetTemplate;

            if (existingTemplate is null)
            {
                targetTemplate = new ProjectionProgramStepTemplate
                {
                    Id = template.Id == Guid.Empty ? Guid.NewGuid() : template.Id,
                    Name = template.Name.Trim(),
                    Description = template.Description?.Trim(),
                    IsActive = true,
                    Parameters = []
                };

                ctx.ProjectionProgramStepTemplates.Add(targetTemplate);
            }
            else
            {
                targetTemplate = existingTemplate;
                targetTemplate.Name = template.Name.Trim();
                targetTemplate.Description = template.Description?.Trim();
                targetTemplate.IsActive = true;
            }

            List<ProjectionProgramStepTemplateParameter> incomingParameters = template.Parameters?.ToList() ?? [];
            Dictionary<Guid, ProjectionProgramStepTemplateParameter> existingParameters = targetTemplate.Parameters.ToDictionary(x => x.Id);
            HashSet<Guid> seenParameterIds = [];

            foreach (ProjectionProgramStepTemplateParameter incomingParameter in incomingParameters)
            {
                if (string.IsNullOrWhiteSpace(incomingParameter.Name) || string.IsNullOrWhiteSpace(incomingParameter.DataType))
                {
                    continue;
                }

                Guid parameterId = incomingParameter.Id == Guid.Empty ? Guid.NewGuid() : incomingParameter.Id;

                if (existingParameters.TryGetValue(parameterId, out ProjectionProgramStepTemplateParameter? existingParameter))
                {
                    existingParameter.Name = incomingParameter.Name.Trim();
                    existingParameter.Description = incomingParameter.Description?.Trim();
                    existingParameter.DataType = incomingParameter.DataType.Trim();
                    existingParameter.IsRequired = incomingParameter.IsRequired;
                    existingParameter.IsActive = true;
                }
                else
                {
                    targetTemplate.Parameters.Add(new ProjectionProgramStepTemplateParameter
                    {
                        Id = parameterId,
                        TemplateId = targetTemplate.Id,
                        Name = incomingParameter.Name.Trim(),
                        Description = incomingParameter.Description?.Trim(),
                        DataType = incomingParameter.DataType.Trim(),
                        IsRequired = incomingParameter.IsRequired,
                        IsActive = true
                    });
                }

                seenParameterIds.Add(parameterId);
            }

            foreach (ProjectionProgramStepTemplateParameter existingParameter in targetTemplate.Parameters.Where(x => !seenParameterIds.Contains(x.Id)))
            {
                existingParameter.IsActive = false;
            }

            await ctx.SaveChangesAsync();

            ProjectionProgramStepTemplate? savedTemplate = await ctx.ProjectionProgramStepTemplates
                .AsNoTracking()
                .Include(x => x.Parameters.Where(p => p.IsActive))
                .FirstOrDefaultAsync(x => x.Id == targetTemplate.Id);

            if (savedTemplate is null)
            {
                return Result<ProjectionProgramStepTemplate>.Fail("Template saved but could not be reloaded.");
            }

            return Result<ProjectionProgramStepTemplate>.Ok(savedTemplate);
        }
        catch (Exception ex)
        {
            return Result<ProjectionProgramStepTemplate>.Fail(ex.Message);
        }
    }

    public async Task<Result<bool>> DeleteProjectionProgramStepTemplateAsync(Guid templateId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            ProjectionProgramStepTemplate? template = await ctx.ProjectionProgramStepTemplates
                .Include(x => x.Parameters)
                .FirstOrDefaultAsync(x => x.Id == templateId);

            if (template is null)
            {
                return Result<bool>.Fail("Projection program step template not found.");
            }

            template.IsActive = false;

            foreach (ProjectionProgramStepTemplateParameter parameter in template.Parameters)
            {
                parameter.IsActive = false;
            }

            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail(ex.Message);
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

        //await _clientService.SendUpdatedProjectionProgramInfoToClientsAsync(existingStep.ProjectionProgramId);
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
                .Include(x=> x.ProjectionProgramSteps.Where(x=> x.IsActive))
                    .ThenInclude(x=> x.ParameterValues)
                .Include(x=> x.ProjectionProgramSteps.Where(x=> x.IsActive))
                    .ThenInclude(x=> x.Template)
                        .ThenInclude(x=> x!.Parameters.Where(p => p.IsActive))
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

    public async Task<Result<bool>> DeleteProjectionProgramAsync(Guid projectionProgramId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            ProjectionProgram? projectionProgram = await ctx.ProjectionPrograms
                .Where(x => x.Id == projectionProgramId)
                .FirstOrDefaultAsync();

            if (projectionProgram == null)
            {
                return Result<bool>.Fail("Projection program not found.");
            }

            projectionProgram.IsActive = false;

            await ctx.SaveChangesAsync();

            await _clientService.SendUpdatedProjectionProgramInfoToClientsAsync(projectionProgramId);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail(ex.Message);
        }
    }

    public async Task<Result<bool>> TriggerProjectionProgramAsync(Guid projectionProgramId, Guid selectedClientId)
    {
        try
        {
            Result<ProjectionProgram> programResult = await GetProjectionProgramAsync(projectionProgramId);

            if (!programResult.IsSuccess || programResult.Data == null)
            {
                return Result<bool>.Fail($"Projection program not found: {projectionProgramId}");
            }

            Result<bool> triggerResult = await _clientService.TriggerProjectionProgramOnClientAsync(selectedClientId, projectionProgramId);

            if (!triggerResult.IsSuccess)
            {
                return Result<bool>.Fail(triggerResult.Error ?? "Failed to trigger projection program on client.");
            }

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail(ex.Message);
        }
    }
}
