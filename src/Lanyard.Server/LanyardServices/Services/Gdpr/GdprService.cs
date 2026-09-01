using System.Security.Cryptography;
using System.Text;
using Lanyard.Application.Services.Authentication;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Infrastructure.Models.Dmx;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Gdpr;

public class GdprService : IGdprService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory;
    private readonly UserManager<UserProfile> _userManager;
    private readonly ISecurityService _securityService;
    private readonly ILogger<GdprService> _logger;

    public GdprService(
        IDbContextFactory<ApplicationDbContext> factory,
        UserManager<UserProfile> userManager,
        ISecurityService securityService,
        ILogger<GdprService> logger)
    {
        _factory = factory;
        _userManager = userManager;
        _securityService = securityService;
        _logger = logger;
    }

    public async Task<Result<bool>> EraseUserDataAsync(string userId)
    {
        try
        {
            if (!await _securityService.IsCurrentUserInRoleAsync("Admin"))
            {
                return Result<bool>.Fail("You must be an administrator to perform this action!");
            }

            if (userId == ApplicationDbContext.SystemDeletedUserPlaceholderId)
            {
                return Result<bool>.Fail("This account cannot be erased.");
            }

            UserProfile? user = await _userManager.FindByIdAsync(userId);

            if (user is null)
            {
                return Result<bool>.Fail("User not found!");
            }

            Result<UserProfile> performingAdminResult = await _securityService.GetCurrentUserProfileAsync();

            if (!performingAdminResult.IsSuccess || performingAdminResult.Data is null)
            {
                return Result<bool>.Fail("Unable to identify the administrator performing this action.");
            }

            // Written first and durably, before any destructive step: even if a later step
            // crashes partway through, this proves an erasure was attempted, when, and by whom.
            // Every step below is idempotent, so a partial failure is safely retried by the admin.
            Result<bool> auditResult = await WriteErasureAuditRecordAsync(user, performingAdminResult.Data);

            if (!auditResult.IsSuccess)
            {
                return Result<bool>.Fail($"Failed to record erasure audit trail: {auditResult.Error}");
            }

            Result<bool> anonymizeResult = await AnonymizeAttributionAsync(userId);

            if (!anonymizeResult.IsSuccess)
            {
                return Result<bool>.Fail($"Failed to anonymize authored content: {anonymizeResult.Error}");
            }

            Result<bool> removalResult = await RemoveOwnedRecordsAsync(userId);

            if (!removalResult.IsSuccess)
            {
                return Result<bool>.Fail($"Failed to remove owned records: {removalResult.Error}");
            }

            IdentityResult identityResult = await _userManager.DeleteAsync(user);

            if (!identityResult.Succeeded)
            {
                string errors = string.Join(", ", identityResult.Errors.Select(e => e.Description));
                return Result<bool>.Fail($"Failed to delete user account: {errors}");
            }

            _logger.LogInformation(
                "Erased GDPR data for user {ErasedUserId}, performed by {PerformedByUserId}",
                userId,
                performingAdminResult.Data.Id);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GDPR erasure failed for user {UserId}", userId);
            return Result<bool>.Fail(ex.Message);
        }
    }

    private async Task<Result<bool>> WriteErasureAuditRecordAsync(UserProfile user, UserProfile performingAdmin)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            ctx.UserErasureRecords.Add(new UserErasureRecord
            {
                Id = Guid.NewGuid(),
                ErasedUserId = user.Id,
                ErasedEmailHash = HashEmail(user.NormalizedEmail ?? user.Email ?? user.Id),
                ErasedAtUtc = DateTime.UtcNow,
                PerformedByUserId = performingAdmin.Id,
                PerformedByUserName = performingAdmin.GetName()
            });

            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail(ex.Message);
        }
    }

    private static string HashEmail(string email) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(email.ToUpperInvariant())));

    // Content the user authored (DMX scenes, playlists, roles, uploads) is kept - only the
    // attribution linking it back to them is scrubbed. Two FKs (DmxScene/DmxSceneStep/
    // DmxSceneStepChannelValue.CreateByUserId, ApplicationRole.CreatedByUserId) are `required
    // string` and cannot be nulled, so they're repointed to the seeded placeholder account instead.
    private async Task<Result<bool>> AnonymizeAttributionAsync(string userId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<DmxScene> scenes = await ctx.DmxScenes
                .Where(x => x.CreateByUserId == userId || x.UpdateByUserId == userId)
                .ToListAsync();

            foreach (DmxScene scene in scenes)
            {
                if (scene.CreateByUserId == userId)
                {
                    scene.CreateByUserId = ApplicationDbContext.SystemDeletedUserPlaceholderId;
                }

                if (scene.UpdateByUserId == userId)
                {
                    scene.UpdateByUserId = null;
                }
            }

            List<DmxSceneStep> steps = await ctx.DmxSceneSteps
                .Where(x => x.CreateByUserId == userId || x.UpdateByUserId == userId)
                .ToListAsync();

            foreach (DmxSceneStep step in steps)
            {
                if (step.CreateByUserId == userId)
                {
                    step.CreateByUserId = ApplicationDbContext.SystemDeletedUserPlaceholderId;
                }

                if (step.UpdateByUserId == userId)
                {
                    step.UpdateByUserId = null;
                }
            }

            List<DmxSceneStepChannelValue> channelValues = await ctx.DmxSceneStepChannelValues
                .Where(x => x.CreateByUserId == userId || x.UpdateByUserId == userId)
                .ToListAsync();

            foreach (DmxSceneStepChannelValue channelValue in channelValues)
            {
                if (channelValue.CreateByUserId == userId)
                {
                    channelValue.CreateByUserId = ApplicationDbContext.SystemDeletedUserPlaceholderId;
                }

                if (channelValue.UpdateByUserId == userId)
                {
                    channelValue.UpdateByUserId = null;
                }
            }

            List<ApplicationRole> roles = await ctx.Roles
                .Where(x => x.CreatedByUserId == userId)
                .ToListAsync();

            foreach (ApplicationRole role in roles)
            {
                role.CreatedByUserId = ApplicationDbContext.SystemDeletedUserPlaceholderId;
            }

            List<Playlist> playlists = await ctx.Playlists
                .Where(x => x.CreateByUserId == userId || x.DeleteByUserId == userId)
                .ToListAsync();

            foreach (Playlist playlist in playlists)
            {
                if (playlist.CreateByUserId == userId)
                {
                    playlist.CreateByUserId = null;
                }

                if (playlist.DeleteByUserId == userId)
                {
                    playlist.DeleteByUserId = null;
                }
            }

            List<PlaylistSongMember> playlistSongMembers = await ctx.PlaylistSongMembers
                .Where(x => x.CreateByUserId == userId || x.DeleteByUserId == userId)
                .ToListAsync();

            foreach (PlaylistSongMember member in playlistSongMembers)
            {
                if (member.CreateByUserId == userId)
                {
                    member.CreateByUserId = null;
                }

                if (member.DeleteByUserId == userId)
                {
                    member.DeleteByUserId = null;
                }
            }

            List<Announcement> announcements = await ctx.Announcements
                .Where(x => x.CreatedByUserId == userId)
                .ToListAsync();

            foreach (Announcement announcement in announcements)
            {
                announcement.CreatedByUserId = null;
            }

            List<CourseAssignment> assignedByRows = await ctx.CourseAssignments
                .Where(x => x.AssignedByUserId == userId)
                .ToListAsync();

            foreach (CourseAssignment assignment in assignedByRows)
            {
                assignment.AssignedByUserId = null;
            }

            List<FileMetadata> uploadedFiles = await ctx.FileMetadata
                .Where(x => x.UploadedBy == userId)
                .ToListAsync();

            foreach (FileMetadata file in uploadedFiles)
            {
                file.UploadedBy = null;
            }

            List<Folder> createdFolders = await ctx.Folders
                .Where(x => x.CreatedBy == userId)
                .ToListAsync();

            foreach (Folder folder in createdFolders)
            {
                folder.CreatedBy = null;
            }

            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail(ex.Message);
        }
    }

    // Real ownership, not attribution - these rows exist only because of this user
    // (mirrors ICourseAssignmentService.UnassignAllForUserAsync's per-user cleanup, but hard-deletes
    // rather than soft-deletes since the assignment itself is personal data being erased, not
    // content to keep). Removing CourseAssignment cascades to its CourseQuizAttempt/
    // CourseSectionProgress children via their required FKs - also this user's own personal data.
    private async Task<Result<bool>> RemoveOwnedRecordsAsync(string userId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<CourseAssignment> ownedAssignments = await ctx.CourseAssignments
                .Where(x => x.UserId == userId)
                .ToListAsync();

            ctx.CourseAssignments.RemoveRange(ownedAssignments);

            List<UserLocationMembership> memberships = await ctx.UserLocationMemberships
                .Where(x => x.UserId == userId)
                .ToListAsync();

            ctx.UserLocationMemberships.RemoveRange(memberships);

            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail(ex.Message);
        }
    }
}
