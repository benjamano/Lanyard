using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Notifications;
using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Chat;
using Lanyard.Infrastructure.DTO.Notifications;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Chat;

public class ChatModerationService(
    IDbContextFactory<ApplicationDbContext> factory,
    INotificationDispatcher notifications,
    IChatEventBus eventBus,
    TimeProvider timeProvider,
    ILogger<ChatModerationService> logger) : IChatModerationService
{
    public const int MaxDetailsLength = 1000;

    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly INotificationDispatcher _notifications = notifications;
    private readonly IChatEventBus _eventBus = eventBus;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<ChatModerationService> _logger = logger;

    private DateTime Now => _timeProvider.GetUtcNow().UtcDateTime;

    public async Task<Result<ChatReport>> ReportAsync(string reporterUserId, Guid messageId, ChatReportReason reason, string? details, bool alsoBlock)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            ChatMessage? message = await ctx.ChatMessages
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.Conversation)
                .FirstOrDefaultAsync(x => x.Id == messageId);

            // Only someone in the conversation can report from it: the same answer for a message
            // that doesn't exist, so a guessed id reveals nothing.
            bool isMember = message is not null && await ctx.ChatMembers.AsNoTracking()
                .AnyAsync(x => x.ConversationId == message.ConversationId && x.UserId == reporterUserId && x.LeftUtc == null);

            if (message is null || !isMember)
            {
                return Result<ChatReport>.Fail("That message isn't available.");
            }

            if (message.AuthorUserId == reporterUserId)
            {
                return Result<ChatReport>.Fail("You can't report your own message. Delete it instead.");
            }

            if (message.IsDeleted)
            {
                return Result<ChatReport>.Fail("That message has already been removed.");
            }

            if (await ctx.ChatReports.AnyAsync(x => x.MessageId == messageId && x.ReporterUserId == reporterUserId && x.Status == ChatReportStatus.Open))
            {
                return Result<ChatReport>.Fail("You've already reported this message. A manager will look at it.");
            }

            // Reviewed by the managers of the reporter's own location in this company.
            int? locationId = await ctx.UserLocationMemberships
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.UserId == reporterUserId && x.Location!.CompanyId == message.Conversation!.CompanyId && x.Location.IsActive)
                .OrderBy(x => x.LocationId)
                .Select(x => (int?)x.LocationId)
                .FirstOrDefaultAsync();

            if (locationId is null)
            {
                return Result<ChatReport>.Fail("You need to belong to a location to report a message.");
            }

            DateTime now = Now;

            ChatReport report = new()
            {
                Id = Guid.NewGuid(),
                MessageId = messageId,
                ReporterUserId = reporterUserId,
                ReportedUserId = message.AuthorUserId,
                Reason = reason,
                Details = string.IsNullOrWhiteSpace(details) ? null : details.Trim()[..Math.Min(details.Trim().Length, MaxDetailsLength)],
                MessageHtmlSnapshot = message.BodyHtml,
                MessageSentUtc = message.CreateUtc,
                LocationId = locationId.Value,
                CreatedUtc = now
            };

            ctx.ChatReports.Add(report);

            bool blocked = false;

            if (alsoBlock && message.Conversation!.Kind == ChatConversationKind.Direct
                && !await ctx.ChatBlocks.AnyAsync(x => x.BlockerUserId == reporterUserId && x.BlockedUserId == message.AuthorUserId))
            {
                ctx.ChatBlocks.Add(new ChatBlock { Id = Guid.NewGuid(), BlockerUserId = reporterUserId, BlockedUserId = message.AuthorUserId, CreatedUtc = now });
                blocked = true;
            }

            await ctx.SaveChangesAsync();

            _logger.LogInformation("{ReporterId} reported chat message {MessageId} ({Reason}); location {LocationId}", reporterUserId, messageId, reason, locationId);

            try
            {
                List<string> managers = await SchedulingRecipients.ManagersOfLocationAsync(ctx, locationId.Value);
                _notifications.Enqueue(managers.Where(x => x != message.AuthorUserId && x != reporterUserId), NotificationTopic.ChatReport, new ChatReportedPayload(locationId.Value));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Saved chat report {ReportId} but couldn't notify managers", report.Id);
            }

            _eventBus.Publish(new ChatEvent(message.ConversationId, blocked ? [reporterUserId, message.AuthorUserId] : [], ChatEventKind.ConversationChanged));

            return Result<ChatReport>.Ok(report);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to report chat message {MessageId}", messageId);
            return Result<ChatReport>.Fail($"Couldn't send the report: {ex.Message}");
        }
    }

    public async Task<Result<List<ChatReportView>>> GetReportsAsync(LocationScope scope, int locationId, bool openOnly)
    {
        try
        {
            if (!SchedulingAccess.CanManageLocation(scope, locationId))
            {
                return Result<List<ChatReportView>>.Fail("You can only review reports for your own location.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            IQueryable<ChatReport> query = ctx.ChatReports
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.Reporter)
                .Include(x => x.Reported)
                .Include(x => x.Message).ThenInclude(x => x!.Conversation)
                .Where(x => x.LocationId == locationId);

            query = openOnly
                ? query.Where(x => x.Status == ChatReportStatus.Open)
                : query.Where(x => x.CreatedUtc > Now.AddDays(-90));

            List<ChatReport> reports = await query
                .OrderBy(x => x.Status)
                .ThenByDescending(x => x.CreatedUtc)
                .ToListAsync();

            // Only the snapshot and who's involved leave this method - never the message's
            // conversation, its other messages or its members.
            List<ChatReportView> views = reports
                .Select(x =>
                {
                    bool wasDirect = x.Message?.Conversation?.Kind == ChatConversationKind.Direct;
                    bool stillVisible = x.Message is { IsDeleted: false };

                    return new ChatReportView(StripConversation(x), RotaNames.For(x.Reporter), RotaNames.For(x.Reported), wasDirect, stillVisible);
                })
                .ToList();

            return Result<List<ChatReportView>>.Ok(views);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load chat reports for location {LocationId}", locationId);
            return Result<List<ChatReportView>>.Fail($"Failed to load reports: {ex.Message}");
        }
    }

    public async Task<Result<bool>> ResolveAsync(LocationScope scope, Guid reportId, bool removeMessage, bool suspend, int? suspendDays, string? outcome, string reviewerUserId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            ChatReport? report = await ctx.ChatReports
                .Include(x => x.Message)
                .Include(x => x.Location)
                .FirstOrDefaultAsync(x => x.Id == reportId);

            if (report is null || !SchedulingAccess.CanManageLocation(scope, report.LocationId))
            {
                return Result<bool>.Fail("That report isn't available.");
            }

            if (report.Status != ChatReportStatus.Open)
            {
                return Result<bool>.Fail("That report has already been dealt with.");
            }

            if (!scope.IsAdmin && report.ReportedUserId == reviewerUserId)
            {
                return Result<bool>.Fail("This report is about you, so another manager needs to review it.");
            }

            if (suspend && suspendDays is <= 0 or > 365)
            {
                return Result<bool>.Fail("Choose how long to suspend them for.");
            }

            DateTime now = Now;
            int companyId = report.Location!.CompanyId;

            if (removeMessage && report.Message is { IsDeleted: false } message)
            {
                ChatRules.Erase(message, reviewerUserId, now);
            }

            if (suspend)
            {
                ctx.ChatSuspensions.Add(new ChatSuspension
                {
                    Id = Guid.NewGuid(),
                    UserId = report.ReportedUserId,
                    CompanyId = companyId,
                    CreatedUtc = now,
                    UntilUtc = suspendDays is int days ? now.AddDays(days) : null,
                    Reason = string.IsNullOrWhiteSpace(outcome) ? $"Reported message ({report.Reason})" : outcome.Trim(),
                    ImposedByUserId = reviewerUserId
                });
            }

            report.Status = removeMessage || suspend ? ChatReportStatus.ActionTaken : ChatReportStatus.Dismissed;
            report.ReviewedByUserId = reviewerUserId;
            report.ReviewedUtc = now;
            report.Outcome = string.IsNullOrWhiteSpace(outcome) ? null : outcome.Trim();

            // Anyone else who reported the same message has had it dealt with too.
            List<ChatReport> sameMessage = await ctx.ChatReports
                .Where(x => x.MessageId == report.MessageId && x.Id != report.Id && x.Status == ChatReportStatus.Open && x.LocationId == report.LocationId)
                .ToListAsync();

            foreach (ChatReport other in sameMessage)
            {
                other.Status = report.Status;
                other.ReviewedByUserId = reviewerUserId;
                other.ReviewedUtc = now;
                other.Outcome = report.Outcome;
            }

            await ctx.SaveChangesAsync();

            _logger.LogInformation("{ReviewerId} resolved chat report {ReportId}: remove={Remove} suspend={Suspend} days={Days}",
                reviewerUserId, reportId, removeMessage, suspend, suspendDays);

            // The reporters learn it's been dealt with, not what was done; the reported person
            // isn't told who reported them.
            _notifications.Enqueue([report.ReporterUserId, .. sameMessage.Select(x => x.ReporterUserId)], NotificationTopic.ChatReportReviewed,
                new ChatReportReviewedPayload(report.LocationId));

            if (removeMessage && report.Message is not null)
            {
                List<string> members = await ctx.ChatMembers.AsNoTracking().TagWithCallSite()
                    .Where(x => x.ConversationId == report.Message.ConversationId && x.LeftUtc == null)
                    .Select(x => x.UserId)
                    .ToListAsync();

                _eventBus.Publish(new ChatEvent(report.Message.ConversationId, members, ChatEventKind.MessageChanged));
            }

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve chat report {ReportId}", reportId);
            return Result<bool>.Fail($"Couldn't save the decision: {ex.Message}");
        }
    }

    public async Task<Result<List<ChatSuspensionView>>> GetSuspensionsAsync(LocationScope scope, int companyId)
    {
        try
        {
            if (!SchedulingAccess.CanManageCompany(scope, companyId))
            {
                return Result<List<ChatSuspensionView>>.Fail("You can only see suspensions at your own company.");
            }

            DateTime now = Now;
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<ChatSuspension> suspensions = await ctx.ChatSuspensions
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.User)
                .Where(x => x.CompanyId == companyId && x.LiftedUtc == null && (x.UntilUtc == null || x.UntilUtc > now))
                .OrderBy(x => x.CreatedUtc)
                .ToListAsync();

            List<string> imposers = suspensions.Select(x => x.ImposedByUserId).Distinct().ToList();
            Dictionary<string, string> names = await ctx.Users.AsNoTracking().TagWithCallSite()
                .Where(x => imposers.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => RotaNames.For(x));

            return Result<List<ChatSuspensionView>>.Ok(suspensions
                .Select(x => new ChatSuspensionView(x, RotaNames.For(x.User), names.GetValueOrDefault(x.ImposedByUserId, "A manager")))
                .ToList());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load chat suspensions for company {CompanyId}", companyId);
            return Result<List<ChatSuspensionView>>.Fail($"Failed to load suspensions: {ex.Message}");
        }
    }

    public async Task<Result<bool>> LiftSuspensionAsync(LocationScope scope, Guid suspensionId, string userId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            ChatSuspension? suspension = await ctx.ChatSuspensions.FirstOrDefaultAsync(x => x.Id == suspensionId);

            if (suspension is null || !SchedulingAccess.CanManageCompany(scope, suspension.CompanyId))
            {
                return Result<bool>.Fail("That suspension isn't available.");
            }

            suspension.LiftedUtc = Now;
            suspension.LiftedByUserId = userId;
            await ctx.SaveChangesAsync();

            _logger.LogInformation("{UserId} lifted chat suspension {SuspensionId}", userId, suspensionId);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to lift chat suspension {SuspensionId}", suspensionId);
            return Result<bool>.Fail($"Couldn't lift the suspension: {ex.Message}");
        }
    }

    public async Task<Result<int>> CountOpenForNavAsync(LocationScope scope, string? viewerUserId)
    {
        try
        {
            if ((!scope.IsAdmin && !scope.IsManager) || scope.LocationId is not int locationId)
            {
                return Result<int>.Ok(0);
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            IQueryable<ChatReport> open = ctx.ChatReports
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.LocationId == locationId && x.Status == ChatReportStatus.Open);

            // Reports about the manager themselves are for someone else to review.
            if (!scope.IsAdmin && viewerUserId is not null)
            {
                open = open.Where(x => x.ReportedUserId != viewerUserId);
            }

            return Result<int>.Ok(await open.CountAsync());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to count open chat reports");
            return Result<int>.Fail($"Failed to count reports: {ex.Message}");
        }
    }

    // The report without its navigation to the live message and conversation, so nothing beyond
    // the snapshot can reach a page by accident.
    private static ChatReport StripConversation(ChatReport report)
    {
        report.Message = null;
        return report;
    }
}
