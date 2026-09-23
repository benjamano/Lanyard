using System.Text.Json;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.VisualTestFixtures;

/// <summary>
/// A fresh CI Postgres only gets DatabaseSeeder's baseline (roles + admin user) - there is no
/// dashboard, music library, or training course anywhere, so the "populated" screens the visual
/// regression suite screenshots have nothing to render without fixture data. This seeds exactly
/// what those screens need, with fixed ids/content so every run (CI or local baseline regen)
/// produces byte-identical seed data, then writes those ids to a JSON file the TypeScript test
/// suite reads to build URLs (e.g. /training/{assignmentId}) - a generated file, not duplicated
/// literals, so the two languages can never drift out of sync.
///
/// The fixed guids below are plain test data, chosen not to collide with anything
/// DatabaseSeeder/ApplicationDbContext inserts (those use string Identity ids and a seeded
/// company/location - a different key space entirely).
///
/// Must run after the server process has finished its own startup seeding (Program.cs ->
/// DatabaseSeeder.SeedAsync) - the CI workflow and local dev script both launch the server and
/// wait for its port to accept connections before invoking this, so there is no race writing to
/// the same database.
/// </summary>
public static class Program
{
    private static readonly Guid DashboardId = Guid.Parse("00000000-0000-0000-0000-000000000101");
    private static readonly Guid DashboardWidgetId = Guid.Parse("00000000-0000-0000-0000-000000000102");

    private static readonly Guid PlaylistId = Guid.Parse("00000000-0000-0000-0000-000000000201");
    private static readonly Guid Song1Id = Guid.Parse("00000000-0000-0000-0000-000000000202");
    private static readonly Guid Song2Id = Guid.Parse("00000000-0000-0000-0000-000000000203");

    private static readonly Guid CourseId = Guid.Parse("00000000-0000-0000-0000-000000000301");
    private static readonly Guid CourseSectionId = Guid.Parse("00000000-0000-0000-0000-000000000302");
    private static readonly Guid CourseAssignmentId = Guid.Parse("00000000-0000-0000-0000-000000000303");

    private static readonly Guid DmxClientId = Guid.Parse("00000000-0000-0000-0000-000000000401");

    public static async Task<int> Main(string[] args)
    {
        string connectionString =
            Environment.GetEnvironmentVariable("VISUAL_TESTS_CONNECTION_STRING")
            ?? "Host=localhost;Port=5432;Database=lanyarddb;Username=lanyard_dev;Password=lanyard_dev_password";

        string outputPath = args.Length > 0 ? args[0] : "fixture-ids.generated.json";

        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        await using (ApplicationDbContext context = new(options))
        {
            await SeedAsync(context);
        }

        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(new
        {
            dashboardId = DashboardId,
            playlistId = PlaylistId,
            courseAssignmentId = CourseAssignmentId,
            dmxClientId = DmxClientId,
        }));

        Console.WriteLine($"Visual test fixtures seeded. Ids written to {outputPath}.");
        return 0;
    }

    private static async Task SeedAsync(ApplicationDbContext context)
    {
        if (!await context.Dashboards.AnyAsync(d => d.Id == DashboardId))
        {
            context.Dashboards.Add(new Dashboard
            {
                Id = DashboardId,
                Name = "Visual Test Dashboard",
                Description = "Seeded fixture for the visual regression suite - do not edit by hand.",
                IsActive = true,
                CreateDate = DateTime.UtcNow,
            });

            // TextAreaWidget specifically: plain static content, no timers/clocks/live data, so
            // it's the one widget type safe to pixel-diff without masking. See the
            // visual-regression-testing skill for why DigitalClock/Greeting/etc. are avoided here.
            context.DashboardWidgets.Add(new TextAreaWidget
            {
                Id = DashboardWidgetId,
                DashboardId = DashboardId,
                Title = "Notes",
                Content = "This widget's content is fixed by the visual regression fixture seeder.",
                GridX = 0,
                GridY = 0,
            });
        }

        if (!await context.Playlists.AnyAsync(p => p.Id == PlaylistId))
        {
            context.Playlists.Add(new Playlist
            {
                Id = PlaylistId,
                Name = "Visual Test Playlist",
                CreateDate = DateTime.UtcNow,
            });

            context.Songs.AddRange(
                new Song
                {
                    Id = Song1Id,
                    Name = "Visual Test Song One",
                    AlbumName = "Visual Test Album",
                    FilePath = "visual-test-song-one.mp3",
                    DurationSeconds = 180,
                    IsActive = true,
                    CreateDate = DateTime.UtcNow,
                },
                new Song
                {
                    Id = Song2Id,
                    Name = "Visual Test Song Two",
                    AlbumName = "Visual Test Album",
                    FilePath = "visual-test-song-two.mp3",
                    DurationSeconds = 210,
                    IsActive = true,
                    CreateDate = DateTime.UtcNow,
                });

            context.PlaylistSongMembers.AddRange(
                new PlaylistSongMember { SongId = Song1Id, PlaylistId = PlaylistId, CreateDate = DateTime.UtcNow },
                new PlaylistSongMember { SongId = Song2Id, PlaylistId = PlaylistId, CreateDate = DateTime.UtcNow });
        }

        if (!await context.Courses.AnyAsync(c => c.Id == CourseId))
        {
            context.Courses.Add(new Course
            {
                Id = CourseId,
                Name = "Visual Test Course",
                IsActive = true,
            });

            context.CourseSections.Add(new CourseSection
            {
                Id = CourseSectionId,
                CourseId = CourseId,
                Title = "Getting Started",
                BodyHtml = "<p>This section's content is fixed by the visual regression fixture seeder.</p>",
                SortOrder = 0,
                IsActive = true,
            });

            // UserId must be the logged-in user's own id - CourseAssignmentService hard-checks
            // assignment ownership - so this only works for the seeded dev admin.
            context.CourseAssignments.Add(new CourseAssignment
            {
                Id = CourseAssignmentId,
                CourseId = CourseId,
                UserId = ApplicationDbContext.SeedAdminUserId,
                AssignedDate = DateTime.UtcNow,
                IsActive = true,
            });
        }

        // The DMX desk's ClientSelector dropdown is populated from this table, but "connected"
        // state comes from an in-memory SignalR set the Windows-only kiosk client would populate -
        // there is no DB-only way to simulate that on Linux CI. This fixture only gives the
        // dropdown a real option to show; the DMX desk test screenshots the page's default
        // no-client-selected state. See the visual-regression-testing skill.
        if (!await context.Clients.AnyAsync(c => c.Id == DmxClientId))
        {
            context.Clients.Add(new Client
            {
                Id = DmxClientId,
                Name = "Visual Test Client",
                CreateDate = DateTime.UtcNow,
            });
        }

        await context.SaveChangesAsync();
    }
}
