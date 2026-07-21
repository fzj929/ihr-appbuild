using System.ComponentModel.DataAnnotations;

namespace ReleaseManager.Api.Domain;

public static class Roles
{
    public const string Admin = "Admin";
    public const string Operator = "Operator";
    public const string Viewer = "Viewer";
    public static readonly string[] All = [Admin, Operator, Viewer];
}

public enum DeploymentStatus { Queued, FetchingSource, BuildingBackend, BuildingAdmin, BuildingTerminal, Assembling, ApplyingConfiguration, Validating, Switching, Starting, Succeeded, Failed, Cancelled, RolledBack, Interrupted }
public enum ProcessState { NotRunning, Starting, Running, Stopping, Stopped, Crashed, Unknown }
public enum PackageState { Pending, Building, Ready, Failed }

public sealed class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(64)] public string UserName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    [MaxLength(20)] public string Role { get; set; } = Roles.Viewer;
    public bool IsEnabled { get; set; } = true;
    public bool MustChangePassword { get; set; } = true;
    public int FailedLoginCount { get; set; }
    public DateTime? LockedUntilUtc { get; set; }
    public DateTime? LastLoginUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class Session
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User? User { get; set; }
    [MaxLength(64)] public string TokenHash { get; set; } = "";
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(64)] public string Slug { get; set; } = "";
    [MaxLength(120)] public string Name { get; set; } = "";
    [MaxLength(500)] public string Description { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public bool IsDeleted { get; set; }
    [MaxLength(1000)] public string SvnUrl { get; set; } = "";
    [MaxLength(128)] public string SvnUserName { get; set; } = "";
    public string? SvnPasswordProtected { get; set; }
    public bool TrustServerCertificate { get; set; }
    [MaxLength(200)] public string TrunkPath { get; set; } = "05-trunk";
    [MaxLength(300)] public string BackendDirectory { get; set; } = "05-trunk/webapi/iHR.Hosting";
    [MaxLength(200)] public string BackendProjectFile { get; set; } = "iHR.Hosting.csproj";
    [MaxLength(200)] public string EntryAssembly { get; set; } = "iHR.Hosting.dll";
    [MaxLength(300)] public string AdminDirectory { get; set; } = "05-trunk/webui/admin";
    [MaxLength(300)] public string TerminalDirectory { get; set; } = "05-trunk/webui/terminal";
    [MaxLength(100)] public string AdminBuildCommand { get; set; } = "npm run build";
    [MaxLength(100)] public string TerminalBuildCommand { get; set; } = "npm run build";
    [MaxLength(100)] public string AdminOutputDirectory { get; set; } = "dist";
    [MaxLength(100)] public string TerminalOutputDirectory { get; set; } = "dist";
    [MaxLength(30)] public string BuildConfiguration { get; set; } = "Release";
    [MaxLength(30)] public string TargetFramework { get; set; } = "net8.0";
    public string EnvironmentVariablesJson { get; set; } = "{}";
    public string StartArgumentsJson { get; set; } = "[]";
    [MaxLength(1000)] public string? HealthCheckUrl { get; set; }
    public int HealthExpectedStatus { get; set; } = 200;
    public int HealthTimeoutSeconds { get; set; } = 10;
    public int StopTimeoutSeconds { get; set; } = 15;
    public int BuildTimeoutMinutes { get; set; } = 30;
    public int RetainReleaseCount { get; set; } = 3;
    public int RetainPackageDays { get; set; } = 30;
    public Guid? CurrentReleaseId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class AppConfiguration
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public int Version { get; set; }
    public string JsonContent { get; set; } = "{}";
    [MaxLength(500)] public string Comment { get; set; } = "";
    public Guid CreatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; }
}

public sealed class DeploymentTask
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }
    public DeploymentStatus Status { get; set; } = DeploymentStatus.Queued;
    public int Progress { get; set; }
    public long? RequestedRevision { get; set; }
    public long? ResolvedRevision { get; set; }
    public Guid TriggeredByUserId { get; set; }
    public string TriggeredByName { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public bool CancellationRequested { get; set; }
    [MaxLength(2000)] public string? Error { get; set; }
    [MaxLength(100)] public string? ReleaseVersion { get; set; }
}

public sealed class DeploymentLog
{
    public long Id { get; set; }
    public Guid TaskId { get; set; }
    public long Sequence { get; set; }
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    [MaxLength(20)] public string Level { get; set; } = "Info";
    [MaxLength(50)] public string Stage { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed class Release
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string Version { get; set; } = "";
    public long SvnRevision { get; set; }
    public Guid DeploymentTaskId { get; set; }
    public Guid? ConfigurationId { get; set; }
    public int ConfigurationVersion { get; set; }
    public string DirectoryPath { get; set; } = "";
    public string ManifestSha256 { get; set; } = "";
    public string TriggeredByName { get; set; } = "";
    public DateTime BuiltAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsAvailable { get; set; } = true;
}

public sealed class ManagedProcess
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Guid ReleaseId { get; set; }
    public int ProcessId { get; set; }
    public ProcessState State { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? StoppedAtUtc { get; set; }
    public int? ExitCode { get; set; }
    public string Command { get; set; } = "";
    public string LogPath { get; set; } = "";
}

public sealed class ReleasePackage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ReleaseId { get; set; }
    public PackageState State { get; set; }
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public string? Error { get; set; }
    public DateTime? GeneratedAtUtc { get; set; }
}

public sealed class AuditLog
{
    public long Id { get; set; }
    public Guid? UserId { get; set; }
    public string UserName { get; set; } = "anonymous";
    public string Action { get; set; } = "";
    public string Target { get; set; } = "";
    public string Result { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public string Details { get; set; } = "";
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
}
