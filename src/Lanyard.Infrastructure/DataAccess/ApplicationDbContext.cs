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
        public const string SeedCanPostAnnouncementsRoleId = "dev-role-can-post-announcements";
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
        public DbSet<Location> Locations { get; set; }
        public DbSet<UserLocationMembership> UserLocationMemberships { get; set; }
        public DbSet<ClientAvailableDmxDevice> ClientAvailableDmxDevices { get; set; }
        public DbSet<DmxScene> DmxScenes { get; set; }
        public DbSet<DataProtectionKey> DataProtectionKeys { get; set; }
        public DbSet<ZoneScoreboardSettings> ZoneScoreboardSettings { get; set; }
        public DbSet<ClientAvailableNetworkInterface> ClientAvailableNetworkInterfaces { get; set; }
        public DbSet<ClientAvailableVideoDevice> ClientAvailableVideoDevices { get; set; }
        public DbSet<DmxSceneStep> DmxSceneSteps { get; set; }
        public DbSet<DmxSceneStepChannelValue> DmxSceneStepChannelValues { get; set; }
        public DbSet<DmxFixture> DmxFixtures { get; set; }
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
        public DbSet<Announcement> Announcements { get; set; }
        public DbSet<StaffDocumentType> StaffDocumentTypes { get; set; }
        public DbSet<StaffDocument> StaffDocuments { get; set; }
        public DbSet<CompanyOnboardingSettings> CompanyOnboardingSettings { get; set; }
        public DbSet<CompanyOnboardingStandingAttachment> CompanyOnboardingStandingAttachments { get; set; }
        public DbSet<StaffPosition> StaffPositions { get; set; }
        public DbSet<UserPosition> UserPositions { get; set; }
        public DbSet<ContractRequirement> ContractRequirements { get; set; }
        public DbSet<UserClockInPin> UserClockInPins { get; set; }
        public DbSet<CompanySchedulingSettings> CompanySchedulingSettings { get; set; }
        public DbSet<Shift> Shifts { get; set; }
        public DbSet<ClockInTerminal> ClockInTerminals { get; set; }
        public DbSet<TimeEntry> TimeEntries { get; set; }
        public DbSet<TimeOffType> TimeOffTypes { get; set; }
        public DbSet<TimeOffAllowance> TimeOffAllowances { get; set; }
        public DbSet<TimeOffRequest> TimeOffRequests { get; set; }

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
                .HasValue<AnnouncementsWidget>(WidgetType.Announcements)
                .HasValue<ProjectionStatusWidget>(WidgetType.ProjectionStatus);

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

            // Same hazard as ClientId above, but for MaxItems: MyTrainingWidget got there first
            // and owns the plain column name, so pin both. Left unpinned, EF uniquifies them and
            // the generated migration renames the existing column out from under stored data.
            modelBuilder.Entity<MyTrainingWidget>()
                .Property(x => x.MaxItems)
                .HasColumnName("MaxItems");

            modelBuilder.Entity<AnnouncementsWidget>()
                .Property(x => x.MaxItems)
                .HasColumnName("AnnouncementsWidget_MaxItems");

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

            modelBuilder.Entity<UserErasureRecord>()
                .HasIndex(x => x.ErasedAtUtc);

            // The Hall of Fame always queries a time window, either venue-wide or for one kiosk.
            // Both indexes exist because those are the two shapes GameResultService issues.
            modelBuilder.Entity<GameResult>()
                .HasIndex(x => x.PlayedAtUtc);

            modelBuilder.Entity<GameResult>()
                .HasIndex(x => new { x.ClientId, x.PlayedAtUtc });

            // Mirrors Location's (CompanyId, Name) uniqueness below - a company shouldn't end up
            // with two document types of the same name via a race in the admin catalog UI.
            modelBuilder.Entity<StaffDocumentType>()
                .HasIndex(x => new { x.CompanyId, x.Name })
                .IsUnique();

            // One company-wide row (LocationId IS NULL) plus at most one row per location
            // override. A single unique index on (CompanyId, LocationId) can't express this -
            // Postgres treats every NULL as distinct, so it would happily allow several
            // company-wide rows for the same company. Two partial indexes instead: one enforces
            // "at most one company-wide row per company", the other "at most one row per
            // CompanyId+LocationId pair" for the location-specific rows.
            modelBuilder.Entity<CompanyOnboardingSettings>()
                .HasIndex(x => x.CompanyId)
                .IsUnique()
                .HasFilter("\"LocationId\" IS NULL");

            modelBuilder.Entity<CompanyOnboardingSettings>()
                .HasIndex(x => new { x.CompanyId, x.LocationId })
                .IsUnique()
                .HasFilter("\"LocationId\" IS NOT NULL");

            // FileMetadata is shared across unrelated features (folders, logos, video devices,
            // staff documents, onboarding attachments). The default convention-based FK behavior
            // for a required reference is Cascade, which would let deleting a file from the
            // general File Manager silently take a StaffDocument or a company's onboarding
            // attachment down with it. Restrict instead: FileService.DeleteFileAsync surfaces a
            // clear "file is in use" failure rather than an invisible cross-feature side effect.
            modelBuilder.Entity<StaffDocument>()
                .HasOne(x => x.FileMetadata)
                .WithMany()
                .HasForeignKey(x => x.FileMetadataId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<CompanyOnboardingStandingAttachment>()
                .HasOne(x => x.FileMetadata)
                .WithMany()
                .HasForeignKey(x => x.FileMetadataId)
                .OnDelete(DeleteBehavior.Restrict);

            // Staff scheduling. Same (CompanyId, Name) uniqueness as StaffDocumentType.
            modelBuilder.Entity<StaffPosition>()
                .HasIndex(x => new { x.CompanyId, x.Name })
                .IsUnique();

            modelBuilder.Entity<UserPosition>()
                .HasIndex(x => new { x.UserId, x.StaffPositionId })
                .IsUnique();

            // "Exactly one primary position per user" is what the tier resolution relies on;
            // enforce it in the database rather than trusting every writer to keep it.
            modelBuilder.Entity<UserPosition>()
                .HasIndex(x => x.UserId)
                .IsUnique()
                .HasFilter("\"IsPrimary\"");

            // ContractRequirement holds three tiers in one table, distinguished by which of
            // StaffPositionId / UserId is set. As with CompanyOnboardingSettings, Postgres
            // treats every NULL as distinct so one composite unique index can't express "one
            // company-default row per company" - three partial indexes, one per tier, do.
            modelBuilder.Entity<ContractRequirement>()
                .HasIndex(x => x.CompanyId)
                .IsUnique()
                .HasFilter("\"StaffPositionId\" IS NULL AND \"UserId\" IS NULL");

            modelBuilder.Entity<ContractRequirement>()
                .HasIndex(x => x.StaffPositionId)
                .IsUnique()
                .HasFilter("\"StaffPositionId\" IS NOT NULL");

            modelBuilder.Entity<ContractRequirement>()
                .HasIndex(x => new { x.CompanyId, x.UserId })
                .IsUnique()
                .HasFilter("\"UserId\" IS NOT NULL");

            modelBuilder.Entity<UserClockInPin>()
                .HasIndex(x => x.UserId)
                .IsUnique();

            modelBuilder.Entity<CompanySchedulingSettings>()
                .HasIndex(x => x.CompanyId)
                .IsUnique();

            // The rota builder reads a location's date window; overlap checks and My Shifts read
            // one person's date window.
            modelBuilder.Entity<Shift>()
                .HasIndex(x => new { x.LocationId, x.StartUtc });

            modelBuilder.Entity<Shift>()
                .HasIndex(x => new { x.UserId, x.StartUtc });

            // Restrict, not the default Cascade: shift history is kept for 6 years
            // (docs/DATA_RETENTION.md), so deleting a user must never silently take it with them.
            // Both delete paths (SecurityService.DeleteUserAsync, GdprService) re-point a user's
            // shifts to the placeholder account first via ScheduleRetention.
            modelBuilder.Entity<Shift>()
                .HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<Shift>()
                .HasOne(x => x.StaffPosition)
                .WithMany()
                .HasForeignKey(x => x.StaffPositionId)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<Shift>()
                .HasOne(x => x.Location)
                .WithMany()
                .HasForeignKey(x => x.LocationId)
                .OnDelete(DeleteBehavior.Restrict);

            // Terminals are looked up by the hash of the device's cookie token on every page load.
            modelBuilder.Entity<ClockInTerminal>()
                .HasIndex(x => x.DeviceTokenHash)
                .IsUnique();

            modelBuilder.Entity<ClockInTerminal>()
                .HasIndex(x => x.LocationId);

            modelBuilder.Entity<ClockInTerminal>()
                .HasOne(x => x.Location)
                .WithMany()
                .HasForeignKey(x => x.LocationId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<TimeEntry>()
                .HasIndex(x => new { x.UserId, x.ClockInUtc });

            modelBuilder.Entity<TimeEntry>()
                .HasIndex(x => new { x.LocationId, x.ClockInUtc });

            // At most one open entry per person - the database-level guard against two terminals
            // (or a terminal and a phone) clocking the same person in at the same moment.
            modelBuilder.Entity<TimeEntry>()
                .HasIndex(x => x.UserId)
                .IsUnique()
                .HasFilter("\"ClockOutUtc\" IS NULL AND \"IsActive\"");

            // Same retention reasoning as Shift.UserId: timesheets outlive accounts.
            modelBuilder.Entity<TimeEntry>()
                .HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<TimeEntry>()
                .HasOne(x => x.Location)
                .WithMany()
                .HasForeignKey(x => x.LocationId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<TimeEntry>()
                .HasOne(x => x.Shift)
                .WithMany()
                .HasForeignKey(x => x.ShiftId)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<TimeEntry>()
                .HasOne(x => x.ClockInTerminal)
                .WithMany()
                .HasForeignKey(x => x.ClockInTerminalId)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<TimeOffType>()
                .HasIndex(x => new { x.CompanyId, x.Name })
                .IsUnique();

            // Same three-tier shape as ContractRequirement, per type: one company default, one
            // row per position and one per person, each enforced by its own partial index.
            modelBuilder.Entity<TimeOffAllowance>()
                .HasIndex(x => x.TimeOffTypeId)
                .IsUnique()
                .HasFilter("\"StaffPositionId\" IS NULL AND \"UserId\" IS NULL");

            modelBuilder.Entity<TimeOffAllowance>()
                .HasIndex(x => new { x.TimeOffTypeId, x.StaffPositionId })
                .IsUnique()
                .HasFilter("\"StaffPositionId\" IS NOT NULL");

            modelBuilder.Entity<TimeOffAllowance>()
                .HasIndex(x => new { x.TimeOffTypeId, x.UserId })
                .IsUnique()
                .HasFilter("\"UserId\" IS NOT NULL");

            modelBuilder.Entity<TimeOffAllowance>()
                .HasOne(x => x.TimeOffType)
                .WithMany()
                .HasForeignKey(x => x.TimeOffTypeId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<TimeOffAllowance>()
                .HasOne(x => x.StaffPosition)
                .WithMany()
                .HasForeignKey(x => x.StaffPositionId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<TimeOffAllowance>()
                .HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // My Time Off and the balance read one person's requests by date; the manager list
            // reads a location's requests by status.
            modelBuilder.Entity<TimeOffRequest>()
                .HasIndex(x => new { x.UserId, x.StartDate });

            modelBuilder.Entity<TimeOffRequest>()
                .HasIndex(x => new { x.LocationId, x.Status });

            // Cascade, unlike shifts and time entries: a leave request is the person's own record,
            // not payroll history (hours actually worked live in TimeEntries), so it goes when the
            // account does. docs/DATA_RETENTION.md says so.
            modelBuilder.Entity<TimeOffRequest>()
                .HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // Types are archived, never deleted, so a request can always name its type.
            modelBuilder.Entity<TimeOffRequest>()
                .HasOne(x => x.TimeOffType)
                .WithMany()
                .HasForeignKey(x => x.TimeOffTypeId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<TimeOffRequest>()
                .HasOne(x => x.Location)
                .WithMany()
                .HasForeignKey(x => x.LocationId)
                .OnDelete(DeleteBehavior.Restrict);

            // CourseAssignments is filtered by UserId on every home-page load (outstanding
            // training card), the MyTraining widget/page and every bulk-assign duplicate check,
            // and it grows by users x courses x recurrence cycles. Only the CourseId and
            // LocationId foreign keys had indexes.
            modelBuilder.Entity<CourseAssignment>()
                .HasIndex(x => x.UserId);

            // The automation execution log is append-only (one row per rule fire, including
            // retries) and is always read newest-first: the Automation page's recent list and
            // the rule-status widget's "latest execution for this rule". Without these the first
            // is a full sort of the table and the second a scan of the rule's rows.
            // AutomationExecutionRetentionHostedService keeps the table bounded.
            modelBuilder.Entity<AutomationRuleExecution>()
                .HasIndex(x => x.ExecutedAt);

            modelBuilder.Entity<AutomationRuleExecution>()
                .HasIndex(x => new { x.AutomationRuleId, x.ExecutedAt });

            // SongAnalysisHostedService sweeps for NotAnalyzed songs every five minutes.
            modelBuilder.Entity<Song>()
                .HasIndex(x => x.BpmAnalysisStatus);
        }
    }
}
