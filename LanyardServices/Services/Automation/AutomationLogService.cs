#nullable enable

using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services;

public class AutomationLogService(IDbContextFactory<ApplicationDbContext> factory) : IAutomationLogService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;

    public async Task<Result<IEnumerable<AutomationRuleExecution>>> GetRecentExecutionsAsync(int count)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();
            IEnumerable<AutomationRuleExecution> executions = await ctx.AutomationRuleExecutions
                .Include(e => e.ActionExecutions)
                    .ThenInclude(ae => ae.AutomationRuleAction)
                .OrderByDescending(e => e.ExecutedAt)
                .Take(count)
                .AsNoTracking()
                .ToListAsync();
            return Result<IEnumerable<AutomationRuleExecution>>.Ok(executions);
        }
        catch (Exception ex)
        {
            return Result<IEnumerable<AutomationRuleExecution>>.Fail(ex.Message);
        }
    }
}
