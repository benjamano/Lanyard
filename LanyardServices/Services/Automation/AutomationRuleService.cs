#nullable enable

using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Enum;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services;

public class AutomationRuleService(
    IDbContextFactory<ApplicationDbContext> factory,
    AutomationEngineService engineService) : IAutomationRuleService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly AutomationEngineService _engineService = engineService;

    public async Task<Result<IEnumerable<AutomationRule>>> GetRulesAsync()
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();
            IEnumerable<AutomationRule> rules = await ctx.AutomationRules
                .Where(r => r.IsActive)
                .Include(r => r.Actions.Where(a => a.IsActive))
                .AsNoTracking()
                .ToListAsync();
            return Result<IEnumerable<AutomationRule>>.Ok(rules);
        }
        catch (Exception ex)
        {
            return Result<IEnumerable<AutomationRule>>.Fail(ex.Message);
        }
    }

    public async Task<Result<AutomationRule?>> GetRuleAsync(Guid id)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();
            AutomationRule? rule = await ctx.AutomationRules
                .Include(r => r.Actions.Where(a => a.IsActive))
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == id);
            return Result<AutomationRule?>.Ok(rule);
        }
        catch (Exception ex)
        {
            return Result<AutomationRule?>.Fail(ex.Message);
        }
    }

    public async Task<Result<IEnumerable<AutomationRule>>> GetRulesByTriggerAsync(Guid triggerClientId, GameStatus triggerEvent)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();
            IEnumerable<AutomationRule> rules = await ctx.AutomationRules
                .Where(r => r.TriggerClientId == triggerClientId && r.TriggerEvent == triggerEvent && r.IsActive)
                .Include(r => r.Actions.Where(a => a.IsActive))
                .AsNoTracking()
                .ToListAsync();
            return Result<IEnumerable<AutomationRule>>.Ok(rules);
        }
        catch (Exception ex)
        {
            return Result<IEnumerable<AutomationRule>>.Fail(ex.Message);
        }
    }

    public async Task<Result<AutomationRule>> CreateRuleAsync(AutomationRule rule)
    {
        try
        {
            rule.CreateDate = DateTime.UtcNow;
            rule.IsActive = true;
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();
            ctx.AutomationRules.Add(rule);
            await ctx.SaveChangesAsync();
            _engineService.InvalidateRuleCache();
            return Result<AutomationRule>.Ok(rule);
        }
        catch (Exception ex)
        {
            return Result<AutomationRule>.Fail(ex.Message);
        }
    }

    public async Task<Result<AutomationRule>> UpdateRuleAsync(AutomationRule rule)
    {
        try
        {
            rule.LastUpdateDate = DateTime.UtcNow;
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();
            ctx.AutomationRules.Update(rule);
            await ctx.SaveChangesAsync();
            _engineService.InvalidateRuleCache();
            return Result<AutomationRule>.Ok(rule);
        }
        catch (Exception ex)
        {
            return Result<AutomationRule>.Fail(ex.Message);
        }
    }

    public async Task<Result<bool>> DeleteRuleAsync(Guid id)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();
            AutomationRule? rule = await ctx.AutomationRules.FindAsync(id);
            if (rule == null)
            {
                return Result<bool>.Fail("Rule not found.");
            }
            rule.IsActive = false;
            rule.LastUpdateDate = DateTime.UtcNow;
            await ctx.SaveChangesAsync();
            _engineService.InvalidateRuleCache();
            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail(ex.Message);
        }
    }
}
