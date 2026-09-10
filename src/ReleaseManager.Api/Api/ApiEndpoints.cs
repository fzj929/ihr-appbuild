using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReleaseManager.Api.Data;
using ReleaseManager.Api.Domain;
using ReleaseManager.Api.Infrastructure;
using ReleaseManager.Api.Services;

namespace ReleaseManager.Api.Api;

public static class ApiEndpoints
{
    public static void MapPlatformApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");
        api.MapPost("/auth/login", Login);
        api.MapPost("/auth/logout", Logout).RequireAuthorization();
        api.MapGet("/auth/me", (ClaimsPrincipal u) => Results.Ok(new { id = u.UserId(), userName = u.UserName(), role = u.FindFirstValue(ClaimTypes.Role), mustChangePassword = bool.Parse(u.FindFirstValue("must_change_password") ?? "false") })).RequireAuthorization();
        api.MapPut("/auth/password", ChangePassword).RequireAuthorization();

        api.MapGet("/dashboard", Dashboard).RequireAuthorization();
        api.MapGet("/projects", Projects).RequireAuthorization();
        api.MapGet("/projects/{id:guid}", ProjectDetail).RequireAuthorization();
        api.MapPost("/projects", SaveProject).RequireAuthorization(Roles.Admin);
        api.MapPut("/projects/{id:guid}", SaveProject).RequireAuthorization(Roles.Admin);
        api.MapDelete("/projects/{id:guid}", SoftDeleteProject).RequireAuthorization(Roles.Admin);
        api.MapPost("/projects/{id:guid}/test-svn", TestSvn).RequireAuthorization(Roles.Admin);
        api.MapGet("/environment", EnvironmentCheck).RequireAuthorization();

        api.MapPost("/projects/{id:guid}/deployments", CreateDeployment).RequireAuthorization("CanOperate");
        api.MapPost("/projects/{id:guid}/workspace/clean", CleanProjectWorkspace).RequireAuthorization("CanOperate");
        api.MapGet("/projects/{id:guid}/deployments", Deployments).RequireAuthorization();
        api.MapGet("/deployments/{id:guid}", Deployment).RequireAuthorization();
        api.MapGet("/deployments/{id:guid}/logs", DeploymentLogs).RequireAuthorization();
        api.MapPost("/deployments/{id:guid}/cancel", CancelDeployment).RequireAuthorization("CanOperate");

        api.MapGet("/projects/{id:guid}/configurations", Configurations).RequireAuthorization();
        api.MapPost("/projects/{id:guid}/configurations", SaveConfiguration).RequireAuthorization(Roles.Admin);
        api.MapPost("/projects/{id:guid}/configurations/{configId:guid}/activate", ActivateConfiguration).RequireAuthorization(Roles.Admin);
        api.MapGet("/projects/{id:guid}/releases", Releases).RequireAuthorization();
        api.MapPost("/projects/{id:guid}/releases/{releaseId:guid}/rollback", Rollback).RequireAuthorization("CanOperate");
        api.MapGet("/releases/{id:guid}/files", ReleaseFiles).RequireAuthorization();
        api.MapGet("/releases/{id:guid}/files/download", DownloadReleaseFile).RequireAuthorization();
        api.MapPost("/releases/{id:guid}/files", AddReleaseFile).RequireAuthorization("CanOperate");
        api.MapPost("/releases/{id:guid}/files/replace", ReplaceReleaseFile).RequireAuthorization("CanOperate");
        api.MapPost("/releases/{id:guid}/package", CreatePackage).RequireAuthorization("CanOperate");
        api.MapGet("/packages/{id:guid}/download", DownloadPackage).RequireAuthorization("CanOperate");

        api.MapGet("/projects/{id:guid}/runtime", RuntimeStatus).RequireAuthorization();
        api.MapPost("/projects/{id:guid}/runtime/start", RuntimeStart).RequireAuthorization("CanOperate");
        api.MapPost("/projects/{id:guid}/runtime/stop", RuntimeStop).RequireAuthorization("CanOperate");
        api.MapPost("/projects/{id:guid}/runtime/restart", RuntimeRestart).RequireAuthorization("CanOperate");
        api.MapGet("/projects/{id:guid}/runtime/log", RuntimeLog).RequireAuthorization();

        api.MapGet("/users", Users).RequireAuthorization(Roles.Admin);
        api.MapPost("/users", CreateUser).RequireAuthorization(Roles.Admin);
        api.MapPut("/users/{id:guid}", UpdateUser).RequireAuthorization(Roles.Admin);
        api.MapGet("/audit", GetAudit).RequireAuthorization(Roles.Admin);
        api.MapGet("/settings", Settings).RequireAuthorization(Roles.Admin);
    }

    private static async Task<IResult> Login(LoginRequest input, HttpContext http, AppDbContext db, IOptions<PlatformOptions> options)
    {
        var user = await db.Users.SingleOrDefaultAsync(x => x.UserName == input.UserName); if (user == null || !user.IsEnabled) return Results.Json(new { code = "AUTH_INVALID", message = "用户名或密码错误。" }, statusCode: 401);
        if (user.LockedUntilUtc > DateTime.UtcNow) return Results.Json(new { code = "AUTH_LOCKED", message = "登录失败次数过多，请稍后再试。" }, statusCode: 423);
        if (new PasswordHasher<User>().VerifyHashedPassword(user, user.PasswordHash, input.Password) == PasswordVerificationResult.Failed)
        {
            user.FailedLoginCount++; if (user.FailedLoginCount >= options.Value.LoginFailureLimit) { user.LockedUntilUtc = DateTime.UtcNow.AddMinutes(options.Value.LoginLockMinutes); user.FailedLoginCount = 0; } await db.SaveChangesAsync(); return Results.Json(new { code = "AUTH_INVALID", message = "用户名或密码错误。" }, statusCode: 401);
        }
        user.FailedLoginCount = 0; user.LockedUntilUtc = null; user.LastLoginUtc = DateTime.UtcNow; var token = AuthHelpers.NewToken(); db.Sessions.Add(new Session { UserId = user.Id, TokenHash = AuthHelpers.HashToken(token), ExpiresAtUtc = DateTime.UtcNow.AddHours(options.Value.SessionHours) }); Audit(db, http, user, "Auth.Login", user.UserName, "Success"); await db.SaveChangesAsync(); return Results.Ok(new { token, user = new { user.Id, user.UserName, user.Role, user.MustChangePassword } });
    }
    private static async Task<IResult> Logout(HttpContext http, AppDbContext db) { var token = http.Request.Headers.Authorization.ToString().Replace("Bearer ", "", StringComparison.OrdinalIgnoreCase); var hash = AuthHelpers.HashToken(token); await db.Sessions.Where(x => x.TokenHash == hash).ExecuteDeleteAsync(); return Results.NoContent(); }
    private static async Task<IResult> ChangePassword(PasswordRequest input, HttpContext http, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct)
    {
        if (input.NewPassword.Length < 10) return Problem("新密码至少 10 个字符。", 400);
        var user = await db.Users.FindAsync([principal.UserId()], ct);
        if (user == null || new PasswordHasher<User>().VerifyHashedPassword(user, user.PasswordHash, input.CurrentPassword) == PasswordVerificationResult.Failed) return Problem("当前密码不正确。", 400);
        user.PasswordHash = new PasswordHasher<User>().HashPassword(user, input.NewPassword);
        user.MustChangePassword = false;
        var currentToken = http.Request.Headers.Authorization.ToString().Replace("Bearer ", "", StringComparison.OrdinalIgnoreCase);
        var currentTokenHash = AuthHelpers.HashToken(currentToken);
        await db.Sessions.Where(x => x.UserId == user.Id && x.TokenHash != currentTokenHash).ExecuteDeleteAsync(ct);
        Audit(db, http, principal, "Auth.PasswordChange", user.UserName, "Success", "已注销其他会话");
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Dashboard(AppDbContext db, RuntimeService runtime, CancellationToken ct) { var projects = await db.Projects.Where(x => !x.IsDeleted).OrderBy(x => x.Name).ToListAsync(ct); var rows = new List<object>(); foreach (var p in projects) { var release = p.CurrentReleaseId == null ? null : await db.Releases.FindAsync([p.CurrentReleaseId], ct); var releaseConfig = ReadReleaseConfiguration(release); var last = await db.DeploymentTasks.Where(x => x.ProjectId == p.Id).OrderByDescending(x => x.CreatedAtUtc).FirstOrDefaultAsync(ct); rows.Add(new { p.Id, p.Slug, p.Name, p.Description, p.IsEnabled, currentVersion = release?.Version, svnRevision = release?.SvnRevision, endpoints = releaseConfig.Endpoints, lastDeployment = last == null ? null : new { last.Status, last.Progress, last.CreatedAtUtc, last.Error }, runtime = await runtime.StatusAsync(p.Id, ct) }); } return Results.Ok(rows); }
    private static async Task<IResult> Projects(AppDbContext db, int page = 1, int pageSize = 50) { var q = db.Projects.Where(x => !x.IsDeleted).OrderBy(x => x.Name); var total = await q.CountAsync(); var rows = await q.Skip((page - 1) * pageSize).Take(Math.Min(pageSize, 100)).ToListAsync(); return Results.Ok(new { total, items = rows.Select(x => ProjectDto(x)) }); }
    private static async Task<IResult> ProjectDetail(Guid id, AppDbContext db, CancellationToken ct)
    {
        var p = await db.Projects.SingleOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct);
        if (p == null) return Results.NotFound();
        var release = p.CurrentReleaseId == null ? null : await db.Releases.FindAsync([p.CurrentReleaseId], ct);
        var releaseConfig = ReadReleaseConfiguration(release);
        var releaseIds = db.Releases.Where(x => x.ProjectId == id).Select(x => x.Id);
        var lastPackageAtUtc = await db.Packages
            .Where(x => releaseIds.Contains(x.ReleaseId) && x.GeneratedAtUtc != null)
            .MaxAsync(x => (DateTime?)x.GeneratedAtUtc, ct);
        return Results.Ok(ProjectDto(p, releaseConfig.Endpoints, releaseConfig.JsonContent, release?.Version, release?.BuiltAtUtc, lastPackageAtUtc));
    }
    private static async Task<IResult> SaveProject(Guid? id, ProjectInput input, HttpContext http, ClaimsPrincipal user, AppDbContext db, IDataProtectionProvider protection) { try { Validation.ProjectInput(input); } catch (Exception e) { return Problem(e.Message, 400); } var p = id == null ? new Project() : await db.Projects.SingleOrDefaultAsync(x => x.Id == id) ?? new Project(); if (id != null && p.Id != id) return Results.NotFound(); Map(input, p); if (!string.IsNullOrEmpty(input.SvnPassword)) p.SvnPasswordProtected = protection.CreateProtector("ReleaseManager.SvnCredential.v1").Protect(input.SvnPassword); if (id == null) db.Projects.Add(p); Audit(db, http, user, id == null ? "Project.Create" : "Project.Update", p.Name, "Success"); await db.SaveChangesAsync(); return Results.Ok(ProjectDto(p)); }
    private static async Task<IResult> SoftDeleteProject(Guid id, HttpContext http, ClaimsPrincipal user, AppDbContext db) { var p = await db.Projects.FindAsync(id); if (p == null) return Results.NotFound(); p.IsDeleted = true; p.IsEnabled = false; Audit(db, http, user, "Project.Delete", p.Name, "Success"); await db.SaveChangesAsync(); return Results.NoContent(); }
    private static async Task<IResult> TestSvn(Guid id, AppDbContext db, IDataProtectionProvider protection, ICommandRunner runner, CancellationToken ct) { var p = await db.Projects.FindAsync(id); if (p == null) return Results.NotFound(); var password = p.SvnPasswordProtected == null ? null : protection.CreateProtector("ReleaseManager.SvnCredential.v1").Unprotect(p.SvnPasswordProtected); var args = new List<string> { "info", p.SvnUrl, "--non-interactive", "--no-auth-cache", "--username", p.SvnUserName }; if (password != null) args.Add("--password-from-stdin"); var r = await runner.RunAsync("svn", args, Environment.CurrentDirectory, null, TimeSpan.FromSeconds(30), null, ct, password); return r.ExitCode == 0 ? Results.Ok(new { success = true, message = "SVN 连接成功。" }) : Problem("SVN 连接或认证失败，请检查地址、凭据和证书设置。", 400); }
    private static async Task<IResult> EnvironmentCheck(ICommandRunner runner, CancellationToken ct) { async Task<object> One(string file, params string[] args) { try { var r = await runner.RunAsync(file, args, Environment.CurrentDirectory, null, TimeSpan.FromSeconds(10), null, ct); return new { available = r.ExitCode == 0, version = (r.Output + r.Error).Trim() }; } catch (Exception e) { return new { available = false, version = e.Message }; } } var npm = ToolPaths.Npm(); return Results.Ok(new { dotnet = await One("dotnet", "--version"), node = await One("node", "--version"), npm = await One(npm.FileName, [.. npm.PrefixArguments, "--version"]), svn = await One("svn", "--version", "--quiet") }); }

    private static async Task<IResult> CreateDeployment(Guid id, RevisionRequest input, HttpContext http, ClaimsPrincipal user, AppDbContext db) { var p = await db.Projects.SingleOrDefaultAsync(x => x.Id == id && !x.IsDeleted && x.IsEnabled); if (p == null) return Results.NotFound(); var active = await db.DeploymentTasks.AnyAsync(x => x.ProjectId == id && x.FinishedAtUtc == null); if (active) return Results.Conflict(new { code = "DEPLOYMENT_ACTIVE", message = "此项目已有进行中的发布任务。" }); var task = new DeploymentTask { ProjectId = id, RequestedRevision = input.Revision, TriggeredByUserId = user.UserId(), TriggeredByName = user.UserName() }; db.DeploymentTasks.Add(task); Audit(db, http, user, "Deployment.Create", p.Name, "Queued"); await db.SaveChangesAsync(); return Results.Accepted($"/api/deployments/{task.Id}", new { task.Id, task.Status }); }
    private static async Task<IResult> CleanProjectWorkspace(Guid id, HttpContext http, ClaimsPrincipal user, AppDbContext db, DataPaths paths, CancellationToken ct)
    {
        var project = await db.Projects.SingleOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct);
        if (project == null) return Results.NotFound();
        if (!ReleaseOrchestrator.TryAcquireProject(id)) return Problem("项目正在发布，不能清理源码工作区。", 409);
        try
        {
            if (await db.DeploymentTasks.AnyAsync(x => x.ProjectId == id && x.FinishedAtUtc == null, ct))
                return Problem("项目已有排队或进行中的发布任务，不能清理源码工作区。", 409);

            var workspace = paths.SafeChild(paths.Workspaces, project.Slug);
            if (!Directory.Exists(workspace))
            {
                Audit(db, http, user, "Workspace.Clean", project.Name, "NoOp", "源码工作区不存在，无需清理");
                await db.SaveChangesAsync(ct);
                return Results.Ok(new { removed = false, fileCount = 0, freedBytes = 0L, message = "没有可清理的源码工作区。" });
            }

            var (fileCount, totalBytes) = MeasureDirectory(workspace);
            try
            {
                NormalizeAttributes(workspace);
                Directory.Delete(workspace, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Audit(db, http, user, "Workspace.Clean", project.Name, "Failed", ex.Message);
                await db.SaveChangesAsync(CancellationToken.None);
                return Problem("源码工作区清理失败，可能仍有程序占用其中的文件。", 409);
            }

            Audit(db, http, user, "Workspace.Clean", project.Name, "Success", $"删除 {fileCount} 个文件，释放 {totalBytes} 字节");
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { removed = true, fileCount, freedBytes = totalBytes, message = "上一次下载的源码已清理。" });
        }
        finally
        {
            ReleaseOrchestrator.ReleaseProject(id);
        }
    }
    private static async Task<IResult> Deployments(Guid id, AppDbContext db, int page = 1, int pageSize = 30) { var q = db.DeploymentTasks.Where(x => x.ProjectId == id).OrderByDescending(x => x.CreatedAtUtc); return Results.Ok(new { total = await q.CountAsync(), items = await q.Skip((page - 1) * pageSize).Take(Math.Min(pageSize, 100)).ToListAsync() }); }
    private static async Task<IResult> Deployment(Guid id, AppDbContext db) { var x = await db.DeploymentTasks.FindAsync(id); return x == null ? Results.NotFound() : Results.Ok(x); }
    private static async Task<IResult> DeploymentLogs(Guid id, AppDbContext db, long after = 0, int limit = 500, bool tail = false)
    {
        limit = Math.Clamp(limit, 1, 1000);
        if (tail)
        {
            var latest = await db.DeploymentLogs.Where(x => x.TaskId == id).OrderByDescending(x => x.Sequence).Take(limit).ToListAsync();
            latest.Reverse();
            return Results.Ok(latest);
        }
        return Results.Ok(await db.DeploymentLogs.Where(x => x.TaskId == id && x.Sequence > after).OrderBy(x => x.Sequence).Take(limit).ToListAsync());
    }
    private static async Task<IResult> CancelDeployment(Guid id, AppDbContext db) { var t = await db.DeploymentTasks.FindAsync(id); if (t == null) return Results.NotFound(); if (t.Status >= DeploymentStatus.Switching) return Problem("任务已进入版本切换阶段，不能取消。", 409); t.CancellationRequested = true; await db.SaveChangesAsync(); return Results.Accepted(); }

    private static async Task<IResult> Configurations(Guid id, AppDbContext db) => Results.Ok(await db.Configurations.Where(x => x.ProjectId == id).OrderByDescending(x => x.Version).Select(x => new { x.Id, x.Version, x.JsonContent, x.Comment, x.CreatedByUserId, x.CreatedAtUtc, x.IsActive }).ToListAsync());
    private static async Task<IResult> SaveConfiguration(Guid id, ConfigRequest input, HttpContext http, ClaimsPrincipal user, AppDbContext db) { try { using var json = Validation.ParseAppSettings(input.JsonContent); } catch (JsonException e) { return Results.Json(new { code = "JSON_INVALID", message = $"appsettings.json 格式错误（第 {(e.LineNumber ?? 0) + 1} 行，第 {(e.BytePositionInLine ?? 0) + 1} 列），请检查缺少的逗号、引号或括号。" }, statusCode: 400); } var version = (await db.Configurations.Where(x => x.ProjectId == id).MaxAsync(x => (int?)x.Version) ?? 0) + 1; await db.Configurations.Where(x => x.ProjectId == id).ExecuteUpdateAsync(x => x.SetProperty(y => y.IsActive, false)); var c = new AppConfiguration { ProjectId = id, Version = version, JsonContent = input.JsonContent, Comment = input.Comment, CreatedByUserId = user.UserId(), IsActive = true }; db.Configurations.Add(c); Audit(db, http, user, "Configuration.Save", id.ToString(), "Success", $"v{version}: {input.Comment}"); await db.SaveChangesAsync(); return Results.Ok(c); }
    private static async Task<IResult> ActivateConfiguration(Guid id, Guid configId, HttpContext http, ClaimsPrincipal user, AppDbContext db) { var c = await db.Configurations.SingleOrDefaultAsync(x => x.Id == configId && x.ProjectId == id); if (c == null) return Results.NotFound(); await db.Configurations.Where(x => x.ProjectId == id).ExecuteUpdateAsync(x => x.SetProperty(y => y.IsActive, false)); c.IsActive = true; Audit(db, http, user, "Configuration.Activate", id.ToString(), "Success", $"v{c.Version}"); await db.SaveChangesAsync(); return Results.Ok(c); }
    private static async Task<IResult> Releases(Guid id, AppDbContext db) => Results.Ok(await db.Releases.Where(x => x.ProjectId == id).OrderByDescending(x => x.BuiltAtUtc).ToListAsync());
    private static async Task<IResult> ReleaseFiles(Guid id, string? path, AppDbContext db, CancellationToken ct)
    {
        var release = await db.Releases.SingleOrDefaultAsync(x => x.Id == id && x.IsAvailable, ct);
        if (release == null || !Directory.Exists(release.DirectoryPath)) return Results.NotFound();
        string directory;
        try { directory = ResolveReleasePath(release.DirectoryPath, path, requireExisting: true); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FileNotFoundException or DirectoryNotFoundException) { return Problem(ex.Message, 400); }
        if (!Directory.Exists(directory)) return Problem("指定路径不是目录。", 400);
        var relativeDirectory = NormalizeRelativePath(Path.GetRelativePath(release.DirectoryPath, directory));
        var entries = Directory.EnumerateFileSystemEntries(directory)
            .Where(x => (File.GetAttributes(x) & FileAttributes.ReparsePoint) == 0)
            .Select(x =>
            {
                var isDirectory = Directory.Exists(x);
                var info = isDirectory ? (FileSystemInfo)new DirectoryInfo(x) : new FileInfo(x);
                return new { name = info.Name, path = NormalizeRelativePath(Path.GetRelativePath(release.DirectoryPath, x)), isDirectory, size = isDirectory ? (long?)null : ((FileInfo)info).Length, lastModifiedUtc = info.LastWriteTimeUtc };
            })
            .OrderByDescending(x => x.isDirectory).ThenBy(x => x.name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Results.Ok(new { release = new { release.Id, release.ProjectId, release.Version, release.SvnRevision, release.BuiltAtUtc, release.ManifestSha256 }, path = relativeDirectory, entries });
    }
    private static async Task<IResult> DownloadReleaseFile(Guid id, string path, HttpContext http, ClaimsPrincipal user, AppDbContext db, CancellationToken ct)
    {
        var release = await db.Releases.SingleOrDefaultAsync(x => x.Id == id && x.IsAvailable, ct);
        if (release == null) return Results.NotFound();
        string file;
        try { file = ResolveReleasePath(release.DirectoryPath, path, requireExisting: true); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FileNotFoundException) { return Problem(ex.Message, 400); }
        if (!File.Exists(file)) return Problem("指定路径不是文件。", 400);
        Audit(db, http, user, "ReleaseFile.Download", $"{release.Version}/{NormalizeRelativePath(path)}", "Success");
        await db.SaveChangesAsync(ct);
        return Results.File(file, "application/octet-stream", Path.GetFileName(file), enableRangeProcessing: true);
    }
    private static Task<IResult> AddReleaseFile(Guid id, HttpRequest request, HttpContext http, ClaimsPrincipal user, AppDbContext db, CancellationToken ct) =>
        SaveReleaseFile(id, request, http, user, db, replace: false, ct);
    private static Task<IResult> ReplaceReleaseFile(Guid id, HttpRequest request, HttpContext http, ClaimsPrincipal user, AppDbContext db, CancellationToken ct) =>
        SaveReleaseFile(id, request, http, user, db, replace: true, ct);
    private static async Task<IResult> SaveReleaseFile(Guid id, HttpRequest request, HttpContext http, ClaimsPrincipal user, AppDbContext db, bool replace, CancellationToken ct)
    {
        if (!request.HasFormContentType) return Problem("请使用 multipart/form-data 上传文件。", 400);
        var release = await db.Releases.SingleOrDefaultAsync(x => x.Id == id && x.IsAvailable, ct);
        if (release == null || !Directory.Exists(release.DirectoryPath)) return Results.NotFound();
        var form = await request.ReadFormAsync(ct);
        var upload = form.Files.GetFile("file");
        if (upload == null || upload.Length == 0) return Problem("请选择非空文件。", 400);
        if (upload.FileName.Length > 255 || Path.GetFileName(upload.FileName) != upload.FileName) return Problem("文件名无效。", 400);
        var requestedPath = replace ? form["path"].ToString() : Path.Combine(form["directory"].ToString(), upload.FileName);
        string destination;
        try { destination = ResolveReleasePath(release.DirectoryPath, requestedPath, requireExisting: replace); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FileNotFoundException) { return Problem(ex.Message, 400); }
        if (replace && !File.Exists(destination)) return Problem("要替换的文件不存在。", 404);
        if (!replace && File.Exists(destination)) return Problem("当前目录已存在同名文件，请使用替换文件功能。", 409);
        if (Directory.Exists(destination)) return Problem("目标路径是目录，不能写入文件。", 409);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var temporary = destination + $".upload-{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                    await upload.CopyToAsync(stream, ct);
                File.Move(temporary, destination, replace);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Problem($"文件写入失败：{ex.Message}", 409); }
        release.ManifestSha256 = ComputeReleaseManifest(release.DirectoryPath);
        await db.Packages.Where(x => x.ReleaseId == release.Id && x.State == PackageState.Ready)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.State, PackageState.Failed).SetProperty(y => y.Error, "版本文件已修改，请重新生成发布包。"), ct);
        var relative = NormalizeRelativePath(Path.GetRelativePath(release.DirectoryPath, destination));
        Audit(db, http, user, replace ? "ReleaseFile.Replace" : "ReleaseFile.Add", $"{release.Version}/{relative}", "Success", $"{upload.Length} 字节");
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { path = relative, size = upload.Length, release.ManifestSha256 });
    }
    private static async Task<IResult> Rollback(Guid id, Guid releaseId, HttpContext http, ClaimsPrincipal user, AppDbContext db, RuntimeService runtime, CancellationToken ct) { var p = await db.Projects.FindAsync([id], ct); var r = await db.Releases.SingleOrDefaultAsync(x => x.Id == releaseId && x.ProjectId == id && x.IsAvailable, ct); if (p == null || r == null) return Results.NotFound(); try { await runtime.StopAsync(id, false, ct); } catch { } p.CurrentReleaseId = r.Id; db.DeploymentTasks.Add(new DeploymentTask { ProjectId = id, Status = DeploymentStatus.RolledBack, Progress = 100, TriggeredByUserId = user.UserId(), TriggeredByName = user.UserName(), ResolvedRevision = r.SvnRevision, ReleaseVersion = r.Version, StartedAtUtc = DateTime.UtcNow, FinishedAtUtc = DateTime.UtcNow }); await db.SaveChangesAsync(ct); await runtime.StartAsync(id, ct); Audit(db, http, user, "Release.Rollback", r.Version, "Success"); await db.SaveChangesAsync(ct); return Results.Ok(r); }
    private static async Task<IResult> CreatePackage(Guid id, HttpContext http, ClaimsPrincipal user, PackageService service, AppDbContext db, CancellationToken ct) { var p = await service.CreateAsync(id, ct); Audit(db, http, user, "Package.Create", id.ToString(), p.State.ToString()); await db.SaveChangesAsync(ct); return Results.Ok(p); }
    private static async Task<IResult> DownloadPackage(Guid id, HttpContext http, ClaimsPrincipal user, AppDbContext db, CancellationToken ct) { var p = await db.Packages.FindAsync([id], ct); if (p == null || p.State != PackageState.Ready || !File.Exists(p.Path)) return Results.NotFound(); Audit(db, http, user, "Package.Download", id.ToString(), "Success", "下载包可能包含敏感配置"); await db.SaveChangesAsync(ct); return Results.File(p.Path, "application/zip", Path.GetFileName(p.Path), enableRangeProcessing: true); }

    private static async Task<IResult> RuntimeStatus(Guid id, RuntimeService service, CancellationToken ct) => Results.Ok(await service.StatusAsync(id, ct));
    private static async Task<IResult> RuntimeStart(Guid id, HttpContext http, ClaimsPrincipal user, RuntimeService service, AppDbContext db, CancellationToken ct) { try { var p = await service.StartAsync(id, ct); Audit(db, http, user, "Runtime.Start", id.ToString(), "Success"); await db.SaveChangesAsync(ct); return Results.Ok(p); } catch (InvalidOperationException ex) { return Problem(ex.Message, 409); } }
    private static async Task<IResult> RuntimeStop(Guid id, bool? force, HttpContext http, ClaimsPrincipal user, RuntimeService service, AppDbContext db, CancellationToken ct) { try { var isForce = force == true; var p = await service.StopAsync(id, isForce, ct); Audit(db, http, user, isForce ? "Runtime.ForceStop" : "Runtime.Stop", id.ToString(), "Success"); await db.SaveChangesAsync(ct); return Results.Ok(p); } catch (InvalidOperationException ex) { return Problem(ex.Message, 409); } }
    private static async Task<IResult> RuntimeRestart(Guid id, HttpContext http, ClaimsPrincipal user, RuntimeService service, AppDbContext db, CancellationToken ct) { try { try { await service.StopAsync(id, false, ct); } catch (InvalidOperationException ex) when (ex.Message == "项目未运行。") { } var p = await service.StartAsync(id, ct); Audit(db, http, user, "Runtime.Restart", id.ToString(), "Success"); await db.SaveChangesAsync(ct); return Results.Ok(p); } catch (InvalidOperationException ex) { return Problem(ex.Message, 409); } }
    private static async Task<IResult> RuntimeLog(Guid id, AppDbContext db, int lines = 300) { var p = await db.ManagedProcesses.Where(x => x.ProjectId == id).OrderByDescending(x => x.StartedAtUtc).FirstOrDefaultAsync(); if (p == null || !File.Exists(p.LogPath)) return Results.Ok(new { content = "" }); var all = await File.ReadAllLinesAsync(p.LogPath); return Results.Ok(new { content = string.Join(Environment.NewLine, all.TakeLast(Math.Min(lines, 2000))) }); }

    private static async Task<IResult> Users(AppDbContext db) => Results.Ok(await db.Users.OrderBy(x => x.UserName).Select(x => new { x.Id, x.UserName, x.Role, x.IsEnabled, x.MustChangePassword, x.LastLoginUtc, x.CreatedAtUtc }).ToListAsync());
    private static async Task<IResult> CreateUser(UserRequest input, AppDbContext db) { if (!Roles.All.Contains(input.Role) || input.Password.Length < 10) return Problem("角色无效或密码少于 10 个字符。", 400); var u = new User { UserName = input.UserName, Role = input.Role, IsEnabled = input.IsEnabled, MustChangePassword = true }; u.PasswordHash = new PasswordHasher<User>().HashPassword(u, input.Password); db.Users.Add(u); await db.SaveChangesAsync(); return Results.Ok(new { u.Id, u.UserName, u.Role, u.IsEnabled }); }
    private static async Task<IResult> UpdateUser(Guid id, UserUpdateRequest input, HttpContext http, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct)
    {
        var user = await db.Users.FindAsync([id], ct);
        if (user == null) return Results.NotFound();
        if (!Roles.All.Contains(input.Role)) return Problem("角色无效。", 400);
        var removesEnabledAdmin = user.Role == Roles.Admin && user.IsEnabled && (input.Role != Roles.Admin || !input.IsEnabled);
        if (removesEnabledAdmin && !await db.Users.AnyAsync(x => x.Id != id && x.Role == Roles.Admin && x.IsEnabled, ct))
            return Problem("系统至少需要保留一个启用的管理员账户。", 409);
        user.Role = input.Role;
        user.IsEnabled = input.IsEnabled;
        if (!string.IsNullOrEmpty(input.Password))
        {
            if (input.Password.Length < 10) return Problem("密码至少 10 个字符。", 400);
            user.PasswordHash = new PasswordHasher<User>().HashPassword(user, input.Password);
            user.MustChangePassword = true;
            await db.Sessions.Where(x => x.UserId == id).ExecuteDeleteAsync(ct);
            Audit(db, http, principal, "User.PasswordReset", user.UserName, "Success", "已注销目标用户全部会话，下次登录必须修改密码");
        }
        else
        {
            Audit(db, http, principal, "User.Update", user.UserName, "Success", $"Role={user.Role}, Enabled={user.IsEnabled}");
        }
        await db.SaveChangesAsync(ct);
        return Results.Ok();
    }
    private static async Task<IResult> GetAudit(AppDbContext db, int page = 1, int pageSize = 50, string? action = null) { var q = db.AuditLogs.Where(x => action == null || x.Action.Contains(action)).OrderByDescending(x => x.TimestampUtc); return Results.Ok(new { total = await q.CountAsync(), items = await q.Skip((page - 1) * pageSize).Take(Math.Min(pageSize, 100)).ToListAsync() }); }
    private static IResult Settings(IConfiguration c, DataPaths paths, IOptions<PlatformOptions> o) => Results.Ok(new { dataRoot = paths.Root, o.Value.MaxConcurrentDeployments, o.Value.SessionHours, platform = Environment.OSVersion.ToString(), framework = Environment.Version.ToString() });

    private static object ProjectDto(Project p, IReadOnlyList<string>? endpoints = null, string? appSettingsJson = null, string? currentVersion = null, DateTime? lastBuiltAtUtc = null, DateTime? lastPackageAtUtc = null) => new { p.Id, p.Slug, p.Name, p.Description, p.IsEnabled, p.SvnUrl, p.SvnUserName, hasSvnPassword = p.SvnPasswordProtected != null, p.TrustServerCertificate, p.TrunkPath, p.BackendDirectory, p.BackendProjectFile, p.EntryAssembly, p.AdminDirectory, p.TerminalDirectory, p.AdminBuildCommand, p.TerminalBuildCommand, p.AdminOutputDirectory, p.TerminalOutputDirectory, p.BuildConfiguration, p.TargetFramework, p.EnvironmentVariablesJson, p.StartArgumentsJson, p.HealthCheckUrl, p.HealthExpectedStatus, p.HealthTimeoutSeconds, p.StopTimeoutSeconds, p.BuildTimeoutMinutes, p.RetainReleaseCount, p.RetainPackageDays, p.CurrentReleaseId, p.CreatedAtUtc, p.UpdatedAtUtc, endpoints = endpoints ?? [], appSettingsJson, currentVersion, lastBuiltAtUtc, lastPackageAtUtc };
    private static void Map(ProjectInput i, Project p) { p.Slug = i.Slug; p.Name = i.Name; p.Description = i.Description; p.IsEnabled = i.IsEnabled; p.SvnUrl = i.SvnUrl; p.SvnUserName = i.SvnUserName; p.TrustServerCertificate = i.TrustServerCertificate; p.TrunkPath = i.TrunkPath; p.BackendDirectory = i.BackendDirectory; p.BackendProjectFile = i.BackendProjectFile; p.EntryAssembly = i.EntryAssembly; p.AdminDirectory = i.AdminDirectory; p.TerminalDirectory = i.TerminalDirectory; p.AdminBuildCommand = i.AdminBuildCommand; p.TerminalBuildCommand = i.TerminalBuildCommand; p.AdminOutputDirectory = i.AdminOutputDirectory; p.TerminalOutputDirectory = i.TerminalOutputDirectory; p.BuildConfiguration = i.BuildConfiguration; p.TargetFramework = i.TargetFramework; p.EnvironmentVariablesJson = i.EnvironmentVariablesJson; p.StartArgumentsJson = i.StartArgumentsJson; p.HealthCheckUrl = i.HealthCheckUrl; p.HealthExpectedStatus = i.HealthExpectedStatus; p.HealthTimeoutSeconds = i.HealthTimeoutSeconds; p.StopTimeoutSeconds = i.StopTimeoutSeconds; p.BuildTimeoutMinutes = i.BuildTimeoutMinutes; p.RetainReleaseCount = i.RetainReleaseCount; p.RetainPackageDays = i.RetainPackageDays; p.UpdatedAtUtc = DateTime.UtcNow; }
    private static void Audit(AppDbContext db, HttpContext http, ClaimsPrincipal user, string action, string target, string result, string details = "") => db.AuditLogs.Add(new AuditLog { UserId = user.Identity?.IsAuthenticated == true ? user.UserId() : null, UserName = user.Identity?.Name ?? "anonymous", Action = action, Target = target, Result = result, Details = details, IpAddress = http.Connection.RemoteIpAddress?.ToString() ?? "" });
    private static void Audit(AppDbContext db, HttpContext http, User user, string action, string target, string result, string details = "") => db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.UserName, Action = action, Target = target, Result = result, Details = details, IpAddress = http.Connection.RemoteIpAddress?.ToString() ?? "" });
    private static (long FileCount, long TotalBytes) MeasureDirectory(string path)
    {
        long count = 0, bytes = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in Directory.EnumerateFiles(path, "*", options))
        {
            count++;
            try { bytes += new FileInfo(file).Length; } catch (IOException) { }
        }
        return (count, bytes);
    }
    private static void NormalizeAttributes(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in Directory.EnumerateFiles(path, "*", options)) File.SetAttributes(file, FileAttributes.Normal);
    }
    private static (string? JsonContent, IReadOnlyList<string> Endpoints) ReadReleaseConfiguration(Release? release)
    {
        if (release == null) return (null, []);
        var path = Path.Combine(release.DirectoryPath, "appsettings.json");
        if (!File.Exists(path)) return (null, []);
        try
        {
            var content = File.ReadAllText(path);
            using var document = Validation.ParseAppSettings(content);
            var endpoints = new List<string>();
            if (TryGetProperty(document.RootElement, "Kestrel", out var kestrel)
                && TryGetProperty(kestrel, "Endpoints", out var endpointObject)
                && endpointObject.ValueKind == JsonValueKind.Object)
            {
                foreach (var endpoint in endpointObject.EnumerateObject())
                    if (endpoint.Value.ValueKind == JsonValueKind.Object
                        && TryGetProperty(endpoint.Value, "Url", out var url)
                        && url.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(url.GetString())) endpoints.Add(url.GetString()!);
            }
            return (content, endpoints);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return (null, []); }
    }
    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        value = default;
        return false;
    }
    private static string ResolveReleasePath(string root, string? relativePath, bool requireExisting)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var relative = string.IsNullOrWhiteSpace(relativePath) || relativePath == "." ? "" : relativePath.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(relative) || relative.IndexOf('\0') >= 0 || relative.Split(Path.DirectorySeparatorChar).Any(x => x == "..")) throw new ArgumentException("文件路径无效。");
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relative));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (fullPath != fullRoot && !fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison)) throw new InvalidOperationException("文件路径超出版本目录。");
        var existing = fullRoot;
        foreach (var segment in Path.GetRelativePath(fullRoot, fullPath).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            existing = Path.Combine(existing, segment);
            if ((File.Exists(existing) || Directory.Exists(existing)) && (File.GetAttributes(existing) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("不允许访问版本目录中的链接路径。");
        }
        if (requireExisting && !File.Exists(fullPath) && !Directory.Exists(fullPath)) throw new FileNotFoundException("指定的文件或目录不存在。");
        return fullPath;
    }
    private static string NormalizeRelativePath(string path) => path == "." ? "" : path.Replace('\\', '/');
    private static string ComputeReleaseManifest(string root)
    {
        using var sha = SHA256.Create();
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
            var bytes = Encoding.UTF8.GetBytes(Path.GetRelativePath(root, file) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }
    private static IResult Problem(string message, int status) => Results.Json(new { code = "REQUEST_FAILED", message }, statusCode: status);
}

public sealed record LoginRequest(string UserName, string Password);
public sealed record PasswordRequest(string CurrentPassword, string NewPassword);
public sealed record RevisionRequest(long? Revision);
public sealed record ConfigRequest(string JsonContent, string Comment);
public sealed record UserRequest(string UserName, string Password, string Role, bool IsEnabled);
public sealed record UserUpdateRequest(string Role, bool IsEnabled, string? Password);
