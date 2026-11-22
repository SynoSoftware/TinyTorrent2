using System.ComponentModel;
using System.Globalization;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shell;

namespace Syno.TinyTorrent.Settings;

/// <summary>
/// Library folders' tree: the drives gozler indexes and their folders as
/// gozler indexes them, read as each folder first opens, each with its check
/// box from the saved choices. A click saves at once, as every Windows setting
/// does.
/// </summary>
/// <remarks>
/// The tree shows gozler's view, not the file system's: Library finds media
/// only through gozler, so a folder gozler does not index can never give
/// Library a file, and showing it would mislead.
/// </remarks>
public sealed class FolderTree : INotifyPropertyChanged
{
    private readonly MainViewModel _main;
    private CancellationTokenSource _open = new();

    // gozler's failure, which ends the tree until the page opens again, and
    // the drives whose index is still being built or cannot keep up.
    private DiskState? _failure;
    private readonly SortedSet<string> _indexing = new(StringComparer.OrdinalIgnoreCase);
    private readonly SortedSet<string> _stuck = new(StringComparer.OrdinalIgnoreCase);

    internal FolderTree(MainViewModel main) => _main = main;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Raised on the UI thread with the folders whose subfolders have arrived,
    /// so the page names them to the table, which reads them on its worker.
    /// </summary>
    internal event EventHandler<IReadOnlyList<FolderNode>>? Filled;

    public IReadOnlyList<FolderNode> Roots { get; private set; } = [];

    /// <summary>The roots of the drives in the tree, for the summary.</summary>
    internal IReadOnlyList<string> Drives => [.. Roots.Select(root => root.Path)];

    internal Strings Text => _main.Text;

    // Until the page has read them, no folder differs.
    internal FolderChoices Choices => _main.Folders ?? FolderChoices.Empty;

    /// <summary>
    /// How many folders are left out, and what is wrong with gozler's answer
    /// when something is, for the page's header.
    /// </summary>
    internal string Summary
    {
        get
        {
            var summary =
                _main.Folders is not { } folders ? string.Empty
                : folders.LeftOut == 0 ? Text.Get("discovery", "all_folders")
                : Text.FormatCount("discovery", "left_out_count", folders.LeftOut);
            var state =
                _failure
                ?? (_indexing.Count > 0 ? new DiskState(DiskStatus.Indexing) { Drives = [.. _indexing] }
                    : _stuck.Count > 0 ? new DiskState(DiskStatus.OutOfDate) { Drives = [.. _stuck] }
                    : null);
            return state is null ? summary
                : summary.Length == 0 ? state.Describe(Text)
                : Text.Format("discovery", "summary", summary, state.Describe(Text));
        }
    }

    /// <summary>
    /// Reads the choices and gozler's drives, and opens each drive and the
    /// path to each choice, so every choice is in view. A drive gozler does
    /// not index keeps a row while it has choices, so they can be cleared.
    /// </summary>
    internal async Task Open()
    {
        _open.Cancel();
        _open = new();
        var cancellation = _open.Token;
        _failure = null;
        _indexing.Clear();
        _stuck.Clear();
        await _main.ReadFolders();
        IReadOnlyList<Drive> drives;
        try
        {
            // Starting gozler and a sleeping disk's label take time the UI
            // thread must not wait for.
            drives = await Task.Run(() => ReadDrives(cancellation), cancellation);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (GozlerException error)
        {
            // No file-system tree in its place: the tree shows gozler's view.
            _failure = error.State;
            drives = [];
        }
        if (cancellation.IsCancellationRequested)
            return;
        foreach (var drive in drives)
        {
            if (drive.Readiness == VolumeReadiness.Indexing)
                _indexing.Add(drive.Root.TrimEnd('\\'));
            else if (drive.Readiness == VolumeReadiness.Stuck)
                _stuck.Add(drive.Root.TrimEnd('\\'));
        }
        var indexed = drives.Select(drive => drive.Root).ToHashSet(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> missing = _failure is not null
            ? []
            : Choices
                .All.Select(choice => Path.GetPathRoot(choice.Path)!)
                .Where(root => !indexed.Contains(root))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        Roots =
        [
            .. drives.Select(drive => new FolderNode(
                this,
                drive.Root,
                Text.Format(
                    "discovery",
                    "drive",
                    drive.Label.Length > 0 ? drive.Label : Text.Get("discovery", "local_disk"),
                    drive.Root.TrimEnd('\\')
                )
            )),
            .. missing.Order(StringComparer.OrdinalIgnoreCase).Select(root => new FolderNode(
                this,
                root,
                root.TrimEnd('\\'),
                FolderState.Loaded
            )
            {
                Detail = Text.Get("discovery", "not_connected"),
            }),
        ];
        Changed(nameof(Roots));
        Changed(nameof(Summary));
        foreach (var root in Roots.Where(root => root.State == FolderState.Unloaded))
            Expand(root, true);
    }

    // Stops reading folders when the page closes.
    internal void Close() => _open.Cancel();

    // An open folder reads its subfolders when they are not read yet, and the
    // subfolders of each of them, so every row it shows knows whether it has
    // subfolders and opens at once, as gozler's own tree does.
    internal void Expand(FolderNode folder, bool expanded)
    {
        folder.IsExpanded = expanded;
        if (!expanded)
        {
            if (folder.State == FolderState.Loading)
                folder.State = FolderState.Unloaded;
            return;
        }
        if (folder.State == FolderState.Unloaded)
        {
            folder.State = FolderState.Loading;
            _ = Load(folder, []);
        }
        else if (folder.State == FolderState.Loaded)
        {
            FolderNode[] ahead = [.. folder.Children.Where(child => child.State == FolderState.Unloaded)];
            foreach (var child in ahead)
                child.State = FolderState.Loading;
            if (ahead.Length > 0)
                _ = Load(folder, ahead);
            OpenPartial(folder);
        }
    }

    // A folder on the way to a choice opens, so every choice is in view. Only a
    // folder with a choice inside asks the choices about each subfolder, so
    // the 25,662 of C:\Windows\WinSxS cost nothing.
    private void OpenPartial(FolderNode folder)
    {
        if (Choices.Check(folder.Path) is not null)
            return;
        foreach (var child in folder.Children)
        {
            if (!child.IsExpanded && Choices.Check(child.Path) is null)
                Expand(child, true);
        }
    }

    /// <summary>
    /// A click on a folder's check box: a partial or cleared folder becomes
    /// shown, and a shown one left out, with everything inside it. Returns
    /// whether the change was saved; a saved change shows the saved choice
    /// through MainViewModel.Folders, and after a failed one the page sets the
    /// boxes back, because the box toggled itself on the click.
    /// </summary>
    internal async Task<bool> Toggle(FolderNode folder)
    {
        var shown = folder.Check != true;
        return await _main.ChangeFolders(choices => choices.Set(folder.Path, shown), folder.Path) is not null;
    }

    // Reads `folder`'s subfolders when `ahead` is empty, or else the
    // subfolders of each folder of `ahead`, and shows them.
    private async Task Load(FolderNode folder, IReadOnlyList<FolderNode> ahead)
    {
        var cancellation = _open.Token;
        var choices = Choices;
        // Read here, because the language can change on the UI thread while
        // the worker runs.
        var unlisted = Text.Get("discovery", "not_indexed");
        var reading = ahead.Count == 0;
        Found read;
        try
        {
            read = await Task.Run(() => Read(folder, ahead, choices, unlisted, cancellation), cancellation);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (GozlerException error)
        {
            // A folder gozler cannot answer for shows no subfolders, and the
            // header says why. A folder read ahead is read again when it opens.
            _failure ??= error.State;
            Changed(nameof(Summary));
            read = new([], new Dictionary<FolderNode, List<FolderNode>>(), Incomplete: false, Missing: false);
            foreach (var child in ahead)
            {
                if (child.State == FolderState.Loading)
                    child.State = FolderState.Unloaded;
            }
        }
        if (cancellation.IsCancellationRequested)
            return;
        if (read.Incomplete && _indexing.Add(Path.GetPathRoot(folder.Path)!.TrimEnd('\\')))
            Changed(nameof(Summary));
        List<FolderNode> filled = [];
        if (reading && folder.State == FolderState.Loading)
        {
            // A folder removed since its parent was listed says so on its own row.
            if (read.Missing)
                folder.Detail = unlisted;
            folder.Fill(read.Folders);
            filled.Add(folder);
        }
        foreach (var (child, children) in read.Ahead)
        {
            if (child.State != FolderState.Loading)
                continue;
            child.Fill(children);
            filled.Add(child);
        }
        if (filled.Count == 0)
            return;
        Filled?.Invoke(this, filled);
        // A folder opened while it was read reads the level below it now.
        foreach (var opened in filled.Where(node => node.IsExpanded))
            Expand(opened, true);
    }

    // Reads, in one gozler call, the subfolders of `folder` and of each of
    // them when `ahead` is empty, or else the subfolders of each folder of
    // `ahead`, and makes their rows. A row read with its parent comes filled,
    // so it shows whether it has subfolders and opens at once.
    private async Task<Found> Read(
        FolderNode folder,
        IReadOnlyList<FolderNode> ahead,
        FolderChoices choices,
        string unlisted,
        CancellationToken cancellation
    )
    {
        var reading = ahead.Count == 0;
        var depth = Depth(folder.Path);
        // gozler is not asked about a folder it did not list, because it has
        // nothing to list there.
        var listing = folder.IsIndexed
            ? await Gozler.Folders(folder.Path, reading ? depth : depth + 1, depth + 1, cancellation)
                .ConfigureAwait(false)
            : new GozlerFolders([], Incomplete: false);
        // -path follows folder links and answers with the rows below each
        // link's real target, so a folder can come twice and rows can lie
        // outside `folder`. Each folder takes only the rows whose parent it is,
        // once.
        var below = listing
            .Folders.DistinctBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToLookup(entry => Path.GetDirectoryName(entry.Path) ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        var children = reading ? List(folder.Path, below[folder.Path], choices, unlisted) : [];
        foreach (var child in children)
        {
            if (child.State == FolderState.Unloaded)
                child.Fill(List(child.Path, below[child.Path], choices, unlisted));
        }
        var filled = ahead.ToDictionary(child => child, child => List(child.Path, below[child.Path], choices, unlisted));
        return new(children, filled, listing.Incomplete, listing.Missing);
    }

    // gozler's depth of the subfolders of `folder`: the folders above them,
    // the drive's root included, so the subfolders of C:\ are 1 deep. Only a
    // drive's root ends with a separator.
    private static int Depth(string folder) =>
        folder.Count(c => c == '\\') + (Path.EndsInDirectorySeparator(folder) ? 0 : 1);

    // A read's rows: the folder's own subfolders, and those of each folder read ahead.
    private sealed record Found(
        List<FolderNode> Folders,
        IReadOnlyDictionary<FolderNode, List<FolderNode>> Ahead,
        bool Incomplete,
        bool Missing
    );

    // The rows of `folder`'s subfolders in `entries`, in name order. File
    // Explorer hides hidden folders by default, so they are left out unless a
    // choice is at or inside one. A junction or a symbolic link is listed but
    // never opens, because it leads elsewhere and gozler finds the files at
    // their own path. A choice inside a folder gozler no longer lists keeps a
    // row on its way, so it can be cleared.
    private List<FolderNode> List(
        string folder,
        IEnumerable<GozlerFolder> entries,
        FolderChoices choices,
        string unlisted
    )
    {
        var holds = choices.Check(folder) is null;
        var found = new List<FolderNode>();
        foreach (var entry in entries)
        {
            if (
                entry.Attributes.HasFlag(FileAttributes.Hidden)
                && (!holds || choices.Within(entry.Path).Count == 0)
            )
            {
                continue;
            }
            var node = new FolderNode(
                this,
                entry.Path,
                Path.GetFileName(entry.Path),
                entry.Attributes.HasFlag(FileAttributes.ReparsePoint) ? FolderState.Loaded : FolderState.Unloaded
            )
            {
                Size = entry.Size,
                Files = entry.Files,
                Folders = entry.Folders,
            };
            found.Add(node);
        }
        if (holds)
            AddUnlisted(folder, choices, unlisted, found);
        found.Sort((left, right) => StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name));
        return found;
    }

    // A row for each subfolder on the way to a choice that gozler did not
    // list, marked as not in gozler's index.
    private void AddUnlisted(string folder, FolderChoices choices, string unlisted, List<FolderNode> found)
    {
        var listed = found.Select(node => node.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var inside = Path.EndsInDirectorySeparator(folder) ? folder.Length : folder.Length + 1;
        foreach (var choice in choices.Within(folder))
        {
            if (choice.Path.Length <= inside)
                continue;
            var end = choice.Path.IndexOf('\\', inside);
            var path = end < 0 ? choice.Path : choice.Path[..end];
            if (!listed.Add(path))
                continue;
            found.Add(new FolderNode(this, path, Path.GetFileName(path)) { Detail = unlisted, IsIndexed = false });
        }
    }

    // The drives gozler indexes: each volume's drive letters, or its first
    // mount when it has none.
    private static async Task<IReadOnlyList<Drive>> ReadDrives(CancellationToken cancellation)
    {
        var volumes = await Gozler.Status(cancellation).ConfigureAwait(false);
        var drives = new List<Drive>();
        foreach (var volume in volumes)
        {
            var roots = volume.Mounts.Where(mount => Path.GetPathRoot(mount) == mount).ToList();
            if (roots.Count == 0 && volume.Mounts.Count > 0)
                roots.Add(volume.Mounts[0]);
            foreach (var root in roots)
                drives.Add(new(root, Label(root), volume.Readiness));
        }
        return [.. drives.OrderBy(drive => drive.Root, StringComparer.OrdinalIgnoreCase)];
    }

    private static string Label(string root)
    {
        try
        {
            return new DriveInfo(root).VolumeLabel;
        }
        catch (IOException)
        {
            return string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return string.Empty;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    private void Changed(string property) => PropertyChanged?.Invoke(this, new(property));

    private sealed record Drive(string Root, string Label, VolumeReadiness Readiness);
}

/// <summary>A drive or a folder of Library folders' tree.</summary>
public sealed class FolderNode : INotifyPropertyChanged
{
    private readonly FolderTree _tree;
    private FolderState _state;
    private bool _expanded;

    // A drive that is not connected and a link have nothing to list, so they
    // start Loaded.
    internal FolderNode(FolderTree tree, string path, string name, FolderState state = FolderState.Unloaded)
    {
        _tree = tree;
        Path = path;
        Name = name;
        Glyph = System.IO.Path.GetPathRoot(path) == path ? Segoe.HardDrive : Segoe.Folder;
        _state = state;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; }
    public string Name { get; }
    public string Glyph { get; }

    public string Detail { get; internal set; } = string.Empty;
    public bool HasDetail => Detail.Length > 0;

    // A folder on the way to a choice that gozler did not list. Its
    // subfolders come from the choices alone.
    internal bool IsIndexed { get; init; } = true;

    // Until the folder is read, the shared empty array, so asking an unread
    // folder for its subfolders makes nothing.
    public IReadOnlyList<FolderNode> Children { get; private set; } = [];

    public bool IsExpanded
    {
        get => _expanded;
        internal set
        {
            _expanded = value;
            PropertyChanged?.Invoke(this, new(nameof(IsLoading)));
        }
    }

    // Only a folder the person opened shows that it is read, not one read ahead.
    public bool IsLoading => _state == FolderState.Loading && _expanded;

    // Checked, cleared, or partial (null) when a folder inside it differs.
    public bool? Check => _tree.Choices.Check(Path);

    // The total size of the files below the folder and how many files and
    // folders lie below it, as gozler's listing gave them, each null when it
    // gave none.
    public long? Size { get; init; }
    public long? Files { get; init; }
    public long? Folders { get; init; }

    // Empty while a value is not known, as File Explorer leaves a folder's Size.
    public string SizeText => Size is { } bytes ? _tree.Text.Bytes(bytes) : string.Empty;
    public string FilesText => Files is { } files ? files.ToString("N0", CultureInfo.CurrentCulture) : string.Empty;
    public string FoldersText =>
        Folders is { } folders ? folders.ToString("N0", CultureInfo.CurrentCulture) : string.Empty;

    public override string ToString() => Name;

    internal FolderState State
    {
        get => _state;
        set
        {
            _state = value;
            PropertyChanged?.Invoke(this, new(nameof(IsLoading)));
        }
    }

    internal void Fill(IReadOnlyList<FolderNode> children)
    {
        Children = children;
        _state = FolderState.Loaded;
        PropertyChanged?.Invoke(this, new(string.Empty));
    }

    /// <summary>Shows the check box of the choices as last saved, and the numbers in the current language.</summary>
    internal void Refresh()
    {
        PropertyChanged?.Invoke(this, new(nameof(Check)));
        PropertyChanged?.Invoke(this, new(nameof(SizeText)));
        PropertyChanged?.Invoke(this, new(nameof(FilesText)));
        PropertyChanged?.Invoke(this, new(nameof(FoldersText)));
    }
}
