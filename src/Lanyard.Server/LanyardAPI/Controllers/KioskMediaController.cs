using Lanyard.API.Extensions;
using Lanyard.Application.Services;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.API.Controllers
{
    /// <summary>
    /// Media for the anonymous kiosk page (/kiosk/{clientId}/{programId}). Like the company
    /// logo endpoint, it never accepts a raw file id: the file is resolved server-side from the
    /// projection program step an admin configured, so it can only ever serve a video that is
    /// already part of a program. The kiosk page previously embedded the entire video in its
    /// (equally anonymous) HTML as a base64 data: URL, so this exposes nothing new while letting
    /// the browser stream it with Range requests instead of the server buffering it in memory.
    /// </summary>
    [ApiController]
    [Route("api/kiosk")]
    public class KioskMediaController(IDbContextFactory<ApplicationDbContext> factory, IFileService fileService) : ControllerBase
    {
        // Mirrors KioskTemplateConstants.ParameterNames.Video in Lanyard.App, which this project can't reference.
        public const string VideoParameterName = "Video File";

        [HttpGet("programs/{programId:guid}/steps/{stepId:guid}/video")]
        [AllowAnonymous]
        public async Task<IActionResult> GetStepVideo(Guid programId, Guid stepId, CancellationToken cancellationToken)
        {
            await using ApplicationDbContext ctx = await factory.CreateDbContextAsync(cancellationToken);

            string? fileIdValue = await ctx.ProjectionProgramSteps
                .AsNoTracking()
                .TagWithCallSite()
                .Where(s => s.Id == stepId && s.ProjectionProgramId == programId)
                .SelectMany(s => s.ParameterValues)
                .Where(pv => pv.Parameter != null && pv.Parameter.Name == VideoParameterName)
                .Select(pv => pv.Value)
                .FirstOrDefaultAsync(cancellationToken);

            if (!Guid.TryParse(fileIdValue, out Guid fileId))
            {
                return NotFound();
            }

            Result<FileContent> content = await fileService.OpenFileContentAsync(fileId, FileContentStreamingExtensions.ParseRequestedRange(Request), cancellationToken);

            if (!content.Success || content.Data is null)
            {
                return this.FileContentFailure(content.Error);
            }

            if (content.Data.Metadata.ContentType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) != true)
            {
                await content.Data.DisposeAsync();
                return NotFound();
            }

            return await this.StreamFileContentAsync(content.Data, cacheFor: TimeSpan.FromDays(1));
        }
    }
}
