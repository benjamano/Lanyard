using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services.StaffDocuments;

public class StaffDocumentTypeService(IDbContextFactory<ApplicationDbContext> factory) : IStaffDocumentTypeService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;

    public async Task<Result<List<StaffDocumentType>>> GetDocumentTypesAsync(int companyId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<StaffDocumentType> types = await ctx.StaffDocumentTypes
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.ReminderIntervals.Where(i => i.IsActive))
                .Where(x => x.CompanyId == companyId && x.IsActive)
                .OrderBy(x => x.SortOrder)
                .ToListAsync();

            return Result<List<StaffDocumentType>>.Ok(types);
        }
        catch (Exception ex)
        {
            return Result<List<StaffDocumentType>>.Fail($"Failed to retrieve document types: {ex.Message}");
        }
    }

    public async Task<Result<StaffDocumentType>> SaveDocumentTypeAsync(StaffDocumentType documentType)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            if (string.IsNullOrWhiteSpace(documentType.Name))
            {
                return Result<StaffDocumentType>.Fail("A document type name is required.");
            }

            if (documentType.Id == Guid.Empty)
            {
                documentType.Id = Guid.NewGuid();
                documentType.IsActive = true;
                ctx.StaffDocumentTypes.Add(documentType);
            }
            else
            {
                StaffDocumentType? existing = await ctx.StaffDocumentTypes
                    .FirstOrDefaultAsync(x => x.Id == documentType.Id && x.CompanyId == documentType.CompanyId);

                if (existing is null)
                {
                    return Result<StaffDocumentType>.Fail("Document type not found.");
                }

                existing.Name = documentType.Name;
                existing.Description = documentType.Description;
                existing.RequiresExpiryDate = documentType.RequiresExpiryDate;
                existing.SortOrder = documentType.SortOrder;
            }

            await ctx.SaveChangesAsync();

            return Result<StaffDocumentType>.Ok(documentType);
        }
        catch (Exception ex)
        {
            return Result<StaffDocumentType>.Fail($"Failed to save document type: {ex.Message}");
        }
    }

    public async Task<Result<bool>> DeactivateDocumentTypeAsync(Guid documentTypeId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            StaffDocumentType? documentType = await ctx.StaffDocumentTypes.FirstOrDefaultAsync(x => x.Id == documentTypeId);

            if (documentType is null)
            {
                return Result<bool>.Fail("Document type not found.");
            }

            documentType.IsActive = false;

            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to deactivate document type: {ex.Message}");
        }
    }

    public async Task<Result<bool>> SaveReminderIntervalsAsync(Guid documentTypeId, List<int> daysBeforeExpiry)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            bool typeExists = await ctx.StaffDocumentTypes.AnyAsync(x => x.Id == documentTypeId);

            if (!typeExists)
            {
                return Result<bool>.Fail("Document type not found.");
            }

            List<StaffDocumentReminderInterval> existingIntervals = await ctx.StaffDocumentReminderIntervals
                .Where(x => x.StaffDocumentTypeId == documentTypeId)
                .ToListAsync();

            HashSet<int> desired = [.. daysBeforeExpiry.Where(x => x > 0).Distinct()];

            foreach (StaffDocumentReminderInterval existing in existingIntervals)
            {
                existing.IsActive = desired.Contains(existing.DaysBeforeExpiry);
            }

            HashSet<int> alreadyPresent = [.. existingIntervals.Select(x => x.DaysBeforeExpiry)];

            foreach (int days in desired.Where(x => !alreadyPresent.Contains(x)))
            {
                ctx.StaffDocumentReminderIntervals.Add(new StaffDocumentReminderInterval
                {
                    Id = Guid.NewGuid(),
                    StaffDocumentTypeId = documentTypeId,
                    DaysBeforeExpiry = days,
                    IsActive = true
                });
            }

            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to save reminder intervals: {ex.Message}");
        }
    }
}
