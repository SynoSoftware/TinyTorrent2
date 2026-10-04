using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;

namespace Syno.TinyTorrent;

public sealed class FileSelection(Strings strings) : INotifyPropertyChanged
{
    private readonly List<FileNode> _roots = [];
    private readonly List<FileNode> _files = [];
    private string _search = string.Empty;
    public Strings Text => strings;
    public ObservableCollection<FileNode> Roots { get; } = [];
    public string Search
    {
        get => _search;
        set { if (_search == value) return; _search = value; Project(); }
    }
    public bool HasWanted => _files.Any(file => file.Priority > 0);
    public string Summary => strings.Format("files", "summary", _files.Count(file => file.Priority > 0),
        _files.Count(file => !file.IsPadding), strings.Bytes(_files.Where(file => file.Priority > 0).Sum(file => file.Size)));
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? Changed;

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
                var node = leaf ? null : siblings.FirstOrDefault(node => node.Name == parts[i] && node.Index < 0);
                if (node is null)
                {
                    var index = leaf ? item.GetProperty("index").GetInt32() : -1;
                    node = new FileNode(this, parts[i], parent, index)
                    {
                        Path = string.Join('/', parts.Take(i + 1)),
                        IsPadding = leaf && item.GetProperty("padding").GetBoolean(),
                        Size = leaf ? item.GetProperty("size").GetInt64() : 0
                    };
                    if (leaf)
                    {
                        node.SetPriority(node.IsPadding ? 0 : choices.GetValueOrDefault(index, item.GetProperty("priority").GetInt32()));
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

    internal void Want(FileNode node, bool wanted) => Want(node.Files(), wanted);

    private void Want(IEnumerable<FileNode> files, bool wanted)
    {
        foreach (var file in files)
            if (!file.IsPadding && (!wanted || file.Priority == 0)) file.SetPriority(wanted ? 4 : 0);
        Refresh();
    }

    internal void Change(FileNode node, int priority)
    {
        if (priority < 0) return;
        foreach (var file in node.Files()) file.SetPriority(priority);
        Refresh();
    }

    internal int[] Priorities() => _files.OrderBy(file => file.Index).Select(file => file.Priority).ToArray();

    private bool Matches(FileNode file) => file.Path.Contains(_search, StringComparison.OrdinalIgnoreCase);

    private void Project()
    {
        Reconcile(Roots, _roots.Where(root => root.Project(Matches)).ToArray());
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    internal static void Reconcile(ObservableCollection<FileNode> nodes, FileNode[] desired)
    {
        var retained = desired.ToHashSet();
        for (var index = nodes.Count - 1; index >= 0; index--)
            if (!retained.Contains(nodes[index])) nodes.RemoveAt(index);
        for (var index = 0; index < desired.Length; index++)
            if (index >= nodes.Count || nodes[index] != desired[index]) nodes.Insert(index, desired[index]);
    }

    internal void Refresh()
    {
        foreach (var node in _roots.SelectMany(root => root.Nodes())) node.Refresh();
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
    public long Size { get; internal set; }
    public string SizeText => _owner.Text.Bytes(Files().Sum(file => file.Size));
    public bool? Wanted
    {
        get
        {
            var files = Files().ToArray();
            return files.All(file => file._priority > 0) ? true : files.All(file => file._priority == 0) ? false : null;
        }
        set { if (value is { } wanted) _owner.Want(this, wanted); }
    }
    public int Priority
    {
        get
        {
            if (Index >= 0) return _priority;
            var priorities = Files().Select(file => file._priority).Distinct().Take(2).ToArray();
            return priorities.Length == 1 ? priorities[0] : -1;
        }
        set => _owner.Change(this, value);
    }
    public int PriorityIndex
    {
        get => Priority switch { 0 => 1, 1 => 2, 4 => 3, 7 => 4, _ => 0 };
        set => Priority = value switch { 1 => 0, 2 => 1, 3 => 4, 4 => 7, _ => -1 };
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

    internal IEnumerable<FileNode> Files() => Index >= 0 ? IsPadding ? [] : [this] : Descendants.SelectMany(child => child.Files());
    internal IEnumerable<FileNode> Nodes() => new[] { this }.Concat(Descendants.SelectMany(child => child.Nodes()));
    internal void SetPriority(int priority) => _priority = priority;
    internal bool Project(Func<FileNode, bool> matches)
    {
        FileSelection.Reconcile(Children, Descendants.Where(child => child.Project(matches)).ToArray());
        return Index >= 0 ? !IsPadding && matches(this) : Children.Count > 0;
    }
    internal void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    public override string ToString() => Name;
}
