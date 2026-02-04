using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Infrastructure.DataAccess
{
    public class ApplicationDbContext
        : IdentityDbContext<UserProfile, ApplicationRole, string>
    {
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
        public DbSet<FileMetadata> FileMetadata { get; set; }
        public DbSet<Folder> Folders { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            if (!optionsBuilder.IsConfigured)
            {
                optionsBuilder.UseSqlServer(
                    "Server=(localdb)\\mssqllocaldb;Database=LanyardDB;Trusted_Connection=True;",
                    b => b.MigrationsAssembly("Lanyard.Infrastructure"));
            }
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
        }
    }
}
