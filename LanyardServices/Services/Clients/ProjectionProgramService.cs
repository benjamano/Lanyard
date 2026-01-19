using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using System;
using System;
using System.Collections.Generic;
using System.Text;

namespace Lanyard.Application.Services.Clients;

public class ProjectionProgramService(IDbContextFactory<ApplicationDbContext> factory) : IProjectionProgramService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;

    public async Task<Result<IEnumerable<ProjectionProgram>>> GetProjectionProgramsAsync()
    {
        try
        {
            ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            IEnumerable<ProjectionProgram> programs = await ctx.ProjectionPrograms
                .AsNoTracking()
                .Include(x=> x.ProjectionProgramSteps)
                    .ThenInclude(x=> x.Template)
                        .ThenInclude(x=> x.Parameters)
                .Include(x=> x.ProjectionProgramSteps)
                    .ThenInclude(x=> x.ParameterValues)
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
            ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            IEnumerable<ProjectionProgramStepTemplate> templates = await ctx.ProjectionProgramStepTemplates
                .AsNoTracking()
                .Include(x=> x.Parameters)
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
            ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

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
            ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

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
            ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            IEnumerable<ProjectionProgramStep> existingSteps = await ctx.ProjectionProgramSteps
                .Where(x => x.ProjectionProgramId == projectionProgramSteps.First().ProjectionProgramId)
                .ToListAsync();

            foreach (ProjectionProgramStep existingStep in existingSteps)
            {
                ProjectionProgramStep? updatedStep = projectionProgramSteps.FirstOrDefault(x => x.Id == existingStep.Id);

                if (updatedStep != null)
                {
                    existingStep.SortOrder = updatedStep.SortOrder;
                    existingStep.Source = updatedStep.Source;
                    existingStep.TemplateId = updatedStep.TemplateId;
                    existingStep.IsActive = updatedStep.IsActive;
                    existingStep.ProjectionProgramId = updatedStep.ProjectionProgramId;
                    existingStep.ParameterValues = updatedStep.ParameterValues;
                }
            }

            foreach (ProjectionProgramStep newStep in projectionProgramSteps.Where(x=> existingSteps.Select(y=> y.Id).Contains(x.Id) == false))
            {
                newStep.IsActive = true;
                newStep.Template = null;

                ctx.Add(newStep);
            }

            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail(ex.Message);
        }
    }
}
