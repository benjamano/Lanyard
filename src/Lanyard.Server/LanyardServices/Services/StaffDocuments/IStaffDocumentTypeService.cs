using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.StaffDocuments;

public interface IStaffDocumentTypeService
{
    Task<Result<List<StaffDocumentType>>> GetDocumentTypesAsync(int companyId);
    Task<Result<StaffDocumentType>> SaveDocumentTypeAsync(StaffDocumentType documentType);
    Task<Result<bool>> DeactivateDocumentTypeAsync(Guid documentTypeId);

    // Replace-all: the admin UI edits the full set of thresholds for a document type as one list,
    // so this diffs against what's stored rather than requiring separate add/remove calls per row.
    Task<Result<bool>> SaveReminderIntervalsAsync(Guid documentTypeId, List<int> daysBeforeExpiry);
}
