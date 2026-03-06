using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.FlowActions;

public interface IFlowActionDispatcher
{
    Task<Result<bool>> DispatchAsync(string templateKey, ProjectionProgramStep step, FlowActionExecutionContext context, CancellationToken ct);
}
