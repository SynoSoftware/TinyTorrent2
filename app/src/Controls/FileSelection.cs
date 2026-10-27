using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Syno.TinyTorrent.Shared;

namespace Syno.TinyTorrent.Controls;

internal sealed record FileEntry(int Index, string Path, long Size, int Priority, bool Padding)
{
    internal static FileEntry Read(JsonElement value) => new(
        value.GetProperty("index").GetInt32(),
        value.GetProperty("path").GetString()!,
        value.GetProperty("size").GetInt64(),
        value.GetProperty("priority").ValueKind == JsonValueKind.Null
            ? -1 : value.GetProperty("priority").GetInt32(),
        value.GetProperty("padding").GetBoolean()
    );
}

internal sealed record FileChoice(int Index, int Priority, long Size, int Baseline);

internal sealed record FilePage
{
    public IReadOnlyList<FileEntry> Files { get; init; } = [];
    public int Offset { get; init; }
    public int? Next { get; init; }
    public int Count { get; init; }
    public int SelectableCount { get; init; }
    public long Bytes { get; init; }
    public int WantedCount { get; init; }
    public long WantedBytes { get; init; }

    internal static FilePage Read(JsonElement value)
    {
        var files = value.GetProperty("files").EnumerateArray().Select(FileEntry.Read).ToArray();
        return new()
        {
            Files = files,
            Offset = value.GetProperty("file_offset").GetInt32(),
            Next = value.GetProperty("next_offset").ValueKind == JsonValueKind.Null
                ? null : value.GetProperty("next_offset").GetInt32(),
            Count = value.GetProperty("file_count").GetInt32(),
            SelectableCount = value.GetProperty("selectable_count").GetInt32(),
            Bytes = value.GetProperty("file_bytes").GetInt64(),
            WantedCount = value.GetProperty("wanted_count").GetInt32(),
            WantedBytes = value.GetProperty("wanted_bytes").GetInt64(),
        };
    }
}

public sealed class FileSelection(Strings strings) : INotifyPropertyChanged
{
    private readonly List<FileNode> _roots = [];
    private readonly List<FileNode> _files = [];
    private readonly Dictionary<int, FileChoice> _choices = [];
    private readonly List<FilePage> _pages = [];
    private readonly Dictionary<int, int> _previous = [];
    private readonly Dictionary<int, FileEntry> _entries = [];
    private Func<int, string, Task<FilePage>>? _read;
    private FilePage? _totals;
    private int? _defaultPriority;
    private int _generation;
    private bool _reading;
    private string _search = string.Empty;
    private string _query = string.Empty;
    private bool _showsProgress;
    private bool _enabled = true;
    public Strings Text => strings;
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
            _query = FileName.Normalize(value);
            if (_read is null)
                Project();
            else
                _ = Reload();
        }
    }
    public bool HasFolders => _roots.Any(root => root.IsFolder);
    public bool HasWanted => WantedCount > 0;
    internal int? DefaultPriority => _defaultPriority;
    internal bool HasMore => _pages.LastOrDefault()?.Next is not null;
    internal bool HasPrevious => _pages.Count > 0 && _previous.ContainsKey(_pages[0].Offset);
    internal bool IsPaged => _read is not null;
    private int WantedCount => _totals is { } totals
        ? (_defaultPriority is { } priority ? priority > 0 ? totals.SelectableCount : 0 : totals.WantedCount)
            + _choices.Values.Sum(choice => (choice.Priority > 0 ? 1 : 0)
                - ((_defaultPriority ?? choice.Baseline) > 0 ? 1 : 0))
        : _files.Count(file => !file.IsPadding && file.Priority > 0);
    public bool? AllMatching
    {
        get
        {
            if (_read is not null && _query.Length == 0 && _totals is { } totals)
                return WantedCount == 0 ? false : WantedCount == totals.SelectableCount ? true : null;
            var matching = _files.Where(file => !file.IsPadding && Matches(file)).ToArray();
            if (matching.Any(file => file.Priority < 0))
                return null;
            var wanted = matching.Count(file => file.Priority > 0);
            return wanted == 0 ? false
                : wanted == matching.Length ? true
                : null;
        }
    }
    public long WantedBytes => _totals is { } totals
        ? (_defaultPriority is { } priority ? priority > 0 ? totals.Bytes : 0 : totals.WantedBytes)
            + _choices.Values.Sum(choice => (choice.Priority > 0 ? choice.Size : 0)
                - ((_defaultPriority ?? choice.Baseline) > 0 ? choice.Size : 0))
        : _files.Where(file => file.Priority > 0).Sum(file => file.Size);
    public string Summary => _read is not null && (HasMore || HasPrevious || _query.Length > 0)
        ? strings.Format("files", "window", SelectionSummary, _files.Count(file => !file.IsPadding))
        : SelectionSummary;
    public string AllLabel => strings.Get("files", _read is not null && _query.Length > 0
        ? "all_displayed" : "all_matching");
    private string SelectionSummary =>
        _files.Any(file => !file.IsPadding && file.Priority < 0)
            ? "—"
            : strings.Format(
                "files",
                "summary",
                WantedCount,
                _totals?.SelectableCount ?? _files.Count(file => !file.IsPadding),
                strings.Bytes(WantedBytes)
            );
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? Changed;
    public event EventHandler<IReadOnlyDictionary<int, int>>? Edited;
    internal event EventHandler<Exception>? Failed;

    internal void Clear()
    {
        _roots.Clear();
        _files.Clear();
        _choices.Clear();
        _pages.Clear();
        _previous.Clear();
        _entries.Clear();
        _read = null;
        _totals = null;
        _defaultPriority = null;
        _generation++;
        _search = string.Empty;
        _query = string.Empty;
        Refresh(project: true);
    }

    internal void Load(JsonElement files)
    {
        LoadEntries(files.EnumerateArray().Select(FileEntry.Read));
    }

    private void LoadEntries(IEnumerable<FileEntry> entries)
    {
        var choices = _files.ToDictionary(file => file.Index, file => file.Priority);
        var expanded = _roots.SelectMany(root => root.Nodes()).Where(node => node.IsFolder)
            .ToDictionary(node => node.Path, node => node.IsExpanded);
        var folders = new Dictionary<(FileNode? Parent, string Name), FileNode>();
        _roots.Clear();
        _files.Clear();
        _entries.Clear();
        foreach (var item in entries)
        {
            var path = item.Path;
            var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            var siblings = _roots;
            FileNode? parent = null;
            for (var i = 0; i < parts.Length; i++)
            {
                var leaf = i == parts.Length - 1;
                var node = leaf ? null : folders.GetValueOrDefault((parent, parts[i]));
                if (node is null)
                {
                    var index = leaf ? item.Index : -1;
                    node = new FileNode(this, parts[i], parent, index)
                    {
                        Path = string.Join('/', parts.Take(i + 1)),
                        IsPadding = leaf && item.Padding,
                        Size = leaf ? item.Size : 0,
                    };
                    if (leaf)
                    {
                        node.SetPriority(
                            node.IsPadding
                                ? 0
                                : _read is not null
                                    ? _choices.TryGetValue(index, out var choice) ? choice.Priority
                                        : _defaultPriority ?? item.Priority
                                    : choices.GetValueOrDefault(index, item.Priority)
                        );
                        _files.Add(node);
                        _entries.Add(index, item);
                    }
                    else
                        node.IsExpanded = expanded.GetValueOrDefault(node.Path, true);
                    siblings.Add(node);
                    if (!leaf)
                        folders.Add((parent, parts[i]), node);
                }
                parent = node;
                siblings = node.Descendants;
            }
        }
        Refresh(project: true);
    }

    public void SelectMatching(bool wanted)
    {
        if (!IsEnabled)
            return;
        if (_read is null || _query.Length > 0)
        {
            Want(_files.Where(Matches), wanted);
            return;
        }
        SelectAll(wanted);
    }

    internal void SelectAll() => SelectAll(true);

    private void SelectAll(bool wanted)
    {
        if (_read is null)
        {
            Want(_files, wanted);
            return;
        }
        _defaultPriority = wanted ? 4 : 0;
        _choices.Clear();
        foreach (var file in _files)
            file.SetPriority(file.IsPadding ? 0 : _defaultPriority.Value);
        Refresh();
    }

    internal void InitializePage(FilePage page, Func<int, string, Task<FilePage>> read)
    {
        _generation++;
        _read = read;
        _totals = page;
        _pages.Clear();
        _previous.Clear();
        _pages.Add(page);
        _defaultPriority = null;
        _choices.Clear();
        LoadEntries(page.Files);
    }

    internal async Task<bool> ReadWindow(bool forward)
    {
        if (_read is not { } read || _reading || _pages.Count == 0)
            return false;
        var offset = forward ? _pages[^1].Next
            : _previous.TryGetValue(_pages[0].Offset, out var previous) ? previous : (int?)null;
        if (offset is null)
            return false;
        var generation = _generation;
        _reading = true;
        try
        {
            var page = await read(offset.Value, _search);
            if (_generation != generation)
                return false;
            if (forward)
            {
                if (_pages[^1].Offset == page.Offset)
                    _pages[^1] = page;
                else
                {
                    _previous[page.Offset] = _pages[^1].Offset;
                    _pages.Add(page);
                }
                if (_pages.Count > 3)
                    _pages.RemoveAt(0);
            }
            else
            {
                _pages.Insert(0, page);
                if (_pages.Count > 3)
                    _pages.RemoveAt(_pages.Count - 1);
            }
            LoadEntries(_pages.SelectMany(item => item.Files));
            return true;
        }
        catch (Exception error)
        {
            if (_generation == generation && error is not OperationCanceledException)
                Failed?.Invoke(this, error);
            return false;
        }
        finally
        {
            _reading = false;
        }
    }

    private async Task Reload()
    {
        if (_read is not { } read)
            return;
        var generation = ++_generation;
        _pages.Clear();
        _previous.Clear();
        LoadEntries([]);
        try
        {
            var page = await read(0, _search);
            if (_generation != generation)
                return;
            _pages.Add(page);
            LoadEntries(page.Files);
        }
        catch (Exception error)
        {
            if (_generation == generation && error is not OperationCanceledException)
                Failed?.Invoke(this, error);
        }
    }

    internal IReadOnlyList<FileNode> VisibleNodes() => Roots.SelectMany(Visible).ToArray();

    private static IEnumerable<FileNode> Visible(FileNode node) => new[] { node }.Concat(
        node.IsFolder && node.IsExpanded ? node.Children.SelectMany(Visible) : []
    );

    internal FileNode? Reveal(int index)
    {
        var file = _files.FirstOrDefault(file => file.Index == index);
        if (file is null)
            return null;
        for (var parent = file.Parent; parent is not null; parent = parent.Parent)
            parent.IsExpanded = true;
        Search = string.Empty;
        return file;
    }

    public void Expand(bool expanded)
    {
        foreach (var node in _roots.SelectMany(root => root.Nodes()).Where(node => node.IsFolder))
            node.IsExpanded = expanded;
    }

    internal void Apply(JsonElement files, bool preserveChoices)
    {
        var known = _files.ToDictionary(file => file.Index);
        foreach (var item in files.EnumerateArray())
        {
            if (!known.TryGetValue(item.GetProperty("index").GetInt32(), out var file))
                continue;
            var downloaded = item.GetProperty("downloaded");
            if (downloaded.ValueKind != JsonValueKind.Null)
                file.Downloaded = downloaded.GetInt64();
            if (!preserveChoices)
            {
                var priority = item.GetProperty("priority");
                if (priority.ValueKind != JsonValueKind.Null)
                    file.SetPriority(priority.GetInt32());
            }
        }
        Refresh(notify: false);
    }

    internal void Want(FileNode node, bool wanted) => Want(node.Files(), wanted);

    private void Want(IEnumerable<FileNode> files, bool wanted)
    {
        if (wanted)
            files = files.Where(file => file.Priority <= 0);
        Change(files, wanted ? 4 : 0);
    }

    internal void Change(IEnumerable<FileNode> nodes, int priority)
    {
        if (!IsEnabled || priority < 0)
            return;
        var changes = new Dictionary<int, int>();
        var current = _files.ToHashSet();
        foreach (var file in nodes.SelectMany(node => node.Files()).DistinctBy(file => file.Index))
        {
            if (!current.Contains(file) || file.Priority == priority)
                continue;
            file.SetPriority(priority);
            if (_read is not null && _entries.TryGetValue(file.Index, out var entry))
            {
                if (priority == (_defaultPriority ?? entry.Priority))
                    _choices.Remove(file.Index);
                else
                    _choices[file.Index] = new(file.Index, priority, entry.Size, entry.Priority);
            }
            changes.Add(file.Index, priority);
        }
        Refresh(notify: false);
        if (changes.Count > 0)
            Edited?.Invoke(this, changes);
    }

    internal int[] Priorities() =>
        _files.OrderBy(file => file.Index).Select(file => file.Priority).ToArray();

    internal IReadOnlyList<FileChoice> Choices() => _read is not null
        ? _choices.Values.ToArray()
        : _files.Where(file => !file.IsPadding).Select(file => new FileChoice(file.Index, file.Priority, file.Size, file.Priority)).ToArray();

    private bool Matches(FileNode file) =>
        _search.Length == 0 || file.SearchText.Contains(_query, StringComparison.Ordinal);

    private void Project()
    {
        Roots = _roots.Where(root => root.Project(Matches)).ToArray();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    internal void Refresh(bool project = false, bool notify = true)
    {
        foreach (var root in _roots)
            root.Refresh(notify);
        if (project)
            Project();
        else
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class FileNode : INotifyPropertyChanged
{
    private readonly FileSelection _owner;
    private int _choice = 4;
    private int _priority = 4;
    private bool _hasFiles;
    private long _totalSize;
    private long? _downloaded;
    private bool? _wanted = true;
    private string _path = string.Empty;
    internal List<FileNode> Descendants { get; } = [];
    public IReadOnlyList<FileNode> Children { get; private set; } = [];
    public string Name { get; }
    public string Path
    {
        get => _path;
        internal set
        {
            _path = value;
            SearchText = IsFolder ? string.Empty : FileName.Normalize(value);
        }
    }
    internal string SearchText { get; private set; } = string.Empty;
    public FileNode? Parent { get; }
    public int Index { get; }
    public bool IsFolder => Index < 0;
    public bool IsPadding { get; internal set; }
    public bool IsExpanded { get; set; } = true;
    public long Size { get; internal set; }
    public long? Downloaded { get; internal set; }
    public string Glyph => IsFolder ? Lucide.Folder : FileName.Glyph(FileName.Kind(Name));
    public long TotalSize => _totalSize;
    public string SizeText => _owner.Text.Bytes(TotalSize);
    public double? Progress =>
        _downloaded is not { } downloaded ? null
        : _totalSize == 0 ? 1
        : Math.Clamp((double)downloaded / _totalSize, 0, 1);
    public string ProgressText => Progress?.ToString("P1", CultureInfo.CurrentCulture) ?? "—";
    public double ProgressValue => Progress ?? 0;
    public bool IsIndeterminate => Progress is null;
    public bool IsComplete => Progress is >= 1;
    public bool IsEnabled => _owner.IsEnabled;
    public bool? Wanted
    {
        get => _wanted;
        set
        {
            if (value is { } wanted)
                _owner.Want(this, wanted);
        }
    }
    public int Priority
    {
        get => _priority;
        set => _owner.Change([this], value);
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
    }

    internal IEnumerable<FileNode> Files() =>
        Index >= 0
            ? IsPadding
                ? []
                : [this]
            : Descendants.SelectMany(child => child.Files());

    internal IEnumerable<FileNode> Nodes() =>
        new[] { this }.Concat(Descendants.SelectMany(child => child.Nodes()));

    internal void SetPriority(int priority) => _choice = priority;

    internal bool Project(Func<FileNode, bool> matches)
    {
        Children = Descendants.Where(child => child.Project(matches)).ToArray();
        return Index >= 0 ? !IsPadding && matches(this) : Children.Count > 0;
    }

    internal void Refresh(bool notify)
    {
        var previous = (_totalSize, _downloaded, _wanted, _priority);
        if (IsFolder)
        {
            _hasFiles = false;
            _totalSize = 0;
            _downloaded = 0;
            _priority = -1;
            var wanted = true;
            var skipped = true;
            foreach (var child in Descendants)
            {
                child.Refresh(notify);
                if (!child._hasFiles)
                    continue;
                _priority =
                    !_hasFiles ? child._priority
                    : _priority == child._priority ? _priority
                    : -1;
                _hasFiles = true;
                _totalSize += child._totalSize;
                _downloaded += child._downloaded;
                wanted &= child._wanted == true;
                skipped &= child._wanted == false;
            }
            _wanted =
                wanted ? true
                : skipped ? false
                : null;
        }
        else
        {
            _hasFiles = !IsPadding;
            _totalSize = IsPadding ? 0 : Size;
            _downloaded = IsPadding ? 0 : Downloaded;
            _priority = _choice;
            _wanted =
                IsPadding || _priority > 0 ? true
                : _priority == 0 ? false
                : null;
        }
        if (notify || previous != (_totalSize, _downloaded, _wanted, _priority))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public override string ToString() => Path;
}
