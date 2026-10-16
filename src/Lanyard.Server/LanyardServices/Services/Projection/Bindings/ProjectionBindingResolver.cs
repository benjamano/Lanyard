using System.Collections.Concurrent;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Projection.Bindings;

public class ProjectionBindingResolver(
    IEnumerable<IProjectionBindingSource> sources,
    ILogger<ProjectionBindingResolver> logger,
    TimeProvider? timeProvider = null) : IProjectionBindingResolver
{
    private readonly Dictionary<string, IProjectionBindingSource> _sources = sources.ToDictionary(s => s.Key, StringComparer.Ordinal);
    private readonly ILogger<ProjectionBindingResolver> _logger = logger;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    // Several displays at one venue showing the same source would otherwise each query for it on
    // every step and every refresh.
    private readonly ConcurrentDictionary<(string Key, int CompanyId, int? LocationId), (DateTimeOffset ExpiresAt, string? Value)> _cache = new();

    /// <summary>How long one resolved value is reused. Settable for tests.</summary>
    public TimeSpan CacheDuration { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A slow source can't hold a step up for longer than this; the step shows the fallback and the
    /// next refresh tries again.
    /// </summary>
    public TimeSpan SourceTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public IReadOnlyCollection<string> RegisteredKeys => _sources.Keys;

    public async Task<IReadOnlyDictionary<Guid, string>> ResolveStepAsync(ProjectionBindingContext context, ProjectionProgramStep step, CancellationToken cancellationToken)
    {
        Dictionary<Guid, string> values = [];

        foreach (ProjectionProgramParameterValue parameterValue in step.ParameterValues)
        {
            if (parameterValue.BindingKey is null)
            {
                continue;
            }

            // A binding saved somewhere it doesn't belong (only possible by editing the database)
            // is ignored rather than pushing text into, say, a video's volume.
            if (!ProjectionBindingCatalogue.IsBindable(step.Template?.Name, parameterValue.Parameter?.Name))
            {
                continue;
            }

            string? resolved = await ResolveKeyAsync(parameterValue.BindingKey, context, cancellationToken);
            string text = ProjectionBindingText.Normalise(resolved);

            values[parameterValue.Id] = text.Length > 0
                ? text
                : ProjectionBindingCatalogue.FallbackFor(parameterValue.BindingKey, parameterValue.Value) ?? string.Empty;
        }

        return values;
    }

    public async Task<Result<string?>> PreviewAsync(string bindingKey, int companyId, int? locationId, CancellationToken cancellationToken = default)
    {
        if (!_sources.ContainsKey(bindingKey))
        {
            return Result<string?>.Fail("That data source isn't available.");
        }

        ProjectionBindingContext context = new(companyId, locationId, Guid.Empty, _time.GetUtcNow().UtcDateTime);

        string? resolved = await ResolveKeyAsync(bindingKey, context, cancellationToken);
        string text = ProjectionBindingText.Normalise(resolved);

        return Result<string?>.Ok(text.Length > 0 ? text : null);
    }

    private async Task<string?> ResolveKeyAsync(string key, ProjectionBindingContext context, CancellationToken cancellationToken)
    {
        if (!_sources.TryGetValue(key, out IProjectionBindingSource? source))
        {
            _logger.LogWarning("No projection binding source is registered for {BindingKey}; showing the fallback", key);
            return null;
        }

        (string, int, int?) cacheKey = (key, context.CompanyId, context.LocationId);
        DateTimeOffset now = _time.GetUtcNow();

        if (_cache.TryGetValue(cacheKey, out (DateTimeOffset ExpiresAt, string? Value) cached) && cached.ExpiresAt > now)
        {
            return cached.Value;
        }

        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(SourceTimeout);

            // WaitAsync as well as the token, so a source that ignores cancellation still can't
            // stall the step.
            Result<string?> result = await source.ResolveAsync(context, timeout.Token).WaitAsync(SourceTimeout, cancellationToken);

            if (!result.IsSuccess)
            {
                _logger.LogWarning("Projection binding {BindingKey} failed for company {CompanyId} location {LocationId}: {Error}", key, context.CompanyId, context.LocationId, result.Error);
                return null;
            }

            _cache[cacheKey] = (now + CacheDuration, result.Data);

            return result.Data;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            _logger.LogWarning("Projection binding {BindingKey} timed out after {TimeoutMs}ms for company {CompanyId} location {LocationId}", key, SourceTimeout.TotalMilliseconds, context.CompanyId, context.LocationId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Projection binding {BindingKey} threw for company {CompanyId} location {LocationId}", key, context.CompanyId, context.LocationId);
            return null;
        }
    }
}
