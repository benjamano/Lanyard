using Lanyard.Infrastructure.DTO;

namespace Lanyard.Application.Services.Projection.Bindings;

/// <summary>
/// Who a binding is resolved for. CompanyId is the program's company, which the runner has already
/// checked matches the kiosk's; LocationId is the kiosk's location, null when none is assigned.
/// </summary>
public sealed record ProjectionBindingContext(int CompanyId, int? LocationId, Guid ClientId, DateTime NowUtc);

/// <summary>
/// One live data source a projection parameter can be bound to. Sources are singletons and run
/// outside any signed-in tenant (the runner uses the system context factory), so each one must
/// filter by <see cref="ProjectionBindingContext.CompanyId"/> itself.
/// </summary>
public interface IProjectionBindingSource
{
    /// <summary>One of <see cref="ProjectionBindingKeys"/>.</summary>
    string Key { get; }

    /// <summary>The text to show, or Ok(null) when there's no data yet and the fallback should show.</summary>
    Task<Result<string?>> ResolveAsync(ProjectionBindingContext context, CancellationToken cancellationToken);
}
