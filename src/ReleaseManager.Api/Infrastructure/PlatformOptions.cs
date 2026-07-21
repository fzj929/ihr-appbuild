namespace ReleaseManager.Api.Infrastructure;

public sealed class PlatformOptions
{
    public int MaxConcurrentDeployments { get; set; } = 2;
    public int SessionHours { get; set; } = 8;
    public int LoginFailureLimit { get; set; } = 5;
    public int LoginLockMinutes { get; set; } = 15;
    public string DefaultAdminUser { get; set; } = "admin";
    public string DefaultAdminPassword { get; set; } = "Admin@123456";
}

public sealed class DataPaths(IConfiguration configuration, IWebHostEnvironment environment)
{
    public string Root { get; } = Path.GetFullPath(configuration["DataRoot"] ?? Path.Combine(environment.ContentRootPath, "data"));
    public string Database => Path.Combine(Root, "database");
    public string Keys => Path.Combine(Root, "keys");
    public string Workspaces => Path.Combine(Root, "workspaces");
    public string Releases => Path.Combine(Root, "releases");
    public string Staging => Path.Combine(Root, "staging");
    public string Packages => Path.Combine(Root, "packages");
    public string Logs => Path.Combine(Root, "logs");

    public void EnsureCreated()
    {
        foreach (var path in new[] { Root, Database, Keys, Workspaces, Releases, Staging, Packages, Logs }) Directory.CreateDirectory(path);
    }

    public string SafeChild(string root, params string[] parts)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine([root, .. parts]));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!result.StartsWith(fullRoot, comparison)) throw new InvalidOperationException("路径超出安全数据根目录。");
        return result;
    }
}
