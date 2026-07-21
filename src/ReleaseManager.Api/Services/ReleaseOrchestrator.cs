using System.Collections.Concurrent;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReleaseManager.Api.Data;
using ReleaseManager.Api.Domain;
using ReleaseManager.Api.Hubs;
using ReleaseManager.Api.Infrastructure;

namespace ReleaseManager.Api.Services;

public sealed class ReleaseWorker(IServiceScopeFactory scopeFactory, IOptions<PlatformOptions> options, ILogger<ReleaseWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await MarkInterruptedAsync(stoppingToken);
        using var semaphore = new SemaphoreSlim(Math.Max(1, options.Value.MaxConcurrentDeployments));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var ids = await db.DeploymentTasks.Where(x => x.Status == DeploymentStatus.Queued).OrderBy(x => x.CreatedAtUtc).Select(x => x.Id).Take(options.Value.MaxConcurrentDeployments).ToListAsync(stoppingToken);
                foreach (var id in ids)
                {
                    await semaphore.WaitAsync(stoppingToken);
                    _ = Task.Run(async () => { try { await using var s = scopeFactory.CreateAsyncScope(); await s.ServiceProvider.GetRequiredService<ReleaseOrchestrator>().RunAsync(id, stoppingToken); } catch (Exception e) { logger.LogError(e, "Deployment {TaskId} crashed", id); } finally { semaphore.Release(); } }, CancellationToken.None);
                }
            }
            catch (Exception e) { logger.LogError(e, "Release worker poll failed"); }
            await Task.Delay(1500, stoppingToken);
        }
    }

    private async Task MarkInterruptedAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var active = await db.DeploymentTasks.Where(x => x.Status != DeploymentStatus.Queued && x.Status != DeploymentStatus.Succeeded && x.Status != DeploymentStatus.Failed && x.Status != DeploymentStatus.Cancelled && x.Status != DeploymentStatus.RolledBack && x.FinishedAtUtc == null).ToListAsync(ct);
        foreach (var item in active) { item.Status = DeploymentStatus.Interrupted; item.Error = "平台重启导致任务中断，请重新发布。"; item.FinishedAtUtc = DateTime.UtcNow; }
        await db.SaveChangesAsync(ct);
    }
}

public sealed class ReleaseOrchestrator(AppDbContext db, DataPaths paths, ICommandRunner commands, IDataProtectionProvider protection, IHubContext<ReleaseHub> hub)
{
    private static readonly ConcurrentDictionary<Guid, byte> ProjectLocks = new();
    private readonly IDataProtector _protector = protection.CreateProtector("ReleaseManager.SvnCredential.v1");

    public static bool TryAcquireProject(Guid projectId) => ProjectLocks.TryAdd(projectId, 0);
    public static void ReleaseProject(Guid projectId) => ProjectLocks.TryRemove(projectId, out _);

    public async Task RunAsync(Guid taskId, CancellationToken hostToken)
    {
        var task = await db.DeploymentTasks.Include(x => x.Project).SingleAsync(x => x.Id == taskId, hostToken); var project = task.Project!;
        if (!TryAcquireProject(project.Id)) return;
        var staging = paths.SafeChild(paths.Staging, task.Id.ToString("N"), "publish");
        using var logWriteLock = new SemaphoreSlim(1, 1);
        try
        {
            task.StartedAtUtc = DateTime.UtcNow; await db.SaveChangesAsync(hostToken);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(hostToken); var ct = cts.Token;
            await Stage(DeploymentStatus.FetchingSource, 5, "获取源码", async () => await FetchSource(
                project, task, ct,
                (line, error) => Log(error ? "Error" : "Info", "获取源码", line),
                command => Log("Command", "获取源码", command)));
            var revision = task.ResolvedRevision!.Value;
            var version = $"{DateTime.UtcNow:yyyyMMddHHmmss}-r{revision}"; task.ReleaseVersion = version;
            if (Directory.Exists(staging)) Directory.Delete(staging, true); Directory.CreateDirectory(staging);
            await Log("Info", "预检", $"已清空并确认暂存目录：{staging}");
            var workspace = paths.SafeChild(paths.Workspaces, project.Slug);
            var backend = Path.Combine(workspace, project.BackendDirectory); var csproj = Path.Combine(backend, project.BackendProjectFile);
            await Stage(DeploymentStatus.BuildingBackend, 20, "构建后台", async () =>
            {
                RequiredFile(csproj); var args = new[] { "publish", csproj, "-c", project.BuildConfiguration, "-f", project.TargetFramework, "-o", staging, $"-p:InformationalVersion={version}+svn.r{revision}" };
                await RunChecked("dotnet", args, backend, project, ct);
                RequiredFile(Path.Combine(staging, project.EntryAssembly));
            });
            await Stage(DeploymentStatus.BuildingAdmin, 40, "构建管理员前端", () => BuildFrontend(
                workspace, project.AdminDirectory, project.AdminOutputDirectory, project, ct,
                (line, error) => Log(error ? "Error" : "Info", "构建管理员前端", line),
                command => Log("Command", "构建管理员前端", command)));
            await Stage(DeploymentStatus.BuildingTerminal, 58, "构建终端前端", () => BuildFrontend(
                workspace, project.TerminalDirectory, project.TerminalOutputDirectory, project, ct,
                (line, error) => Log(error ? "Error" : "Info", "构建终端前端", line),
                command => Log("Command", "构建终端前端", command)));
            await Stage(DeploymentStatus.Assembling, 68, "组装产物", async () =>
            {
                var adminSource = Path.Combine(workspace, project.AdminDirectory, project.AdminOutputDirectory); var adminTarget = Path.Combine(staging, "wwwroot", "admin");
                var terminalSource = Path.Combine(workspace, project.TerminalDirectory, project.TerminalOutputDirectory); var terminalTarget = Path.Combine(staging, "wwwroot", "terminal");
                await Log("Info", "组装产物", $"复制 admin 产物：{adminSource} -> {adminTarget}（{CountFiles(adminSource)} 个文件）"); CopyDirectory(adminSource, adminTarget);
                await Log("Info", "组装产物", $"复制 terminal 产物：{terminalSource} -> {terminalTarget}（{CountFiles(terminalSource)} 个文件）"); CopyDirectory(terminalSource, terminalTarget);
            });
            var config = await db.Configurations.Where(x => x.ProjectId == project.Id && x.IsActive).OrderByDescending(x => x.Version).FirstOrDefaultAsync(ct);
            await Stage(DeploymentStatus.ApplyingConfiguration, 76, "应用配置", async () =>
            {
                var target = Path.Combine(staging, "appsettings.json");
                if (config != null) { await Log("Info", "应用配置", $"应用平台配置版本 v{config.Version} 到 {target}"); await File.WriteAllTextAsync(target, config.JsonContent, new UTF8Encoding(false), ct); }
                else { await Log("Warning", "应用配置", $"未配置平台环境版本，沿用源码发布产物中的 {target}"); RequiredFile(target); }
            });
            await Stage(DeploymentStatus.Validating, 85, "校验", async () =>
            {
                RequiredNonEmptyDirectory(Path.Combine(staging, "wwwroot", "admin")); RequiredNonEmptyDirectory(Path.Combine(staging, "wwwroot", "terminal"));
                var appSettingsPath = Path.Combine(staging, "appsettings.json");
                await Log("Info", "校验", $"校验入口程序集及 SVN Revision：{project.EntryAssembly} -> svn.r{revision}");
                await Log("Info", "校验", "校验 admin、terminal 目录非空及 appsettings.json 语法");
                try
                {
                    using var appSettings = Validation.ParseAppSettings(await File.ReadAllTextAsync(appSettingsPath, ct));
                }
                catch (JsonException ex)
                {
                    throw new InvalidOperationException($"appsettings.json 格式无效：第 {(ex.LineNumber ?? 0) + 1} 行，第 {(ex.BytePositionInLine ?? 0) + 1} 列。请检查缺少的逗号、引号或括号。", ex);
                }
                ValidateInformationalVersion(Path.Combine(staging, project.EntryAssembly), revision);
                var info = new { projectName = project.Name, releaseVersion = version, svnUrl = project.SvnUrl, svnRevision = revision, builtAtUtc = DateTime.UtcNow, configurationVersion = config?.Version ?? 0, entryAssembly = project.EntryAssembly };
                await File.WriteAllTextAsync(Path.Combine(staging, "release-info.json"), JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false), ct);
                await Log("Info", "校验", $"生成 release-info.json，版本 {version}，配置版本 v{config?.Version ?? 0}");
            });
            await Stage(DeploymentStatus.Switching, 94, "切换版本", async () =>
            {
                var releaseDir = paths.SafeChild(paths.Releases, project.Slug, version); Directory.CreateDirectory(Path.GetDirectoryName(releaseDir)!);
                await Log("Info", "切换版本", $"提升暂存产物为不可变版本目录：{releaseDir}");
                if (Directory.Exists(releaseDir)) throw new InvalidOperationException("目标版本目录已存在。"); Directory.Move(staging, releaseDir);
                var release = new Release { ProjectId = project.Id, Version = version, SvnRevision = revision, DeploymentTaskId = task.Id, ConfigurationId = config?.Id, ConfigurationVersion = config?.Version ?? 0, DirectoryPath = releaseDir, ManifestSha256 = ComputeManifest(releaseDir), TriggeredByName = task.TriggeredByName };
                db.Releases.Add(release); project.CurrentReleaseId = release.Id; await db.SaveChangesAsync(ct);
            });
            task.Status = DeploymentStatus.Succeeded; task.Progress = 100; task.FinishedAtUtc = DateTime.UtcNow; await Log("Info", "完成", $"发布成功：{version}");
        }
        catch (OperationCanceledException) { task.Status = DeploymentStatus.Cancelled; task.Error = "任务已取消"; task.FinishedAtUtc = DateTime.UtcNow; await Log("Warning", "取消", "任务已安全取消"); }
        catch (Exception ex) { task.Status = DeploymentStatus.Failed; task.Error = Sanitize(ex.Message, project.SvnPasswordProtected is null ? null : _protector.Unprotect(project.SvnPasswordProtected)); task.FinishedAtUtc = DateTime.UtcNow; await Log("Error", task.Status.ToString(), task.Error); }
        finally { await db.SaveChangesAsync(CancellationToken.None); ReleaseProject(project.Id); }

        async Task Stage(DeploymentStatus status, int progress, string name, Func<Task> work)
        {
            await db.Entry(task).ReloadAsync(hostToken); if (task.CancellationRequested && status < DeploymentStatus.Switching) throw new OperationCanceledException();
            task.Status = status; task.Progress = progress; await db.SaveChangesAsync(hostToken); await Log("Info", name, $"开始{name}"); await work(); await Log("Info", name, $"{name}完成");
        }
        async Task Log(string level, string stage, string message)
        {
            await logWriteLock.WaitAsync(CancellationToken.None);
            try
            {
                var next = (await db.DeploymentLogs.Where(x => x.TaskId == task.Id).MaxAsync(x => (long?)x.Sequence, CancellationToken.None) ?? 0) + 1;
                var item = new DeploymentLog { TaskId = task.Id, Sequence = next, Level = level, Stage = stage, Message = Sanitize(message, project.SvnPasswordProtected is null ? null : _protector.Unprotect(project.SvnPasswordProtected)) };
                db.DeploymentLogs.Add(item);
                await db.SaveChangesAsync(CancellationToken.None);
                await hub.Clients.Group(task.Id.ToString()).SendAsync("log", item, CancellationToken.None);
            }
            finally
            {
                logWriteLock.Release();
            }
        }
        async Task RunChecked(string executable, IEnumerable<string> args, string cwd, Project p, CancellationToken ct)
        {
            var arguments = args.ToArray();
            await Log("Command", "构建后台", FormatCommand(executable, arguments));
            var result = await commands.RunAsync(executable, arguments, cwd, ParseEnvironment(p.EnvironmentVariablesJson), TimeSpan.FromMinutes(p.BuildTimeoutMinutes), (line, err) => Log(err ? "Error" : "Info", "构建后台", line), ct);
            if (result.ExitCode != 0) throw new InvalidOperationException($"命令 {executable} 执行失败，退出码 {result.ExitCode}。");
        }
    }

    private async Task FetchSource(Project p, DeploymentTask task, CancellationToken ct, Func<string, bool, Task> onLine, Func<string, Task> onCommand)
    {
        var workspace = paths.SafeChild(paths.Workspaces, p.Slug); var password = string.IsNullOrEmpty(p.SvnPasswordProtected) ? null : _protector.Unprotect(p.SvnPasswordProtected);
        var common = new List<string> { "--non-interactive", "--no-auth-cache", "--username", p.SvnUserName };
        if (password != null) common.Add("--password-from-stdin");
        if (p.TrustServerCertificate) { common.Add("--trust-server-cert-failures"); common.Add("unknown-ca,cn-mismatch,expired,not-yet-valid,other"); }
        var revision = task.RequestedRevision?.ToString() ?? "HEAD"; CommandResult result;
        if (Directory.Exists(Path.Combine(workspace, ".svn")))
        {
            string[] args = ["update", "-r", revision, .. common]; await onCommand(FormatCommand("svn", args));
            result = await commands.RunAsync("svn", args, workspace, null, TimeSpan.FromMinutes(p.BuildTimeoutMinutes), onLine, ct, password);
        }
        else
        {
            Directory.CreateDirectory(workspace); string[] args = ["checkout", p.SvnUrl, workspace, "-r", revision, .. common]; await onCommand(FormatCommand("svn", args));
            result = await commands.RunAsync("svn", args, paths.Workspaces, null, TimeSpan.FromMinutes(p.BuildTimeoutMinutes), onLine, ct, password);
        }
        if (result.ExitCode != 0) throw new InvalidOperationException("SVN 获取源码失败：" + result.Error);
        string[] infoArgs = ["info", "--show-item", "revision", workspace, .. common]; await onCommand(FormatCommand("svn", infoArgs));
        var info = await commands.RunAsync("svn", infoArgs, workspace, null, TimeSpan.FromMinutes(2), onLine, ct, password);
        if (info.ExitCode != 0 || !long.TryParse(info.Output.Trim(), out var resolved)) throw new InvalidOperationException("无法解析 SVN Revision。"); task.ResolvedRevision = resolved; await db.SaveChangesAsync(ct);
    }

    private async Task BuildFrontend(string workspace, string relative, string output, Project p, CancellationToken ct, Func<string, bool, Task> onLine, Func<string, Task> onCommand)
    {
        var dir = Path.Combine(workspace, relative); RequiredFile(Path.Combine(dir, "package.json"));
        var install = File.Exists(Path.Combine(dir, "package-lock.json")) ? new[] { "ci" } : new[] { "install" };
        var npm = ToolPaths.Npm();
        string[] installArgs = [.. npm.PrefixArguments, .. install]; await onCommand(FormatCommand("npm", install));
        var r1 = await commands.RunAsync(npm.FileName, installArgs, dir, ParseEnvironment(p.EnvironmentVariablesJson), TimeSpan.FromMinutes(p.BuildTimeoutMinutes), onLine, ct); if (r1.ExitCode != 0) throw new InvalidOperationException($"{relative} 依赖安装失败。");
        string[] buildArgs = [.. npm.PrefixArguments, "run", "build"]; await onCommand(FormatCommand("npm", ["run", "build"]));
        var r2 = await commands.RunAsync(npm.FileName, buildArgs, dir, ParseEnvironment(p.EnvironmentVariablesJson), TimeSpan.FromMinutes(p.BuildTimeoutMinutes), onLine, ct); if (r2.ExitCode != 0) throw new InvalidOperationException($"{relative} 构建失败。"); RequiredNonEmptyDirectory(Path.Combine(dir, output));
    }

    private static Dictionary<string, string> ParseEnvironment(string json) => JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
    private static void RequiredFile(string path) { if (!File.Exists(path)) throw new FileNotFoundException($"必要文件不存在：{path}"); }
    private static void RequiredNonEmptyDirectory(string path) { if (!Directory.Exists(path) || !Directory.EnumerateFileSystemEntries(path).Any()) throw new DirectoryNotFoundException($"构建输出目录不存在或为空：{path}"); }
    private static void CopyDirectory(string source, string destination) { RequiredNonEmptyDirectory(source); if (Directory.Exists(destination)) Directory.Delete(destination, true); Directory.CreateDirectory(destination); foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories)) { var dest = Path.Combine(destination, Path.GetRelativePath(source, file)); Directory.CreateDirectory(Path.GetDirectoryName(dest)!); File.Copy(file, dest, true); } }
    private static int CountFiles(string path) => Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Count() : 0;
    private static string Sanitize(string text, string? secret) { if (!string.IsNullOrEmpty(secret)) text = text.Replace(secret, "******", StringComparison.Ordinal); return Regex.Replace(text, @"(?i)(password|pwd|token|secret)(\s*[=:]\s*)([^\s;,]+)", "$1$2******"); }
    private static string FormatCommand(string executable, IEnumerable<string> arguments) => "$ " + string.Join(" ", new[] { executable }.Concat(arguments).Select(QuoteArgument));
    private static string QuoteArgument(string value) => value.Length == 0 || value.Any(char.IsWhiteSpace) ? $"\"{value.Replace("\"", "\\\"")}\"" : value;
    private static string ComputeManifest(string root) { using var sha = SHA256.Create(); foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(x => x)) { var bytes = Encoding.UTF8.GetBytes(Path.GetRelativePath(root, f) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))); sha.TransformBlock(bytes, 0, bytes.Length, null, 0); } sha.TransformFinalBlock([], 0, 0); return Convert.ToHexString(sha.Hash!); }
    private static void ValidateInformationalVersion(string dll, long revision) { using var stream = File.OpenRead(dll); using var pe = new PEReader(stream); var md = pe.GetMetadataReader(); var value = md.GetAssemblyDefinition().GetCustomAttributes().Select(x => md.GetCustomAttribute(x)).Select(a => a.Value.IsNil ? "" : Encoding.UTF8.GetString(md.GetBlobBytes(a.Value))).FirstOrDefault(x => x.Contains("svn.r", StringComparison.OrdinalIgnoreCase)); if (value == null || !value.Contains($"svn.r{revision}", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"入口程序集信息版本未包含 svn.r{revision}。"); }
}
