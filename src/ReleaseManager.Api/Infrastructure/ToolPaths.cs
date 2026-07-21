namespace ReleaseManager.Api.Infrastructure;

public sealed record ToolInvocation(string FileName, string[] PrefixArguments);

public static class ToolPaths
{
    public static ToolInvocation Npm()
    {
        if (!OperatingSystem.IsWindows()) return new ToolInvocation("npm", []);
        var path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim(' ', '"')).Select(x => Path.Combine(x, "node.exe")).FirstOrDefault(File.Exists);
        if (path == null) return new ToolInvocation("node", ["npm-cli.js"]);
        var cli = Path.Combine(Path.GetDirectoryName(path)!, "node_modules", "npm", "bin", "npm-cli.js");
        return new ToolInvocation(path, [cli]);
    }
}
