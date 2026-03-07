using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.FlowActions;

public interface IFlowActionHandler
{
    bool CanHandle(string templateKey);

    Task<Result<bool>> ExecuteAsync(ProjectionProgramStep step, FlowActionExecutionContext context, CancellationToken ct);
}
