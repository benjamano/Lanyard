using System.Security.Cryptography;
using System.Text.Json;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.DTO;
using Lanyard.Shared.Projection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services;

/// <summary>
/// Builds the snapshot a kiosk caches for its offline fallback. The kiosk calls this with the
/// shared secret and no user, so the tenant filter is off (see TenantContext): every query here
/// is scoped to the kiosk's own company by hand.
/// </summary>
public class KioskOfflineManifestService(
    IDbContextFactory<ApplicationDbContext> factory,
    TimeProvider timeProvider,
    ILogger<KioskOfflineManifestService> logger) : IKioskOfflineManifestService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<KioskOfflineManifestService> _logger = logger;

    // Only these play without the server. Dashboards need live data and Live Capture needs a
    // live stream, so they're dropped here rather than left for the kiosk to interpret.
    private static readonly HashSet<string> OfflineTemplates =
    [
        ProjectionTemplateNames.ShowText,
        ProjectionTemplateNames.PlayVideo,
        ProjectionTemplateNames.Delay,
    ];

    public async Task<Result<KioskOfflineManifestDTO>> BuildManifestAsync(Guid clientId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            Client? client = await ctx.Clients
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.Id == clientId, cancellationToken);

            if (client == null)
            {
                return Result<KioskOfflineManifestDTO>.Fail("Client not found.");
            }

            List<ClientProjectionSettings> settings = await ctx.ClientProjectionSettings
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.ClientId == clientId && x.IsActive)
                .Where(x => x.ProjectionProgram != null && x.ProjectionProgram.IsActive && x.ProjectionProgram.CompanyId == client.CompanyId)
                .Where(x => x.Width != null && x.Height != null)
                .Include(x => x.ProjectionProgram)
                    .ThenInclude(x => x!.ProjectionProgramSteps.Where(s => s.IsActive))
                        .ThenInclude(x => x.Template)
                .Include(x => x.ProjectionProgram)
                    .ThenInclude(x => x!.ProjectionProgramSteps.Where(s => s.IsActive))
                        .ThenInclude(x => x.ParameterValues)
                            .ThenInclude(x => x.Parameter)
                .OrderBy(x => x.DisplayIndex)
                .ThenBy(x => x.Id)
                .ToListAsync(cancellationToken);

            Dictionary<Guid, FileMetadata> videos = await LoadVideosAsync(ctx, settings, client.CompanyId, cancellationToken);

            // The kiosk opens one window per display and a later assignment replaces an earlier one,
            // so only the last assignment per display ever shows. Mirror that.
            List<KioskOfflineDisplayDTO> displays = [.. settings
                .GroupBy(x => x.DisplayIndex)
                .Select(g => BuildDisplay(g.Last(), videos))];

            KioskOfflineManifestDTO manifest = new()
            {
                GeneratedUtc = _timeProvider.GetUtcNow().UtcDateTime,
                OfflineFallbackEnabled = client.OfflineFallbackEnabled,
                OfflineAfterSeconds = client.OfflineAfterSeconds,
                CacheLimitMb = client.ProjectionCacheLimitMb,
                Displays = displays,
            };

            manifest.ManifestVersion = ComputeVersion(manifest);

            return Result<KioskOfflineManifestDTO>.Ok(manifest);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to build the offline manifest for client {ClientId}", clientId);
            return Result<KioskOfflineManifestDTO>.Fail($"Failed to build offline manifest: {ex.Message}");
        }
    }

    public async Task<Result<bool>> IsFileInManifestAsync(Guid clientId, Guid fileId, CancellationToken cancellationToken = default)
    {
        Result<KioskOfflineManifestDTO> manifest = await BuildManifestAsync(clientId, cancellationToken);

        if (!manifest.IsSuccess)
        {
            return Result<bool>.Fail(manifest.Error ?? "Failed to build offline manifest.");
        }

        bool referenced = manifest.Data!.Displays
            .SelectMany(x => x.Steps)
            .Any(x => x.VideoFileId == fileId);

        return Result<bool>.Ok(referenced);
    }

    private static async Task<Dictionary<Guid, FileMetadata>> LoadVideosAsync(ApplicationDbContext ctx, List<ClientProjectionSettings> settings, int companyId, CancellationToken cancellationToken)
    {
        HashSet<Guid> fileIds = [.. settings
            .SelectMany(x => x.ProjectionProgram!.ProjectionProgramSteps)
            .Where(x => x.Template?.Name == ProjectionTemplateNames.PlayVideo)
            .Select(x => GetParameterValue(x, ProjectionParameterNames.Video))
            .Select(x => Guid.TryParse(x, out Guid id) ? id : Guid.Empty)
            .Where(x => x != Guid.Empty)];

        if (fileIds.Count == 0)
        {
            return [];
        }

        return await ctx.FileMetadata
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => fileIds.Contains(x.Id) && x.CompanyId == companyId)
            .Where(x => x.ContentType != null && x.ContentType.StartsWith("video/"))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
    }

    private static KioskOfflineDisplayDTO BuildDisplay(ClientProjectionSettings setting, Dictionary<Guid, FileMetadata> videos)
    {
        ProjectionProgram program = setting.ProjectionProgram!;

        KioskOfflineDisplayDTO display = new()
        {
            DisplayIndex = setting.DisplayIndex,
            Width = setting.Width!.Value,
            Height = setting.Height!.Value,
            IsFullScreen = setting.IsFullScreen,
            IsBorderless = setting.IsBorderless,
            IsDarkTheme = setting.IsDarkTheme,
            RepeatInfinitely = setting.RepeatInfinitely,
            RepeatNumberOfTimes = setting.RepeatNumberOfTimes,
            ProgramId = program.Id,
            ProgramName = program.Name,
        };

        // Same ordering, and the same "a step with no template can't play" rule, as the runner.
        foreach (ProjectionProgramStep step in program.ProjectionProgramSteps.Where(x => x.Template != null).OrderBy(x => x.SortOrder))
        {
            KioskOfflineStepDTO? offlineStep = BuildStep(step, videos);

            if (offlineStep == null)
            {
                display.SkippedStepCount++;
                continue;
            }

            display.Steps.Add(offlineStep);
        }

        return display;
    }

    private static KioskOfflineStepDTO? BuildStep(ProjectionProgramStep step, Dictionary<Guid, FileMetadata> videos)
    {
        string templateName = step.Template!.Name;

        if (!OfflineTemplates.Contains(templateName))
        {
            return null;
        }

        KioskOfflineStepDTO offlineStep = new()
        {
            StepId = step.Id,
            SortOrder = step.SortOrder,
            TemplateName = templateName,
            HoldForMilliseconds = step.HoldForMilliseconds,
        };

        if (templateName == ProjectionTemplateNames.ShowText)
        {
            offlineStep.Text = GetParameterValue(step, ProjectionParameterNames.Text);
        }
        else if (templateName == ProjectionTemplateNames.PlayVideo)
        {
            // A missing, deleted or non-video file can't be cached, so the step can't play offline.
            if (!Guid.TryParse(GetParameterValue(step, ProjectionParameterNames.Video), out Guid fileId)
                || !videos.TryGetValue(fileId, out FileMetadata? file))
            {
                return null;
            }

            offlineStep.VideoFileId = fileId;
            offlineStep.VideoSizeBytes = file.FileSize;
            offlineStep.VideoShouldRepeat = bool.TryParse(GetParameterValue(step, ProjectionParameterNames.ShouldRepeat), out bool repeat) && repeat;
            offlineStep.VideoVolume = ParseVolume(GetParameterValue(step, ProjectionParameterNames.Volume));
        }

        return offlineStep;
    }

    private static int ParseVolume(string? value)
    {
        if (!double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double volume))
        {
            return 100;
        }

        return (int)Math.Clamp(Math.Round(volume), 0, 100);
    }

    private static string? GetParameterValue(ProjectionProgramStep step, string parameterName)
    {
        return step.ParameterValues
            .Where(x => x.Parameter?.Name == parameterName)
            .Select(x => x.Value)
            .FirstOrDefault();
    }

    /// <summary>
    /// A hash of everything the kiosk would play or download. GeneratedUtc is left out so an
    /// unchanged program keeps the same version and the kiosk doesn't re-sync for nothing.
    /// </summary>
    public static string ComputeVersion(KioskOfflineManifestDTO manifest)
    {
        var content = new
        {
            manifest.OfflineFallbackEnabled,
            manifest.OfflineAfterSeconds,
            manifest.CacheLimitMb,
            manifest.Displays,
        };

        byte[] hash = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(content));

        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
