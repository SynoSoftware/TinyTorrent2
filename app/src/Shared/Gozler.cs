using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Syno.TinyTorrent.Shared;

/// <summary>
/// Runs the person's gozler.exe, the only way TinyTorrent asks gozler
/// anything. gozler's pipe protocol must match its version exactly, while
/// its command line keeps working across versions.
/// </summary>
internal static class Gozler
{
    // The release that names each file by its volume and ID. A newer gozler
    // is used as it is, because later releases only add fields.
    internal static readonly Version ExpectedVersion = new(0, 5, 10);

    private const string IdentityOption = "--file-id";

    // The fields of a result that name its file.
    internal const string VolumeField = "volume";
    internal const string IdField = "file_id";

    // gozler refuses a query with more terms, counting each ext: value.
    private const int MaxTerms = 256;
    private const int Tries = 3;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Pause = TimeSpan.FromSeconds(5);

    // .ts and .mts are also TypeScript files, which never reach 20 MB; a
    // transport-stream video below it is one segment of a film. As its own
    // clause the rule costs 3 terms: inside the media group, gozler would
    // distribute the group and count every extension twice.
    private const string Streams = "!ext:ts;mts|size:>20mb";

    internal static int HiddenLimit { get; } = MaxTerms - Terms([]).Count;

    internal static GozlerProgram Locate() => Find(out _);

    // The program comes from the folder of the gozler service's program,
    // so a folder that anyone can write to never decides what runs.
    private static GozlerProgram Find(out string program)
    {
        program = string.Empty;
        using var service = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Services\gozler"
        );
        if (service?.GetValue("ImagePath") is not string image)
        {
            return GozlerProgram.NotInstalled;
        }
        var command = Environment.ExpandEnvironmentVariables(image).Trim();
        var daemon = command.StartsWith('"')
            ? command[1..Math.Max(1, command.IndexOf('"', 1))]
            : command.Split(' ', 2)[0];
        if (!Path.IsPathFullyQualified(daemon))
        {
            return GozlerProgram.ProgramMissing;
        }
        program = Path.Combine(Path.GetDirectoryName(daemon)!, "gozler.exe");
        return File.Exists(program) ? GozlerProgram.Installed : GozlerProgram.ProgramMissing;
    }

    // Asks for every media file that Library wants, with each file's
    // identity. An answer that may miss files is still used.
    internal static async Task<GozlerAnswer> Search(
        IReadOnlyCollection<string> hidden,
        CancellationToken cancellation
    )
    {
        List<string> arguments = ["--json", "-n", "all", "-a-d", "-attributes", IdentityOption];
        arguments.AddRange(Terms(hidden).Terms);
        for (var attempt = 1; ; attempt++)
        {
            var reply = await Run(arguments, cancellation).ConfigureAwait(false);
            switch (reply.Code)
            {
                case 0:
                    return new(reply.Output, Incomplete: false);
                case 2:
                    return new(reply.Output, Incomplete: true);
                // The service's pipe may not be open yet.
                case 7 when attempt < Tries:
                    await Task.Delay(Pause, cancellation).ConfigureAwait(false);
                    continue;
                default:
                    throw await Explain(reply, cancellation).ConfigureAwait(false);
            }
        }
    }

    internal static async Task<IReadOnlyList<GozlerVolume>> Status(CancellationToken cancellation)
    {
        var reply = await Run(["--json", "--status"], cancellation).ConfigureAwait(false);
        if (reply.Code is not (0 or 2))
        {
            throw await Explain(reply, cancellation).ConfigureAwait(false);
        }
        using var status = JsonDocument.Parse(reply.Output);
        var volumes = new List<GozlerVolume>();
        foreach (var volume in status.RootElement.GetProperty("volumes").EnumerateArray())
        {
            var readiness = volume.GetProperty("phase").GetProperty("phase").GetString() switch
            {
                "Live" or "Snapshot" => VolumeReadiness.Ready,
                "Bootstrapping" or "Rescanning" => VolumeReadiness.Indexing,
                _ => VolumeReadiness.Stuck,
            };
            volumes.Add(
                new(
                    volume.GetProperty("guid").GetString()!,
                    volume
                        .GetProperty("mounts")
                        .EnumerateArray()
                        .Select(mount => mount.GetString()!)
                        .ToArray(),
                    readiness
                )
            );
        }
        return volumes;
    }

    // The query's terms and how many of gozler's terms they use. Every path
    // is quoted inside its term: without the quotes, a path with a space
    // still makes a valid query that matches nothing.
    internal static (IReadOnlyList<string> Terms, int Count) Terms(IReadOnlyCollection<string> hidden)
    {
        string[] extensions =
        [
            .. FileName.VideoExtensions,
            .. FileName.AudioExtensions,
            .. FileName.SubtitleExtensions,
        ];
        var media = "ext:" + string.Join(';', extensions.Select(extension => extension.TrimStart('.')));
        var profiles = Path.GetDirectoryName(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        );
        string[] system =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        ];
        var left = system
            .Where(folder => folder.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(Excluded)
            .ToList();
        if (!string.IsNullOrEmpty(profiles))
        {
            left.Add($"!\"{Path.TrimEndingDirectorySeparator(profiles)}\\*\\AppData\\*\"");
        }
        left.Add("!\"*\\$Recycle.Bin\\*\"");
        left.AddRange(
            hidden
                .Where(folder =>
                    !hidden.Any(other => other != folder && FolderComparer.Instance.Contains(other, folder))
                )
                .Select(Excluded)
        );
        List<string> terms = [media, Streams, .. left];
        // gozler counts each ext: value, each other term, and -a-d.
        var count = extensions.Length + 3 + left.Count + 1;
        return (terms, count);

        static string Excluded(string folder) => $"!\"{FileSources.Folder(folder)}*\"";
    }

    private static async Task<GozlerException> Explain(Reply reply, CancellationToken cancellation)
    {
        switch (reply.Code)
        {
            case 7:
                return new(new DiskState(DiskStatus.NoAnswer));
            case 8:
                return new(new DiskState(DiskStatus.Stopped));
        }
        Debug.WriteLine($"gozler exited with {reply.Code}: {reply.Errors}");
        // A failed call is the only time the version is asked, so a gozler
        // that works is never questioned.
        var answer = await Run(["--json", "--server-version"], cancellation).ConfigureAwait(false);
        if (answer.Code == 6)
        {
            return new(new DiskState(DiskStatus.Older), reply.Errors);
        }
        if (answer.Code == 0 && ReadVersion(answer.Output) is { } version && version < ExpectedVersion)
        {
            return new(new DiskState(DiskStatus.Older) { Version = version.ToString() }, reply.Errors);
        }
        return new(new DiskState(DiskStatus.Failed), reply.Errors);
    }

    // The answer is a JSON string such as "gozlerd 0.5.9".
    private static Version? ReadVersion(byte[] output)
    {
        try
        {
            var text = JsonSerializer.Deserialize<string>(output);
            return Version.TryParse(text?.Split(' ').LastOrDefault(), out var version) ? version : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Runs gozler.exe directly, never through a shell, with each argument
    // passed on its own, and ends it at the deadline.
    private static async Task<Reply> Run(IEnumerable<string> arguments, CancellationToken cancellation)
    {
        var located = Find(out var program);
        if (located != GozlerProgram.Installed)
        {
            throw new GozlerException(
                new DiskState(
                    located == GozlerProgram.NotInstalled
                        ? DiskStatus.NotInstalled
                        : DiskStatus.ProgramMissing
                )
            );
        }
        var start = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        Process process;
        try
        {
            process = Process.Start(start)!;
        }
        catch (Win32Exception error)
        {
            throw new GozlerException(new DiskState(DiskStatus.Failed), error.Message);
        }
        using (process)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(Deadline);
            try
            {
                using var output = new MemoryStream();
                var reading = process.StandardOutput.BaseStream.CopyToAsync(output, deadline.Token);
                var errors = process.StandardError.ReadToEndAsync(deadline.Token);
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                await reading.ConfigureAwait(false);
                return new(process.ExitCode, output.ToArray(), await errors.ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                cancellation.ThrowIfCancellationRequested();
                throw new GozlerException(new DiskState(DiskStatus.Timeout));
            }
        }
    }

    private sealed record Reply(int Code, byte[] Output, string Errors);
}

internal enum GozlerProgram
{
    NotInstalled,
    ProgramMissing,
    Installed,
}

// Whether gozler's index of a volume is whole, is being built and will be
// soon, or cannot keep up until someone acts in gozler.
internal enum VolumeReadiness
{
    Ready,
    Indexing,
    Stuck,
}

internal sealed record GozlerVolume(string Volume, IReadOnlyList<string> Mounts, VolumeReadiness Readiness);

internal sealed record GozlerAnswer(byte[] Json, bool Incomplete);

// gozler could not give an answer; the state says what the person sees.
internal sealed class GozlerException(DiskState state, string? errors = null)
    : Exception(errors ?? state.Status.ToString()), IFailure
{
    internal DiskState State { get; } = state;

    // gozler's stderr goes to the debug log, not to the person.
    public string Describe(Strings text) => State.Describe(text);
}
