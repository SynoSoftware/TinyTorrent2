using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Controls;

public sealed class FileSelection(Strings strings) : INotifyPropertyChanged
{
    private readonly List<FileNode> _roots = [];
    private readonly List<FileNode> _files = [];
    private string _search = string.Empty;
    private bool _showsProgress;
    private bool _enabled = true;
    public Strings Text => strings;
    public ObservableCollection<FileNode> Roots { get; } = [];
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
            var wanted = matching.Count(file => file.Priority > 0);
            return wanted == 0 ? false
                : wanted == matching.Length ? true
                : null;
        }
    }
    public long WantedBytes => _files.Where(file => file.Priority > 0).Sum(file => file.Size);
    public string Summary =>
        strings.Format(
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
        Project();
        Refresh();
    }

    internal void Load(JsonElement files)
    {
        var choices = _files.ToDictionary(file => file.Index, file => file.Priority);
        _roots.Clear();
        _files.Clear();
        foreach (var item in files.EnumerateArray())
        {
            var path = item.GetProperty("path").GetString()!;
            var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            var siblings = _roots;
            FileNode? parent = null;
            for (var i = 0; i < parts.Length; i++)
            {
                var leaf = i == parts.Length - 1;
                var node = leaf
                    ? null
                    : siblings.FirstOrDefault(node => node.Name == parts[i] && node.Index < 0);
                if (node is null)
                {
                    var index = leaf ? item.GetProperty("index").GetInt32() : -1;
                    node = new FileNode(this, parts[i], parent, index)
                    {
                        Path = string.Join('/', parts.Take(i + 1)),
                        IsPadding = leaf && item.GetProperty("padding").GetBoolean(),
                        Size = leaf ? item.GetProperty("size").GetInt64() : 0,
                    };
                    if (leaf)
                    {
                        node.SetPriority(
                            node.IsPadding
                                ? 0
                                : choices.GetValueOrDefault(
                                    index,
                                    item.GetProperty("priority").GetInt32()
                                )
                        );
                        _files.Add(node);
                    }
                    siblings.Add(node);
                }
                parent = node;
                siblings = node.Descendants;
            }
        }
        Project();
        Refresh();
    }

    public void SelectMatching(bool wanted) => Want(_files.Where(Matches), wanted);

    internal void SelectAll() => Want(_files, true);

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
            file.Downloaded = item.GetProperty("downloaded").GetInt64();
            if (!preserveChoices)
                file.SetPriority(item.GetProperty("priority").GetInt32());
        }
        Refresh();
    }

    internal void Want(FileNode node, bool wanted) => Want(node.Files(), wanted);

    private void Want(IEnumerable<FileNode> files, bool wanted)
    {
        if (!IsEnabled)
            return;
        var changes = new Dictionary<int, int>();
        foreach (var file in files)
        {
            if (file.IsPadding)
                continue;
            if (wanted && file.Priority > 0)
                continue;
            var priority = wanted ? 4 : 0;
            if (file.Priority == priority)
                continue;
            file.SetPriority(priority);
            changes.Add(file.Index, file.Priority);
        }
        Refresh();
        if (changes.Count > 0)
            Edited?.Invoke(this, changes);
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
        Refresh();
        if (changes.Count > 0)
            Edited?.Invoke(this, changes);
    }

    internal int[] Priorities() =>
        _files.OrderBy(file => file.Index).Select(file => file.Priority).ToArray();

    private bool Matches(FileNode file) =>
        file.Path.Contains(_search, StringComparison.OrdinalIgnoreCase);

    private void Project()
    {
        Reconcile(Roots, _roots.Where(root => root.Project(Matches)).ToArray());
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    internal static void Reconcile(ObservableCollection<FileNode> nodes, FileNode[] desired)
    {
        var retained = desired.ToHashSet();
        for (var index = nodes.Count - 1; index >= 0; index--)
            if (!retained.Contains(nodes[index]))
                nodes.RemoveAt(index);
        for (var index = 0; index < desired.Length; index++)
            if (index >= nodes.Count || nodes[index] != desired[index])
                nodes.Insert(index, desired[index]);
    }

    internal void Refresh()
    {
        foreach (var node in _roots.SelectMany(root => root.Nodes()))
            node.Refresh();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class FileNode : INotifyPropertyChanged
{
    private readonly FileSelection _owner;
    private int _priority = 4;
    internal List<FileNode> Descendants { get; } = [];
    public ObservableCollection<FileNode> Children { get; } = [];
    public string Name { get; }
    public string Path { get; internal set; } = string.Empty;
    public FileNode? Parent { get; }
    public int Index { get; }
    public bool IsFolder => Index < 0;
    public bool IsPadding { get; internal set; }
    public bool IsExpanded { get; set; } = true;
    public long Size { get; internal set; }
    public long Downloaded { get; internal set; }
    public string Glyph =>
        IsFolder
            ? Lucide.Folder
            : System.IO.Path.GetExtension(Name).ToLowerInvariant() switch
            {
                ".mkv"
                or ".mp4"
                or ".avi"
                or ".mov"
                or ".wmv"
                or ".webm"
                or ".m4v"
                or ".mpg"
                or ".mpeg"
                or ".ts" => Lucide.FileVideoCamera,
                ".flac" or ".mp3" or ".wav" or ".aac" or ".ogg" or ".opus" or ".m4a" or ".wma" =>
                    Lucide.FileMusic,
                ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" => Lucide.FileImage,
                ".txt"
                or ".nfo"
                or ".md"
                or ".pdf"
                or ".doc"
                or ".docx"
                or ".srt"
                or ".ass"
                or ".sub" => Lucide.FileText,
                ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".iso" => Lucide.FileArchive,
                _ => Lucide.File,
            };
    public long TotalSize => Files().Sum(file => file.Size);
    public string SizeText => _owner.Text.Bytes(TotalSize);
    public double Progress
    {
        get
        {
            var files = Files().ToArray();
            var size = files.Sum(file => file.Size);
            return size == 0
                ? 1
                : Math.Clamp((double)files.Sum(file => file.Downloaded) / size, 0, 1);
        }
    }
    public string ProgressText => Progress.ToString("P1", CultureInfo.CurrentCulture);
    public bool IsEnabled => _owner.IsEnabled;
    public bool? Wanted
    {
        get
        {
            var files = Files().ToArray();
            return files.All(file => file._priority > 0) ? true
                : files.All(file => file._priority == 0) ? false
                : null;
        }
        set
        {
            if (value is { } wanted)
                _owner.Want(this, wanted);
        }
    }
    public int Priority
    {
        get
        {
            if (Index >= 0)
                return _priority;
            var priorities = Files().Select(file => file._priority).Distinct().Take(2).ToArray();
            return priorities.Length == 1 ? priorities[0] : -1;
        }
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

    internal void SetPriority(int priority) => _priority = priority;

    internal bool Project(Func<FileNode, bool> matches)
    {
        FileSelection.Reconcile(
            Children,
            Descendants.Where(child => child.Project(matches)).ToArray()
        );
        return Index >= 0 ? !IsPadding && matches(this) : Children.Count > 0;
    }

    internal void Refresh() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    public override string ToString() => Path;
}
