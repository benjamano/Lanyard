using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Notifications;

public class NotificationPreferenceService(
    IDbContextFactory<ApplicationDbContext> factory,
    ILogger<NotificationPreferenceService> logger) : INotificationPreferenceService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly ILogger<NotificationPreferenceService> _logger = logger;

    public async Task<Result<List<TopicPreference>>> GetForUserAsync(string userId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Dictionary<NotificationTopic, NotificationPreference> saved = await ctx.NotificationPreferences
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.UserId == userId)
                .ToDictionaryAsync(x => x.Topic);

            List<TopicPreference> preferences = NotificationTopics.All
                .Select(info => saved.TryGetValue(info.Topic, out NotificationPreference? row)
                    ? new TopicPreference(info.Topic, row.Push, row.Email)
                    : NotificationTopics.Default(info.Topic))
                .ToList();

            return Result<List<TopicPreference>>.Ok(preferences);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading notification settings for {UserId}", userId);

            return Result<List<TopicPreference>>.Fail($"Couldn't load your notification settings: {ex.Message}");
        }
    }

    public async Task<Result<TopicPreference>> SaveAsync(string userId, NotificationTopic topic, bool push, bool email)
    {
        try
        {
            TopicPreference defaults = NotificationTopics.Default(topic);

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            NotificationPreference? row = await ctx.NotificationPreferences
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.UserId == userId && x.Topic == topic);

            // Back to the default means no row, so a later change to the default still reaches
            // everyone who never chose differently.
            if (push == defaults.Push && email == defaults.Email)
            {
                if (row is not null)
                {
                    ctx.NotificationPreferences.Remove(row);
                }
            }
            else if (row is null)
            {
                ctx.NotificationPreferences.Add(new NotificationPreference { UserId = userId, Topic = topic, Push = push, Email = email });
            }
            else
            {
                row.Push = push;
                row.Email = email;
            }

            await ctx.SaveChangesAsync();

            return Result<TopicPreference>.Ok(new TopicPreference(topic, push, email));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving {Topic} notification setting for {UserId}", topic, userId);

            return Result<TopicPreference>.Fail($"Couldn't save that setting: {ex.Message}");
        }
    }

    public async Task<Result<bool>> GetShowMessagePreviewsAsync(string userId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            bool? show = await ctx.Users
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.Id == userId)
                .Select(x => (bool?)x.ShowMessagePreviews)
                .FirstOrDefaultAsync();

            return show is bool value ? Result<bool>.Ok(value) : Result<bool>.Fail("Account not found.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading the message-preview setting for {UserId}", userId);

            return Result<bool>.Fail($"Couldn't load that setting: {ex.Message}");
        }
    }

    public async Task<Result<bool>> SetShowMessagePreviewsAsync(string userId, bool show)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            UserProfile? user = await ctx.Users.FirstOrDefaultAsync(x => x.Id == userId);

            if (user is null)
            {
                return Result<bool>.Fail("Account not found.");
            }

            user.ShowMessagePreviews = show;
            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(show);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving the message-preview setting for {UserId}", userId);

            return Result<bool>.Fail($"Couldn't save that setting: {ex.Message}");
        }
    }
}
