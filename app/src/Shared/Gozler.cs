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

    // The folder tree's Files and Folders columns: how many files and folders
    // lie below each folder, which gozler keeps in memory as it keeps sizes.
    private const string FilesOption = "--files";
    private const string FoldersOption = "--folders";

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
        await RequireOptions(cancellation).ConfigureAwait(false);
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

    // The folders below `folder` that gozler indexes, from `from` to `to`
    // folders deep, with their attributes and sizes, in one call. gozler
    // counts the drive's root, so the subfolders of C:\ are 1 deep.
    // Tab-separated output, because a folder of 25,662 folders takes 252 ms
    // that way and 1,057 ms as JSON. An answer that may miss folders is still
    // used.
    internal static async Task<GozlerFolders> Folders(string folder, int from, int to, CancellationToken cancellation)
    {
        await RequireOptions(cancellation).ConfigureAwait(false);
        string[] arguments =
        [
            OwnAccessOption, "-path", folder, "/ad", $"depth:{from}..{to}", "-attributes", "-size", FilesOption,
            FoldersOption, "-n", "all", "-tsv",
        ];
        for (var attempt = 1; ; attempt++)
        {
            var reply = await Run(arguments, cancellation).ConfigureAwait(false);
            switch (reply.Code)
            {
                case 0:
                case 2:
                    return new(ReadFolders(reply.Output), Incomplete: reply.Code == 2);
                // The folder is gone since it was listed. It fails only itself,
                // not gozler's answer for every other folder.
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

    // The header row names the columns, which follow the order of their
    // options, with Filename last. Each later line holds a folder's
    // FILE_ATTRIBUTE_* number, its size in bytes, the files and folders below
    // it, each empty when gozler does not know it, and its path, separated by
    // tabs, in UTF-8. Windows allows no tab in a name.
    private static IReadOnlyList<GozlerFolder> ReadFolders(byte[] output)
    {
        var folders = new List<GozlerFolder>();
        ReadOnlySpan<byte> lines = output;
        // Where Attributes, Size, Files, Folders and Filename are in a line.
        Span<int> at = stackalloc int[5];
        at.Fill(-1);
        Span<Range> cells = stackalloc Range[at.Length];
        var header = true;
        while (!lines.IsEmpty)
        {
            var end = lines.IndexOf((byte)'\n');
            var line = end < 0 ? lines : lines[..end];
            lines = end < 0 ? [] : lines[(end + 1)..];
            line = line.TrimEnd((byte)'\r');
            var count = 0;
            foreach (var cell in line.Split((byte)'\t'))
            {
                if (count < cells.Length)
                    cells[count] = cell;
                count++;
            }
            if (header)
            {
                header = false;
                for (var index = 0; index < Math.Min(count, cells.Length); index++)
                {
                    if (ColumnOf(line[cells[index]]) is >= 0 and var column)
                        at[column] = index;
                }
                continue;
            }
            if (count > cells.Length || at[0] < 0 || at[4] < 0)
                continue;
            if (System.Buffers.Text.Utf8Parser.TryParse(line[cells[at[0]]], out int attributes, out _))
                folders.Add(
                    new(
                        Encoding.UTF8.GetString(line[cells[at[4]]]),
                        (FileAttributes)attributes,
                        Number(line, cells, at[1]),
                        Number(line, cells, at[2]),
                        Number(line, cells, at[3])
                    )
                );
        }
        return folders;
    }

    private static int ColumnOf(ReadOnlySpan<byte> name) =>
        name.SequenceEqual("Attributes"u8) ? 0
        : name.SequenceEqual("Size"u8) ? 1
        : name.SequenceEqual("Files"u8) ? 2
        : name.SequenceEqual("Folders"u8) ? 3
        : name.SequenceEqual("Filename"u8) ? 4
        : -1;

    // The whole number in a line's cell, or null when the cell is empty or
    // the column is missing.
    private static long? Number(ReadOnlySpan<byte> line, ReadOnlySpan<Range> cells, int column)
    {
        if (column < 0)
            return null;
        var cell = line[cells[column]];
        return System.Buffers.Text.Utf8Parser.TryParse(cell, out long value, out var used) && used == cell.Length
            ? value
            : null;
    }

    // Whether this gozler knows every option that Search and the folder tree
    // send, without a search. gozler reads its options before it asks the service, so
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
            reply = await Run([.. Arguments(probe), FilesOption, FoldersOption, "--version"], cancellation)
                .ConfigureAwait(false);
        }
        catch (GozlerException)
        {
            return true;
        }
        var knows = reply.Code != 6;
        _probed = new(program, written, knows);
        return knows;
    }

    // Every call that uses gozler's options checks first that gozler knows
    // all of them, so a gozler is either used fully or shown as too old: no
    // feature works while another asks for an update.
    private static async Task RequireOptions(CancellationToken cancellation)
    {
        if (!await KnowsOptions(cancellation).ConfigureAwait(false))
            throw new GozlerException(new DiskState(DiskStatus.Older));
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

// The total size of the files below the folder and how many files and
// folders lie below it, each null when gozler does not give it.
internal readonly record struct GozlerFolder(
    string Path,
    FileAttributes Attributes,
    long? Size,
    long? Files,
    long? Folders
);

internal sealed record GozlerFolders(IReadOnlyList<GozlerFolder> Folders, bool Incomplete, bool Missing = false);

// gozler could not give an answer; the state says what the person sees.
internal sealed class GozlerException(DiskState state, string? errors = null)
    : Exception(errors ?? state.Status.ToString()), IFailure
{
    internal DiskState State { get; } = state;

    // gozler's stderr goes to the debug log, not to the person.
    public string Describe(Strings text) => State.Describe(text);
}
