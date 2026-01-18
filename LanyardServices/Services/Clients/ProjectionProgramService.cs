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
}
