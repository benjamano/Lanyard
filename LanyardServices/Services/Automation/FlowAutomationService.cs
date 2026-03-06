using System.Text;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services;

public class FlowAutomationService(IDbContextFactory<ApplicationDbContext> factory) : IFlowAutomationService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;

    public async Task<Result<bool>> HandleTriggerAsync(FlowTriggerEvent trigger, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(trigger.TriggerKey))
            {
                return Result<bool>.Fail("Trigger key is required.");
            }

            await using ApplicationDbContext readCtx = await _factory.CreateDbContextAsync(ct);

            List<AutomationFlow> matchingFlows = await readCtx.AutomationFlows
                .AsNoTracking()
                .Where(x => x.IsActive)
                .Where(x => x.TriggerKey == trigger.TriggerKey)
                .Where(x => x.ClientId == null || x.ClientId == trigger.ClientId)
                .Include(x => x.Steps.Where(s => s.IsActive))
                .ToListAsync(ct);

            if (matchingFlows.Count == 0)
            {
                return Result<bool>.Ok(true);
            }

            List<AutomationFlowRun> runSummaries = [];

            foreach (AutomationFlow flow in matchingFlows)
            {
                AutomationFlowRun run = await ExecuteFlowAsync(flow, trigger, ct);
                runSummaries.Add(run);
            }

            await using ApplicationDbContext writeCtx = await _factory.CreateDbContextAsync(ct);
            writeCtx.AutomationFlowRuns.AddRange(runSummaries);
            await writeCtx.SaveChangesAsync(ct);

            bool allSucceeded = runSummaries.All(x => x.IsSuccess);
            return Result<bool>.Ok(allSucceeded);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail(ex.Message);
        }
    }

    public async Task<Result<IEnumerable<AutomationFlow>>> GetFlowsAsync(
        string? triggerKey = null,
        Guid? clientId = null,
        bool activeOnly = true,
        CancellationToken ct = default)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(ct);

            IQueryable<AutomationFlow> query = ctx.AutomationFlows
                .AsNoTracking()
                .Include(x => x.Steps.Where(s => s.IsActive));

            if (activeOnly)
            {
                query = query.Where(x => x.IsActive);
            }

            if (!string.IsNullOrWhiteSpace(triggerKey))
            {
                query = query.Where(x => x.TriggerKey == triggerKey);
            }

            if (clientId.HasValue)
            {
                query = query.Where(x => x.ClientId == null || x.ClientId == clientId.Value);
            }

            List<AutomationFlow> flows = await query
                .OrderBy(x => x.Name)
                .ToListAsync(ct);

            foreach (AutomationFlow flow in flows)
            {
                flow.Steps = flow.Steps.OrderBy(x => x.SortOrder).ToList();
            }

            return Result<IEnumerable<AutomationFlow>>.Ok(flows);
        }
        catch (Exception ex)
        {
            return Result<IEnumerable<AutomationFlow>>.Fail(ex.Message);
        }
    }

    public async Task<Result<AutomationFlow>> SaveFlowAsync(AutomationFlow flow, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(flow.Name))
            {
                return Result<AutomationFlow>.Fail("Flow name is required.");
            }

            if (string.IsNullOrWhiteSpace(flow.TriggerKey))
            {
                return Result<AutomationFlow>.Fail("Trigger key is required.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(ct);

            AutomationFlow? existingFlow = flow.Id == Guid.Empty
                ? null
                : await ctx.AutomationFlows
                    .Include(x => x.Steps)
                    .FirstOrDefaultAsync(x => x.Id == flow.Id, ct);

            AutomationFlow target;

            if (existingFlow is null)
            {
                target = new AutomationFlow
                {
                    Id = flow.Id == Guid.Empty ? Guid.NewGuid() : flow.Id,
                    Name = flow.Name.Trim(),
                    TriggerKey = flow.TriggerKey.Trim(),
                    ClientId = flow.ClientId,
                    ConditionExpression = flow.ConditionExpression?.Trim(),
                    ContinueOnStepFailure = flow.ContinueOnStepFailure,
                    IsActive = true,
                    CreateDate = DateTime.UtcNow,
                    LastUpdateDate = DateTime.UtcNow,
                    Steps = []
                };

                ctx.AutomationFlows.Add(target);
            }
            else
            {
                target = existingFlow;
                target.Name = flow.Name.Trim();
                target.TriggerKey = flow.TriggerKey.Trim();
                target.ClientId = flow.ClientId;
                target.ConditionExpression = flow.ConditionExpression?.Trim();
                target.ContinueOnStepFailure = flow.ContinueOnStepFailure;
                target.IsActive = flow.IsActive;
                target.LastUpdateDate = DateTime.UtcNow;
            }

            List<AutomationFlowStep> incomingSteps = flow.Steps
                .OrderBy(x => x.SortOrder)
                .ToList();

            Dictionary<Guid, AutomationFlowStep> existingSteps = target.Steps.ToDictionary(x => x.Id);
            HashSet<Guid> seenStepIds = [];

            for (int i = 0; i < incomingSteps.Count; i++)
            {
                AutomationFlowStep incomingStep = incomingSteps[i];
                Guid stepId = incomingStep.Id == Guid.Empty ? Guid.NewGuid() : incomingStep.Id;

                if (existingSteps.TryGetValue(stepId, out AutomationFlowStep? existingStep))
                {
                    existingStep.SortOrder = incomingStep.SortOrder == 0 ? i : incomingStep.SortOrder;
                    existingStep.ActionType = incomingStep.ActionType;
                    existingStep.ActionPayloadJson = incomingStep.ActionPayloadJson;
                    existingStep.ContinueOnFailure = incomingStep.ContinueOnFailure;
                    existingStep.IsActive = incomingStep.IsActive;
                }
                else
                {
                    AutomationFlowStep newStep = new()
                    {
                        Id = stepId,
                        AutomationFlowId = target.Id,
                        SortOrder = incomingStep.SortOrder == 0 ? i : incomingStep.SortOrder,
                        ActionType = incomingStep.ActionType,
                        ActionPayloadJson = incomingStep.ActionPayloadJson,
                        ContinueOnFailure = incomingStep.ContinueOnFailure,
                        IsActive = incomingStep.IsActive
                    };

                    ctx.AutomationFlowSteps.Add(newStep);
                    target.Steps.Add(newStep);
                }

                seenStepIds.Add(stepId);
            }

            foreach (AutomationFlowStep existingStep in target.Steps.Where(x => !seenStepIds.Contains(x.Id)))
            {
                existingStep.IsActive = false;
            }

            await ctx.SaveChangesAsync(ct);

            AutomationFlow? savedFlow = await ctx.AutomationFlows
                .AsNoTracking()
                .Include(x => x.Steps.Where(s => s.IsActive))
                .FirstOrDefaultAsync(x => x.Id == target.Id, ct);

            if (savedFlow is null)
            {
                return Result<AutomationFlow>.Fail("Flow saved but could not be reloaded.");
            }

            savedFlow.Steps = savedFlow.Steps.OrderBy(x => x.SortOrder).ToList();

            return Result<AutomationFlow>.Ok(savedFlow);
        }
        catch (Exception ex)
        {
            return Result<AutomationFlow>.Fail(ex.Message);
        }
    }

    private static async Task<AutomationFlowRun> ExecuteFlowAsync(AutomationFlow flow, FlowTriggerEvent trigger, CancellationToken ct)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();

        DateTime startedAtUtc = DateTime.UtcNow;

        if (!EvaluateConditions(flow.ConditionExpression, trigger))
        {
            return new AutomationFlowRun
            {
                Id = Guid.NewGuid(),
                AutomationFlowId = flow.Id,
                TriggerKey = trigger.TriggerKey,
                ClientId = trigger.ClientId,
                IsSuccess = true,
                ExecutedSteps = 0,
                FailedSteps = 0,
                Summary = "Flow skipped: condition did not match trigger context.",
                RunStartedAtUtc = startedAtUtc,
                RunCompletedAtUtc = DateTime.UtcNow,
                StepResults = []
            };
        }

        List<AutomationFlowStep> orderedSteps = flow.Steps
            .Where(x => x.IsActive)
            .OrderBy(x => x.SortOrder)
            .ToList();

        List<AutomationFlowRunStepResult> stepResults = [];
        int failedSteps = 0;

        foreach (AutomationFlowStep step in orderedSteps)
        {
            ct.ThrowIfCancellationRequested();

            Result<bool> stepResult = ExecuteStep(step, trigger);
            if (!stepResult.IsSuccess)
            {
                failedSteps++;
            }

            stepResults.Add(new AutomationFlowRunStepResult
            {
                Id = Guid.NewGuid(),
                AutomationFlowStepId = step.Id,
                SortOrder = step.SortOrder,
                IsSuccess = stepResult.IsSuccess,
                Message = stepResult.IsSuccess ? "Step completed." : stepResult.Error,
                LoggedAtUtc = DateTime.UtcNow
            });

            bool shouldContinue = stepResult.IsSuccess || step.ContinueOnFailure || flow.ContinueOnStepFailure;
            if (!shouldContinue)
            {
                break;
            }
        }

        bool runSucceeded = failedSteps == 0;
        StringBuilder summaryBuilder = new();
        summaryBuilder.Append("Executed ");
        summaryBuilder.Append(stepResults.Count);
        summaryBuilder.Append(" step(s); failures: ");
        summaryBuilder.Append(failedSteps);
        summaryBuilder.Append('.');

        return new AutomationFlowRun
        {
            Id = Guid.NewGuid(),
            AutomationFlowId = flow.Id,
            TriggerKey = trigger.TriggerKey,
            ClientId = trigger.ClientId,
            IsSuccess = runSucceeded,
            ExecutedSteps = stepResults.Count,
            FailedSteps = failedSteps,
            Summary = summaryBuilder.ToString(),
            RunStartedAtUtc = startedAtUtc,
            RunCompletedAtUtc = DateTime.UtcNow,
            StepResults = stepResults
        };
    }

    private static bool EvaluateConditions(string? conditionExpression, FlowTriggerEvent trigger)
    {
        if (string.IsNullOrWhiteSpace(conditionExpression))
        {
            return true;
        }

        string[] clauses = conditionExpression.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (string clause in clauses)
        {
            if (clause.Contains("==", StringComparison.Ordinal))
            {
                string[] parts = clause.Split("==", StringSplitOptions.TrimEntries);
                if (parts.Length != 2)
                {
                    return false;
                }

                bool hasValue = trigger.Attributes.TryGetValue(parts[0], out string? actualValue);
                if (!hasValue || !string.Equals(actualValue, parts[1], StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                continue;
            }

            if (clause.Contains("!=", StringComparison.Ordinal))
            {
                string[] parts = clause.Split("!=", StringSplitOptions.TrimEntries);
                if (parts.Length != 2)
                {
                    return false;
                }

                bool hasValue = trigger.Attributes.TryGetValue(parts[0], out string? actualValue);
                if (!hasValue)
                {
                    continue;
                }

                if (string.Equals(actualValue, parts[1], StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                continue;
            }

            return false;
        }

        return true;
    }

    private static Result<bool> ExecuteStep(AutomationFlowStep step, FlowTriggerEvent trigger)
    {
        _ = trigger;

        if (string.IsNullOrWhiteSpace(step.ActionType))
        {
            return Result<bool>.Fail("Step action type is required.");
        }

        if (string.Equals(step.ActionType, "NoOp", StringComparison.OrdinalIgnoreCase))
        {
            return Result<bool>.Ok(true);
        }

        if (string.Equals(step.ActionType, "LogMessage", StringComparison.OrdinalIgnoreCase))
        {
            return Result<bool>.Ok(true);
        }

        return Result<bool>.Fail($"Unsupported action type '{step.ActionType}'.");
    }
}
