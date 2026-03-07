using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.FlowActions;

public sealed class FlowActionDispatcher(IEnumerable<IFlowActionHandler> handlers) : IFlowActionDispatcher
{
    private readonly IEnumerable<IFlowActionHandler> _handlers = handlers;

    public async Task<Result<bool>> DispatchAsync(string templateKey, ProjectionProgramStep step, FlowActionExecutionContext context, CancellationToken ct)
    {
        IFlowActionHandler? handler = _handlers.FirstOrDefault(x => x.CanHandle(templateKey));

        if (handler is null)
        {
            return Result<bool>.Fail($"No flow action handler registered for '{templateKey}'.");
        }

        return await handler.ExecuteAsync(step, context, ct);
    }
}
