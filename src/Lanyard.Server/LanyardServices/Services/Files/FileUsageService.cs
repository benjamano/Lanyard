using System.Text.RegularExpressions;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Files;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services;

public interface IFileUsageService
{
    /// <summary>Everything that uses each of the files, keyed by file id. Files nothing uses are left out.</summary>
    Task<Result<IReadOnlyDictionary<Guid, IReadOnlyList<FileUsage>>>> GetUsagesAsync(IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken);
}

/// <summary>
/// The one place that knows what in Lanyard points at a file. Most references are real foreign
/// keys; projection program steps keep the file id as a parameter's text value, and training
/// courses embed /api/files/download/{id} in their section HTML, so those are found by value.
/// Add a new reference here when a feature starts storing file ids, or a file it uses can be
/// deleted for good from under it.
/// </summary>
public sealed partial class FileUsageService(IDbContextFactory<ApplicationDbContext> dbFactory, ILogger<FileUsageService> logger) : IFileUsageService
{
    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidPattern();

    public async Task<Result<IReadOnlyDictionary<Guid, IReadOnlyList<FileUsage>>>> GetUsagesAsync(IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken)
    {
        try
        {
            Dictionary<Guid, List<FileUsage>> usages = [];

            if (fileIds.Count == 0)
                return Ok(usages);

            List<Guid> ids = fileIds.Distinct().ToList();
            List<string> idStrings = ids.Select(x => x.ToString()).ToList();

            void Add(Guid fileId, FileUsageKind kind, string description, string? link, bool blocks = true)
            {
                if (!usages.TryGetValue(fileId, out List<FileUsage>? list))
                    usages[fileId] = list = [];

                list.Add(new FileUsage(fileId, kind, description, link, blocks));
            }

            await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);

            // A song is retired when its file is deleted for good (FileService.DeleteFileAsync), so
            // it's worth knowing about but never stops the delete.
            var songs = await db.Songs.AsNoTracking().TagWithCallSite()
                .Where(x => x.IsActive && x.FileMetadataId != null && ids.Contains(x.FileMetadataId.Value))
                .Select(x => new { FileId = x.FileMetadataId!.Value, x.Name })
                .ToListAsync(cancellationToken);

            foreach (var song in songs)
                Add(song.FileId, FileUsageKind.Song, $"Song \"{song.Name}\" in the music library", "/music", blocks: false);

            var clips = await db.AudioClips.AsNoTracking().TagWithCallSite()
                .Where(x => x.DeleteDate == null && x.FileMetadataId != null && ids.Contains(x.FileMetadataId.Value))
                .Select(x => new { FileId = x.FileMetadataId!.Value, x.Name })
                .ToListAsync(cancellationToken);

            foreach (var clip in clips)
                Add(clip.FileId, FileUsageKind.AudioClip, $"Audio clip \"{clip.Name}\"", "/manage/audio-clips");

            // No names or links: who a staff document belongs to isn't something Files should show.
            List<Guid> staffDocumentFiles = await db.StaffDocuments.AsNoTracking().TagWithCallSite()
                .Where(x => x.IsActive && ids.Contains(x.FileMetadataId))
                .Select(x => x.FileMetadataId)
                .ToListAsync(cancellationToken);

            foreach (Guid fileId in staffDocumentFiles.Distinct())
                Add(fileId, FileUsageKind.StaffDocument, "A staff document", null);

            List<Guid> attachmentFiles = await db.CompanyOnboardingStandingAttachments.AsNoTracking().TagWithCallSite()
                .Where(x => x.IsActive && ids.Contains(x.FileMetadataId))
                .Select(x => x.FileMetadataId)
                .ToListAsync(cancellationToken);

            foreach (Guid fileId in attachmentFiles.Distinct())
                Add(fileId, FileUsageKind.OnboardingAttachment, "Attached to the onboarding welcome email", "/manage/onboarding");

            var companies = await db.Companies.AsNoTracking().TagWithCallSite()
                .Where(x => (x.LogoFileId != null && ids.Contains(x.LogoFileId.Value))
                    || (x.BackgroundImageFileId != null && ids.Contains(x.BackgroundImageFileId.Value)))
                .Select(x => new { x.Name, x.LogoFileId, x.BackgroundImageFileId })
                .ToListAsync(cancellationToken);

            foreach (var company in companies)
            {
                if (company.LogoFileId is Guid logo && ids.Contains(logo))
                    Add(logo, FileUsageKind.CompanyLogo, $"{company.Name}'s logo", "/manage/companies-locations");

                if (company.BackgroundImageFileId is Guid background && ids.Contains(background))
                    Add(background, FileUsageKind.CompanyBackground, $"{company.Name}'s sign-in background", "/manage/companies-locations");
            }

            var printouts = await db.PartyDocumentTemplates.AsNoTracking().TagWithCallSite()
                .Where(x => x.ArtworkFileId != null && ids.Contains(x.ArtworkFileId.Value))
                .Select(x => new { FileId = x.ArtworkFileId!.Value, x.Document })
                .ToListAsync(cancellationToken);

            foreach (var printout in printouts)
                Add(printout.FileId, FileUsageKind.PartyPrintout, $"Party printout artwork ({PrintoutName(printout.Document)})", "/manage/parties/printouts");

            var steps = await db.ProjectionProgramSteps.AsNoTracking().TagWithCallSite()
                .Where(x => x.IsActive && x.ProjectionProgram != null && x.ProjectionProgram.IsActive)
                .SelectMany(x => x.ParameterValues
                    .Where(pv => pv.Value != null && idStrings.Contains(pv.Value.ToLower()))
                    .Select(pv => new { pv.Value, Program = x.ProjectionProgram!.Name, x.SortOrder }))
                .ToListAsync(cancellationToken);

            foreach (var step in steps)
            {
                if (Guid.TryParse(step.Value, out Guid fileId))
                    Add(fileId, FileUsageKind.ProjectionProgram, $"Projection program \"{step.Program}\", step {step.SortOrder + 1}", "/manage/projectionprograms");
            }

            // Section HTML is small and there are few sections, so scan it here rather than run
            // one LIKE per file id.
            var sections = await db.CourseSections.AsNoTracking().TagWithCallSite()
                .Where(x => x.IsActive && x.Course != null && x.Course.IsActive && x.BodyHtml.Contains("/api/files/download/"))
                .Select(x => new { x.CourseId, Course = x.Course!.Name, x.Title, x.BodyHtml })
                .ToListAsync(cancellationToken);

            HashSet<Guid> wanted = [.. ids];

            foreach (var section in sections)
            {
                foreach (Guid fileId in GuidPattern().Matches(section.BodyHtml).Select(m => Guid.Parse(m.Value)).Where(wanted.Contains).Distinct())
                    Add(fileId, FileUsageKind.TrainingCourse, $"Training course \"{section.Course}\", section \"{section.Title}\"", $"/manage/training/courses/{section.CourseId}");
            }

            return Ok(usages);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to work out what uses {Count} files", fileIds.Count);
            return Result<IReadOnlyDictionary<Guid, IReadOnlyList<FileUsage>>>.Fail("Couldn't check what uses these files.");
        }
    }

    // "ReservedCard" -> "reserved card".
    private static string PrintoutName(PartyArtworkDocument document) =>
        Regex.Replace(document.ToString(), "(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant();

    private static Result<IReadOnlyDictionary<Guid, IReadOnlyList<FileUsage>>> Ok(Dictionary<Guid, List<FileUsage>> usages) =>
        Result<IReadOnlyDictionary<Guid, IReadOnlyList<FileUsage>>>.Ok(usages.ToDictionary(x => x.Key, x => (IReadOnlyList<FileUsage>)x.Value));
}
