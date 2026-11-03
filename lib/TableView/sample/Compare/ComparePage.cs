using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Syno.TableView;

namespace Syno.TableViewSample.Compare;

/// <summary>
/// The table beside a plain ListView over the same rows. Every button changes the model once and
/// then applies the result to both panes in the same UI turn: the table gets a new list, the
/// ListView gets the collection operations a plain WinUI host would use.
/// </summary>
public sealed partial class ComparePage : Page
{
    private const int SmallRowCount = 40;
    private const int LargeRowCount = 2000;
    private const int Seed = 20240601;
    private const int ChangedPerUpdate = 8;
    private const int ReplacedPerClick = 5;

    private static readonly string[] Firsts =
    {
        "Amber", "Birch", "Cobalt", "Delta", "Ember", "Fjord", "Garnet", "Harbor",
        "Indigo", "Juniper", "Kestrel", "Lagoon", "Maple", "Nimbus", "Onyx", "Pebble",
    };

    private static readonly string[] Seconds =
    {
        "render", "export", "encode", "archive", "sync", "index", "upload", "backup",
    };

    private readonly HashSet<int> _usedSpeeds = new();
    private Random _random = new(Seed);
    private List<CompareRow>[] _lists;
    private int _rowsPerList = SmallRowCount;
    private int _active;
    private int _nextNewId;
    private int _tick;

    /// <summary>
    /// The row the last removal tick took out. The next removal tick puts this same instance
    /// back instead of a new row, so the table sees a row leave and return.
    /// </summary>
    private CompareRow? _removed;
    private ObservableCollection<CompareRow> _items = new();
    private DispatcherQueueTimer? _timer;

    public ComparePage()
    {
        InitializeComponent();

        Table
            .Schema<CompareRow>()
            .Key(row => row.Id)
            .SortKey(NameColumn, row => row.Name)
            .SortKey(SpeedColumn, row => row.Speed);
        Table.Sort = new Sort(SpeedColumn, SortDirection.Descending);

        _lists = MakeLists();
        Show();

        Unloaded += (_, _) => StopAuto();
    }

    /// <summary>
    /// The list on screen, in Id order, which is the table's source order. A header click that
    /// returns the table to source order therefore changes what it shows.
    /// </summary>
    private List<CompareRow> Rows => _lists[_active];

    /// <summary>The rows fastest first, which is the ListView's order.</summary>
    private List<CompareRow> BySpeed() => Rows.OrderByDescending(row => row.Speed).ToList();

    private List<CompareRow>[] MakeLists() => new[] { MakeList("a"), MakeList("b") };

    private List<CompareRow> MakeList(string prefix)
    {
        List<CompareRow> rows = new();
        for (int index = 1; index <= _rowsPerList; index++)
        {
            rows.Add(new CompareRow($"{prefix}{index:0000}", NewName(), NewSpeed()));
        }

        return rows;
    }

    private string NewName() =>
        $"{Firsts[_random.Next(Firsts.Length)]} {Seconds[_random.Next(Seconds.Length)]} {_random.Next(100, 1000)}";

    /// <summary>Speeds are unique, so the order has no ties for the two panes to break differently.</summary>
    private int NewSpeed()
    {
        int speed;
        do
        {
            speed = _random.Next(10, 100_000);
        }
        while (!_usedSpeeds.Add(speed));

        return speed;
    }

    /// <summary>Ids have four digits, so their ordinal order is their numeric order.</summary>
    private void AddInIdOrder(CompareRow row)
    {
        int index = Rows.FindIndex(other => string.CompareOrdinal(other.Id, row.Id) > 0);
        Rows.Insert(index < 0 ? Rows.Count : index, row);
    }

    /// <summary>Gives the table a new list and the ListView a new collection.</summary>
    private void Show()
    {
        Table.ItemsSource = new List<CompareRow>(Rows);
        _items = new ObservableCollection<CompareRow>(BySpeed());
        Plain.ItemsSource = _items;
    }

    private void ChangeSpeeds()
    {
        foreach (CompareRow row in Rows.OrderBy(_ => _random.Next()).Take(ChangedPerUpdate).ToList())
        {
            _usedSpeeds.Remove(row.Speed);
            row.Speed = NewSpeed();
        }
    }

    /// <summary>
    /// Removes a random row and adds one. The add is the row the previous removal tick took out,
    /// when there is one, and a new row otherwise. The returning row keeps its speed reserved.
    /// </summary>
    private void RemoveOneAddOne()
    {
        CompareRow removed = Rows[_random.Next(Rows.Count)];
        Rows.Remove(removed);

        if (_removed is null)
        {
            _removed = removed;
            AddInIdOrder(new CompareRow($"n{_nextNewId++:0000}", NewName(), NewSpeed()));
        }
        else
        {
            _usedSpeeds.Remove(removed.Speed);
            AddInIdOrder(_removed);
            _removed = null;
        }
    }

    /// <summary>A row held for return belongs to the list it left, so a list change drops it.</summary>
    private void DropRemoved()
    {
        if (_removed is not null)
        {
            _usedSpeeds.Remove(_removed.Speed);
            _removed = null;
        }
    }

    /// <summary>
    /// Gives the table a new list, and brings the collection to speed order with Remove, Insert
    /// and Move.
    /// </summary>
    private void Publish()
    {
        Table.ItemsSource = new List<CompareRow>(Rows);

        List<CompareRow> sorted = BySpeed();
        HashSet<CompareRow> kept = new(sorted);
        for (int index = _items.Count - 1; index >= 0; index--)
        {
            if (!kept.Contains(_items[index]))
            {
                _items.RemoveAt(index);
            }
        }

        for (int index = 0; index < sorted.Count; index++)
        {
            if (index < _items.Count && _items[index] == sorted[index])
            {
                continue;
            }

            int from = _items.IndexOf(sorted[index]);
            if (from < 0)
            {
                _items.Insert(index, sorted[index]);
            }
            else
            {
                _items.Move(from, index);
            }
        }
    }

    private void OnUpdateSpeeds(object sender, RoutedEventArgs e)
    {
        ChangeSpeeds();
        Publish();
    }

    private void OnSwitchList(object sender, RoutedEventArgs e)
    {
        DropRemoved();
        _active = 1 - _active;
        Show();
    }

    /// <summary>Rebuilds both lists at the other size from the same seed.</summary>
    private void OnRowCountClicked(object sender, RoutedEventArgs e)
    {
        bool large = RowCountButton.IsChecked == true;
        RowCountButton.Content = large ? "Rows: 2,000" : "Rows: 40";

        _rowsPerList = large ? LargeRowCount : SmallRowCount;
        _random = new Random(Seed);
        _usedSpeeds.Clear();
        _removed = null;
        _nextNewId = 0;
        _lists = MakeLists();
        Show();
    }

    private void OnReplaceRows(object sender, RoutedEventArgs e)
    {
        foreach (int index in Enumerable.Range(0, Rows.Count).OrderBy(_ => _random.Next()).Take(ReplacedPerClick))
        {
            CompareRow old = Rows[index];
            CompareRow fresh = new(old.Id, old.Name, old.Speed);
            Rows[index] = fresh;
            _items[_items.IndexOf(old)] = fresh;
        }

        Table.ItemsSource = new List<CompareRow>(Rows);
    }

    private void OnAutoClicked(object sender, RoutedEventArgs e)
    {
        if (AutoButton.IsChecked == true)
        {
            StartAuto();
        }
        else
        {
            StopAuto();
        }
    }

    private void StartAuto()
    {
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) =>
        {
            ChangeSpeeds();
            if (++_tick % 3 == 0)
            {
                RemoveOneAddOne();
            }

            Publish();
        };
        _timer.Start();
    }

    private void StopAuto()
    {
        _timer?.Stop();
        _timer = null;
    }
}
