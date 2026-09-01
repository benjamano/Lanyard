using Lanyard.Infrastructure.Models;
using Lanyard.Infrastructure.Models.Dmx;
using Lanyard.Infrastructure.Enum;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Lanyard.Infrastructure.DTO.ZoneScoreboard;

namespace Lanyard.Infrastructure.DataAccess
{
    public class ApplicationDbContext : IdentityDbContext<UserProfile, ApplicationRole, string>, IDataProtectionKeyContext
    {
        public const string SeedAdminUserId = "dev-admin-user";
        public const string SeedAdminRoleId = "dev-role-admin";
        public const string SeedManagerRoleId = "dev-role-manager";
        public const string SeedStaffRoleId = "dev-role-staff";
        public const string SeedCanControlMusicRoleId = "dev-role-can-control-music";
        public const string SeedCanClockInRoleId = "dev-role-can-clock-in";
        public const string SeedCanManageDmxSystemsRoleId = "dev-role-can-manage-dmx-systems";
        public const string SeedCanManageFilesRoleId = "dev-role-can-manage-files";
        public const string SeedCanManageKitchenRoleId = "dev-role-can-manage-kitchen";
        public const string SystemDeletedUserPlaceholderId = "system-deleted-user-placeholder";
        public const int SeedPlay2DayCompanyId = 1;
        public const int SeedIpswichLocationId = 1;
        public const int SeedWisbechLocationId = 2;
        public static readonly DateTime SeedRoleCreateDateUtc = new DateTime(2026, 03, 11, 0, 0, 0, DateTimeKind.Utc);

        public ApplicationDbContext() : base() { }

        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        public DbSet<Song> Songs { get; set; }
        public DbSet<Playlist> Playlists { get; set; }
        public DbSet<PlaylistSongMember> PlaylistSongMembers { get; set; }
        public DbSet<Client> Clients { get; set; }
        public DbSet<ClientProjectionSettings> ClientProjectionSettings { get; set; }
        public DbSet<ProjectionProgram> ProjectionPrograms { get; set; }
        public DbSet<ProjectionProgramStep> ProjectionProgramSteps { get; set; }
        public DbSet<ClientAvailableScreen> ClientAvailableScreens { get; set; }
        public DbSet<ProjectionProgramStepTemplate> ProjectionProgramStepTemplates { get; set; }
        public DbSet<ProjectionProgramStepTemplateParameter> ProjectionProgramStepTemplateParameters { get; set; }
        public DbSet<Dashboard> Dashboards { get; set; }
        public DbSet<DashboardWidget> DashboardWidgets { get; set; }
        public DbSet<FileMetadata> FileMetadata { get; set; }
        public DbSet<Folder> Folders { get; set; }
        public DbSet<AutomationRule> AutomationRules { get; set; }
        public DbSet<AutomationRuleAction> AutomationRuleActions { get; set; }
        public DbSet<AutomationRuleExecution> AutomationRuleExecutions { get; set; }
        public DbSet<AutomationRuleActionExecution> AutomationRuleActionExecutions { get; set; }
        public DbSet<AppSetting> AppSettings { get; set; }
        public DbSet<Company> Companies { get; set; }
        public DbSet<CompanyDomain> CompanyDomains { get; set; }
        public DbSet<Location> Locations { get; set; }
        public DbSet<UserLocationMembership> UserLocationMemberships { get; set; }
        public DbSet<MenuCategory> MenuCategories { get; set; }
        public DbSet<MenuItem> MenuItems { get; set; }
        public DbSet<QrTableToken> QrTableTokens { get; set; }
        public DbSet<KitchenOrder> KitchenOrders { get; set; }
        public DbSet<KitchenOrderItem> KitchenOrderItems { get; set; }
        public DbSet<ClientAvailableDmxDevice> ClientAvailableDmxDevices { get; set; }
        public DbSet<DmxScene> DmxScenes { get; set; }
        public DbSet<DataProtectionKey> DataProtectionKeys { get; set; }
        public DbSet<ZoneScoreboardSettings> ZoneScoreboardSettings { get; set; }
        public DbSet<ClientAvailableNetworkInterface> ClientAvailableNetworkInterfaces { get; set; }
        public DbSet<ClientAvailableVideoDevice> ClientAvailableVideoDevices { get; set; }
        public DbSet<DmxSceneStep> DmxSceneSteps { get; set; }
        public DbSet<DmxSceneStepChannelValue> DmxSceneStepChannelValues { get; set; }
        public DbSet<Course> Courses { get; set; }
        public DbSet<CourseSection> CourseSections { get; set; }
        public DbSet<CourseQuestion> CourseQuestions { get; set; }
        public DbSet<CourseQuestionOption> CourseQuestionOptions { get; set; }
        public DbSet<CourseAssignment> CourseAssignments { get; set; }
        public DbSet<CourseQuizAttempt> CourseQuizAttempts { get; set; }
        public DbSet<CourseQuizAttemptAnswer> CourseQuizAttemptAnswers { get; set; }
        public DbSet<CourseSectionProgress> CourseSectionProgresses { get; set; }
        public DbSet<UserErasureRecord> UserErasureRecords { get; set; }
        public DbSet<GameResult> GameResults { get; set; }
        public DbSet<GameResultPlayerScore> GameResultPlayerScores { get; set; }

        // Connection string used only when the context is created without configured options -
        // i.e. by design-time tooling (dotnet ef migrations/database update). It reads
        // ConnectionStrings__DefaultConnection from the environment and otherwise falls back to
        // the local Docker Postgres from docker-compose.yml. It must never contain a real/remote
        // password; runtime connections are configured via DI in the host's Program.cs instead.
        private const string LocalDesignTimeConnectionString =
            "Host=localhost;Port=5432;Database=lanyarddb;Username=lanyard_dev;Password=lanyard_dev_password";

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            if (!optionsBuilder.IsConfigured)
            {
                string connectionString =
                    Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
                    ?? LocalDesignTimeConnectionString;

                optionsBuilder.UseNpgsql(
                    connectionString,
                    b => b.MigrationsAssembly("Lanyard.Infrastructure"));
            }

            optionsBuilder.ConfigureWarnings(warnings =>
            {
                warnings.Ignore(RelationalEventId.PendingModelChangesWarning);
            });
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<DashboardWidget>()
                .HasDiscriminator(x => x.Type)
                .HasValue<DashboardWidget>(WidgetType.Unknown)
                .HasValue<DigitalClockWidget>(WidgetType.DigitalClock)
                .HasValue<ClientZoneLaserGameStatusWidget>(WidgetType.ClientZoneLaserGameStatus)
                .HasValue<ClientZoneLaserScoreboardWidget>(WidgetType.ClientZoneLaserScoreboard)
                .HasValue<ButtonWidget>(WidgetType.Button)
                .HasValue<TextAreaWidget>(WidgetType.TextArea)
                .HasValue<MusicPlaylistSelectorWidget>(WidgetType.MusicPlaylistSelector)
                .HasValue<MusicTimelineWidget>(WidgetType.MusicTimeline)
                .HasValue<AutomationRuleStatusWidget>(WidgetType.AutomationRuleStatus)
                .HasValue<KioskHealthWidget>(WidgetType.KioskHealth)
                .HasValue<HallOfFameWidget>(WidgetType.HallOfFame)
                .HasValue<MyTrainingWidget>(WidgetType.MyTraining)
                .HasValue<GreetingWidget>(WidgetType.Greeting)
                .HasValue<KitchenOrdersWidget>(WidgetType.KitchenOrders)
                .HasValue<KitchenOrderQueueWidget>(WidgetType.KitchenOrderQueue)
                .HasValue<KitchenStatsWidget>(WidgetType.KitchenStats);

            // Three kitchen widgets share a KitchenLocationId in the TPH table; pin the column
            // names so EF's automatic uniquification cannot rename the existing one out from
            // under rows that are already stored, exactly as the scoreboard widgets do above.
            modelBuilder.Entity<KitchenOrdersWidget>()
                .Property(x => x.KitchenLocationId)
                .HasColumnName("KitchenLocationId");

            modelBuilder.Entity<KitchenOrderQueueWidget>()
                .Property(x => x.KitchenLocationId)
                .HasColumnName("KitchenLocationId");

            modelBuilder.Entity<KitchenStatsWidget>()
                .Property(x => x.KitchenLocationId)
                .HasColumnName("KitchenLocationId");

            // Sibling widget types share a ClientId property in the TPH table; pin the
            // column names so EF's automatic uniquification cannot rename existing columns.
            modelBuilder.Entity<ClientZoneLaserScoreboardWidget>()
                .Property(x => x.ClientId)
                .HasColumnName("ClientId");

            modelBuilder.Entity<ClientZoneLaserGameStatusWidget>()
                .Property(x => x.ClientId)
                .HasColumnName("ClientZoneLaserGameStatusWidget_ClientId");

            modelBuilder.Entity<ButtonWidget>()
                .Property(x => x.ClientId)
                .HasColumnName("ButtonWidget_ClientId");

            modelBuilder.Entity<MusicPlaylistSelectorWidget>()
                .Property(x => x.ClientId)
                .HasColumnName("MusicPlaylistSelectorWidget_ClientId");

            modelBuilder.Entity<MusicTimelineWidget>()
                .Property(x => x.ClientId)
                .HasColumnName("MusicTimelineWidget_ClientId");

            modelBuilder.Entity<HallOfFameWidget>()
                .Property(x => x.ClientId)
                .HasColumnName("HallOfFameWidget_ClientId");

            // A song may be backed by an uploaded file. When that file row is hard-deleted,
            // null the link rather than cascade-deleting the song (it is soft-deleted instead).
            modelBuilder.Entity<Song>()
                .HasOne(s => s.FileMetadata)
                .WithMany()
                .HasForeignKey(s => s.FileMetadataId)
                .OnDelete(DeleteBehavior.SetNull);

            // RecordSectionTransitionAsync finds-or-creates by (AssignmentId, SectionId) -
            // this backstops that against a double-fired write creating a duplicate row.
            modelBuilder.Entity<CourseSectionProgress>()
                .HasIndex(x => new { x.AssignmentId, x.SectionId })
                .IsUnique();

            modelBuilder.Entity<Location>()
                .HasIndex(x => new { x.CompanyId, x.Name })
                .IsUnique();

            modelBuilder.Entity<UserLocationMembership>()
                .HasIndex(x => new { x.UserId, x.LocationId })
                .IsUnique();

            // A company may point at an uploaded file as its logo. When that file row is
            // hard-deleted, null the link rather than cascade-deleting the company (companies
            // are soft-deleted via IsActive instead).
            modelBuilder.Entity<Company>()
                .HasOne(x => x.LogoFile)
                .WithMany()
                .HasForeignKey(x => x.LogoFileId)
                .OnDelete(DeleteBehavior.SetNull);

            // Same reasoning as LogoFile above, for the optional login background image.
            modelBuilder.Entity<Company>()
                .HasOne(x => x.BackgroundImageFile)
                .WithMany()
                .HasForeignKey(x => x.BackgroundImageFileId)
                .OnDelete(DeleteBehavior.SetNull);

            // Hostname is the lookup key for every single public request, including static
            // assets, so it is indexed and unique. Unique across all companies, not per company:
            // one hostname can only ever mean one tenant, and letting two rows claim the same
            // host would make which tenant a customer sees depend on row order.
            modelBuilder.Entity<CompanyDomain>()
                .HasIndex(x => x.Hostname)
                .IsUnique();

            modelBuilder.Entity<CompanyDomain>()
                .HasOne(x => x.Company)
                .WithMany(x => x.Domains)
                .HasForeignKey(x => x.CompanyId)
                .OnDelete(DeleteBehavior.Cascade);

            // Slug is the pre-DNS fallback route, so it has to be unambiguous the same way a
            // hostname does. Nullable, hence a filtered index - most companies never set one.
            modelBuilder.Entity<Company>()
                .HasIndex(x => x.Slug)
                .IsUnique()
                .HasFilter("\"Slug\" IS NOT NULL");

            modelBuilder.Entity<MenuCategory>()
                .HasOne(x => x.Location)
                .WithMany()
                .HasForeignKey(x => x.LocationId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<MenuItem>()
                .HasOne(x => x.Category)
                .WithMany(x => x.Items)
                .HasForeignKey(x => x.CategoryId)
                .OnDelete(DeleteBehavior.Cascade);

            // Same reasoning as Company.LogoFile: a menu photo being hard-deleted should leave
            // the item on the menu without a picture, not remove the item from the menu.
            modelBuilder.Entity<MenuItem>()
                .HasOne(x => x.ImageFile)
                .WithMany()
                .HasForeignKey(x => x.ImageFileId)
                .OnDelete(DeleteBehavior.SetNull);

            // Resolved on every scan of a printed code, so indexed; unique because the token is
            // the only thing identifying which table an order came from.
            modelBuilder.Entity<QrTableToken>()
                .HasIndex(x => x.Token)
                .IsUnique();

            modelBuilder.Entity<QrTableToken>()
                .HasOne(x => x.Location)
                .WithMany()
                .HasForeignKey(x => x.LocationId)
                .OnDelete(DeleteBehavior.Cascade);

            // The customer's status poll hits this on a timer for the life of every open order.
            modelBuilder.Entity<KitchenOrder>()
                .HasIndex(x => x.OrderToken)
                .IsUnique();

            // Payment webhooks arrive keyed by PaymentIntent and have to find their order fast.
            // Filtered because most rows have none until checkout starts, and unique because two
            // orders sharing a PaymentIntent would mean one payment marking both as paid.
            modelBuilder.Entity<KitchenOrder>()
                .HasIndex(x => x.PaymentIntentId)
                .IsUnique()
                .HasFilter("\"PaymentIntentId\" IS NOT NULL");

            // The kitchen display's query shape: open tickets for one venue, oldest first.
            modelBuilder.Entity<KitchenOrder>()
                .HasIndex(x => new { x.LocationId, x.Status, x.CreateDate });

            // Retiring a table must not delete the orders taken at it - the label was snapshotted
            // onto the order precisely so the history survives without the table row.
            modelBuilder.Entity<KitchenOrder>()
                .HasOne(x => x.QrTableToken)
                .WithMany()
                .HasForeignKey(x => x.QrTableTokenId)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<KitchenOrderItem>()
                .HasOne(x => x.Order)
                .WithMany(x => x.Items)
                .HasForeignKey(x => x.OrderId)
                .OnDelete(DeleteBehavior.Cascade);

            // Likewise: deleting a menu item must not erase the orders that contained it. The
            // name and price on the line are snapshots, so the line still reads correctly.
            modelBuilder.Entity<KitchenOrderItem>()
                .HasOne(x => x.MenuItem)
                .WithMany()
                .HasForeignKey(x => x.MenuItemId)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<UserErasureRecord>()
                .HasIndex(x => x.ErasedAtUtc);

            // The Hall of Fame always queries a time window, either venue-wide or for one kiosk.
            // Both indexes exist because those are the two shapes GameResultService issues; the
            // automation execution log next door is the cautionary example of a time-queried
            // append-only table with no index on its timestamp.
            modelBuilder.Entity<GameResult>()
                .HasIndex(x => x.PlayedAtUtc);

            modelBuilder.Entity<GameResult>()
                .HasIndex(x => new { x.ClientId, x.PlayedAtUtc });
        }
    }
}
