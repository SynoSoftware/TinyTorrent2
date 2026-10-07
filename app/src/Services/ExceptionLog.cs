using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Syno.TinyTorrent.Services;

internal static class ExceptionLog
{
    private static readonly object Gate = new();

    internal static string? Write(string directory, Exception error, string source, string? message = null)
    {
        try
        {
            var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var report = $"""
                TinyTorrent UI exception
                Time (UTC): {DateTimeOffset.UtcNow:O}
                Version: {version}
                Source: {source}
                Process: {Environment.ProcessId}
                Executable: {Environment.ProcessPath}
                Thread: {Environment.CurrentManagedThreadId}
                OS: {RuntimeInformation.OSDescription}
                Runtime: {RuntimeInformation.FrameworkDescription}
                Architecture: {RuntimeInformation.ProcessArchitecture}
                HRESULT: 0x{error.HResult:X8}
                Message: {message ?? error.Message}

                {error}
                """;
            // This character limit keeps each UTF-8 report below 1 MiB.
            if (report.Length > 250_000) report = report[..250_000] + "\n[Report truncated]";
            Debug.WriteLine(report);
            lock (Gate)
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "ui-error.log");
                var previous = Path.Combine(directory, "ui-error.previous.log");
                if (File.Exists(path)) File.Move(path, previous, overwrite: true);
                File.WriteAllText(path, report, Encoding.UTF8);
                return path;
            }
        }
        catch (Exception failure)
        {
            Debug.WriteLine($"Could not save the exception report: {failure}");
            return null;
        }
    }
}
