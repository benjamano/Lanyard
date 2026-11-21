using Lanyard.API.Extensions;
using Lanyard.Application.Services;
using Lanyard.Application.Services.Authentication;
using Lanyard.Infrastructure.Models;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Files;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

namespace Lanyard.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class FilesController : ControllerBase
    {
        private readonly IFileService _fileService;
        private readonly IFileLibraryService _library;
        private readonly IMemoryCache _cache;
        private readonly ILogger<FilesController> _logger;

        private const string ManageRoles = "Admin, CanManageFiles";

        // How long a prepared zip download link works for.
        private static readonly TimeSpan ZipLinkLifetime = TimeSpan.FromMinutes(5);

        public FilesController(IFileService fileService, IFileLibraryService library, IMemoryCache cache, ILogger<FilesController> logger)
        {
            _fileService = fileService;
            _library = library;
            _cache = cache;
            _logger = logger;
        }

        // A little over the largest upload (a video) plus its poster frame, for the multipart framing;
        // FileService makes the real per-type size and type check. Files' upload queue sends the
        // picture's or video's size, its length and a poster frame it took in the browser.
        [HttpPost("upload")]
        [Authorize(Roles = ManageRoles)]
        [RequestSizeLimit(FileUploadPolicy.MaxVideoBytes + 8 * 1024 * 1024)]
        [RequestFormLimits(MultipartBodyLengthLimit = FileUploadPolicy.MaxVideoBytes + 8 * 1024 * 1024)]
        public async Task<IActionResult> Upload([FromForm] IFormFile file, [FromForm] Guid? folderId,
            [FromForm] int? width, [FromForm] int? height, [FromForm] double? durationSeconds, IFormFile? poster,
            CancellationToken cancellationToken)
        {
            if (file == null)
                return BadRequest(Result<FileListItemDto>.Fail("No file provided."));

            // The user id, so Files can show who uploaded it (it used to record the username).
            string uploadedBy = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.Identity?.Name ?? "unknown";

            UploadedMedia media = new(
                width is > 0 and < 100_000 ? width : null,
                height is > 0 and < 100_000 ? height : null,
                durationSeconds is > 0 and < 86_400 ? durationSeconds : null);

            Result<FileMetadata> result = await _fileService.UploadFileAsync(file, folderId, uploadedBy,
                new FileUploadOptions(Media: media, Poster: poster), cancellationToken);

            return result.IsSuccess
                ? Ok(Result<FileListItemDto>.Ok(FileListItemDto.From(result.Data!)))
                : BadRequest(Result<FileListItemDto>.Fail(result.Error ?? "Couldn't upload the file."));
        }

        // Files' grid thumbnail. 404 when there isn't one: the page then shows the picture or an icon.
        [HttpGet("thumb/{id}")]
        public async Task<IActionResult> Thumbnail(Guid id, CancellationToken cancellationToken)
        {
            if (await KioskRequestAuthorization.AuthorizeAsync(HttpContext) == KioskRequestAccess.Denied)
                return Challenge(KioskAuthenticationDefaults.Scheme);

            Result<Stream> thumbnail = await _fileService.OpenThumbnailAsync(id, cancellationToken);

            if (!thumbnail.IsSuccess || thumbnail.Data is null)
                return NotFound();

            // A file's thumbnail never changes (a new upload is a new id).
            Response.Headers.CacheControl = "private, max-age=2592000, immutable";
            return File(thumbnail.Data, "image/webp");
        }

        [HttpGet("download/{id}")]
        public async Task<IActionResult> Download(Guid id, [FromQuery] bool inline, CancellationToken cancellationToken)
        {
            if (await KioskRequestAuthorization.AuthorizeAsync(HttpContext) == KioskRequestAccess.Denied)
                return Challenge(KioskAuthenticationDefaults.Scheme);

            // One metadata lookup (was two), Range requests honoured against the bucket, and a
            // cache header so a thumbnail grid or a kiosk isn't re-fetching the same bytes.
            Result<FileContent> result = await _fileService.OpenFileContentAsync(id, FileContentStreamingExtensions.ParseRequestedRange(Request), cancellationToken);
            if (!result.Success || result.Data == null)
                return this.FileContentFailure(result.Error);

            // ?inline=true lets the Files preview show a PDF or a text file in the page. Only those:
            // anything that could carry script (an SVG, say) is always sent as a download.
            bool showInline = inline && IsSafeInline(result.Data.Metadata.ContentType);

            if (showInline && result.Data.Metadata.ContentType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true)
                Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'self'";

            return await this.StreamFileContentAsync(result.Data,
                downloadFileName: showInline ? null : result.Data.Metadata.FileName ?? "file.bin",
                cacheFor: TimeSpan.FromDays(1));
        }

        [HttpGet("list")]
        public async Task<IActionResult> List([FromQuery] Guid? folderId, CancellationToken cancellationToken)
        {
            if (await KioskRequestAuthorization.AuthorizeAsync(HttpContext) == KioskRequestAccess.Denied)
                return Challenge(KioskAuthenticationDefaults.Scheme);

            if (!CanBrowseFiles())
                return StatusCode(StatusCodes.Status403Forbidden);

            Result<IReadOnlyList<FileMetadata>> result = await _fileService.ListFilesAsync(folderId, cancellationToken);

            return result.IsSuccess
                ? Ok(Result<List<FileListItemDto>>.Ok(result.Data!.Select(FileListItemDto.From).ToList()))
                : result.ToActionResult();
        }

        // Sends the file to the Recycle bin; api/files/bin/delete-forever removes it for good.
        [HttpDelete("{id}")]
        [Authorize(Roles = ManageRoles)]
        public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
        {
            Result<BulkOperationResult> result = await _library.MoveToBinAsync([id], [], cancellationToken);
            return BulkToActionResult(result);
        }

        [HttpPut("rename/{id}")]
        [Authorize(Roles = "Admin, CanManageFiles")]
        public async Task<IActionResult> Rename(Guid id, [FromBody] string newName, CancellationToken cancellationToken)
        {
            Result<FileMetadata> result = await _fileService.RenameFileAsync(id, newName, cancellationToken);
            return result.ToActionResult();
        }

        [HttpPut("move/{id}")]
        [Authorize(Roles = "Admin, CanManageFiles")]
        public async Task<IActionResult> Move(Guid id, [FromQuery] Guid? destinationFolderId, CancellationToken cancellationToken)
        {
            Result<FileMetadata> result = await _fileService.MoveFileAsync(id, destinationFolderId, cancellationToken);
            return result.ToActionResult();
        }

        [HttpPost("folders")]
        [Authorize(Roles = "Admin, CanManageFiles")]
        public async Task<IActionResult> CreateFolder([FromBody] string name, [FromQuery] Guid? parentFolderId, CancellationToken cancellationToken)
        {
            string createdBy = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.Identity?.Name ?? "unknown";
            Result<Folder> result = await _fileService.CreateFolderAsync(name, parentFolderId, createdBy, cancellationToken);
            return result.ToActionResult();
        }

        [HttpPut("folders/rename/{id}")]
        [Authorize(Roles = "Admin, CanManageFiles")]
        public async Task<IActionResult> RenameFolder(Guid id, [FromBody] string newName, CancellationToken cancellationToken)
        {
            Result<Folder> result = await _fileService.RenameFolderAsync(id, newName, cancellationToken);
            return result.ToActionResult();
        }

        // Sends the folder, with everything in it, to the Recycle bin.
        [HttpDelete("folders/{id}")]
        [Authorize(Roles = ManageRoles)]
        public async Task<IActionResult> DeleteFolder(Guid id, CancellationToken cancellationToken)
        {
            Result<BulkOperationResult> result = await _library.MoveToBinAsync([], [id], cancellationToken);
            return BulkToActionResult(result);
        }

        [HttpGet("folders/list")]
        public async Task<IActionResult> ListFolders([FromQuery] Guid? parentFolderId, CancellationToken cancellationToken)
        {
            if (await KioskRequestAuthorization.AuthorizeAsync(HttpContext) == KioskRequestAccess.Denied)
                return Challenge(KioskAuthenticationDefaults.Scheme);

            if (!CanBrowseFiles())
                return StatusCode(StatusCodes.Status403Forbidden);

            Result<IReadOnlyList<Folder>> result = await _fileService.ListFoldersAsync(parentFolderId, cancellationToken);

            return result.IsSuccess
                ? Ok(Result<List<FolderListItemDto>>.Ok(result.Data!.Select(FolderListItemDto.From).ToList()))
                : result.ToActionResult();
        }

        private static bool IsSafeInline(string? contentType) =>
            contentType is "application/pdf" or "text/plain" or "text/csv";

        // ---------- Files library (the Files page's model, also usable over HTTP) ----------

        // Staff who don't manage files see only folders shared with staff; kiosks see nothing.
        [HttpGet("tree")]
        [Authorize]
        public async Task<IActionResult> Tree(CancellationToken cancellationToken)
        {
            if (BrowseAccess() is not bool sharedOnly)
                return StatusCode(StatusCodes.Status403Forbidden);

            return (await _library.GetTreeAsync(sharedOnly, cancellationToken)).ToActionResult();
        }

        [HttpPost("query")]
        [Authorize]
        public async Task<IActionResult> Query([FromBody] FileQuery query, CancellationToken cancellationToken)
        {
            if (BrowseAccess() is not bool sharedOnly)
                return StatusCode(StatusCodes.Status403Forbidden);

            return (await _library.QueryAsync(query with { SharedOnly = sharedOnly || query.SharedOnly }, cancellationToken)).ToActionResult();
        }

        [HttpGet("storage")]
        [Authorize(Roles = ManageRoles)]
        public async Task<IActionResult> Storage(CancellationToken cancellationToken) =>
            (await _library.GetStorageSummaryAsync(cancellationToken)).ToActionResult();

        [HttpPost("move")]
        [Authorize(Roles = ManageRoles)]
        public async Task<IActionResult> MoveMany([FromBody] MoveRequest request, CancellationToken cancellationToken) =>
            BulkToActionResult(await _library.MoveAsync(request.FileIds ?? [], request.FolderIds ?? [], request.DestinationFolderId, cancellationToken));

        [HttpPut("folders/{id}/sharing")]
        [Authorize(Roles = ManageRoles)]
        public async Task<IActionResult> SetSharing(Guid id, [FromBody] bool sharedWithStaff, CancellationToken cancellationToken) =>
            (await _library.SetFolderSharedAsync(id, sharedWithStaff, cancellationToken)).ToActionResult();

        [HttpGet("bin")]
        [Authorize(Roles = ManageRoles)]
        public async Task<IActionResult> Bin(CancellationToken cancellationToken) =>
            (await _library.ListBinAsync(cancellationToken)).ToActionResult();

        [HttpPost("bin/add")]
        [Authorize(Roles = ManageRoles)]
        public async Task<IActionResult> AddToBin([FromBody] FileSelection selection, CancellationToken cancellationToken) =>
            BulkToActionResult(await _library.MoveToBinAsync(selection.Files, selection.Folders, cancellationToken));

        [HttpPost("bin/restore")]
        [Authorize(Roles = ManageRoles)]
        public async Task<IActionResult> Restore([FromBody] FileSelection selection, CancellationToken cancellationToken) =>
            BulkToActionResult(await _library.RestoreAsync(selection.Files, selection.Folders, cancellationToken));

        [HttpPost("bin/delete-forever")]
        [Authorize(Roles = ManageRoles)]
        public async Task<IActionResult> DeleteForever([FromBody] FileSelection selection, CancellationToken cancellationToken) =>
            BulkToActionResult(await _library.DeleteForeverAsync(selection.Files, selection.Folders, cancellationToken));

        [HttpPost("bin/empty")]
        [Authorize(Roles = ManageRoles)]
        public async Task<IActionResult> EmptyBin(CancellationToken cancellationToken) =>
            BulkToActionResult(await _library.EmptyBinAsync(cancellationToken));

        // Step 1 of a zip download: checks the selection and returns a link that works for a few
        // minutes, for this user only. Step 2 (GET zip/{token}) is a plain browser download, so a
        // big zip streams to disk instead of being built in the page's memory.
        [HttpPost("zip")]
        [Authorize]
        public async Task<IActionResult> PrepareZip([FromBody] FileSelection selection, CancellationToken cancellationToken)
        {
            if (BrowseAccess() is not bool sharedOnly)
                return StatusCode(StatusCodes.Status403Forbidden);

            Result<IReadOnlyList<ZipEntryPlan>> plan = await _library.PlanZipAsync(selection.Files, selection.Folders, sharedOnly, cancellationToken);

            if (!plan.IsSuccess || plan.Data is null)
                return BadRequest(Result<string>.Fail(plan.Error ?? "Couldn't prepare the download."));

            string token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            _cache.Set(ZipCacheKey(token), new PreparedZip(User.FindFirstValue(ClaimTypes.NameIdentifier), plan.Data, ZipName(selection, plan.Data)), ZipLinkLifetime);

            return Ok(Result<string>.Ok($"api/files/zip/{token}"));
        }

        [HttpGet("zip/{token}")]
        [Authorize]
        public async Task<IActionResult> DownloadZip(string token, CancellationToken cancellationToken)
        {
            if (!_cache.TryGetValue(ZipCacheKey(token), out PreparedZip? prepared) || prepared is null
                || prepared.UserId != User.FindFirstValue(ClaimTypes.NameIdentifier))
            {
                return NotFound();
            }

            _cache.Remove(ZipCacheKey(token));

            // ZipArchive writes synchronously; let it, for this response only.
            HttpContext.Features.Get<IHttpBodyControlFeature>()?.AllowSynchronousIO = true;

            Response.ContentType = "application/zip";
            Response.Headers.ContentDisposition = new System.Net.Mime.ContentDisposition { FileName = prepared.FileName, DispositionType = "attachment" }.ToString();
            Response.Headers.CacheControl = "no-store";

            using ZipArchive zip = new(Response.Body, ZipArchiveMode.Create, leaveOpen: true);

            foreach (ZipEntryPlan entry in prepared.Entries)
            {
                Result<FileContent> content = await _fileService.OpenFileContentAsync(entry.FileId, null, cancellationToken);

                if (!content.IsSuccess || content.Data is null)
                {
                    _logger.LogWarning("Left {FileId} out of a zip download: {Error}", entry.FileId, content.Error);
                    continue;
                }

                await using FileContent file = content.Data;

                // Media is already compressed; storing it is as small and far quicker.
                CompressionLevel level = FileTypeInfo.GroupOf(entry.EntryPath) is FileTypeGroup.Document or FileTypeGroup.Other
                    ? CompressionLevel.Fastest
                    : CompressionLevel.NoCompression;

                await using Stream target = zip.CreateEntry(entry.EntryPath, level).Open();
                await file.Stream.CopyToAsync(target, cancellationToken);
            }

            _logger.LogInformation("Streamed a zip of {Count} files", prepared.Entries.Count);
            return new EmptyResult();
        }

        private sealed record PreparedZip(string? UserId, IReadOnlyList<ZipEntryPlan> Entries, string FileName);

        private static string ZipCacheKey(string token) => $"files-zip:{token}";

        private static string ZipName(FileSelection selection, IReadOnlyList<ZipEntryPlan> entries)
        {
            // One folder: name the zip after it. Anything else: "Files 18-11-2026.zip".
            string? single = selection.Folders.Count == 1 && selection.Files.Count == 0
                ? entries.Select(x => x.EntryPath.Split('/')[0]).Distinct().SingleOrDefault()
                : null;

            return $"{single ?? $"Files {DateTime.UtcNow:dd-MM-yyyy}"}.zip";
        }

        // Null: no browsing at all (a kiosk). False: everything. True: only folders shared with staff.
        private bool? BrowseAccess()
        {
            if (User.Identity?.IsAuthenticated != true || User.FindFirst(KioskClaimTypes.ClientId) is not null)
                return null;

            return !(User.IsInRole("Admin") || User.IsInRole("CanManageFiles"));
        }

        // A bulk action that partly worked is still a 200: the result lists what couldn't be done.
        private static IActionResult BulkToActionResult(Result<BulkOperationResult> result) => result.ToActionResult();

        // Listing is browsing the File Manager, which is only for the people who manage files (the
        // page itself is [Authorize(Roles = "Admin, CanManageFiles")]). Before this any signed-in
        // staff member or kiosk could list every file and folder in the company. Kiosks never list:
        // they get their media through the music/kiosk-media/branding endpoints. Download stays open
        // to every signed-in user and kiosk because course images, party artwork and logos are
        // embedded by id on pages everyone sees; files that must stay private (staff documents)
        // are kept out of the File Manager's routes entirely.
        private bool CanBrowseFiles() =>
            User.Identity?.IsAuthenticated == true
            && User.FindFirst(KioskClaimTypes.ClientId) is null
            && (User.IsInRole("Admin") || User.IsInRole("CanManageFiles"));
    }
}
