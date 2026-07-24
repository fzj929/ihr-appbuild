using System.Text;

namespace ReleaseManager.Api.Infrastructure;

public static class StartupDiagnostics
{
    public static string ResolveLogPath(string[] args)
    {
        string? configured = null;
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index].StartsWith("--DataRoot=", StringComparison.OrdinalIgnoreCase))
                configured = args[index]["--DataRoot=".Length..].Trim('"');
            else if (args[index].Equals("--DataRoot", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
                configured = args[++index].Trim('"');
        }
        var root = configured ?? Path.Combine(AppContext.BaseDirectory, "data");
        if (!Path.IsPathRooted(root)) root = Path.GetFullPath(root, AppContext.BaseDirectory);
        return Path.Combine(root, "logs", "service-startup.log");
    }

    public static void Write(string path, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var text = $"{DateTimeOffset.Now:O} STARTUP FAILURE{Environment.NewLine}{exception}{Environment.NewLine}{new string('-', 80)}{Environment.NewLine}";
            File.AppendAllText(path, text, new UTF8Encoding(false));
        }
        catch { }
    }
}
