using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.DTO;

namespace Lanyard.Application.Services.Kitchen;

/// <summary>
/// Manages the printed QR codes that sit on tables, and resolves a scanned one back to a venue.
/// </summary>
public interface IQrTableTokenService
{
    /// <summary>
    /// Resolves a scanned token. Returns the owning company as well as the location so callers
    /// can confirm the token belongs to the tenant whose site was scanned from - a token must
    /// never resolve on another company's domain.
    /// </summary>
    Task<Result<TableResolutionDto>> ResolveAsync(string token);

    Task<Result<List<QrTableToken>>> GetForLocationAsync(int locationId, bool includeInactive);

    Task<Result<QrTableToken>> SaveAsync(QrTableToken tableToken);

    /// <summary>
    /// Issues a fresh token for the same table, invalidating the printed code. Used when a code
    /// leaks (photographed and shared) without disturbing any other table's code.
    /// </summary>
    Task<Result<QrTableToken>> RotateTokenAsync(int tableTokenId);

    Task<Result<bool>> DeactivateAsync(int tableTokenId);

    /// <summary>
    /// PNG data URI of the QR code for this table, encoding an absolute URL on the company's
    /// primary host. Absolute because the printed code outlives the page that produced it.
    /// </summary>
    Task<Result<string>> GetQrCodeDataUriAsync(int tableTokenId);
}
