using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Parties;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services.Parties;

public class PartyTemplateService(IDbContextFactory<ApplicationDbContext> factory) : IPartyTemplateService
{
    private const string NoAccess = "You can only change the printouts for your own company.";

    // Bigger than A3 or smaller than a business card isn't a voucher.
    private const decimal MinSizeMm = 40;
    private const decimal MaxSizeMm = 420;

    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;

    public async Task<Result<PartyDocumentTemplate>> GetTemplateAsync(LocationScope scope, int companyId, PartyArtworkDocument document)
    {
        try
        {
            if (!SchedulingAccess.CanManageCompany(scope, companyId))
            {
                return Result<PartyDocumentTemplate>.Fail(NoAccess);
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            PartyDocumentTemplate? template = await ctx.PartyDocumentTemplates
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.CompanyId == companyId && x.Document == document);

            return Result<PartyDocumentTemplate>.Ok(template ?? PartyTemplateFields.Default(document, companyId));
        }
        catch (Exception ex)
        {
            return Result<PartyDocumentTemplate>.Fail($"Failed to load the printout: {ex.Message}");
        }
    }

    public async Task<Result<PartyDocumentTemplate>> SaveTemplateAsync(LocationScope scope, PartyDocumentTemplate template, string actingUserId)
    {
        try
        {
            if (!SchedulingAccess.CanManageCompany(scope, template.CompanyId))
            {
                return Result<PartyDocumentTemplate>.Fail(NoAccess);
            }

            if (template.WidthMm is < MinSizeMm or > MaxSizeMm || template.HeightMm is < MinSizeMm or > MaxSizeMm)
            {
                return Result<PartyDocumentTemplate>.Fail($"The artwork must be between {MinSizeMm} and {MaxSizeMm} mm each way.");
            }

            if (template.VoucherValidityMonths is < 1 or > 60)
            {
                return Result<PartyDocumentTemplate>.Fail("A voucher must be valid for between 1 and 60 months.");
            }

            List<PartyTemplateField> fields = PartyTemplateFields.Parse(template.FieldsJson);
            string? fieldError = ValidateFields(fields, (double)template.WidthMm, (double)template.HeightMm);

            if (fieldError is not null)
            {
                return Result<PartyDocumentTemplate>.Fail(fieldError);
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            PartyDocumentTemplate? existing = await ctx.PartyDocumentTemplates
                .FirstOrDefaultAsync(x => x.CompanyId == template.CompanyId && x.Document == template.Document);

            if (existing is null)
            {
                existing = new PartyDocumentTemplate { Id = Guid.NewGuid(), CompanyId = template.CompanyId, Document = template.Document };
                ctx.PartyDocumentTemplates.Add(existing);
            }

            existing.ArtworkFileId = template.ArtworkFileId;
            existing.WidthMm = template.WidthMm;
            existing.HeightMm = template.HeightMm;
            existing.Font = template.Font;
            existing.FieldsJson = PartyTemplateFields.Serialize(fields);
            existing.VoucherValidityMonths = template.VoucherValidityMonths;
            existing.UpdateDate = DateTime.UtcNow;
            existing.UpdatedByUserId = actingUserId;

            await ctx.SaveChangesAsync();

            return Result<PartyDocumentTemplate>.Ok(existing);
        }
        catch (Exception ex)
        {
            return Result<PartyDocumentTemplate>.Fail($"Failed to save the printout: {ex.Message}");
        }
    }

    private static string? ValidateFields(List<PartyTemplateField> fields, double widthMm, double heightMm)
    {
        foreach (PartyTemplateField field in fields)
        {
            string name = field.Kind.Label();

            if (field.WidthMm < 2 || field.HeightMm < 2)
            {
                return $"The \"{name}\" box is too small to print in.";
            }

            // A little slack: a box that just kisses the edge is fine, one off the page isn't.
            if (field.XMm < 0 || field.YMm < 0 || field.XMm + field.WidthMm > widthMm + 0.5 || field.YMm + field.HeightMm > heightMm + 0.5)
            {
                return $"The \"{name}\" box goes off the edge of the artwork.";
            }

            if (field.FontSize is < 4 or > 120)
            {
                return $"The text size for \"{name}\" must be between 4 and 120.";
            }

            if (!PartyTemplateFields.Rotations.Contains(field.Rotation))
            {
                return $"The \"{name}\" box can only be turned 90 or 270 degrees.";
            }

            if (!System.Text.RegularExpressions.Regex.IsMatch(field.ColourHex ?? string.Empty, "^#[0-9a-fA-F]{6}$"))
            {
                return $"The colour for \"{name}\" must look like #222222.";
            }
        }

        return null;
    }
}
