using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ReleaseManager.Api.Infrastructure;

public sealed record CommandResult(int ExitCode, string Output, string Error, TimeSpan Duration);

public interface ICommandRunner
{
    Task<CommandResult> RunAsync(string fileName, IEnumerable<string> arguments, string workingDirectory, IDictionary<string, string>? environment, TimeSpan timeout, Func<string, bool, Task>? onLine, CancellationToken cancellationToken, string? standardInput = null);
}

public sealed class CommandRunner : ICommandRunner
{
    private static readonly Regex SecretPattern = new(@"(?i)(password|pwd|token|secret)(\s*[=:]\s*)([^\s;,]+)", RegexOptions.Compiled);
    private static readonly Regex AnsiPattern = new(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);

    public async Task<CommandResult> RunAsync(string fileName, IEnumerable<string> arguments, string workingDirectory, IDictionary<string, string>? environment, TimeSpan timeout, Func<string, bool, Task>? onLine, CancellationToken cancellationToken, string? standardInput = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput != null,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);
        if (environment != null) foreach (var pair in environment) psi.Environment[pair.Key] = pair.Value;
        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder(); var stderr = new StringBuilder(); var started = Stopwatch.StartNew();
        process.Start();
        if (standardInput != null) { await process.StandardInput.WriteLineAsync(standardInput); process.StandardInput.Close(); }
        async Task DrainAsync(Stream stream, StringBuilder target, bool isError)
        {
            await foreach (var rawLine in AdaptiveLineReader.ReadLinesAsync(stream, cancellationToken))
            {
                var line = rawLine;
                line = AnsiPattern.Replace(SecretPattern.Replace(line, "$1$2******"), ""); target.AppendLine(line);
                if (onLine != null) await onLine(line, isError);
            }
        }
        var outTask = DrainAsync(process.StandardOutput.BaseStream, stdout, false); var errTask = DrainAsync(process.StandardError.BaseStream, stderr, true);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeoutCts.CancelAfter(timeout);
        try { await process.WaitForExitAsync(timeoutCts.Token); await Task.WhenAll(outTask, errTask); }
        catch (OperationCanceledException) { try { process.Kill(true); } catch { } throw; }
        return new CommandResult(process.ExitCode, stdout.ToString(), stderr.ToString(), started.Elapsed);
    }
}
