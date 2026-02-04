using Lanyard.Application.Services;
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

        public FilesController(IFileService fileService)
        {
            _fileService = fileService;
        }

        [HttpPost("upload")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Upload([FromForm] IFormFile file, [FromForm] Guid? folderId, CancellationToken cancellationToken)
        {
            if (file == null)
                return BadRequest(Result<FileMetadata>.Fail("No file provided."));

            string uploadedBy = User.Identity?.Name ?? "unknown";
            Result<FileMetadata> result = await _fileService.UploadFileAsync(file, folderId, uploadedBy, cancellationToken);
            if (!result.Success)
                return BadRequest(result);
            return Ok(result);
        }

        [HttpGet("download/{id}")]
        [AllowAnonymous]
        public async Task<IActionResult> Download(Guid id, CancellationToken cancellationToken)
        {
            Result<Stream> result = await _fileService.DownloadFileAsync(id, cancellationToken);
            if (!result.Success || result.Data == null)
                return NotFound(Result<string>.Fail("File not found."));
            Result<FileMetadata> meta = await _fileService.GetFileMetadataAsync(id, cancellationToken);
            string fileName = meta.Data?.FileName ?? "file.bin";
            string contentType = meta.Data?.ContentType ?? "application/octet-stream";
            return File(result.Data, contentType, fileName);
        }

        [HttpGet("list")]
        [AllowAnonymous]
        public async Task<IActionResult> List([FromQuery] Guid? folderId, CancellationToken cancellationToken)
        {
            Result<IReadOnlyList<FileMetadata>> result = await _fileService.ListFilesAsync(folderId, cancellationToken);
            return Ok(result);
        }

        [HttpDelete("{id}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
        {
            Result<bool> result = await _fileService.DeleteFileAsync(id, cancellationToken);
            if (!result.Success)
                return BadRequest(result);
            return Ok(result);
        }

        [HttpPut("rename/{id}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Rename(Guid id, [FromBody] string newName, CancellationToken cancellationToken)
        {
            Result<FileMetadata> result = await _fileService.RenameFileAsync(id, newName, cancellationToken);
            if (!result.Success)
                return BadRequest(result);
            return Ok(result);
        }

        [HttpPost("folders")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> CreateFolder([FromBody] string name, [FromQuery] Guid? parentFolderId, CancellationToken cancellationToken)
        {
            string createdBy = User.Identity?.Name ?? "unknown";
            Result<Folder> result = await _fileService.CreateFolderAsync(name, parentFolderId, createdBy, cancellationToken);
            if (!result.Success)
                return BadRequest(result);
            return Ok(result);
        }

        [HttpPut("folders/rename/{id}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> RenameFolder(Guid id, [FromBody] string newName, CancellationToken cancellationToken)
        {
            Result<Folder> result = await _fileService.RenameFolderAsync(id, newName, cancellationToken);
            if (!result.Success)
                return BadRequest(result);
            return Ok(result);
        }

        [HttpDelete("folders/{id}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> DeleteFolder(Guid id, CancellationToken cancellationToken)
        {
            Result<bool> result = await _fileService.DeleteFolderAsync(id, cancellationToken);
            if (!result.Success)
                return BadRequest(result);
            return Ok(result);
        }

        [HttpGet("folders/list")]
        [AllowAnonymous]
        public async Task<IActionResult> ListFolders([FromQuery] Guid? parentFolderId, CancellationToken cancellationToken)
        {
            Result<IReadOnlyList<Folder>> result = await _fileService.ListFoldersAsync(parentFolderId, cancellationToken);
            return Ok(result);
        }
    }
}
