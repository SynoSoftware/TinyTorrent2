using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Syno.TinyTorrent.Shared;

namespace Syno.TinyTorrent.Controls;

// The rows of the file browser in the Add dialog and the inspector, built from
// the engine's `tree` replies for one preview or torrent. A tree that fits the
// window is held whole; a larger one holds the folders the person opens, as
// the pipe API's file browser describes. The engine holds every choice and
// runs every search.
public sealed class FileSelection : INotifyPropertyChanged
{
    // The rows a browser holds, the window of the engine's `tree` replies.
    internal const int Window = 10_000;

    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(200);

    private readonly Strings _strings;
    private readonly Dictionary<string, FileNode> _folders = [];
    private readonly Dictionary<int, FileNode> _files = [];

    // The folders an ordinary open listed, oldest first, which a full window
    // collapses in this order. A whole tree and a search's own folders are
    // never collapsed to make room, so they are not listed.
    private readonly List<FileNode> _opened = [];
    private FileNode _root;
    private Func<Dictionary<string, object>, Task<JsonElement>>? _read;
    private Func<IReadOnlyList<FileChoice>, Task<JsonElement?>>? _edit;

    // Counts each replacement of the rows, so a reply asked for older rows is
    // dropped.
    private int _generation;
    private bool _whole = true;
    private string _search = string.Empty;

    // The search the rows show; null while they show the tree.
    private Matches? _matches;

    // The folders that clearing the search opens again, in the order they
    // were opened.
    private IReadOnlyList<string> _restore = [];
    private bool _showsProgress;
    private bool _enabled = true;

    public FileSelection(Strings strings)
    {
        _strings = strings;
        _root = Root();
        _folders.Add(string.Empty, _root);
    }

    public Strings Text => _strings;
    public IReadOnlyList<FileNode> Roots { get; private set; } = [];
    public bool ShowsProgress
    {
        get => _showsProgress;
        internal set
        {
            if (_showsProgress == value)
                return;
            _showsProgress = value;
            Refresh();
        }
    }
    public bool IsEnabled
    {
        get => _enabled;
        internal set
        {
            if (_enabled == value)
                return;
            _enabled = value;
            Refresh();
        }
    }
    public string Search
    {
        get => _search;
        set
        {
            if (_search == value)
                return;
            _search = value;
            _ = Find(value);
        }
    }
    public bool HasFolders => _root.Children.Any(node => node.IsFolder);

    // Expanding every folder needs no load only when the tree is whole.
    public bool CanExpand => _whole && HasFolders;
    public bool HasWanted => _root.WantedCount > 0;
    public long WantedBytes => _root.WantedSize;
    public bool? AllMatching
    {
        get
        {
            var (count, wanted) = _matches is { } matches
                ? (matches.Count, matches.Wanted)
                : (_root.Count, _root.WantedCount);
            return wanted == 0 ? false
                : wanted == count ? true
                : null;
        }
    }
    public string AllLabel =>
        _matches is { } matches
            ? _strings.FormatCount("files", "all_count", matches.Count)
            : _strings.Get("files", "all_matching");
    public string Summary =>
        _strings.Format(
            "files",
            "summary",
            _root.WantedCount,
            _root.Count,
            _strings.Bytes(_root.WantedSize)
        );

    // What the list cannot show: the matches beyond the window, or a root
    // whose own entries do not fit.
    public string Note =>
        _matches is { Hidden: > 0 } matches ? _strings.FormatCount("files", "hidden", matches.Hidden)
        : _root.State == FolderState.TooMany ? _strings.Get("files", "too_many")
        : string.Empty;
    public bool HasNote => Note.Length > 0;
    private int Rows => _folders.Count - 1 + _files.Count;

    // The rows the inspector's refresh names, so the Files view gives their
    // values.
    internal FileKeys Keys =>
        new([.. _folders.Keys.Where(path => path.Length > 0)], [.. _files.Keys]);
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? Changed;

    // A read the person started failed; the host shows its command error.
    internal event EventHandler<Exception>? Failed;

    internal void Clear()
    {
        Detach();
        _search = string.Empty;
        _matches = null;
        _restore = [];
        _whole = true;
        Build(null);
        Publish();
    }

    internal void Detach()
    {
        _read = null;
        _edit = null;
        _generation++;
    }

    // Shows the tree whose root `tree` reply is `root`. `read` sends a `tree`
    // request with the host's preview or torrent added; `edit` sends choices
    // and gives the engine's reply, or null when the host reported a failure.
    internal void Show(
        JsonElement root,
        Func<Dictionary<string, object>, Task<JsonElement>> read,
        Func<IReadOnlyList<FileChoice>, Task<JsonElement?>> edit,
        CancellationToken cancellation = default
    )
    {
        _read = read;
        _edit = edit;
        _search = string.Empty;
        _matches = null;
        _restore = [];
        ShowTree(root, cancellation);
    }

    // Reads the current rows again, after a choice the engine refused.
    internal void Reload()
    {
        if (_matches is { } matches)
        {
            _ = Query(matches.Query);
            return;
        }
        _restore = Expanded();
        _ = Restore();
    }

    private FileNode Root() => new(this, string.Empty, null, -1);

    private void Build(JsonElement? reply, CancellationToken cancellation = default)
    {
        _generation++;
        _folders.Clear();
        _files.Clear();
        _opened.Clear();
        _root = Root();
        _folders.Add(string.Empty, _root);
        if (reply is { } value)
            Open(_root, value.GetProperty("folder"), new HashSet<FileNode>(), cancellation);
    }

    private void ShowTree(JsonElement reply, CancellationToken cancellation = default)
    {
        Build(reply, cancellation);
        _whole = _folders.Values.All(folder => folder.State == FolderState.Loaded);
        if (_whole)
            _opened.Clear();
        Publish();
    }

    // Lists `value`'s entries under `folder`, collapsing the folders opened
    // longest ago, except those in `keep`, until they fit; then each deeper
    // level the reply holds, in the rows still free.
    private void Open(FileNode folder, JsonElement value, IReadOnlySet<FileNode> keep,
        CancellationToken cancellation = default)
    {
        if (!List(folder, value, keep, cancellation))
            return;
        var level = new Queue<(FileNode Folder, JsonElement Value)>(Listed(folder, value));
        while (level.TryDequeue(out var next))
            if (List(next.Folder, next.Value, null, cancellation))
                foreach (var child in Listed(next.Folder, next.Value))
                    level.Enqueue(child);
    }

    // The folders under `folder` whose entries the reply holds.
    private IEnumerable<(FileNode, JsonElement)> Listed(FileNode folder, JsonElement value) =>
        value
            .GetProperty("folders")
            .EnumerateArray()
            .Where(child => child.GetProperty("folders").ValueKind == JsonValueKind.Array)
            .Select(child =>
                (_folders[FileNode.Join(folder.Path, child.GetProperty("name").GetString()!)], child)
            )
            .ToArray();

    // Lists a folder's direct entries from its reply value. With `keep`, the
    // folders opened longest ago that are not in it collapse until the entries
    // fit, and a folder whose entries still do not fit is too many to list;
    // without it, a folder that does not fit stays closed.
    private bool List(FileNode folder, JsonElement value, IReadOnlySet<FileNode>? keep,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        folder.Read(value);
        var folders = value.GetProperty("folders");
        var files = value.GetProperty("files");
        if (folders.ValueKind != JsonValueKind.Array)
        {
            folder.Close(
                value.GetProperty("too_many").GetBoolean()
                    ? FolderState.TooMany
                    : FolderState.Unloaded
            );
            return false;
        }
        var count = folders.GetArrayLength() + files.GetArrayLength();
        while (
            keep is not null
            && Rows + count > Window
            && _opened.FirstOrDefault(open => !keep.Contains(open)) is { } oldest
        )
            Forget(oldest);
        if (Rows + count > Window)
        {
            folder.Close(keep is null ? FolderState.Unloaded : FolderState.TooMany);
            return false;
        }
        foreach (var entry in folders.EnumerateArray())
        {
            cancellation.ThrowIfCancellationRequested();
            var child = new FileNode(this, entry.GetProperty("name").GetString()!, folder, -1);
            child.Read(entry);
            child.Close(
                entry.GetProperty("too_many").GetBoolean()
                    ? FolderState.TooMany
                    : FolderState.Unloaded
            );
            folder.Add(child);
            _folders.Add(child.Path, child);
        }
        foreach (var entry in files.EnumerateArray())
        {
            cancellation.ThrowIfCancellationRequested();
            var index = entry.GetProperty("index").GetInt32();
            var child = new FileNode(this, entry.GetProperty("name").GetString()!, folder, index);
            child.Read(entry);
            folder.Add(child);
            _files.Add(index, child);
        }
        folder.Open();
        if (folder != _root)
            _opened.Add(folder);
        return true;
    }

    // Collapses a folder and drops its rows, so opening it again reads the
    // engine's current values.
    private void Forget(FileNode folder)
    {
        foreach (var node in folder.Children.SelectMany(child => child.Nodes()))
        {
            if (node.IsFolder)
            {
                _folders.Remove(node.Path);
                _opened.Remove(node);
            }
            else
                _files.Remove(node.Index);
        }
        _opened.Remove(folder);
        folder.Close(FolderState.Unloaded);
    }

    private bool IsHeld(FileNode folder) =>
        _folders.TryGetValue(folder.Path, out var held) && held == folder;

    // The table's expansion request. It only writes state, as the table
    // requires; the load and the forgetting of rows run after it returns.
    internal void Expand(FileNode folder, bool expanded)
    {
        folder.IsExpanded = expanded;
        if (expanded && folder.State == FolderState.Unloaded)
        {
            folder.State = FolderState.Loading;
            _ = Load(folder);
        }
        else if (!expanded && folder.State == FolderState.Loading)
            folder.State = FolderState.Unloaded;
        else if (!expanded && _opened.Contains(folder))
            _ = Collapse(folder);
    }

    // Reads `request` for the tree shown now. Gives null when the read fails,
    // after `failed` and Failed run, or when the tree was replaced or
    // `current` turned false meanwhile, because the reply is then stale.
    private async Task<JsonElement?> ReadCurrent(
        Dictionary<string, object> request,
        Func<bool>? current = null,
        Action? failed = null
    )
    {
        if (_read is not { } read)
            return null;
        var generation = _generation;
        bool Current() => generation == _generation && (current?.Invoke() ?? true);
        JsonElement reply;
        try
        {
            reply = await read(request);
        }
        catch (Exception error)
        {
            if (Current())
            {
                failed?.Invoke();
                Failed?.Invoke(this, error);
            }
            return null;
        }
        return Current() ? reply : null;
    }

    private async Task Load(FileNode folder)
    {
        var reply = await ReadCurrent(
            new() { ["folder"] = folder.Path },
            () => IsHeld(folder) && folder.State == FolderState.Loading,
            () =>
            {
                folder.Close(FolderState.Unloaded);
                Publish();
            }
        );
        if (reply is not { } value)
            return;
        Open(folder, value.GetProperty("folder"), folder.Lineage().ToHashSet());
        Publish();
    }

    private async Task Collapse(FileNode folder)
    {
        await Task.Yield();
        if (folder.IsExpanded || !_opened.Contains(folder))
            return;
        Forget(folder);
        Publish();
    }

    public void Expand(bool expanded)
    {
        var forgot = false;
        foreach (var folder in _folders.Values.Where(folder => folder != _root).ToArray())
        {
            if (expanded)
                folder.IsExpanded |= folder.State == FolderState.Loaded;
            else if (!IsHeld(folder))
                continue;
            else if (_opened.Contains(folder))
            {
                Forget(folder);
                forgot = true;
            }
            else
            {
                folder.IsExpanded = false;
                if (folder.State == FolderState.Loading)
                    folder.State = FolderState.Unloaded;
            }
        }
        if (forgot)
            Publish();
    }

    // Shows a file, opening the folders on its path. Gives the file's row, or
    // the deepest folder on the path that could open.
    internal async Task<FileNode?> Reveal(int index)
    {
        if (_matches is not null)
        {
            _search = string.Empty;
            PropertyChanged?.Invoke(this, new(nameof(Search)));
            await Restore();
        }
        if (_files.TryGetValue(index, out var file))
        {
            foreach (var folder in file.Ancestors())
                folder.IsExpanded = true;
            return file;
        }
        if (await ReadCurrent(new() { ["index"] = index }) is not { } reply)
            return null;
        // The reply lists the root and each folder on the file's path.
        var keep = new HashSet<FileNode>();
        var reached = _root;
        var value = reply.GetProperty("folder");
        while (true)
        {
            keep.Add(reached);
            if (reached.State != FolderState.Loaded && !List(reached, value, keep))
                break;
            reached.IsExpanded = true;
            var next = value
                .GetProperty("folders")
                .EnumerateArray()
                .FirstOrDefault(child =>
                    child.GetProperty("folders").ValueKind == JsonValueKind.Array
                );
            if (
                next.ValueKind != JsonValueKind.Object
                || !_folders.TryGetValue(
                    FileNode.Join(reached.Path, next.GetProperty("name").GetString()!),
                    out var child
                )
            )
                break;
            reached = child;
            value = next;
        }
        Publish();
        return _files.GetValueOrDefault(index) ?? (reached == _root ? null : reached);
    }

    private async Task Find(string text)
    {
        await Task.Delay(Pause);
        if (text != _search || _read is null)
            return;
        if (FileName.SearchWords(text).Length == 0)
        {
            if (_matches is not null)
                await Restore();
        }
        else if (text != _matches?.Query)
            await Query(text);
    }

    private async Task Query(string text)
    {
        if (await ReadCurrent(new() { ["query"] = text }, () => text == _search) is not { } reply)
            return;
        if (_matches is null)
            _restore = Expanded();
        Build(reply);
        _opened.Clear();
        var count = reply.GetProperty("match_count").GetInt64();
        _matches = new(
            text,
            count,
            reply.GetProperty("match_wanted").GetInt64(),
            count
                - _files.Values.Count(node => node.IsMatch)
                - _folders.Values.Where(node => node.IsMatch).Sum(node => node.Count)
        );
        Publish();
    }

    // Shows the tree again: the root, and then each folder in `_restore`, as
    // an ordinary open does.
    private async Task Restore()
    {
        if (
            await ReadCurrent(
                new() { ["folder"] = string.Empty },
                () => FileName.SearchWords(_search).Length == 0
            )
            is not { } reply
        )
            return;
        var restore = _restore;
        _matches = null;
        _restore = [];
        ShowTree(reply);
        if (_whole)
        {
            var expanded = restore.ToHashSet();
            foreach (var folder in _folders.Values)
                folder.IsExpanded = expanded.Contains(folder.Path);
            Publish();
            return;
        }
        var generation = _generation;
        foreach (var path in restore)
        {
            if (generation != _generation)
                return;
            if (!_folders.TryGetValue(path, out var folder) || folder.State != FolderState.Unloaded)
                continue;
            folder.IsExpanded = true;
            folder.State = FolderState.Loading;
            Publish();
            await Load(folder);
        }
    }

    // The expanded folders, in the order they were opened.
    private string[] Expanded() =>
        _whole
            ? [
                .. _folders
                    .Values.Where(folder => folder != _root && folder.IsExpanded)
                    .Select(folder => folder.Path),
            ]
            : [.. _opened.Select(folder => folder.Path)];

    public void SelectMatching(bool wanted)
    {
        FileAction action = wanted ? new FileAction.Want() : new FileAction.Set(0);
        if (_matches is not { } matches)
        {
            Choose([_root], action);
            return;
        }
        if (!IsEnabled || _edit is not { } edit)
            return;
        foreach (var node in _folders.Values.Concat(_files.Values).Where(node => node.IsMatch))
            node.Choose(action);
        _matches = matches with { Wanted = wanted ? matches.Count : 0 };
        Refresh(notify: false);
        _ = Send(edit, [new FileChoice(new FileTarget.Query(matches.Query), action)]);
    }

    internal void SelectAll() => Choose([_root], new FileAction.Want());

    // Applies the choice to the chosen rows at once and sends it. A row under
    // a chosen folder is covered by the folder's choice, so each file is
    // chosen once.
    internal void Choose(IEnumerable<FileNode> nodes, FileAction action)
    {
        if (!IsEnabled || _edit is not { } edit)
            return;
        var chosen = nodes.ToHashSet();
        FileNode[] targets =
        [
            .. chosen.Where(node =>
                !node.Ancestors().Any(chosen.Contains) && !node.Holds(action)
            ),
        ];
        if (targets.Length == 0)
            return;
        foreach (var node in targets)
            node.Choose(action);
        Refresh(notify: false);
        _ = Send(edit, [.. targets.Select(node => new FileChoice(node.Target, action))]);
    }

    // The engine's reply gives the values the choice changed. A search is
    // read again, because its matches' wanted count changed with them.
    private async Task Send(
        Func<IReadOnlyList<FileChoice>, Task<JsonElement?>> edit,
        IReadOnlyList<FileChoice> choices
    )
    {
        var generation = _generation;
        if (await edit(choices) is not { } reply || generation != _generation)
            return;
        if (_matches is { } matches)
            await Query(matches.Query);
        else
            Apply(reply, false);
    }

    // The folder rows whose values the choices change, for an edit's
    // `loaded`: each chosen folder with the folders held under it, and the
    // folders on each chosen path.
    internal string[] Loaded(IEnumerable<FileChoice> choices)
    {
        var paths = new HashSet<string>();
        foreach (var choice in choices)
        {
            var node = choice.Target switch
            {
                FileTarget.File file => _files.GetValueOrDefault(file.Index),
                FileTarget.Folder folder => _folders.GetValueOrDefault(folder.Path),
                _ => null,
            };
            if (node is null)
                continue;
            paths.UnionWith(node.Ancestors().Select(folder => folder.Path));
            paths.UnionWith(node.Nodes().Where(child => child.IsFolder).Select(child => child.Path));
        }
        return [.. paths];
    }

    // Applies the engine's values: a Files view's values of the named rows,
    // or the reply to an edit. A draft keeps its choices on the rows until the
    // engine confirms them.
    internal void Apply(JsonElement values, bool preserveChoices)
    {
        foreach (var entry in Entries(values, "files"))
            if (_files.TryGetValue(entry.GetProperty("index").GetInt32(), out var file))
                file.Read(entry, preserveChoices);
        foreach (var entry in Entries(values, "folders"))
            if (_folders.TryGetValue(entry.GetProperty("path").GetString()!, out var folder))
                folder.Read(entry, preserveChoices);
        if (
            !preserveChoices
            && values.TryGetProperty("wanted_count", out var wanted)
            && wanted.ValueKind == JsonValueKind.Number
        )
            _root.Total(wanted.GetInt64(), values.GetProperty("wanted_size").GetInt64());
        Refresh(notify: false);
    }

    // Whether a Files view reply already holds the choice, which the
    // inspector's draft then no longer needs to send.
    internal bool Holds(JsonElement values, FileChoice choice)
    {
        switch (choice.Target)
        {
            case FileTarget.File file:
                return Entries(values, "files")
                    .Any(entry =>
                        entry.GetProperty("index").GetInt32() == file.Index
                        && entry.GetProperty("priority").ValueKind == JsonValueKind.Number
                        && choice.Action.Holds(entry.GetProperty("priority").GetInt32())
                    );
            case FileTarget.Folder { Path: "" }:
                return values.TryGetProperty("wanted_count", out var wanted)
                    && wanted.ValueKind == JsonValueKind.Number
                    && choice.Action.Holds(wanted.GetInt64(), _root.Count, -1);
            case FileTarget.Folder folder:
                return Entries(values, "folders")
                    .Any(entry =>
                        entry.GetProperty("path").GetString() == folder.Path
                        && choice.Action.Holds(
                            entry.GetProperty("wanted_count").GetInt64(),
                            entry.GetProperty("file_count").GetInt64(),
                            entry.GetProperty("priority") is { ValueKind: JsonValueKind.Number } shared
                                ? shared.GetInt32()
                                : -1
                        )
                    );
            default:
                return false;
        }
    }

    private static IEnumerable<JsonElement> Entries(JsonElement values, string key) =>
        values.TryGetProperty(key, out var entries) && entries.ValueKind == JsonValueKind.Array
            ? entries.EnumerateArray().AsEnumerable()
            : [];

    private void Publish()
    {
        Roots = [.. _root.Children];
        Refresh();
    }

    internal void Refresh(bool notify = true)
    {
        _root.Refresh(notify);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // A search's results. Count and Wanted cover every match; Hidden counts
    // the matches beyond the window, which the rows leave out.
    private sealed record Matches(string Query, long Count, long Wanted, long Hidden);
}

public sealed class FileNode : INotifyPropertyChanged
{
    private readonly FileSelection _owner;
    private readonly List<FileNode> _children = [];

    // A file's own values. A folder's are those the engine reported.
    private long _size;
    private long _count;
    private long _wantedCount;
    private long _wantedSize;

    // A folder's is the priority all its files share, or -1 when they differ.
    private int _priority;
    private long? _downloaded;
    private FolderState _state = FolderState.Loaded;
    private (long, long, long, int, long?, FolderState) _shown;
    public IReadOnlyList<FileNode> Children => _children;
    public string Name { get; }
    public string Path { get; }
    public FileNode? Parent { get; }
    public int Index { get; }
    public bool IsFolder => Index < 0;
    public bool IsExpanded { get; internal set; }
    internal FolderState State
    {
        get => _state;
        set
        {
            _state = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }
    }

    // One of a search's highest matches.
    internal bool IsMatch { get; private set; }
    internal long Count => _count;
    internal long WantedCount => _wantedCount;
    internal long WantedSize => _wantedSize;
    public long Size => _size;
    public long? Downloaded => _downloaded;
    internal FileTarget Target =>
        IsFolder ? new FileTarget.Folder(Path) : new FileTarget.File(Index, Path);
    public string Glyph => IsFolder ? Lucide.Folder : FileName.Glyph(FileName.Kind(Name));
    public string SizeText => _owner.Text.Bytes(_size);
    public double? Progress =>
        _downloaded is not { } downloaded ? null
        : _size == 0 ? 1
        : Math.Clamp((double)downloaded / _size, 0, 1);
    public string ProgressText => Progress?.ToString("P1", CultureInfo.CurrentCulture) ?? "—";
    public double ProgressValue => Progress ?? 0;
    public bool IsIndeterminate => Progress is null;
    public bool IsComplete => Progress is >= 1;
    public bool IsEnabled => _owner.IsEnabled;
    public bool IsLoading => _state == FolderState.Loading;
    public bool IsTooMany => _state == FolderState.TooMany;
    public string TooMany => _owner.Text.Get("files", "too_many");
    public bool? Wanted
    {
        get =>
            _wantedCount == 0 ? false
            : _wantedCount == _count ? true
            : null;
        set
        {
            if (value is { } wanted)
                _owner.Choose(
                    [this],
                    wanted ? new FileAction.Want() : new FileAction.Set(0)
                );
        }
    }
    public int Priority
    {
        get => _priority;
        set
        {
            if (value >= 0)
                _owner.Choose([this], new FileAction.Set(value));
        }
    }
    public int PriorityIndex
    {
        get =>
            Priority switch
            {
                0 => 1,
                1 => 2,
                4 => 3,
                7 => 4,
                _ => 0,
            };
        set =>
            Priority = value switch
            {
                1 => 0,
                2 => 1,
                3 => 4,
                4 => 7,
                _ => -1,
            };
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Unchanged => _owner.Text.Get("files", "unchanged");
    public string Skip => _owner.Text.Get("files", "skip");
    public string Low => _owner.Text.Get("files", "low");
    public string Normal => _owner.Text.Get("files", "normal");
    public string High => _owner.Text.Get("files", "high");

    internal FileNode(FileSelection owner, string name, FileNode? parent, int index)
    {
        _owner = owner;
        Name = name;
        Parent = parent;
        Index = index;
        Path = parent is null ? name : Join(parent.Path, name);
    }

    internal static string Join(string folder, string name) =>
        folder.Length == 0 ? name : folder + "/" + name;

    internal IEnumerable<FileNode> Nodes() =>
        new[] { this }.Concat(_children.SelectMany(child => child.Nodes()));

    internal IEnumerable<FileNode> Ancestors()
    {
        for (var parent = Parent; parent is not null; parent = parent.Parent)
            yield return parent;
    }

    // The node and its ancestors.
    internal IEnumerable<FileNode> Lineage() => new[] { this }.Concat(Ancestors());

    internal void Add(FileNode child) => _children.Add(child);

    internal void Open()
    {
        _state = FolderState.Loaded;
        IsExpanded = true;
    }

    internal void Close(FolderState state)
    {
        _children.Clear();
        _state = state;
        IsExpanded = false;
    }

    // Reads the values a `tree`, edit or Files view reply gives for this row.
    // A draft keeps the choices it shows.
    internal void Read(JsonElement value, bool preserveChoices = false)
    {
        if (value.TryGetProperty("downloaded", out var downloaded))
            _downloaded = downloaded.ValueKind == JsonValueKind.Number ? downloaded.GetInt64() : null;
        if (value.TryGetProperty("size", out var size))
            _size = size.GetInt64();
        if (value.TryGetProperty("file_count", out var count))
            _count = count.GetInt64();
        if (value.TryGetProperty("match", out var match))
            IsMatch = match.GetBoolean();
        if (preserveChoices || !value.TryGetProperty("priority", out var priority))
            return;
        if (!IsFolder)
        {
            if (priority.ValueKind == JsonValueKind.Number)
                _priority = priority.GetInt32();
            return;
        }
        _priority = priority.ValueKind == JsonValueKind.Number ? priority.GetInt32() : -1;
        _wantedCount = value.GetProperty("wanted_count").GetInt64();
        _wantedSize = value.GetProperty("wanted_size").GetInt64();
    }

    internal void Total(long wantedCount, long wantedSize)
    {
        _wantedCount = wantedCount;
        _wantedSize = wantedSize;
    }

    // Whether the choice would change nothing here.
    internal bool Holds(FileAction action) =>
        IsFolder ? action.Holds(_wantedCount, _count, _priority) : action.Holds(_priority);

    // Shows the choice on this row and the rows held under it, by the rule
    // the engine applies. A folder's priority stays Mixed after Want when its
    // files differed, because only the engine knows whether they still do.
    internal void Choose(FileAction action)
    {
        _priority = action.Apply(_priority);
        if (!IsFolder)
            return;
        (_wantedCount, _wantedSize) = action is FileAction.Set { Priority: 0 } ? (0, 0) : (_count, _size);
        foreach (var child in _children)
            child.Choose(action);
    }

    internal void Refresh(bool notify)
    {
        if (!IsFolder)
        {
            _count = 1;
            (_wantedCount, _wantedSize) = _priority > 0 ? (1, _size) : (0, 0);
        }
        foreach (var child in _children)
            child.Refresh(notify);
        var shown = (_size, _count, _wantedCount, _priority, _downloaded, _state);
        if (notify || shown != _shown)
        {
            _shown = shown;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }
    }

    public override string ToString() => Path;
}

// The rows a refresh names: folder paths and file indexes.
internal sealed record FileKeys(string[] Folders, int[] Files);

// The files a choice covers.
internal abstract record FileTarget
{
    private FileTarget() { }

    // The path places the file under the folders a draft also holds.
    internal sealed record File(int Index, string Path) : FileTarget;

    // Every file under the folder; the empty path is the root.
    internal sealed record Folder(string Path) : FileTarget;

    // Every file that matches the search.
    internal sealed record Query(string Text) : FileTarget;

    // Whether this file or folder lies under `folder`.
    internal bool IsUnder(Folder folder) =>
        this switch
        {
            File file => Within(file.Path, folder.Path),
            Folder other => other.Path != folder.Path && Within(other.Path, folder.Path),
            _ => false,
        };

    private static bool Within(string path, string folder) =>
        folder.Length == 0 || path.StartsWith(folder + "/", StringComparison.Ordinal);
}

// What a choice sets on every file it covers, by the rule of the pipe API's
// `edit`.
internal abstract record FileAction
{
    private FileAction() { }

    // That priority: 0 skips the file.
    internal sealed record Set(int Priority) : FileAction;

    // Normal on a skipped file; another priority stays.
    internal sealed record Want : FileAction;

    internal int Apply(int priority) =>
        this is Set set ? set.Priority
        : priority == 0 ? 4
        : priority;

    internal bool Holds(int priority) => this is Set set ? priority == set.Priority : priority > 0;

    // Whether a folder with these values already holds the choice; its
    // priority is -1 when its files differ.
    internal bool Holds(long wantedCount, long count, int priority) =>
        this is Set { Priority: 0 } ? wantedCount == 0
        : this is Set set ? priority == set.Priority
        : wantedCount == count;
}

internal sealed record FileChoice(FileTarget Target, FileAction Action)
{
    // The `changes` of one `edit` that sends these choices. A query choice is
    // sent alone, as the engine requires.
    internal static Dictionary<string, object> Changes(IEnumerable<FileChoice> choices)
    {
        var changes = new Dictionary<string, object>();
        foreach (var choice in choices)
        {
            var (key, entry) = choice.Target switch
            {
                FileTarget.File file => ("files", Entry("index", file.Index, choice.Action)),
                FileTarget.Folder folder => ("folders", Entry("path", folder.Path, choice.Action)),
                FileTarget.Query query => ("query", Entry("text", query.Text, choice.Action)),
                _ => throw new InvalidOperationException(),
            };
            if (key == "query")
                changes[key] = entry;
            else if (changes.TryGetValue(key, out var list))
                ((List<Dictionary<string, object>>)list).Add(entry);
            else
                changes[key] = new List<Dictionary<string, object>> { entry };
        }
        return changes;
    }

    private static Dictionary<string, object> Entry(string key, object value, FileAction action) =>
        action is FileAction.Set set
            ? new() { [key] = value, ["priority"] = set.Priority }
            : new() { [key] = value, ["wanted"] = true };
}
