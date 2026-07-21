using Microsoft.EntityFrameworkCore;
using ReleaseManager.Api.Domain;

namespace ReleaseManager.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<AppConfiguration> Configurations => Set<AppConfiguration>();
    public DbSet<DeploymentTask> DeploymentTasks => Set<DeploymentTask>();
    public DbSet<DeploymentLog> DeploymentLogs => Set<DeploymentLog>();
    public DbSet<Release> Releases => Set<Release>();
    public DbSet<ManagedProcess> ManagedProcesses => Set<ManagedProcess>();
    public DbSet<ReleasePackage> Packages => Set<ReleasePackage>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>().HasIndex(x => x.UserName).IsUnique();
        b.Entity<Session>().HasIndex(x => x.TokenHash).IsUnique();
        b.Entity<Project>().HasIndex(x => x.Slug).IsUnique();
        b.Entity<AppConfiguration>().HasIndex(x => new { x.ProjectId, x.Version }).IsUnique();
        b.Entity<DeploymentTask>().HasIndex(x => new { x.ProjectId, x.Status });
        b.Entity<DeploymentLog>().HasIndex(x => new { x.TaskId, x.Sequence }).IsUnique();
        b.Entity<Release>().HasIndex(x => new { x.ProjectId, x.Version }).IsUnique();
        b.Entity<ManagedProcess>().HasIndex(x => new { x.ProjectId, x.State });
    }
}
