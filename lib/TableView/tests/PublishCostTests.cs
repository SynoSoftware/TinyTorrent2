using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Syno.TableView.Tests;

/// <summary>
/// Measures what one publish costs the UI thread, in CPU cycles so that other load on the machine
/// does not count, through the public API only, so the same test measures any implementation. A
/// flat source changes by one row through each route a host can take: a notification of each kind,
/// or a plain list changed in place and assigned again as a new <c>ItemsSource</c>. A hierarchy
/// publishes a new root array, refreshes, or changes one child list. The product's path changes
/// sort values in place under a live sort and refreshes. It asserts no limit; compare runs on one
/// machine.
/// </summary>
[TestClass]
public class PublishCostTests
{
    private const int Warmup = 4;
    private const int Reps = 24;

    public TestContext TestContext { get; set; } = null!;

    [DllImport("kernel32.dll")]
    private static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    private static ulong Cycles()
    {
        QueryThreadCycleTime(GetCurrentThread(), out ulong cycles);
        return cycles;
    }

    private sealed class CostRow(int key, int rank)
    {
        public int Key { get; } = key;
        public string Name { get; } = "row " + key;
        public int Rank { get; set; } = rank;
        public override string ToString() => Name;
    }

    /// <summary>A notifying source that can also change without a notification, then raise Reset.</summary>
    private sealed class CostSource(IEnumerable<CostRow> rows) : ObservableCollection<CostRow>(rows)
    {
        internal void InsertQuietly(int index, CostRow row) => Items.Insert(index, row);

        internal void RemoveQuietly(int index) => Items.RemoveAt(index);

        internal void RaiseReset() => OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    private sealed class CostNode(int key, int rank)
    {
        public int Key { get; } = key;
        public int Rank { get; } = rank;
        public ObservableCollection<CostNode> Children { get; } = new();
        public bool IsExpanded { get; set; } = true;
        public override string ToString() => "n" + Key;
    }

    /// <summary>One measured operation and the operation that undoes it, applied in turn.</summary>
    private sealed record Step(string Name, Action Apply);

    // Each test stays inside TestHost's 30-second limit for one UI-thread body.
    [TestMethod]
    [TestCategory("Measurement")]
    [DataRow(20)]
    [DataRow(200)]
    [DataRow(1000)]
    [DataRow(2000)]
    public Task NotificationCycles(int size) =>
        TestHost.RunAsync(async () =>
        {
            foreach (bool sorted in new[] { false, true })
            {
                await MeasureNotificationsAsync(size, sorted);
            }
        });

    /// <summary>Notifications at 100,000 rows, the scale the plan states their cost bound for.</summary>
    [TestMethod]
    [TestCategory("Measurement")]
    [DataRow(false)]
    [DataRow(true)]
    public Task NotificationCyclesAtScale(bool sorted) =>
        TestHost.RunAsync(() => MeasureNotificationsAsync(100_000, sorted));

    /// <summary>
    /// Notifications while the pointer rests on the rows. A removed row is held in its place, so
    /// the order has pinned rows, states no difference, and the view walks itself against it.
    /// </summary>
    [TestMethod]
    [TestCategory("Measurement")]
    [DataRow(20)]
    [DataRow(200)]
    [DataRow(1000)]
    [DataRow(2000)]
    public Task HeldCycles(int size) =>
        TestHost.RunAsync(async () =>
        {
            CostSource source = new(Enumerable.Range(0, size).Select(key => new CostRow(key, Rank(key))));
            Table table = await LoadAsync(source, sorted: false);
            Proof.Call(table, "PointAt", new object?[] { null });
            int middle = size / 2;
            CostRow row = source[middle];
            await MeasureAsync(table, $"held {Label(size, sorted: false)}",
                new Step("Remove", () => source.RemoveAt(middle)),
                new Step("Insert back", () => source.Insert(middle, row)));
            Unload(table);
        });

    /// <summary>Notifications under a sort inside the product's three-second settle interval.</summary>
    [TestMethod]
    [TestCategory("Measurement")]
    [DataRow(20)]
    [DataRow(200)]
    [DataRow(1000)]
    [DataRow(2000)]
    public Task NotificationIntervalCycles(int size) =>
        TestHost.RunAsync(() => MeasureNotificationsAsync(size, sorted: true, TimeSpan.FromSeconds(3)));

    [TestMethod]
    [TestCategory("Measurement")]
    [DataRow(20, false)]
    [DataRow(200, false)]
    [DataRow(1000, false)]
    [DataRow(1000, true)]
    [DataRow(2000, false)]
    [DataRow(2000, true)]
    public Task WholeCycles(int size, bool stringKey) =>
        TestHost.RunAsync(async () =>
        {
            foreach (bool sorted in new[] { false, true })
            {
                await MeasureWholeAsync(size, sorted, stringKey);
            }
        });

    [TestMethod]
    [TestCategory("Measurement")]
    [DataRow(1000)]
    [DataRow(2000)]
    public Task HierarchyCycles(int size) =>
        TestHost.RunAsync(async () =>
        {
            foreach (bool sorted in new[] { false, true })
            {
                await MeasureHierarchyAsync(size, sorted);
            }
        });

    [TestMethod]
    [TestCategory("Measurement")]
    [DataRow(20)]
    [DataRow(200)]
    [DataRow(1000)]
    [DataRow(2000)]
    public Task ValueCycles(int size) =>
        TestHost.RunAsync(async () =>
        {
            foreach (bool stringKey in new[] { false, true })
            {
                foreach (TimeSpan interval in new[] { TimeSpan.FromSeconds(3), TimeSpan.Zero })
                {
                    await MeasureValuesAsync(size, stringKey, interval);
                }
            }
        });

    /// <summary>
    /// A first sort from the header: by names in the culture's order or by numbers, which a worker
    /// may order, or by numbers whose comparer the host wrote, which the UI thread runs. It spans
    /// frames, so this records the UI thread's cycles from the click until the sorted rows are laid
    /// out, and the most it spent between two frames.
    /// </summary>
    [TestMethod]
    [TestCategory("Measurement")]
    [DataRow(1000, "names")]
    [DataRow(1000, "numbers")]
    [DataRow(1000, "comparer")]
    [DataRow(2000, "names")]
    [DataRow(2000, "numbers")]
    [DataRow(2000, "comparer")]
    [DataRow(100000, "names")]
    [DataRow(100000, "numbers")]
    [DataRow(100000, "comparer")]
    public Task FirstSortCycles(int size, string key) =>
        TestHost.RunAsync(async () =>
        {
            Table table = await LoadSortableAsync(size);
            Column column = table.Columns[key switch { "names" => 0, "numbers" => 1, _ => 2 }];
            Action<Column> activate = typeof(Table).GetMethod("ActivateSort",
                    BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(Column) }, null)!
                .CreateDelegate<Action<Column>>(table);
            // Within TestHost's 30-second limit at the largest size.
            int reps = size > 10000 ? 4 : 12;
            List<double> totals = new();
            List<double> largest = new();
            List<double> longest = new();
            bool worker = false;
            for (int rep = -1; rep < reps; rep++)
            {
                table.Sort = null;
                await NextFrameAsync();
                await NextFrameAsync();
                List<(ulong Cycles, long Ticks)> marks = new() { (Cycles(), Stopwatch.GetTimestamp()) };
                EventHandler<object> mark = (_, _) => marks.Add((Cycles(), Stopwatch.GetTimestamp()));
                Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += mark;
                activate(column);
                await NextFrameAsync();
                worker = Proof.Field<object?>(table, "_queuedSort") is { } queued
                    && Proof.Field<object?>(queued, "<Work>k__BackingField") is not null;
                while (table.Sort is null)
                {
                    await NextFrameAsync();
                }
                await NextFrameAsync();
                Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= mark;
                marks.Add((Cycles(), Stopwatch.GetTimestamp()));
                if (rep >= 0)
                {
                    totals.Add((marks[^1].Cycles - marks[0].Cycles) / 1e6);
                    largest.Add(marks.Zip(marks.Skip(1), (a, b) => (b.Cycles - a.Cycles) / 1e6).Max());
                    longest.Add(marks.Zip(marks.Skip(1), (a, b) => Stopwatch.GetElapsedTime(a.Ticks, b.Ticks).TotalMilliseconds).Max());
                }
            }
            TestContext.WriteLine($"first sort {size} {key} ({(worker ? "worker" : "UI thread")}): "
                + $"total Mc {Spread(totals)}; largest frame Mc {Spread(largest)}; longest frame ms {Spread(longest)}");
            Unload(table);
        });

    /// <summary>A sort assigned in code, and a layout restore that carries a sort.</summary>
    [TestMethod]
    [TestCategory("Measurement")]
    [DataRow(1000)]
    [DataRow(2000)]
    public Task SortAssignmentCycles(int size) =>
        TestHost.RunAsync(async () =>
        {
            Table table = await LoadSortableAsync(size);
            Column names = table.Columns[0];
            await MeasureAsync(table, $"sort {size} names",
                new Step("Sort", () => table.Sort = new(names)),
                new Step("Sort none", () => table.Sort = null));
            table.Sort = new(names);
            ColumnLayout sorted = table.Layout;
            table.Sort = null;
            ColumnLayout natural = table.Layout;
            await MeasureAsync(table, $"layout {size} names",
                new Step("with sort", () => table.Layout = sorted),
                new Step("without sort", () => table.Layout = natural));
            Unload(table);
        });

    /// <summary>
    /// The view's comparison with a new order, alone: the walk every publish makes before it
    /// changes the view. Measured against an order with the view's own rows, so it compares every
    /// reference and looks nothing up.
    /// </summary>
    [TestMethod]
    [TestCategory("Measurement")]
    [DataRow(1000)]
    [DataRow(2000)]
    public Task WalkCycles(int size) =>
        TestHost.RunAsync(async () =>
        {
            CostSource source = new(Enumerable.Range(0, size).Select(key => new CostRow(key, Rank(key))));
            Table table = await LoadAsync(source, sorted: false);
            object view = Proof.Field<object>(table, "_view");
            // view.Compare(order, default).IsEmpty, without a version, so the view walks.
            Type type = view.GetType();
            ParameterExpression parameter = Expression.Parameter(typeof(IReadOnlyList<object>));
            Type origin = type.GetNestedType("Origin", BindingFlags.NonPublic)!;
            Expression difference = Expression.Call(Expression.Constant(view),
                type.GetMethod("Compare", BindingFlags.Instance | BindingFlags.NonPublic)!,
                parameter, Expression.Default(origin));
            Func<IReadOnlyList<object>, bool> compare = Expression.Lambda<Func<IReadOnlyList<object>, bool>>(
                Expression.Not(Expression.Property(difference, "IsEmpty")), parameter).Compile();
            IReadOnlyList<object> order = ((IEnumerable<object>)view).ToArray();
            List<double> cycles = new();
            for (int rep = 0; rep < Warmup + Reps; rep++)
            {
                ulong start = Cycles();
                bool changed = compare(order);
                ulong end = Cycles();
                Assert.IsFalse(changed);
                if (rep >= Warmup)
                {
                    cycles.Add((end - start) / 1e6);
                }
            }
            TestContext.WriteLine($"walk {size}: Mc {Spread(cycles)}");
            Unload(table);
        });

    /// <summary>
    /// Scrolling from the first row to the last, one page a frame, with no publish: the cost of
    /// realizing and filling the containers on the way.
    /// </summary>
    [TestMethod]
    [TestCategory("Measurement")]
    [DataRow(1000)]
    [DataRow(2000)]
    public Task ScrollCycles(int size) =>
        TestHost.RunAsync(async () =>
        {
            CostSource source = new(Enumerable.Range(0, size).Select(key => new CostRow(key, Rank(key))));
            Table table = await LoadAsync(source, sorted: false);
            ScrollViewer viewer = Proof.Descendant<ScrollViewer>(Proof.Descendant<ListView>(table)!)!;
            int pages = (int)Math.Ceiling(viewer.ScrollableHeight / viewer.ViewportHeight);
            List<double> cycles = new();
            for (int rep = 0; rep < 1 + 4; rep++)
            {
                viewer.ChangeView(null, 0, null, disableAnimation: true);
                await Task.Delay(200);
                ulong start = Cycles();
                for (int page = 1; page <= pages; page++)
                {
                    viewer.ChangeView(null, page * viewer.ViewportHeight, null, disableAnimation: true);
                    await NextFrameAsync();
                }
                // The list fills the last page's containers in the frames after it arrives.
                await NextFrameAsync();
                await NextFrameAsync();
                ulong end = Cycles();
                Assert.AreEqual(viewer.ScrollableHeight, viewer.VerticalOffset, 1);
                if (rep >= 1)
                {
                    cycles.Add((end - start) / 1e6);
                }
            }
            TestContext.WriteLine($"scroll {size}: {pages} pages, Mc {Spread(cycles)}");
            Unload(table);
        });

    private async Task MeasureNotificationsAsync(int size, bool sorted, TimeSpan? interval = null)
    {
        CostSource source = new(Enumerable.Range(0, size).Select(key => new CostRow(key, Rank(key))));
        Table table = await LoadAsync(source, sorted, interval: interval);
        int middle = size / 2;
        CostRow extra = new(size, Rank(size));
        string label = interval is { } settle ? $"{Label(size, sorted)} interval {settle.TotalSeconds}s" : Label(size, sorted);
        await MeasureAsync(table, $"notify {label}",
            new Step("Add", () => source.Insert(middle, extra)),
            new Step("Remove", () => source.RemoveAt(middle)));
        // Half the list away from the previous change, so the next change cannot reuse its place.
        int early = size / 4;
        int late = 3 * size / 4;
        CostRow moved = source[late];
        await MeasureAsync(table, $"notify {label}",
            new Step("Remove far", () =>
            {
                moved = source[late];
                source.RemoveAt(late);
            }),
            new Step("Add far", () => source.Insert(early, moved)));
        CostRow original = source[middle];
        CostRow replacement = new(original.Key, original.Rank + 1);
        await MeasureAsync(table, $"notify {label}",
            new Step("Replace", () => source[middle] = replacement),
            new Step("Replace back", () => source[middle] = original));
        await MeasureAsync(table, $"notify {label}",
            new Step("Move", () => source.Move(size / 4, 3 * size / 4)),
            new Step("Move back", () => source.Move(3 * size / 4, size / 4)));
        await MeasureAsync(table, $"notify {label}",
            new Step("Reset with one added", () =>
            {
                source.InsertQuietly(middle, extra);
                source.RaiseReset();
            }),
            new Step("Reset with one removed", () =>
            {
                source.RemoveQuietly(middle);
                source.RaiseReset();
            }));
        Unload(table);
    }

    /// <summary>
    /// A plain list changed in place and assigned again, as section 5.3 asks of a host whose list
    /// does not notify, and a <c>RefreshView()</c> where nothing changed.
    /// </summary>
    private async Task MeasureWholeAsync(int size, bool sorted, bool stringKey)
    {
        List<CostRow> source = Enumerable.Range(0, size).Select(key => new CostRow(key, Rank(key))).ToList();
        Table table = await LoadAsync(source, sorted, stringKey);
        int middle = size / 2;
        CostRow extra = new(size, Rank(size));
        string label = Label(size, sorted) + (stringKey ? " string key" : " int key");
        void Assign() => table.ItemsSource = source.ToArray();
        await MeasureAsync(table, $"refresh {label}",
            new Step("unchanged", () => table.RefreshView()),
            new Step("unchanged again", () => table.RefreshView()));
        CostRow[] other = Enumerable.Range(size + 1, size).Select(key => new CostRow(key, Rank(key))).ToArray();
        await MeasureAsync(table, $"switch {label}",
            new Step("to another list", () => table.ItemsSource = other),
            new Step("back", Assign));
        await MeasureAsync(table, $"assign {label}",
            new Step("Add", () =>
            {
                source.Insert(middle, extra);
                Assign();
            }),
            new Step("Remove", () =>
            {
                source.RemoveAt(middle);
                Assign();
            }));
        CostRow original = source[middle];
        CostRow replacement = new(original.Key, original.Rank + 1);
        await MeasureAsync(table, $"assign {label}",
            new Step("Replace", () =>
            {
                source[middle] = replacement;
                Assign();
            }),
            new Step("Replace back", () =>
            {
                source[middle] = original;
                Assign();
            }));
        await MeasureAsync(table, $"assign {label}",
            new Step("Move", () =>
            {
                CostRow moved = source[size / 4];
                source.RemoveAt(size / 4);
                source.Insert(3 * size / 4, moved);
                Assign();
            }),
            new Step("Move back", () =>
            {
                CostRow moved = source[3 * size / 4];
                source.RemoveAt(3 * size / 4);
                source.Insert(size / 4, moved);
                Assign();
            }));
        Unload(table);
    }

    private async Task MeasureHierarchyAsync(int size, bool sorted)
    {
        // A tenth of the rows are expanded roots, each with nine children.
        CostNode[] roots = Enumerable.Range(0, size / 10).Select(key => new CostNode(key, Rank(key))).ToArray();
        int next = roots.Length;
        foreach (CostNode root in roots)
        {
            for (int child = 0; child < 9; child++)
            {
                root.Children.Add(new CostNode(next, Rank(next)));
                next++;
            }
        }
        Column name = TestData.Column("name", 200);
        Table table = TestData.Table(name, TestData.Column("b", 200), TestData.Column("c", 200));
        Schema<CostNode> schema = table.Schema<CostNode>().Key(row => row.Key)
            .Hierarchy(name, row => row.Children, row => row.IsExpanded, (row, value) => row.IsExpanded = value);
        if (sorted)
        {
            schema.SortKey(name, row => row.Rank);
        }
        table.Width = 700;
        table.Height = 600;
        table.ItemsSource = roots;
        await TableHarness.LoadAsync(table);
        if (sorted)
        {
            table.SortInterval = TimeSpan.Zero;
            table.Sort = new(name);
        }
        CostNode visible = roots[roots.Length / 2];
        CostNode extra = new(next, Rank(next));
        await MeasureAsync(table, $"hierarchy {Label(size, sorted)}",
            new Step("child Add", () => visible.Children.Insert(4, extra)),
            new Step("child Remove", () => visible.Children.RemoveAt(4)));
        CostNode original = visible.Children[4];
        CostNode replacement = new(original.Key, original.Rank + 1);
        await MeasureAsync(table, $"hierarchy {Label(size, sorted)}",
            new Step("child Replace", () => visible.Children[4] = replacement),
            new Step("child Replace back", () => visible.Children[4] = original));
        await MeasureAsync(table, $"hierarchy {Label(size, sorted)}",
            new Step("new root array", () => table.ItemsSource = roots.ToArray()),
            new Step("RefreshView", () => table.RefreshView()));
        Unload(table);
    }

    /// <summary>
    /// The product's path: a plain list whose sort values change in place, one row in twenty each
    /// time, published with <c>RefreshView()</c> or with <c>RefreshView(changed)</c> naming those
    /// rows, under a live sort with the given settle interval.
    /// </summary>
    private async Task MeasureValuesAsync(int size, bool stringKey, TimeSpan interval)
    {
        List<CostRow> source = Enumerable.Range(0, size).Select(key => new CostRow(key, Rank(key))).ToList();
        Table table = await LoadAsync(source, sorted: true, stringKey, interval);
        CostRow[] changing = source.Where(row => row.Key % 20 == 0).ToArray();
        string label = $"values {size} {(stringKey ? "string key" : "int key")} interval {interval.TotalSeconds:0}s";
        void Shift(int by)
        {
            foreach (CostRow row in changing)
            {
                row.Rank += by;
            }
        }
        await MeasureAsync(table, label,
            new Step("RefreshView()", () =>
            {
                Shift(5000);
                table.RefreshView();
            }),
            new Step("RefreshView() back", () =>
            {
                Shift(-5000);
                table.RefreshView();
            }));
        await MeasureAsync(table, label,
            new Step("RefreshView(changed)", () =>
            {
                Shift(5000);
                table.RefreshView(changing);
            }),
            new Step("RefreshView(changed) back", () =>
            {
                Shift(-5000);
                table.RefreshView(changing);
            }));
        Unload(table);
    }

    private static int Rank(int key) => key * 7919 % 10007;

    private static string Label(int size, bool sorted) => $"{size} {(sorted ? "sorted" : "unsorted")}";

    private static async Task<Table> LoadAsync(IEnumerable<CostRow> source, bool sorted, bool stringKey = false,
        TimeSpan? interval = null)
    {
        Column a = TestData.Column("a", 200);
        Table table = TestData.Table(a, TestData.Column("b", 200), TestData.Column("c", 200));
        Schema<CostRow> schema = stringKey ? table.Schema<CostRow>().Key(row => row.Name)
            : table.Schema<CostRow>().Key(row => row.Key);
        if (sorted)
        {
            schema.SortKey(a, row => row.Rank);
        }
        table.Width = 700;
        table.Height = 600;
        table.ItemsSource = source;
        await TableHarness.LoadAsync(table);
        if (sorted)
        {
            // By default every publish may move rows, so each one does its whole sorting work.
            table.SortInterval = interval ?? TimeSpan.Zero;
            table.Sort = new(a);
        }
        await Task.Delay(500);
        return table;
    }

    /// <summary>
    /// Rows in source order, with a column of names in the culture's order, one of numbers, and
    /// one of numbers by the host's own comparer.
    /// </summary>
    private static async Task<Table> LoadSortableAsync(int size)
    {
        Column names = TestData.Column("names", 200);
        Column numbers = TestData.Column("numbers", 200);
        Column comparer = TestData.Column("comparer", 200);
        Table table = TestData.Table(names, numbers, comparer);
        table.Schema<CostRow>().Key(row => row.Key)
            .SortKey(names, row => row.Name, StringComparer.CurrentCulture)
            .SortKey(numbers, row => row.Rank)
            .SortKey(comparer, row => row.Rank, Comparer<int>.Create((left, right) => left.CompareTo(right)));
        table.Width = 700;
        table.Height = 600;
        table.SortInterval = TimeSpan.Zero;
        table.ItemsSource = Enumerable.Range(0, size).Select(key => new CostRow(key, Rank(key))).ToList();
        await TableHarness.LoadAsync(table);
        await Task.Delay(500);
        return table;
    }

    private static void Unload(Table table) => TestHost.RootPanel.Children.Remove(table);

    /// <summary>
    /// Apply each step in turn, warm up, then record the cycles of the call, which runs the whole
    /// publish, and of the layout pass it causes, forced at once so no frame timing enters. The
    /// frame cycles run from the call to the end of the next frame, so they include what the list
    /// defers to that frame, such as filling containers. Thread cycles do not count the time the
    /// thread waits.
    /// </summary>
    private async Task MeasureAsync(Table table, string group, Step first, Step second)
    {
        Dictionary<string, (List<double> Publish, List<double> Layout, List<double> Frame)> results = new()
        {
            [first.Name] = (new(), new(), new()),
            [second.Name] = (new(), new(), new()),
        };
        for (int rep = 0; rep < Warmup + Reps; rep++)
        {
            foreach (Step step in new[] { first, second })
            {
                ulong start = Cycles();
                step.Apply();
                ulong published = Cycles();
                table.UpdateLayout();
                ulong laidOut = Cycles();
                await NextFrameAsync();
                ulong framed = Cycles();
                if (rep >= Warmup)
                {
                    results[step.Name].Publish.Add((published - start) / 1e6);
                    results[step.Name].Layout.Add((laidOut - published) / 1e6);
                    results[step.Name].Frame.Add((framed - start) / 1e6);
                }
            }
        }
        foreach (var result in results)
        {
            TestContext.WriteLine($"{group} {result.Key}: publish Mc {Spread(result.Value.Publish)}; layout Mc {Spread(result.Value.Layout)}; frame Mc {Spread(result.Value.Frame)}");
        }
    }

    /// <summary>Completes after the next frame's work, layout and the list's deferred work included.</summary>
    private static Task NextFrameAsync()
    {
        TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<object>? rendering = null;
        rendering = (_, _) =>
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= rendering;
            done.SetResult();
        };
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += rendering;
        return done.Task;
    }

    private static string Spread(List<double> values)
    {
        List<double> sorted = values.Order().ToList();
        double median = sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
        return $"median {median:0.000} (range {sorted[0]:0.000}-{sorted[^1]:0.000})";
    }
}
