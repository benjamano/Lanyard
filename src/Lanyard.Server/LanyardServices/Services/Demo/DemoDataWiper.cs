using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services.Demo;

// Deletes everything that belongs to the demo company, so DemoSeeder can put fresh sample data back.
//
// Every delete runs on a context whose tenant is the demo company, and ExecuteDelete honours the
// global company filters - so each statement below can only ever reach demo rows, however it's
// written. Order is children before parents (several FKs are Restrict).
internal static class DemoDataWiper
{
    public static async Task WipeAsync(DbContextOptions<ApplicationDbContext> options, int demoCompanyId, CancellationToken ct)
    {
        await using ApplicationDbContext ctx = new(options, new FixedTenantProvider(demoCompanyId));

        await EnsureIsDemoCompanyAsync(ctx, demoCompanyId, ct);

        // Training
        await ctx.CourseQuizAttemptAnswers.ExecuteDeleteAsync(ct);
        await ctx.CourseQuizAttempts.ExecuteDeleteAsync(ct);
        await ctx.CourseSectionProgresses.ExecuteDeleteAsync(ct);
        await ctx.CourseAssignments.ExecuteDeleteAsync(ct);
        await ctx.CourseQuestionOptions.ExecuteDeleteAsync(ct);
        await ctx.CourseQuestions.ExecuteDeleteAsync(ct);
        await ctx.CourseSections.ExecuteDeleteAsync(ct);
        await ctx.Courses.ExecuteDeleteAsync(ct);

        // Chat
        await ctx.ChatReports.ExecuteDeleteAsync(ct);
        await ctx.ChatMessages.ExecuteDeleteAsync(ct);
        await ctx.ChatMembers.ExecuteDeleteAsync(ct);
        await ctx.ChatConversations.ExecuteDeleteAsync(ct);
        await ctx.ChatSuspensions.ExecuteDeleteAsync(ct);

        // Rota and time off
        await ctx.ShiftClaims.ExecuteDeleteAsync(ct);
        await ctx.TimeEntries.ExecuteDeleteAsync(ct);
        await ctx.Shifts.ExecuteDeleteAsync(ct);
        await ctx.ClockInTerminals.ExecuteDeleteAsync(ct);
        await ctx.LocationSchedulingSettings.ExecuteDeleteAsync(ct);
        await ctx.TimeOffRequests.ExecuteDeleteAsync(ct);
        await ctx.TimeOffAllowances.ExecuteDeleteAsync(ct);
        await ctx.TimeOffTypes.ExecuteDeleteAsync(ct);
        await ctx.UserPositions.ExecuteDeleteAsync(ct);
        await ctx.ContractRequirements.ExecuteDeleteAsync(ct);
        await ctx.StaffPositions.ExecuteDeleteAsync(ct);
        await ctx.CompanySchedulingSettings.ExecuteDeleteAsync(ct);

        // Staff documents and onboarding
        await ctx.StaffDocuments.ExecuteDeleteAsync(ct);
        await ctx.StaffDocumentTypes.ExecuteDeleteAsync(ct);
        await ctx.CompanyOnboardingStandingAttachments.ExecuteDeleteAsync(ct);
        await ctx.CompanyOnboardingSettings.ExecuteDeleteAsync(ct);

        await ctx.Announcements.ExecuteDeleteAsync(ct);

        await ctx.DashboardWidgets.ExecuteDeleteAsync(ct);
        await ctx.Dashboards.ExecuteDeleteAsync(ct);

        // Automation
        await ctx.AutomationRuleActionExecutions.ExecuteDeleteAsync(ct);
        await ctx.AutomationRuleExecutions.ExecuteDeleteAsync(ct);
        await ctx.AutomationRuleActions.ExecuteDeleteAsync(ct);
        await ctx.AutomationRules.ExecuteDeleteAsync(ct);

        // DMX, game results, projection, kiosks
        await ctx.DmxSceneStepChannelValues.ExecuteDeleteAsync(ct);
        await ctx.DmxSceneSteps.ExecuteDeleteAsync(ct);
        await ctx.DmxScenes.ExecuteDeleteAsync(ct);
        await ctx.DmxFixtures.ExecuteDeleteAsync(ct);
        await ctx.GameResultPlayerScores.ExecuteDeleteAsync(ct);
        await ctx.GameResults.ExecuteDeleteAsync(ct);
        await ctx.ClientProjectionSettings.ExecuteDeleteAsync(ct);
        await ctx.Set<ProjectionProgramParameterValue>().ExecuteDeleteAsync(ct);
        await ctx.ProjectionProgramSteps.ExecuteDeleteAsync(ct);
        await ctx.ProjectionPrograms.ExecuteDeleteAsync(ct);
        await ctx.ClientAvailableScreens.ExecuteDeleteAsync(ct);
        await ctx.ClientAvailableVideoDevices.ExecuteDeleteAsync(ct);
        await ctx.ClientAvailableDmxDevices.ExecuteDeleteAsync(ct);
        await ctx.ClientAvailableNetworkInterfaces.ExecuteDeleteAsync(ct);
        await ctx.ClientAvailableAudioDevices.ExecuteDeleteAsync(ct);
        await ctx.ZoneScoreboardSettings.ExecuteDeleteAsync(ct);
        await ctx.Clients.ExecuteDeleteAsync(ct);

        // Music
        await ctx.PlaylistSongMembers.ExecuteDeleteAsync(ct);
        await ctx.Playlists.ExecuteDeleteAsync(ct);
        await ctx.Songs.ExecuteDeleteAsync(ct);

        // Files: uploads are blocked in the demo, but anything seeded goes. Folders nest, so they
        // go leaves-first.
        await ctx.FileMetadata.ExecuteDeleteAsync(ct);

        while (await ctx.Folders.AnyAsync(ct))
        {
            await ctx.Folders
                .Where(f => !ctx.Folders.Any(child => child.ParentFolderId == f.Id))
                .ExecuteDeleteAsync(ct);
        }

        await ctx.AppSettings.ExecuteDeleteAsync(ct);
    }

    // The last line of defence against pointing this at a real customer: whatever the caller
    // passed, refuse unless that company is flagged as the demo and isn't Play2Day.
    private static async Task EnsureIsDemoCompanyAsync(ApplicationDbContext ctx, int companyId, CancellationToken ct)
    {
        bool isDemo = await ctx.Companies.AnyAsync(x => x.Id == companyId && x.IsDemo, ct);

        if (!isDemo || companyId == ApplicationDbContext.SeedPlay2DayCompanyId)
        {
            throw new InvalidOperationException($"Refusing to wipe company {companyId}: it isn't the demo company.");
        }
    }
}
