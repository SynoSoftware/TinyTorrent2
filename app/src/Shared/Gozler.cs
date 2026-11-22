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
    internal const string Repository = "SynoSoftware/gozler";

    // Where a person installs gozler by hand.
    internal const string Releases = "https://github.com/" + Repository + "/releases";

    private const string IdentityOption = "--file-id";

    // Only the files and folders the person can open, even when an
    // administrator put the person's account on gozlerd's list of accounts
    // that see every file: Library and its folder tree offer only what the
    // person can open. --status lists volumes, not entries, so it goes
    // without.
    private const string OwnAccessOption = "--my-permissions";

    // The gozler release whose setup the install prompt downloads, written by
    // hand: the owner moves it to a newer release that Library has been tried
    // with. An installed gozler is judged by the options it knows, never by
    // this number.
    internal static readonly Version SetupRelease = new(0, 6, 2);

    // The fields of a result that name its file.
    internal const string VolumeField = "volume";
    internal const string IdField = "file_id";

    private const int Tries = 3;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Pause = TimeSpan.FromSeconds(5);

    // .ts and .mts are also TypeScript files, which never reach 20 MB; a
    // transport-stream video below it is one segment of a film. As its own
    // clause the rule costs 3 terms: inside the media group, gozler would
    // distribute the group and count every extension twice.
    private const string Streams = "!ext:ts;mts|size:>20mb";

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
    internal static async Task<GozlerAnswer> Search(FolderChoices folders, CancellationToken cancellation)
    {
        var arguments = Arguments(folders);
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
                    throw Explain(reply);
            }
        }
    }

    // The folders directly in `parent` that gozler indexes, with their
    // attributes. Tab-separated output, because a folder of 25,662 folders
    // takes 252 ms that way and 1,057 ms as JSON. An answer that may miss
    // folders is still used.
    internal static async Task<GozlerFolders> Folders(string parent, CancellationToken cancellation)
    {
        string[] arguments = [OwnAccessOption, "-parent", parent, "/ad", "-attributes", "-n", "all", "-tsv"];
        for (var attempt = 1; ; attempt++)
        {
            var reply = await Run(arguments, cancellation).ConfigureAwait(false);
            switch (reply.Code)
            {
                case 0:
                case 2:
                    return new(ReadFolders(reply.Output), Incomplete: reply.Code == 2);
                // The folder is gone since its parent was listed. It fails only
                // itself, not gozler's answer for every other folder.
                case 4:
                    return new([], Incomplete: false, Missing: true);
                // The service's pipe may not be open yet.
                case 7 when attempt < Tries:
                    await Task.Delay(Pause, cancellation).ConfigureAwait(false);
                    continue;
                default:
                    throw Explain(reply);
            }
        }
    }

    // Each line after the header is the folder's FILE_ATTRIBUTE_* number, a
    // tab and its path, in UTF-8. Windows allows no tab in a name.
    private static IReadOnlyList<GozlerFolder> ReadFolders(byte[] output)
    {
        var folders = new List<GozlerFolder>();
        var lines = output.AsSpan();
        var header = true;
        while (!lines.IsEmpty)
        {
            var end = lines.IndexOf((byte)'\n');
            var line = end < 0 ? lines : lines[..end];
            lines = end < 0 ? [] : lines[(end + 1)..];
            line = line.TrimEnd((byte)'\r');
            var tab = line.IndexOf((byte)'\t');
            if (header || tab < 0)
            {
                header = false;
                continue;
            }
            if (System.Buffers.Text.Utf8Parser.TryParse(line[..tab], out int attributes, out _))
                folders.Add(new(Encoding.UTF8.GetString(line[(tab + 1)..]), (FileAttributes)attributes));
        }
        return folders;
    }

    // Whether this gozler knows every option that Search sends, without a
    // search. gozler reads its options before it asks the service, so
    // --version ends the call right after them. The answer is kept for the
    // gozler.exe it came from: a setup that replaces the file changes its
    // write time, and the next call asks the new one. A gozler that cannot
    // be asked counts as knowing them, because the next search says what is
    // wrong with it.
    internal static async Task<bool> KnowsOptions(CancellationToken cancellation)
    {
        var written =
            Find(out var program) == GozlerProgram.Installed ? File.GetLastWriteTimeUtc(program) : default;
        if (_probed is { } probed && probed.Program == program && probed.Written == written)
        {
            return probed.Knows;
        }
        FolderChoices probe = new([new(@"C:\probe", Shown: false), new(@"C:\probe\inner", Shown: true)]);
        Reply reply;
        try
        {
            reply = await Run([.. Arguments(probe), "--version"], cancellation).ConfigureAwait(false);
        }
        catch (GozlerException)
        {
            return true;
        }
        var knows = reply.Code != 6;
        _probed = new(program, written, knows);
        return knows;
    }

    // A record, so a thread reads either the old answer or the new one.
    private static Probed? _probed;

    private sealed record Probed(string Program, DateTime Written, bool Knows);

    internal static async Task<IReadOnlyList<GozlerVolume>> Status(CancellationToken cancellation)
    {
        var reply = await Run(["--json", "--status"], cancellation).ConfigureAwait(false);
        if (reply.Code is not (0 or 2))
        {
            throw Explain(reply);
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

    // Every folder left out is an option, which gozler tests on a file's real
    // path: a path wildcard would also leave out what a link in the left-out
    // folder leads to, such as the Public files that ProgramData's junctions
    // lead to. A path in a term is quoted: without the quotes, a path with a
    // space still makes a valid query that matches nothing.
    internal static IReadOnlyList<string> Arguments(FolderChoices folders)
    {
        string[] extensions =
        [
            .. FileName.VideoExtensions,
            .. FileName.AudioExtensions,
            .. FileName.SubtitleExtensions,
        ];
        var media = "ext:" + string.Join(';', extensions.Select(extension => extension.TrimStart('.')));
        List<string> arguments = [OwnAccessOption, "--json", "-n", "all", "-a-d", "-attributes", IdentityOption];
        // Programs keep their clips in every profile's AppData, which is no
        // folder a person chooses. It comes before the person's choices,
        // because of two choices of one folder gozler applies the later.
        foreach (var appData in AppDataFolders())
        {
            arguments.Add("--leave-out");
            arguments.Add(appData);
        }
        foreach (var choice in folders.All)
        {
            arguments.Add(choice.Shown ? "--leave-in" : "--leave-out");
            arguments.Add(choice.Path);
        }
        arguments.Add(media);
        arguments.Add(Streams);
        // A recycled file is a file that is gone, on whatever drive it was.
        arguments.Add("!\"*\\$Recycle.Bin\\*\"");
        return arguments;
    }

    // Each profile's AppData, listed for each query, so a profile added since
    // the last one is left out too. A profile list that cannot be read leaves
    // out no AppData rather than failing Library's whole query.
    private static IReadOnlyList<string> AppDataFolders()
    {
        var profiles = Path.GetDirectoryName(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        );
        if (string.IsNullOrEmpty(profiles))
        {
            return [];
        }
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
        try
        {
            return
            [
                .. Directory
                    .EnumerateDirectories(profiles, "*", options)
                    .Select(profile => Path.Combine(profile, "AppData")),
            ];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static GozlerException Explain(Reply reply)
    {
        switch (reply.Code)
        {
            case 7:
                return new(new DiskState(DiskStatus.NoAnswer));
            case 8:
                return new(new DiskState(DiskStatus.Stopped));
        }
        Debug.WriteLine($"gozler exited with {reply.Code}: {reply.Errors}");
        // Exit 6 names an option that this gozler does not know, such as
        // --file-id or --leave-out in a gozler released before them. Library
        // needs the options, not a release number, so no version is compared.
        return new(new DiskState(reply.Code == 6 ? DiskStatus.Older : DiskStatus.Failed), reply.Errors);
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

internal readonly record struct GozlerFolder(string Path, FileAttributes Attributes);

internal sealed record GozlerFolders(IReadOnlyList<GozlerFolder> Folders, bool Incomplete, bool Missing = false);

// gozler could not give an answer; the state says what the person sees.
internal sealed class GozlerException(DiskState state, string? errors = null)
    : Exception(errors ?? state.Status.ToString()), IFailure
{
    internal DiskState State { get; } = state;

    // gozler's stderr goes to the debug log, not to the person.
    public string Describe(Strings text) => State.Describe(text);
}
