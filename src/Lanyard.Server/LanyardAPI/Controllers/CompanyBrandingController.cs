using Lanyard.Application.Services;
using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Lanyard.API.Controllers
{
    [ApiController]
    [Route("api/companies")]
    [EnableRateLimiting("ip-fixed")]
    public class CompanyBrandingController : ControllerBase
    {
        private readonly ICompanyLocationService _companyLocationService;
        private readonly IFileService _fileService;

        public CompanyBrandingController(ICompanyLocationService companyLocationService, IFileService fileService)
        {
            _companyLocationService = companyLocationService;
            _fileService = fileService;
        }

        // Raster image types only - see PublicImageContentTypes for why, and note that the
        // ordering API's menu-photo endpoint deliberately shares the same list.
        private static readonly HashSet<string> AllowedImageContentTypes = PublicImageContentTypes.Allowed;

        // Deliberately its own anonymous endpoint, not an addition to FilesController's
        // gated /api/files/download/{id} route - it accepts only a companyId (never a raw
        // file id) and resolves LogoFileId server-side, so it can only ever serve whatever
        // an admin explicitly designated as that company's public logo.
        [HttpGet("{companyId:int}/logo")]
        [AllowAnonymous]
        [ResponseCache(Duration = 3600, Location = ResponseCacheLocation.Any)]
        public async Task<IActionResult> GetLogo(int companyId, CancellationToken cancellationToken)
        {
            Result<CompanyBrandingInfo> branding = await _companyLocationService.GetCompanyBrandingAsync(companyId);

            if (!branding.Success || branding.Data?.LogoFileId is not Guid logoFileId)
            {
                return NotFound();
            }

            Result<FileMetadata> meta = await _fileService.GetFileMetadataAsync(logoFileId, cancellationToken);
            string? contentType = meta.Data?.ContentType;

            // Resolved before opening the stream so a disallowed type never gets one opened.
            // NotFound (rather than an explanatory error) matches this endpoint's don't-leak-details posture.
            if (contentType is null || !AllowedImageContentTypes.Contains(contentType))
            {
                return NotFound();
            }

            Result<Stream> fileResult = await _fileService.DownloadFileAsync(logoFileId, cancellationToken);

            if (!fileResult.Success || fileResult.Data is null)
            {
                return NotFound();
            }

            return File(fileResult.Data, contentType);
        }

        // Same reasoning as GetLogo above. Kept as its own endpoint rather than a query
        // parameter on the logo route so a company that has set one and not the other gets a
        // clean 404 for the missing one, and so each can be cached independently.
        [HttpGet("{companyId:int}/favicon")]
        [AllowAnonymous]
        [ResponseCache(Duration = 3600, Location = ResponseCacheLocation.Any)]
        public async Task<IActionResult> GetFavicon(int companyId, CancellationToken cancellationToken)
        {
            Result<CompanyBrandingInfo> branding = await _companyLocationService.GetCompanyBrandingAsync(companyId);

            if (!branding.Success || branding.Data?.FaviconFileId is not Guid faviconFileId)
            {
                return NotFound();
            }

            Result<FileMetadata> meta = await _fileService.GetFileMetadataAsync(faviconFileId, cancellationToken);
            string? contentType = meta.Data?.ContentType;

            if (contentType is null || !AllowedImageContentTypes.Contains(contentType))
            {
                return NotFound();
            }

            Result<Stream> fileResult = await _fileService.DownloadFileAsync(faviconFileId, cancellationToken);

            if (!fileResult.Success || fileResult.Data is null)
            {
                return NotFound();
            }

            return File(fileResult.Data, contentType);
        }

        // Same reasoning as GetLogo above: its own anonymous endpoint keyed by companyId,
        // never a raw file id, resolving BackgroundImageFileId server-side.
        [HttpGet("{companyId:int}/background")]
        [AllowAnonymous]
        [ResponseCache(Duration = 3600, Location = ResponseCacheLocation.Any)]
        public async Task<IActionResult> GetBackgroundImage(int companyId, CancellationToken cancellationToken)
        {
            Result<CompanyBrandingInfo> branding = await _companyLocationService.GetCompanyBrandingAsync(companyId);

            if (!branding.Success || branding.Data?.BackgroundImageFileId is not Guid backgroundImageFileId)
            {
                return NotFound();
            }

            Result<FileMetadata> meta = await _fileService.GetFileMetadataAsync(backgroundImageFileId, cancellationToken);
            string? contentType = meta.Data?.ContentType;

            if (contentType is null || !AllowedImageContentTypes.Contains(contentType))
            {
                return NotFound();
            }

            Result<Stream> fileResult = await _fileService.DownloadFileAsync(backgroundImageFileId, cancellationToken);

            if (!fileResult.Success || fileResult.Data is null)
            {
                return NotFound();
            }

            return File(fileResult.Data, contentType);
        }
    }
}
