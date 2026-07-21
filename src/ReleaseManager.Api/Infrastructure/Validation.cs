using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReleaseManager.Api.Infrastructure;

public static partial class Validation
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,62}$")]
    private static partial Regex SlugRegex();

    public static void RelativePath(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) || value.Split('/', '\\').Any(x => x == "..")) throw new ArgumentException($"{field} 必须是无目录穿越的相对路径。");
    }

    public static JsonDocument ParseAppSettings(string json) => JsonDocument.Parse(json, new JsonDocumentOptions
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    });

    public static void ProjectInput(ProjectInput input)
    {
        if (!SlugRegex().IsMatch(input.Slug)) throw new ArgumentException("项目标识只能包含小写字母、数字和连字符。");
        if (!Uri.TryCreate(input.SvnUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "svn")) throw new ArgumentException("SVN 地址无效。");
        foreach (var (path, field) in new[] { (input.TrunkPath, "主干路径"), (input.BackendDirectory, "后台目录"), (input.BackendProjectFile, "项目文件"), (input.AdminDirectory, "admin 目录"), (input.TerminalDirectory, "terminal 目录"), (input.AdminOutputDirectory, "admin 输出目录"), (input.TerminalOutputDirectory, "terminal 输出目录") }) RelativePath(path, field);
        if (input.AdminBuildCommand != "npm run build" || input.TerminalBuildCommand != "npm run build") throw new ArgumentException("当前仅允许构建命令 npm run build。");
        JsonDocument.Parse(input.EnvironmentVariablesJson); JsonDocument.Parse(input.StartArgumentsJson);
    }
}

public sealed record ProjectInput(string Slug, string Name, string Description, bool IsEnabled, string SvnUrl, string SvnUserName, string? SvnPassword, bool TrustServerCertificate, string TrunkPath, string BackendDirectory, string BackendProjectFile, string EntryAssembly, string AdminDirectory, string TerminalDirectory, string AdminBuildCommand, string TerminalBuildCommand, string AdminOutputDirectory, string TerminalOutputDirectory, string BuildConfiguration, string TargetFramework, string EnvironmentVariablesJson, string StartArgumentsJson, string? HealthCheckUrl, int HealthExpectedStatus, int HealthTimeoutSeconds, int StopTimeoutSeconds, int BuildTimeoutMinutes, int RetainReleaseCount, int RetainPackageDays);
