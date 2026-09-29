using Lanyard.API.Extensions;
using Lanyard.Application.Services;
using Lanyard.Application.Services.Authentication;
using Lanyard.Infrastructure.Models;
using Lanyard.Infrastructure.DTO;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Lanyard.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class FilesController : ControllerBase
    {
        private readonly IFileService _fileService;
        private readonly IClientSecretValidator _clientSecretValidator;

        public FilesController(IFileService fileService, IClientSecretValidator clientSecretValidator)
        {
            _fileService = fileService;
            _clientSecretValidator = clientSecretValidator;
        }

        [HttpPost("upload")]
        [Authorize(Roles = "Admin, CanManageFiles")]
        public async Task<IActionResult> Upload([FromForm] IFormFile file, [FromForm] Guid? folderId, CancellationToken cancellationToken)
        {
            if (file == null)
                return BadRequest(Result<FileMetadata>.Fail("No file provided."));

            string uploadedBy = User.Identity?.Name ?? "unknown";
            Result<FileMetadata> result = await _fileService.UploadFileAsync(file, folderId, uploadedBy, cancellationToken);
            return result.ToActionResult();
        }

        [HttpGet("download/{id}")]
        public async Task<IActionResult> Download(Guid id, CancellationToken cancellationToken)
        {
            if (!ClientRequestAuthorization.IsAuthorized(HttpContext, _clientSecretValidator))
                return Unauthorized();

            // One metadata lookup (was two), Range requests honoured against the bucket, and a
            // cache header so a thumbnail grid or a kiosk isn't re-fetching the same bytes.
            Result<FileContent> result = await _fileService.OpenFileContentAsync(id, FileContentStreamingExtensions.ParseRequestedRange(Request), cancellationToken);
            if (!result.Success || result.Data == null)
                return this.FileContentFailure(result.Error);

            return await this.StreamFileContentAsync(result.Data, downloadFileName: result.Data.Metadata.FileName ?? "file.bin", cacheFor: TimeSpan.FromDays(1));
        }

        [HttpGet("list")]
        public async Task<IActionResult> List([FromQuery] Guid? folderId, CancellationToken cancellationToken)
        {
            if (!ClientRequestAuthorization.IsAuthorized(HttpContext, _clientSecretValidator))
                return Unauthorized();

            Result<IReadOnlyList<FileMetadata>> result = await _fileService.ListFilesAsync(folderId, cancellationToken);
            return result.ToActionResult();
        }

        [HttpDelete("{id}")]
        [Authorize(Roles = "Admin, CanManageFiles")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
        {
            Result<bool> result = await _fileService.DeleteFileAsync(id, cancellationToken);
            return result.ToActionResult();
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
            string createdBy = User.Identity?.Name ?? "unknown";
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

        [HttpDelete("folders/{id}")]
        [Authorize(Roles = "Admin, CanManageFiles")]
        public async Task<IActionResult> DeleteFolder(Guid id, CancellationToken cancellationToken)
        {
            Result<bool> result = await _fileService.DeleteFolderAsync(id, cancellationToken);
            return result.ToActionResult();
        }

        [HttpGet("folders/list")]
        public async Task<IActionResult> ListFolders([FromQuery] Guid? parentFolderId, CancellationToken cancellationToken)
        {
            if (!ClientRequestAuthorization.IsAuthorized(HttpContext, _clientSecretValidator))
                return Unauthorized();

            Result<IReadOnlyList<Folder>> result = await _fileService.ListFoldersAsync(parentFolderId, cancellationToken);
            return result.ToActionResult();
        }
    }
}
