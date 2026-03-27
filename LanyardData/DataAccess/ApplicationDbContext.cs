using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Lanyard.Infrastructure.DataAccess
{
    public class ApplicationDbContext
        : IdentityDbContext<UserProfile, ApplicationRole, string>
    {
        public const string SeedAdminUserId = "dev-admin-user";
        public const string SeedAdminRoleId = "dev-role-admin";
        public const string SeedManagerRoleId = "dev-role-manager";
        public const string SeedStaffRoleId = "dev-role-staff";
        public const string SeedCanControlMusicRoleId = "dev-role-can-control-music";
        public const string SeedCanClockInRoleId = "dev-role-can-clock-in";
        public const string SeedAdminPasswordHash = "AQAAAAIAAYagAAAAEJ1AhlJOAablYfFpSBJmkOkqLkqidbamfdrRwkTGjXCnkD30AqM6PNAcAh96mQgYXg==";
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

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            if (!optionsBuilder.IsConfigured)
            {
                optionsBuilder.UseNpgsql(
                    "Host=100.67.245.90;Port=5432;Database=lanyarddb;Username=psqluser;Password=nFGDcVxzHbn7HsrrcReJtBY",
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
            modelBuilder.Entity<AutomationRule>()
                .HasIndex(r => r.TriggerClientId);
        }
    }
}
