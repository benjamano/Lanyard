using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Projection.Bindings;

public interface IProjectionBindingResolver
{
    /// <summary>
    /// The text each bound parameter on <paramref name="step"/> should show right now, keyed by
    /// ProjectionProgramParameterValue.Id. Never fails: a source with no data, an error, a timeout
    /// or an unknown key all give the parameter's fallback text instead. Fixed values aren't in it.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> ResolveStepAsync(ProjectionBindingContext context, ProjectionProgramStep step, CancellationToken cancellationToken);

    /// <summary>What a source would show right now, for the editor. Null data means "no data yet".</summary>
    Task<Result<string?>> PreviewAsync(string bindingKey, int companyId, int? locationId, CancellationToken cancellationToken = default);
}
