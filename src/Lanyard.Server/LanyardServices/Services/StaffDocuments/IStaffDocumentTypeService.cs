using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.StaffDocuments;

public interface IStaffDocumentTypeService
{
    Task<Result<List<StaffDocumentType>>> GetDocumentTypesAsync(int companyId);
    Task<Result<StaffDocumentType>> SaveDocumentTypeAsync(StaffDocumentType documentType);
    Task<Result<bool>> DeactivateDocumentTypeAsync(Guid documentTypeId);
}
