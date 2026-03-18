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
        private const string SeedAdminUserId = "dev-admin-user";
        private const string SeedAdminRoleId = "dev-role-admin";
        private const string SeedManagerRoleId = "dev-role-manager";
        private const string SeedStaffRoleId = "dev-role-staff";
        private const string SeedCanControlMusicRoleId = "dev-role-can-control-music";
        private const string SeedCanClockInRoleId = "dev-role-can-clock-in";
        private const string SeedAdminPasswordHash = "AQAAAAIAAYagAAAAEJ1AhlJOAablYfFpSBJmkOkqLkqidbamfdrRwkTGjXCnkD30AqM6PNAcAh96mQgYXg==";
        private static readonly DateTime SeedRoleCreateDateUtc = new DateTime(2026, 03, 11, 0, 0, 0, DateTimeKind.Utc);

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

            modelBuilder.Entity<ApplicationRole>(role =>
            {
                role.HasKey(r => r.Id);

                role.HasOne(r => r.CreatedByUser)
                    .WithMany()
                    .HasForeignKey(r => r.CreatedByUserId)
                    .IsRequired();
            });

            modelBuilder.Entity<FileMetadata>(entity =>
            {
                entity.HasKey(f => f.Id);
                entity.HasIndex(f => f.FileName);
                entity.HasIndex(f => f.FolderId);

                entity.HasOne(f => f.Folder)
                    .WithMany(folder => folder.Files)
                    .HasForeignKey(f => f.FolderId)
                    .OnDelete(DeleteBehavior.SetNull);
            });

            modelBuilder.Entity<Folder>(entity =>
            {
                entity.HasKey(f => f.Id);
                entity.HasIndex(f => f.ParentFolderId);
                entity.HasIndex(f => f.Name);

                entity.HasOne(f => f.ParentFolder)
                    .WithMany(parent => parent.SubFolders)
                    .HasForeignKey(f => f.ParentFolderId)
                    .OnDelete(DeleteBehavior.Restrict);
            });

            modelBuilder.Entity<Dashboard>(entity =>
            {
                entity.HasKey(x => x.Id);
                entity.HasIndex(x => new { x.IsActive, x.Name });
            });

            modelBuilder.Entity<DashboardWidget>(entity =>
            {
                entity.HasKey(x => x.Id);
                entity.HasIndex(x => new { x.DashboardId, x.IsActive, x.SortOrder });

                entity.HasOne(x => x.Dashboard)
                    .WithMany(x => x.Widgets)
                    .HasForeignKey(x => x.DashboardId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            UserProfile seedAdminUser = new UserProfile
            {
                Id = SeedAdminUserId,
                UserName = "admin",
                NormalizedUserName = "ADMIN",
                Email = "admin@play2day.com",
                NormalizedEmail = "ADMIN@PLAY2DAY.COM",
                EmailConfirmed = true,
                FirstName = "System",
                LastName = "Administrator",
                PasswordHash = SeedAdminPasswordHash,
                SecurityStamp = "SEED-ADMIN-SECURITY-STAMP",
                ConcurrencyStamp = "SEED-ADMIN-CONCURRENCY-STAMP"
            };

            modelBuilder.Entity<UserProfile>().HasData(seedAdminUser);

            modelBuilder.Entity<ApplicationRole>().HasData(
                new ApplicationRole
                {
                    Id = SeedAdminRoleId,
                    Name = "Admin",
                    NormalizedName = "ADMIN",
                    ConcurrencyStamp = "SEED-ROLE-ADMIN-CS",
                    CreatedByUserId = SeedAdminUserId,
                    CreateDate = SeedRoleCreateDateUtc,
                    IsActive = true
                },
                new ApplicationRole
                {
                    Id = SeedManagerRoleId,
                    Name = "Manager",
                    NormalizedName = "MANAGER",
                    ConcurrencyStamp = "SEED-ROLE-MANAGER-CS",
                    CreatedByUserId = SeedAdminUserId,
                    CreateDate = SeedRoleCreateDateUtc,
                    IsActive = true
                },
                new ApplicationRole
                {
                    Id = SeedStaffRoleId,
                    Name = "Staff",
                    NormalizedName = "STAFF",
                    ConcurrencyStamp = "SEED-ROLE-STAFF-CS",
                    CreatedByUserId = SeedAdminUserId,
                    CreateDate = SeedRoleCreateDateUtc,
                    IsActive = true
                },
                new ApplicationRole
                {
                    Id = SeedCanControlMusicRoleId,
                    Name = "CanControlMusic",
                    NormalizedName = "CANCONTROLMUSIC",
                    ConcurrencyStamp = "SEED-ROLE-CAN-CONTROL-MUSIC-CS",
                    CreatedByUserId = SeedAdminUserId,
                    CreateDate = SeedRoleCreateDateUtc,
                    IsActive = true
                },
                new ApplicationRole
                {
                    Id = SeedCanClockInRoleId,
                    Name = "CanClockIn",
                    NormalizedName = "CANCLOCKIN",
                    ConcurrencyStamp = "SEED-ROLE-CAN-CLOCK-IN-CS",
                    CreatedByUserId = SeedAdminUserId,
                    CreateDate = SeedRoleCreateDateUtc,
                    IsActive = true
                });

            modelBuilder.Entity<IdentityUserRole<string>>().HasData(
                new IdentityUserRole<string> { UserId = SeedAdminUserId, RoleId = SeedAdminRoleId },
                new IdentityUserRole<string> { UserId = SeedAdminUserId, RoleId = SeedManagerRoleId },
                new IdentityUserRole<string> { UserId = SeedAdminUserId, RoleId = SeedStaffRoleId },
                new IdentityUserRole<string> { UserId = SeedAdminUserId, RoleId = SeedCanControlMusicRoleId },
                new IdentityUserRole<string> { UserId = SeedAdminUserId, RoleId = SeedCanClockInRoleId });
        }
    }
}
