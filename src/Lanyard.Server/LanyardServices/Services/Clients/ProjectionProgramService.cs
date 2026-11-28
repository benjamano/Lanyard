using Lanyard.Application.Services.Projection;
using Lanyard.Application.Services.Projection.Bindings;
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

            string? bindingError = await ValidateBindingsAsync(ctx, stepsList);

            if (bindingError is not null)
            {
                return Result<bool>.Fail(bindingError);
            }

            string? gameWaitError = await ValidateGameWaitStepsAsync(ctx, programId, stepsList);

            if (gameWaitError is not null)
            {
                return Result<bool>.Fail(gameWaitError);
            }

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

                UpdateParameterValues(ctx, existingStep, step.ParameterValues);
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

    /// <summary>
    /// A binding must name a data source that exists and sit on a parameter that can show it (only
    /// Show Text's Text today). Blank keys are cleared to "fixed value" first. Returns the error to
    /// show, or null when every binding is fine.
    /// </summary>
    private static async Task<string?> ValidateBindingsAsync(ApplicationDbContext ctx, List<ProjectionProgramStep> steps)
    {
        List<ProjectionProgramParameterValue> bound = [];

        foreach (ProjectionProgramParameterValue value in steps.SelectMany(s => s.ParameterValues))
        {
            if (string.IsNullOrWhiteSpace(value.BindingKey))
            {
                value.BindingKey = null;
                continue;
            }

            if (ProjectionBindingCatalogue.Find(value.BindingKey) is null)
            {
                return $"Unknown data source \"{value.BindingKey}\".";
            }

            bound.Add(value);
        }

        if (bound.Count == 0)
        {
            return null;
        }

        HashSet<Guid> parameterIds = [.. bound.Select(v => v.ParameterId)];

        Dictionary<Guid, (string ParameterName, string? TemplateName)> parameters = await ctx.ProjectionProgramStepTemplateParameters
            .AsNoTracking()
            .TagWithCallSite()
            .Where(p => parameterIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name, TemplateName = p.Template!.Name })
            .ToDictionaryAsync(p => p.Id, p => (p.Name, (string?)p.TemplateName));

        foreach (ProjectionProgramParameterValue value in bound)
        {
            if (!parameters.TryGetValue(value.ParameterId, out (string ParameterName, string? TemplateName) parameter)
                || !ProjectionBindingCatalogue.IsBindable(parameter.TemplateName, parameter.ParameterName))
            {
                return "Live data can only be used for a Show Text step's text.";
            }
        }

        return null;
    }

    /// <summary>
    /// Brings an existing step's values in line with the editor's. Adds and removes go on the
    /// caller's context, which saves them with the rest of the steps: they used to go on a
    /// context of their own that was never saved, so a value added to a step that already existed
    /// (or one taken away) was silently lost.
    /// </summary>
    private static void UpdateParameterValues(ApplicationDbContext ctx, ProjectionProgramStep existingStep, List<ProjectionProgramParameterValue> newParameterValues)
    {
        Dictionary<Guid, ProjectionProgramParameterValue> existingValuesDict = existingStep.ParameterValues.ToDictionary(x => x.ParameterId);

        foreach (ProjectionProgramParameterValue newValue in newParameterValues)
        {
            if (existingValuesDict.TryGetValue(newValue.ParameterId, out ProjectionProgramParameterValue? existingValue))
            {
                existingValue.Value = newValue.Value;
                existingValue.BindingKey = newValue.BindingKey;
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
    }

    /// <summary>
    /// A "Wait for Game" step needs a game event it knows, a laser kiosk in this program's
    /// company, and sensible numbers - the same rules the runner applies when the program starts,
    /// so a program that saves can always play. Returns the error to show, or null.
    /// </summary>
    private static async Task<string?> ValidateGameWaitStepsAsync(ApplicationDbContext ctx, Guid programId, List<ProjectionProgramStep> steps)
    {
        HashSet<Guid> templateIds = [.. steps.Select(s => s.TemplateId)];

        List<Guid> waitTemplateIds = await ctx.ProjectionProgramStepTemplates
            .AsNoTracking()
            .TagWithCallSite()
            .Where(t => templateIds.Contains(t.Id) && t.Name == GameWaitStepNames.Template)
            .Select(t => t.Id)
            .ToListAsync();

        if (waitTemplateIds.Count == 0)
        {
            return null;
        }

        Dictionary<Guid, string> parameterNames = await ctx.ProjectionProgramStepTemplateParameters
            .AsNoTracking()
            .TagWithCallSite()
            .Where(p => waitTemplateIds.Contains(p.TemplateId) && p.IsActive)
            .ToDictionaryAsync(p => p.Id, p => p.Name);

        Dictionary<ProjectionProgramStep, GameWaitSettings> waits = [];

        foreach (ProjectionProgramStep step in steps.Where(s => waitTemplateIds.Contains(s.TemplateId)).OrderBy(s => s.SortOrder))
        {
            Dictionary<string, string?> values = new(StringComparer.Ordinal);

            foreach (ProjectionProgramParameterValue value in step.ParameterValues)
            {
                if (parameterNames.TryGetValue(value.ParameterId, out string? name))
                {
                    values[name] = value.Value;
                }
            }

            Result<GameWaitSettings> parsed = GameWaitSettings.Parse(values);

            if (!parsed.IsSuccess)
            {
                return $"Step {step.SortOrder + 1} ({GameWaitStepNames.Template}): {parsed.Error}";
            }

            waits[step] = parsed.Data!;
        }

        // The editor's context is tenant-filtered, but the company is compared explicitly too, so
        // the check still holds if this ever runs on an unfiltered context.
        int? companyId = await ctx.ProjectionPrograms
            .AsNoTracking()
            .TagWithCallSite()
            .Where(p => p.Id == programId)
            .Select(p => (int?)p.CompanyId)
            .FirstOrDefaultAsync();

        List<Guid> kioskIds = [.. waits.Values.Select(w => w.KioskClientId).Distinct()];

        HashSet<Guid> companyKiosks = companyId is null
            ? []
            : [.. await ctx.Clients
                .AsNoTracking()
                .TagWithCallSite()
                .Where(c => kioskIds.Contains(c.Id) && c.CompanyId == companyId.Value)
                .Select(c => c.Id)
                .ToListAsync()];

        foreach ((ProjectionProgramStep step, GameWaitSettings settings) in waits.OrderBy(w => w.Key.SortOrder))
        {
            if (!companyKiosks.Contains(settings.KioskClientId))
            {
                return $"Step {step.SortOrder + 1} ({GameWaitStepNames.Template}): that laser kiosk isn't available. Choose another.";
            }
        }

        return null;
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

    public async Task<Result<bool>> TriggerProjectionProgramAsync(Guid projectionProgramId, Guid selectedClientId, int? displayIndex = null)
    {
        try
        {
            Result<ProjectionProgram> programResult = await GetProjectionProgramAsync(projectionProgramId);

            if (!programResult.IsSuccess || programResult.Data == null)
            {
                return Result<bool>.Fail($"Projection program not found: {projectionProgramId}");
            }

            Result<bool> triggerResult = await _clientService.TriggerProjectionProgramOnClientAsync(selectedClientId, projectionProgramId, displayIndex);

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
