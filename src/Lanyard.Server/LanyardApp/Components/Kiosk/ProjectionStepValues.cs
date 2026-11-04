using Lanyard.Application.Services.Projection.Bindings;
using Lanyard.Infrastructure.Models;

namespace Lanyard.App.Components.Kiosk;

/// <summary>
/// The one lookup every kiosk template uses for a step parameter, by name. A data-bound parameter
/// shows the runner's live value when there is one, otherwise its fallback text; a fixed one just
/// shows its value. Whatever comes back is plain text - render it with @, never as MarkupString.
/// </summary>
public static class ProjectionStepValues
{
    public static string? Get(ProjectionProgramStep step, string parameterName, IReadOnlyDictionary<Guid, string>? boundValues = null)
    {
        ProjectionProgramParameterValue? parameterValue = step.ParameterValues.FirstOrDefault(x => x.Parameter?.Name == parameterName);

        if (parameterValue is null)
        {
            return null;
        }

        if (parameterValue.BindingKey is not null && boundValues is not null && boundValues.TryGetValue(parameterValue.Id, out string? bound))
        {
            return bound;
        }

        return ProjectionBindingCatalogue.FallbackFor(parameterValue.BindingKey, parameterValue.Value);
    }
}
