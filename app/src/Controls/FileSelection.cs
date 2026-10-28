using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Engine;

namespace Syno.TinyTorrent.Controls;

public sealed class FileSelection(Strings strings) : INotifyPropertyChanged
{
    private readonly List<FileNode> _roots = [];
    private readonly List<FileNode> _files = [];
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
            Project();
        }
    }
    public bool HasFolders => _roots.Any(root => root.IsFolder);
    public bool HasWanted => _files.Any(file => file.Priority > 0);
    public bool? AllMatching
    {
        get
        {
            var matching = _files.Where(file => !file.IsPadding && Matches(file)).ToArray();
            if (matching.Any(file => file.Priority < 0))
                return null;
            var wanted = matching.Count(file => file.Priority > 0);
            return wanted == 0 ? false
                : wanted == matching.Length ? true
                : null;
        }
    }
    public long WantedBytes => _files.Where(file => file.Priority > 0).Sum(file => file.Size);
    public string Summary =>
        _files.Any(file => !file.IsPadding && file.Priority < 0)
            ? "—"
            : strings.Format(
                "files",
                "summary",
                _files.Count(file => file.Priority > 0),
                _files.Count(file => !file.IsPadding),
                strings.Bytes(WantedBytes)
            );
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? Changed;
    public event EventHandler<IReadOnlyDictionary<int, int>>? Edited;

    internal void Clear()
    {
        _roots.Clear();
        _files.Clear();
        _search = string.Empty;
        _query = string.Empty;
        Refresh(project: true);
    }

    internal int Count => _files.Count;

    // Builds the rows from pages of files while the current rows stay shown,
    // so a page that never arrives leaves them as they were.
    internal Loader Load() => new(this);

    internal sealed class Loader
    {
        private readonly FileSelection _owner;
        private readonly Dictionary<int, int> _choices;
        private readonly Dictionary<string, bool> _expanded;
        private readonly Dictionary<(FileNode? Parent, string Name), FileNode> _folders = [];
        private readonly List<FileNode> _roots = [];
        private readonly List<FileNode> _files = [];

        internal Loader(FileSelection owner)
        {
            _owner = owner;
            _choices = owner._files.ToDictionary(file => file.Index, file => file.Priority);
            _expanded = owner
                ._roots.SelectMany(root => root.Nodes())
                .Where(node => node.IsFolder)
                .ToDictionary(node => node.Path, node => node.IsExpanded);
        }

        internal int Count => _files.Count;

        // A page without a priority leaves each file's choice unknown until the
        // Files view reports it.
        internal void Add(JsonElement page)
        {
            foreach (var item in page.GetProperty("files").EnumerateArray())
            {
                var path = item.GetProperty("path").GetString()!;
                var parts = path.Replace('\\', '/')
                    .Split('/', StringSplitOptions.RemoveEmptyEntries);
                var siblings = _roots;
                FileNode? parent = null;
                for (var i = 0; i < parts.Length; i++)
                {
                    var leaf = i == parts.Length - 1;
                    var node = leaf ? null : _folders.GetValueOrDefault((parent, parts[i]));
                    if (node is null)
                    {
                        var index = leaf ? item.GetProperty("index").GetInt32() : -1;
                        node = new FileNode(_owner, parts[i], parent, index)
                        {
                            Path = string.Join('/', parts.Take(i + 1)),
                            IsPadding = leaf && item.GetProperty("padding").GetBoolean(),
                            Size = leaf ? item.GetProperty("size").GetInt64() : 0,
                        };
                        if (leaf)
                        {
                            node.SetPriority(
                                node.IsPadding ? 0
                                : _choices.TryGetValue(index, out var choice) ? choice
                                : item.TryGetProperty("priority", out var priority) ? priority.GetInt32()
                                : -1
                            );
                            _files.Add(node);
                        }
                        else
                            node.IsExpanded = _expanded.GetValueOrDefault(node.Path, true);
                        siblings.Add(node);
                        if (!leaf)
                            _folders.Add((parent, parts[i]), node);
                    }
                    parent = node;
                    siblings = node.Descendants;
                }
            }
        }

        internal void Commit()
        {
            _owner._roots.Clear();
            _owner._roots.AddRange(_roots);
            _owner._files.Clear();
            _owner._files.AddRange(_files);
            _owner.Refresh(project: true);
        }
    }

    public void SelectMatching(bool wanted) => Want(_files.Where(Matches), wanted);

    internal void SelectAll() => Want(_files, true);

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

    internal void Apply(FileStatus status, bool preserveChoices)
    {
        foreach (var file in _files)
        {
            if (status.Downloaded is { } downloaded && file.Index < downloaded.Length)
                file.Downloaded = downloaded[file.Index];
            if (
                !preserveChoices
                && status.Priorities is { } priorities
                && file.Index < priorities.Length
            )
                file.SetPriority(priorities[file.Index]);
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
            changes.Add(file.Index, priority);
        }
        Refresh(notify: false);
        if (changes.Count > 0)
            Edited?.Invoke(this, changes);
    }

    internal int[] Priorities() =>
        _files.OrderBy(file => file.Index).Select(file => file.Priority).ToArray();

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
