using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReleaseManager.Api.Data;
using ReleaseManager.Api.Domain;
using ReleaseManager.Api.Infrastructure;

namespace ReleaseManager.Api.Services;

public sealed class RuntimeService(AppDbContext db, DataPaths paths)
{
    public async Task<ManagedProcess> StartAsync(Guid projectId, CancellationToken ct)
    {
        var project = await db.Projects.SingleAsync(x => x.Id == projectId && !x.IsDeleted, ct);
        if (project.CurrentReleaseId == null) throw new InvalidOperationException("项目没有可启动的当前版本。");
        var processHistory = await db.ManagedProcesses.Where(x => x.ProjectId == projectId).OrderByDescending(x => x.StartedAtUtc).Take(50).ToListAsync(ct);
        var existing = processHistory.FirstOrDefault(IsManagedProcessAlive);
        if (existing != null) throw new InvalidOperationException($"项目已有受管进程在运行（PID {existing.ProcessId}），请先停止后再启动。");
        foreach (var stale in processHistory.Where(x => x.State is ProcessState.Running or ProcessState.Starting or ProcessState.Stopping)) { stale.State = ProcessState.Crashed; stale.StoppedAtUtc ??= DateTime.UtcNow; }
        if (processHistory.Count > 0) await db.SaveChangesAsync(ct);
        var release = await db.Releases.SingleAsync(x => x.Id == project.CurrentReleaseId && x.IsAvailable, ct);
        var dll = Path.Combine(release.DirectoryPath, project.EntryAssembly); if (!File.Exists(dll) || !File.Exists(Path.Combine(release.DirectoryPath, "appsettings.json"))) throw new InvalidOperationException("入口程序集或配置文件不存在。");
        var logDir = paths.SafeChild(paths.Logs, project.Slug, "runtime"); Directory.CreateDirectory(logDir); var logPath = Path.Combine(logDir, $"{DateTime.UtcNow:yyyyMMdd}.log");
        var psi = new ProcessStartInfo("dotnet") { WorkingDirectory = release.DirectoryPath, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        psi.ArgumentList.Add(project.EntryAssembly); foreach (var arg in JsonSerializer.Deserialize<string[]>(project.StartArgumentsJson) ?? []) psi.ArgumentList.Add(arg);
        foreach (var item in JsonSerializer.Deserialize<Dictionary<string, string>>(project.EnvironmentVariablesJson) ?? []) psi.Environment[item.Key] = item.Value;
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true }; process.Start();
        var managed = new ManagedProcess { ProjectId = projectId, ReleaseId = release.Id, ProcessId = process.Id, State = ProcessState.Starting, StartedAtUtc = DateTime.UtcNow, Command = $"dotnet {project.EntryAssembly}", LogPath = logPath };
        db.ManagedProcesses.Add(managed); await db.SaveChangesAsync(ct);
        var logWriteLock = new SemaphoreSlim(1, 1);
        _ = PumpAsync(process.StandardOutput.BaseStream, logPath, "OUT", logWriteLock); _ = PumpAsync(process.StandardError.BaseStream, logPath, "ERR", logWriteLock);
        await Task.Delay(TimeSpan.FromSeconds(1), ct);
        if (process.HasExited)
        {
            managed.State = ProcessState.Crashed; managed.StoppedAtUtc = DateTime.UtcNow; managed.ExitCode = process.ExitCode;
            await db.SaveChangesAsync(ct);
            throw new InvalidOperationException($"目标程序启动后立即退出（退出码 {process.ExitCode}），请查看运行日志。常见原因是端口被占用或配置无效。");
        }
        managed.State = ProcessState.Running; await db.SaveChangesAsync(ct);
        return managed;
    }

    public async Task<ManagedProcess> StopAsync(Guid projectId, bool force, CancellationToken ct)
    {
        var p = await db.Projects.SingleAsync(x => x.Id == projectId, ct);
        var processHistory = await db.ManagedProcesses.Where(x => x.ProjectId == projectId).OrderByDescending(x => x.StartedAtUtc).Take(50).ToListAsync(ct);
        var managed = processHistory.FirstOrDefault(IsManagedProcessAlive);
        if (managed == null)
        {
            foreach (var stale in processHistory.Where(x => x.State is ProcessState.Running or ProcessState.Starting or ProcessState.Stopping)) { stale.State = ProcessState.Stopped; stale.StoppedAtUtc ??= DateTime.UtcNow; }
            await db.SaveChangesAsync(ct);
            throw new InvalidOperationException("项目未运行。");
        }
        managed.State = ProcessState.Stopping; await db.SaveChangesAsync(ct);
        try
        {
            if (OperatingSystem.IsWindows()) await KillWindowsProcessTreeAsync(managed.ProcessId, ct);
            else if (OperatingSystem.IsLinux()) SendSignal(managed.ProcessId, force ? 9 : 15);
            else { using var process = Process.GetProcessById(managed.ProcessId); process.Kill(); }
            await WaitUntilStoppedAsync(managed, TimeSpan.FromSeconds(p.StopTimeoutSeconds), ct);
            managed.State = ProcessState.Stopped; managed.StoppedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct); return managed;
        }
        catch (ArgumentException)
        {
            managed.State = ProcessState.Stopped; managed.StoppedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            return managed;
        }
        catch
        {
            managed.State = IsManagedProcessAlive(managed) ? ProcessState.Running : ProcessState.Stopped;
            if (managed.State == ProcessState.Stopped) managed.StoppedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<object> StatusAsync(Guid projectId, CancellationToken ct)
    {
        var items = await db.ManagedProcesses.Where(x => x.ProjectId == projectId).OrderByDescending(x => x.StartedAtUtc).Take(50).ToListAsync(ct);
        if (items.Count == 0) return new { state = ProcessState.NotRunning.ToString() };
        var item = items.FirstOrDefault(IsManagedProcessAlive) ?? items[0];
        if (!IsManagedProcessAlive(item) && item.State is ProcessState.Running or ProcessState.Starting or ProcessState.Stopping) { item.State = ProcessState.Crashed; item.StoppedAtUtc = DateTime.UtcNow; await db.SaveChangesAsync(ct); }
        return new { state = item.State.ToString(), item.ProcessId, item.StartedAtUtc, item.ExitCode, item.ReleaseId, item.LogPath };
    }

    private static bool IsManagedProcessAlive(ManagedProcess managed) { try { using var process = Process.GetProcessById(managed.ProcessId); return !process.HasExited && Math.Abs((process.StartTime.ToUniversalTime() - managed.StartedAtUtc).TotalSeconds) < 30; } catch { return false; } }
    private static async Task KillWindowsProcessTreeAsync(int processId, CancellationToken ct)
    {
        if (!IsProcessAlive(processId)) return;
        var startInfo = new ProcessStartInfo("taskkill.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        startInfo.ArgumentList.Add("/PID"); startInfo.ArgumentList.Add(processId.ToString()); startInfo.ArgumentList.Add("/T"); startInfo.ArgumentList.Add("/F");
        using var taskkill = new Process { StartInfo = startInfo };
        taskkill.Start();
        var outputTask = taskkill.StandardOutput.ReadToEndAsync(ct); var errorTask = taskkill.StandardError.ReadToEndAsync(ct);
        await taskkill.WaitForExitAsync(ct); var output = await outputTask; var error = await errorTask;
        if (taskkill.ExitCode != 0 && IsProcessAlive(processId))
        {
            try { using var process = Process.GetProcessById(processId); process.Kill(); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                var detail = (string.IsNullOrWhiteSpace(error) ? output : error).Trim();
                throw new InvalidOperationException($"终止进程失败（PID {processId}）：{(string.IsNullOrWhiteSpace(detail) ? ex.Message : detail)}", ex);
            }
        }
    }
    private static async Task WaitUntilStoppedAsync(ManagedProcess managed, TimeSpan timeout, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        while (IsManagedProcessAlive(managed))
        {
            if (started.Elapsed >= timeout) throw new InvalidOperationException("停止超时，请使用强制停止。");
            await Task.Delay(200, ct);
        }
    }
    private static bool IsProcessAlive(int processId) { try { using var process = Process.GetProcessById(processId); return !process.HasExited; } catch { return false; } }
    [DllImport("libc", SetLastError = true)] private static extern int kill(int pid, int signal);
    private static void SendSignal(int pid, int signal) { if (kill(pid, signal) != 0) throw new InvalidOperationException($"发送停止信号失败，错误码 {Marshal.GetLastWin32Error()}。"); }
    private static async Task PumpAsync(Stream stream, string path, string kind, SemaphoreSlim writeLock) { await foreach (var line in AdaptiveLineReader.ReadLinesAsync(stream)) { var safe = System.Text.RegularExpressions.Regex.Replace(line, @"(?i)(password|pwd|token|secret)(\s*[=:]\s*)([^\s;,]+)", "$1$2******"); await writeLock.WaitAsync(); try { await File.AppendAllTextAsync(path, $"{DateTime.UtcNow:O} [{kind}] {safe}{Environment.NewLine}"); } finally { writeLock.Release(); } } }
}

public sealed class PackageService(AppDbContext db, DataPaths paths)
{
    public async Task<ReleasePackage> CreateAsync(Guid releaseId, CancellationToken ct)
    {
        var release = await db.Releases.SingleAsync(x => x.Id == releaseId && x.IsAvailable, ct); var project = await db.Projects.SingleAsync(x => x.Id == release.ProjectId, ct);
        var existing = await db.Packages.FirstOrDefaultAsync(x => x.ReleaseId == releaseId && x.State == PackageState.Ready && x.Path != "", ct); if (existing != null && File.Exists(existing.Path)) return existing;
        var dir = paths.SafeChild(paths.Packages, project.Slug); Directory.CreateDirectory(dir); var file = Path.Combine(dir, $"{project.Slug}-{release.Version}.zip");
        var package = new ReleasePackage { ReleaseId = releaseId, State = PackageState.Building, Path = file }; db.Packages.Add(package); await db.SaveChangesAsync(ct);
        try { if (File.Exists(file)) File.Delete(file); ZipFile.CreateFromDirectory(release.DirectoryPath, file, CompressionLevel.Optimal, false); await using var stream = File.OpenRead(file); package.Sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)); package.Size = stream.Length; package.GeneratedAtUtc = DateTime.UtcNow; package.State = PackageState.Ready; }
        catch (Exception ex) { package.State = PackageState.Failed; package.Error = ex.Message; }
        await db.SaveChangesAsync(ct); return package;
    }
}
