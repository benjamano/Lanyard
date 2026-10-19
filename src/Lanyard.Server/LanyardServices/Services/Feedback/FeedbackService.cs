using Lanyard.Application.Services.Authentication;
using Lanyard.Application.Services.Email;
using Lanyard.Application.Services.Tenancy;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Feedback;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lanyard.Application.Services.Feedback;

public sealed class FeedbackService(
    IDbContextFactory<ApplicationDbContext> factory,
    ISystemDbContextFactory systemFactory,
    ITenantContext tenantContext,
    ISecurityService securityService,
    IEmailService emailService,
    IOptions<EmailOptions> emailOptions,
    TimeProvider timeProvider,
    ILogger<FeedbackService> logger) : IFeedbackService
{
    public const int MaxCommentLength = 2000;
    public const int MaxPagePathLength = 500;

    // Enough for anyone with something to say; stops a stuck finger filling the inbox.
    public const int MaxSubmissionsPerHour = 10;

    public const string InboxPath = "/manage/feedback";

    private const string NotAllowed = "Only a platform admin can read feedback.";

    // Most recent first; older feedback is still in the table, just not on the page.
    private const int InboxLimit = 300;

    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly ISystemDbContextFactory _systemFactory = systemFactory;
    private readonly ITenantContext _tenantContext = tenantContext;
    private readonly ISecurityService _securityService = securityService;
    private readonly IEmailService _emailService = emailService;
    private readonly IOptions<EmailOptions> _emailOptions = emailOptions;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<FeedbackService> _logger = logger;

    private DateTime Now => _timeProvider.GetUtcNow().UtcDateTime;

    public async Task<Result<UserFeedback>> SubmitAsync(FeedbackSubmission submission)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(submission.UserId))
            {
                return Result<UserFeedback>.Fail("Sign in to send feedback.");
            }

            if (ScoreError(submission.Kind, submission.Score) is string scoreError)
            {
                return Result<UserFeedback>.Fail(scoreError);
            }

            string? comment = string.IsNullOrWhiteSpace(submission.Comment) ? null : submission.Comment.Trim();

            if (comment is { Length: > MaxCommentLength })
            {
                return Result<UserFeedback>.Fail($"Keep it under {MaxCommentLength} characters.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            DateTime hourAgo = Now.AddHours(-1);
            int recent = await ctx.UserFeedback
                .AsNoTracking()
                .TagWithCallSite()
                .CountAsync(x => x.UserId == submission.UserId && x.CreatedUtc > hourAgo);

            if (recent >= MaxSubmissionsPerHour)
            {
                return Result<UserFeedback>.Fail("Thanks - you've sent a lot of feedback already. Try again in a little while.");
            }

            UserFeedback feedback = new()
            {
                Id = Guid.NewGuid(),
                UserId = submission.UserId,
                CompanyId = submission.CompanyId,
                LocationId = submission.LocationId,
                Kind = submission.Kind,
                Score = submission.Score,
                Comment = comment,
                PagePath = NormalisePagePath(submission.PagePath),
                CreatedUtc = Now
            };

            ctx.UserFeedback.Add(feedback);
            await ctx.SaveChangesAsync();

            _logger.LogInformation("Feedback {FeedbackId} ({Kind} {Score}) from {UserId}", feedback.Id, feedback.Kind, feedback.Score, feedback.UserId);

            // Saved is what matters: the inbox has it even if the email doesn't go.
            await NotifyPlatformAdminsAsync(feedback);

            return Result<UserFeedback>.Ok(feedback);
        }
        catch (Exception ex)
        {
            return Result<UserFeedback>.Fail($"Failed to send feedback: {ex.Message}");
        }
    }

    public async Task<Result<List<FeedbackInboxItem>>> GetInboxAsync(FeedbackInboxFilter filter, bool unreviewedOnly)
    {
        try
        {
            if (!_tenantContext.IsPlatformAdmin)
            {
                return Result<List<FeedbackInboxItem>>.Fail(NotAllowed);
            }

            // Unfiltered: feedback comes from every company, and so do the names shown with it.
            await using ApplicationDbContext ctx = await _systemFactory.CreateDbContextAsync();

            IQueryable<UserFeedback> query = ctx.UserFeedback.AsNoTracking().TagWithCallSite();

            query = filter switch
            {
                FeedbackInboxFilter.ThumbsUp => query.Where(x => x.Kind == FeedbackKind.Thumbs && x.Score > 0),
                FeedbackInboxFilter.ThumbsDown => query.Where(x => x.Kind == FeedbackKind.Thumbs && x.Score < 0),
                FeedbackInboxFilter.Stars => query.Where(x => x.Kind == FeedbackKind.StarRating),
                _ => query
            };

            if (unreviewedOnly)
            {
                query = query.Where(x => x.ReviewedUtc == null);
            }

            List<UserFeedback> rows = await query
                .OrderByDescending(x => x.CreatedUtc)
                .Take(InboxLimit)
                .ToListAsync();

            List<string> userIds = rows.Select(x => x.UserId).Distinct().ToList();
            List<int> companyIds = rows.Where(x => x.CompanyId != null).Select(x => x.CompanyId!.Value).Distinct().ToList();
            List<int> locationIds = rows.Where(x => x.LocationId != null).Select(x => x.LocationId!.Value).Distinct().ToList();

            Dictionary<string, UserProfile> users = await ctx.Users
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => userIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id);

            Dictionary<int, string> companies = await ctx.Companies
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => companyIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.Name);

            Dictionary<int, string> locations = await ctx.Locations
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => locationIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.Name);

            List<FeedbackInboxItem> items = rows
                .Select(x =>
                {
                    UserProfile? user = users.GetValueOrDefault(x.UserId);

                    return new FeedbackInboxItem(
                        x.Id,
                        x.Kind,
                        x.Score,
                        x.Comment,
                        x.PagePath,
                        user is null ? "Unknown user" : DisplayName(user),
                        user?.Email,
                        x.CompanyId is int companyId ? companies.GetValueOrDefault(companyId) : null,
                        x.LocationId is int locationId ? locations.GetValueOrDefault(locationId) : null,
                        x.CreatedUtc,
                        x.ReviewedUtc);
                })
                .ToList();

            return Result<List<FeedbackInboxItem>>.Ok(items);
        }
        catch (Exception ex)
        {
            return Result<List<FeedbackInboxItem>>.Fail($"Failed to load feedback: {ex.Message}");
        }
    }

    public async Task<Result<int>> CountUnreviewedAsync()
    {
        try
        {
            if (!_tenantContext.IsPlatformAdmin)
            {
                return Result<int>.Fail(NotAllowed);
            }

            await using ApplicationDbContext ctx = await _systemFactory.CreateDbContextAsync();

            int count = await ctx.UserFeedback
                .AsNoTracking()
                .TagWithCallSite()
                .CountAsync(x => x.ReviewedUtc == null);

            return Result<int>.Ok(count);
        }
        catch (Exception ex)
        {
            return Result<int>.Fail($"Failed to count feedback: {ex.Message}");
        }
    }

    public async Task<Result<bool>> SetReviewedAsync(Guid feedbackId, bool reviewed)
    {
        try
        {
            if (!_tenantContext.IsPlatformAdmin)
            {
                return Result<bool>.Fail(NotAllowed);
            }

            await using ApplicationDbContext ctx = await _systemFactory.CreateDbContextAsync();

            UserFeedback? feedback = await ctx.UserFeedback.FirstOrDefaultAsync(x => x.Id == feedbackId);

            if (feedback is null)
            {
                return Result<bool>.Fail("That feedback no longer exists.");
            }

            Result<string> reviewer = await _securityService.GetCurrentUserIdAsync();

            feedback.ReviewedUtc = reviewed ? Now : null;
            feedback.ReviewedByUserId = reviewed && reviewer.IsSuccess ? reviewer.Data : null;

            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to update feedback: {ex.Message}");
        }
    }

    public static string? ScoreError(FeedbackKind kind, int score) => kind switch
    {
        FeedbackKind.Thumbs when score is 1 or -1 => null,
        FeedbackKind.Thumbs => "Pick thumbs up or thumbs down.",
        FeedbackKind.StarRating when score is >= 1 and <= 5 => null,
        FeedbackKind.StarRating => "Pick between 1 and 5 stars.",
        _ => "Unknown kind of feedback."
    };

    // Base-relative path and query only - never a full URL, so nothing about the host is stored.
    private static string NormalisePagePath(string? pagePath)
    {
        string path = "/" + (pagePath ?? string.Empty).Trim().TrimStart('/');

        return path.Length > MaxPagePathLength ? path[..MaxPagePathLength] : path;
    }

    private static string DisplayName(UserProfile user)
    {
        string name = user.GetName().Trim();

        return string.IsNullOrWhiteSpace(name) ? user.UserName ?? user.Email ?? "Unknown user" : name;
    }

    private async Task NotifyPlatformAdminsAsync(UserFeedback feedback)
    {
        try
        {
            await using ApplicationDbContext ctx = await _systemFactory.CreateDbContextAsync();

            List<UserProfile> admins = await (
                    from userRole in ctx.UserRoles
                    join role in ctx.Roles on userRole.RoleId equals role.Id
                    join user in ctx.Users on userRole.UserId equals user.Id
                    where role.Name == LanyardRoles.PlatformAdmin
                        && role.IsActive
                        && user.Id != ApplicationDbContext.SystemDeletedUserPlaceholderId
                        && user.Email != null
                    select user)
                .AsNoTracking()
                .TagWithCallSite()
                .Distinct()
                .ToListAsync();

            if (admins.Count == 0)
            {
                _logger.LogWarning("Feedback {FeedbackId} not emailed: there are no platform admins with an email address", feedback.Id);
                return;
            }

            UserProfile? sender = await ctx.Users.AsNoTracking().TagWithCallSite().FirstOrDefaultAsync(x => x.Id == feedback.UserId);

            string? companyName = feedback.CompanyId is int companyId
                ? await ctx.Companies.AsNoTracking().TagWithCallSite().Where(x => x.Id == companyId).Select(x => x.Name).FirstOrDefaultAsync()
                : null;

            string? locationName = feedback.LocationId is int locationId
                ? await ctx.Locations.AsNoTracking().TagWithCallSite().Where(x => x.Id == locationId).Select(x => x.Name).FirstOrDefaultAsync()
                : null;

            EmailOptions options = _emailOptions.Value;

            FeedbackEmail email = new(
                feedback.Kind,
                feedback.Score,
                feedback.Comment,
                EmailBranding.Link(options, feedback.PagePath),
                sender is null ? "Unknown user" : DisplayName(sender),
                sender?.Email,
                companyName,
                locationName,
                EmailBranding.Link(options, InboxPath));

            foreach (UserProfile admin in admins)
            {
                Result<bool> sent = await _emailService.SendFeedbackReceivedEmailAsync(admin, email);

                if (!sent.IsSuccess)
                {
                    _logger.LogWarning("Failed to email feedback {FeedbackId} to {UserId}: {Error}", feedback.Id, admin.Id, sent.Error);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to email feedback {FeedbackId} to platform admins", feedback.Id);
        }
    }
}
