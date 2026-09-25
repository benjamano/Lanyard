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
        public DbSet<UserPushSubscription> PushSubscriptions { get; set; }
        public DbSet<NotificationPreference> NotificationPreferences { get; set; }
        public DbSet<AppInstallation> AppInstallations { get; set; }
        public DbSet<ShiftClaim> ShiftClaims { get; set; }
        public DbSet<LocationSchedulingSettings> LocationSchedulingSettings { get; set; }
        public DbSet<ChatConversation> ChatConversations { get; set; }
        public DbSet<ChatMember> ChatMembers { get; set; }
        public DbSet<ChatMessage> ChatMessages { get; set; }
        public DbSet<ChatBlock> ChatBlocks { get; set; }
        public DbSet<ChatReport> ChatReports { get; set; }
        public DbSet<ChatSuspension> ChatSuspensions { get; set; }

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
                .HasValue<ProjectionStatusWidget>(WidgetType.ProjectionStatus)
                .HasValue<MyShiftsWidget>(WidgetType.MyShifts)
                .HasValue<WhoIsOnTodayWidget>(WidgetType.WhoIsOnToday)
                .HasValue<PendingTimeOffWidget>(WidgetType.PendingTimeOff);

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

            // The scheduling widgets share names with siblings too (MaxItems above; LocationId is
            // new but pinned up front for the same reason), so each gets its own column.
            modelBuilder.Entity<MyShiftsWidget>()
                .Property(x => x.MaxItems)
                .HasColumnName("MyShiftsWidget_MaxItems");

            modelBuilder.Entity<PendingTimeOffWidget>()
                .Property(x => x.MaxItems)
                .HasColumnName("PendingTimeOffWidget_MaxItems");

            modelBuilder.Entity<WhoIsOnTodayWidget>()
                .Property(x => x.LocationId)
                .HasColumnName("WhoIsOnTodayWidget_LocationId");

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
            // Both indexes exist because those are the two shapes GameResultService issues; the
            // automation execution log next door is the cautionary example of a time-queried
            // append-only table with no index on its timestamp.
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

            // Notification rows exist only for the person they belong to, so they go with the
            // account (docs/DATA_RETENTION.md).
            modelBuilder.Entity<UserPushSubscription>()
                .HasIndex(x => x.Endpoint)
                .IsUnique();

            modelBuilder.Entity<UserPushSubscription>()
                .HasIndex(x => x.UserId);

            modelBuilder.Entity<UserPushSubscription>()
                .HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<NotificationPreference>()
                .HasIndex(x => new { x.UserId, x.Topic })
                .IsUnique();

            modelBuilder.Entity<NotificationPreference>()
                .HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<AppInstallation>()
                .HasIndex(x => new { x.UserId, x.DeviceId })
                .IsUnique();

            modelBuilder.Entity<AppInstallation>()
                .HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // Claims are rota history, kept and anonymised with the shifts (ScheduleRetention), so
            // nothing cascades from the user.
            modelBuilder.Entity<ShiftClaim>()
                .HasIndex(x => new { x.ShiftId, x.Status });

            modelBuilder.Entity<ShiftClaim>()
                .HasIndex(x => new { x.UserId, x.Status });

            modelBuilder.Entity<ShiftClaim>()
                .HasOne(x => x.Shift)
                .WithMany()
                .HasForeignKey(x => x.ShiftId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<ShiftClaim>()
                .HasOne(x => x.OfferedShift)
                .WithMany()
                .HasForeignKey(x => x.OfferedShiftId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<ShiftClaim>()
                .HasOne(x => x.ParentClaim)
                .WithMany()
                .HasForeignKey(x => x.ParentClaimId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<ShiftClaim>()
                .HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<LocationSchedulingSettings>()
                .HasIndex(x => x.LocationId)
                .IsUnique();

            // ---- Chat ----
            // One direct conversation per pair of people.
            modelBuilder.Entity<ChatConversation>()
                .HasIndex(x => x.DirectKey)
                .IsUnique()
                .HasFilter("\"DirectKey\" IS NOT NULL");

            modelBuilder.Entity<ChatConversation>()
                .HasIndex(x => new { x.CompanyId, x.LastMessageUtc });

            modelBuilder.Entity<ChatConversation>()
                .HasOne(x => x.Company)
                .WithMany()
                .HasForeignKey(x => x.CompanyId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<ChatMember>()
                .HasIndex(x => new { x.ConversationId, x.UserId })
                .IsUnique();

            modelBuilder.Entity<ChatMember>()
                .HasIndex(x => x.UserId);

            modelBuilder.Entity<ChatMember>()
                .HasOne(x => x.Conversation)
                .WithMany()
                .HasForeignKey(x => x.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);

            // A deleted person simply stops being a member. What they wrote is kept, re-pointed to
            // the placeholder account (ChatRetention), which is why the message and report user
            // keys below are Restrict: that step can't be skipped silently.
            modelBuilder.Entity<ChatMember>()
                .HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<ChatMessage>()
                .HasIndex(x => new { x.ConversationId, x.CreateUtc });

            modelBuilder.Entity<ChatMessage>()
                .HasOne(x => x.Conversation)
                .WithMany()
                .HasForeignKey(x => x.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<ChatMessage>()
                .HasOne(x => x.Author)
                .WithMany()
                .HasForeignKey(x => x.AuthorUserId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<ChatMessage>()
                .HasOne(x => x.ReplyTo)
                .WithMany()
                .HasForeignKey(x => x.ReplyToMessageId)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<ChatBlock>()
                .HasIndex(x => new { x.BlockerUserId, x.BlockedUserId })
                .IsUnique();

            modelBuilder.Entity<ChatBlock>()
                .HasOne(x => x.Blocker)
                .WithMany()
                .HasForeignKey(x => x.BlockerUserId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<ChatBlock>()
                .HasOne(x => x.Blocked)
                .WithMany()
                .HasForeignKey(x => x.BlockedUserId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<ChatReport>()
                .HasIndex(x => new { x.LocationId, x.Status });

            // The snapshot outlives the message: removing a message keeps its report.
            modelBuilder.Entity<ChatReport>()
                .HasOne(x => x.Message)
                .WithMany()
                .HasForeignKey(x => x.MessageId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<ChatReport>()
                .HasOne(x => x.Reporter)
                .WithMany()
                .HasForeignKey(x => x.ReporterUserId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<ChatReport>()
                .HasOne(x => x.Reported)
                .WithMany()
                .HasForeignKey(x => x.ReportedUserId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<ChatReport>()
                .HasOne(x => x.Location)
                .WithMany()
                .HasForeignKey(x => x.LocationId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<ChatSuspension>()
                .HasIndex(x => new { x.UserId, x.CompanyId });

            modelBuilder.Entity<ChatSuspension>()
                .HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
