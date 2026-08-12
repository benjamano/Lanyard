using Lanyard.Application.Services;
using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Lanyard.API.Controllers
{
    [ApiController]
    [Route("api/companies")]
    public class CompanyBrandingController : ControllerBase
    {
        private readonly ICompanyLocationService _companyLocationService;
        private readonly IFileService _fileService;

        public CompanyBrandingController(ICompanyLocationService companyLocationService, IFileService fileService)
        {
            _companyLocationService = companyLocationService;
            _fileService = fileService;
        }

        // Deliberately its own anonymous endpoint, not an addition to FilesController's
        // gated /api/files/download/{id} route - it accepts only a companyId (never a raw
        // file id) and resolves LogoFileId server-side, so it can only ever serve whatever
        // an admin explicitly designated as that company's public logo.
        [HttpGet("{companyId:int}/logo")]
        [AllowAnonymous]
        public async Task<IActionResult> GetLogo(int companyId, CancellationToken cancellationToken)
        {
            Result<CompanyBrandingInfo> branding = await _companyLocationService.GetCompanyBrandingAsync(companyId);

            if (!branding.Success || branding.Data?.LogoFileId is not Guid logoFileId)
            {
                return NotFound();
            }

            Result<Stream> fileResult = await _fileService.DownloadFileAsync(logoFileId, cancellationToken);

            if (!fileResult.Success || fileResult.Data is null)
            {
                return NotFound();
            }

            Result<FileMetadata> meta = await _fileService.GetFileMetadataAsync(logoFileId, cancellationToken);

            return File(fileResult.Data, meta.Data?.ContentType ?? "application/octet-stream");
        }
    }
}
