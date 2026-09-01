using System.Security.Cryptography;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.DTO;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using QRCoder;

namespace Lanyard.Application.Services.Kitchen;

public class QrTableTokenService(
    IDbContextFactory<ApplicationDbContext> factory,
    ILogger<QrTableTokenService> logger) : IQrTableTokenService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly ILogger<QrTableTokenService> _logger = logger;

    public async Task<Result<TableResolutionDto>> ResolveAsync(string token)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return Result<TableResolutionDto>.Fail("Table token is required.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            var resolved = await ctx.QrTableTokens
                .AsNoTracking()
                .TagWithCallSite()
                .Where(t => t.Token == token && t.IsActive)
                .Select(t => new
                {
                    t.Label,
                    t.LocationId,
                    LocationName = t.Location!.Name,
                    LocationActive = t.Location.IsActive,
                    t.Location.OrderingEnabled,
                    t.Location.CompanyId
                })
                .FirstOrDefaultAsync();

            // One message for "no such token" and "token for a retired venue" alike: an
            // anonymous caller holding a guessed token learns nothing about which it was.
            if (resolved is null || !resolved.LocationActive)
            {
                return Result<TableResolutionDto>.Fail("Table not found.");
            }

            return Result<TableResolutionDto>.Ok(new TableResolutionDto
            {
                CompanyId = resolved.CompanyId,
                LocationId = resolved.LocationId,
                LocationName = resolved.LocationName,
                TableLabel = resolved.Label,
                OrderingEnabled = resolved.OrderingEnabled
            });
        }
        catch (Exception ex)
        {
            return Result<TableResolutionDto>.Fail($"Failed to resolve table token: {ex.Message}");
        }
    }

    public async Task<Result<List<QrTableToken>>> GetForLocationAsync(int locationId, bool includeInactive)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<QrTableToken> tokens = await ctx.QrTableTokens
                .AsNoTracking()
                .TagWithCallSite()
                .Where(t => t.LocationId == locationId && (includeInactive || t.IsActive))
                .OrderBy(t => t.Label)
                .ToListAsync();

            return Result<List<QrTableToken>>.Ok(tokens);
        }
        catch (Exception ex)
        {
            return Result<List<QrTableToken>>.Fail($"Failed to retrieve table codes: {ex.Message}");
        }
    }

    public async Task<Result<QrTableToken>> SaveAsync(QrTableToken tableToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(tableToken.Label))
            {
                return Result<QrTableToken>.Fail("Table label is required.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            if (!await ctx.Locations.AnyAsync(l => l.Id == tableToken.LocationId && l.IsActive))
            {
                return Result<QrTableToken>.Fail("Location not found.");
            }

            DateTime now = DateTime.UtcNow;
            QrTableToken entity;

            if (tableToken.Id == 0)
            {
                entity = new QrTableToken
                {
                    LocationId = tableToken.LocationId,
                    Label = tableToken.Label.Trim(),
                    Token = GenerateToken(),
                    IsActive = true,
                    CreateDate = now,
                    UpdateDate = now
                };

                await ctx.QrTableTokens.AddAsync(entity);
            }
            else
            {
                QrTableToken? existing = await ctx.QrTableTokens.FirstOrDefaultAsync(t => t.Id == tableToken.Id);

                if (existing is null)
                {
                    return Result<QrTableToken>.Fail("Table code not found.");
                }

                // The token itself is deliberately not editable here - changing it invalidates a
                // printed code, so that goes through RotateTokenAsync where it is the point.
                existing.Label = tableToken.Label.Trim();
                existing.IsActive = tableToken.IsActive;
                existing.UpdateDate = now;
                entity = existing;
            }

            await ctx.SaveChangesAsync();

            return Result<QrTableToken>.Ok(entity);
        }
        catch (Exception ex)
        {
            return Result<QrTableToken>.Fail($"Failed to save table code: {ex.Message}");
        }
    }

    public async Task<Result<QrTableToken>> RotateTokenAsync(int tableTokenId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            QrTableToken? existing = await ctx.QrTableTokens.FirstOrDefaultAsync(t => t.Id == tableTokenId);

            if (existing is null)
            {
                return Result<QrTableToken>.Fail("Table code not found.");
            }

            existing.Token = GenerateToken();
            existing.UpdateDate = DateTime.UtcNow;

            await ctx.SaveChangesAsync();

            _logger.LogInformation("Rotated QR token for table {TableTokenId} at location {LocationId}; the previously printed code no longer resolves",
                existing.Id, existing.LocationId);

            return Result<QrTableToken>.Ok(existing);
        }
        catch (Exception ex)
        {
            return Result<QrTableToken>.Fail($"Failed to rotate table code: {ex.Message}");
        }
    }

    public async Task<Result<bool>> DeactivateAsync(int tableTokenId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            QrTableToken? existing = await ctx.QrTableTokens.FirstOrDefaultAsync(t => t.Id == tableTokenId);

            if (existing is null)
            {
                return Result<bool>.Fail("Table code not found.");
            }

            existing.IsActive = false;
            existing.UpdateDate = DateTime.UtcNow;

            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to deactivate table code: {ex.Message}");
        }
    }

    public async Task<Result<string>> GetQrCodeDataUriAsync(int tableTokenId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            var resolved = await ctx.QrTableTokens
                .AsNoTracking()
                .TagWithCallSite()
                .Where(t => t.Id == tableTokenId)
                .Select(t => new { t.Token, t.Location!.CompanyId })
                .FirstOrDefaultAsync();

            if (resolved is null)
            {
                return Result<string>.Fail("Table code not found.");
            }

            string? primaryHost = await ctx.CompanyDomains
                .AsNoTracking()
                .TagWithCallSite()
                .Where(d => d.CompanyId == resolved.CompanyId && d.IsActive && d.IsPrimary)
                .Select(d => d.Hostname)
                .FirstOrDefaultAsync();

            // Refusing rather than guessing a host: a QR code is printed and stuck to furniture,
            // so one built against a placeholder domain is a physical object that has to be
            // reprinted. Better to make the admin set the primary domain first.
            if (string.IsNullOrWhiteSpace(primaryHost))
            {
                return Result<string>.Fail("Set this company's primary domain before printing table codes.");
            }

            string url = $"https://{primaryHost}/order/t/{resolved.Token}";

            return Result<string>.Ok(BuildQrCodeDataUri(url));
        }
        catch (Exception ex)
        {
            return Result<string>.Fail($"Failed to generate QR code: {ex.Message}");
        }
    }

    // 160 bits of CSPRNG output, base64url-encoded. Guessing one is not a realistic attack, so
    // the token alone is enough to identify a table without any additional secret.
    private static string GenerateToken()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(20);

        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    // Same construction as SecurityService.BuildQrCodeDataUri, which produces the 2FA enrolment
    // code; kept local rather than shared because that one is about an otpauth: URI and this one
    // about a public https: URL, and merging them would couple two unrelated features.
    private static string BuildQrCodeDataUri(string url)
    {
        using QRCodeGenerator qrGenerator = new();
        using QRCodeData qrData = qrGenerator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
        PngByteQRCode qrCode = new(qrData);
        byte[] bytes = qrCode.GetGraphic(10);

        return $"data:image/png;base64,{Convert.ToBase64String(bytes)}";
    }
}
