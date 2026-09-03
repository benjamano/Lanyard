using Lanyard.Application.Services.Authentication;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Locations;

/// <summary>
/// Changing which Stripe account a company's takings land in.
///
/// Its own service, and not part of SaveCompanyAsync, for two reasons. It is the single most
/// damaging field on the Companies screen - repointing it sends every future payment to somebody
/// else's bank account - so it gets an admin check and a fresh second-factor check that the
/// ordinary save path has no way to bypass or forget. And CompanyLocationService cannot ask
/// ISecurityService anything, because SecurityService already depends on it; putting the check
/// there would close a dependency cycle.
/// </summary>
public interface ICompanyPayoutAccountService
{
    /// <summary>
    /// Sets or clears a company's Stripe account. Requires an admin and a valid current
    /// second-factor code; a manager cannot do this for their own company.
    /// </summary>
    Task<Result<bool>> SetStripeAccountIdAsync(int companyId, string? stripeAccountId, string twoFactorCode);
}

public class CompanyPayoutAccountService(
    IDbContextFactory<ApplicationDbContext> factory,
    ICompanyAccessService companyAccess,
    ISecurityService securityService,
    ILogger<CompanyPayoutAccountService> logger) : ICompanyPayoutAccountService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly ICompanyAccessService _companyAccess = companyAccess;
    private readonly ISecurityService _securityService = securityService;
    private readonly ILogger<CompanyPayoutAccountService> _logger = logger;

    public async Task<Result<bool>> SetStripeAccountIdAsync(int companyId, string? stripeAccountId, string twoFactorCode)
    {
        try
        {
            // Admin only, deliberately not "whoever administers this company". A venue manager
            // running their own company can change its branding and its wording, but not where
            // the money goes.
            if (!(await _companyAccess.GetCurrentAsync()).IsAdmin)
            {
                return Result<bool>.Fail("Only an administrator can change the payout account.");
            }

            string? normalized = string.IsNullOrWhiteSpace(stripeAccountId) ? null : stripeAccountId.Trim();

            // Shape-checked only. Whether the account exists and can accept charges is Stripe's
            // to answer, and it answers at payment time; rejecting a well-formed id here on a
            // guess would just block onboarding.
            if (normalized is not null && !normalized.StartsWith("acct_", StringComparison.Ordinal))
            {
                return Result<bool>.Fail("A Stripe account ID looks like 'acct_1234...'.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Company? company = await ctx.Companies.FirstOrDefaultAsync(c => c.Id == companyId);

            if (company is null)
            {
                return Result<bool>.Fail("Company not found.");
            }

            // Nothing is changing, so there is nothing to re-authenticate for. Checked before the
            // code rather than after, so re-saving the form does not demand a code for no reason.
            if (company.StripeAccountId == normalized)
            {
                return Result<bool>.Ok(true);
            }

            Result<bool> secondFactor = await _securityService.VerifySecondFactorAsync(twoFactorCode);

            if (!secondFactor.IsSuccess || !secondFactor.Data)
            {
                _logger.LogWarning(
                    "Refused a payout account change for company {CompanyId}: second factor not verified",
                    companyId);

                return Result<bool>.Fail(secondFactor.Error ?? "Invalid or expired code.");
            }

            string? previous = company.StripeAccountId;

            company.StripeAccountId = normalized;
            company.UpdateDate = DateTime.UtcNow;

            await ctx.SaveChangesAsync();

            // Logged at Warning with both values, because "when did the payout account change and
            // what was it before" is the first question asked if money turns up somewhere
            // unexpected. Account ids are identifiers, not secrets.
            _logger.LogWarning(
                "Payout account for company {CompanyId} changed from {PreviousAccount} to {NewAccount}",
                companyId, previous ?? "(none)", normalized ?? "(none)");

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to change the payout account for company {CompanyId}", companyId);

            return Result<bool>.Fail($"Failed to update the payout account: {ex.Message}");
        }
    }
}
