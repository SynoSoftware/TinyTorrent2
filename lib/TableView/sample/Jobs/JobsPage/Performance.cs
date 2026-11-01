using System.Collections.ObjectModel;
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using Syno.TableView;
using Windows.Foundation.Collections;

namespace Syno.TableViewSample.Jobs;

public sealed partial class JobsPage
{
    private int _keyReads;
    private const int PerformanceTrials = 16;

    public sealed record ProbeRow(long RowId, string Name) : System.ComponentModel.INotifyPropertyChanged
    {
        private string _name = Name;
        public string Name
        {
            get => _name;
            set
            {
                if (_name == value)
                    return;
                _name = value;
                PropertyChanged?.Invoke(this, new(nameof(Name)));
            }
        }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }
    private int _sortKeyReads;
    private int _policyReads;
    private sealed record Measurement(double Block, long Allocated, double Frame);

    private async Task MeasureChangesAsync()
    {
        Section("U. Sparse half-removal and immediate collection Add; drag policy disabled");
        ReportLoad();
        foreach (int count in new[] { 1000, 2000 })
        {
            await WithProbeAsync(count, async (table, rows) =>
            {
                table.CanReorder = false;
                ListView list = FindDescendant<ListView>(table)!;
                IObservableVector<object> vector = (IObservableVector<object>)list.ItemsSource;
                int notifications = 0;
                void Changed(IObservableVector<object> sender, IVectorChangedEventArgs args) => notifications++;
                vector.VectorChanged += Changed;
                try
                {
                    foreach (bool distributed in new[] { false, true })
                    {
                        ProbeRow[] reduced = rows.Where((_, index) => distributed ? index % 2 == 1 : index >= count / 2).ToArray();
                        List<Measurement> results = new();
                        int events = 0;
                        int identities = 0;
                        for (int trial = 0; trial < PerformanceTrials; trial++)
                        {
                            table.ItemsSource = rows;
                            await PerformanceFrameAsync();
                            notifications = _keyReads = 0;
                            results.Add(await MeasureFrameAsync(() => table.ItemsSource = reduced));
                            events += notifications;
                            identities += _keyReads;
                        }
                        ReportMeasurements($"{count} {(distributed ? "distributed" : "front")} half-removal assignment", results);
                        W($"  vector events/operation={(double)events / PerformanceTrials:0.0}, identity reads={(double)identities / PerformanceTrials:0.0}; source assignment validates remaining rows; native packet commit included");
                    }
                    foreach (bool sorted in new[] { false, true })
                    {
                        ObservableCollection<ProbeRow> source = new(rows);
                        ProbeRow arrival = new(count + 1, $"Render {count / 2:D6}.5");
                        table.ItemsSource = source;
                        table.Sort = sorted ? new(table.Columns[0]) : null;
                        table.SortInterval = TimeSpan.FromHours(1);
                        source.Add(arrival);
                        source.RemoveAt(source.Count - 1);
                        List<Measurement> results = new();
                        long comparisons = 0;
                        int identities = 0;
                        int keys = 0;
                        int events = 0;
                        for (int trial = 0; trial < PerformanceTrials; trial++)
                        {
                            _keyReads = _sortKeyReads = notifications = 0;
                            long before = SortComparisons(table);
                            results.Add(await MeasureFrameAsync(() => source.Add(arrival)));
                            identities += _keyReads;
                            keys += _sortKeyReads;
                            events += notifications;
                            comparisons += SortComparisons(table) - before;
                            source.RemoveAt(source.Count - 1);
                            await PerformanceFrameAsync();
                        }
                        ReportMeasurements($"{count} warmed single Add, {(sorted ? "culture sort" : "natural")}", results);
                        W($"  identity reads={(double)identities / PerformanceTrials:0.0}, sort-key reads={(double)keys / PerformanceTrials:0.0}, comparisons={(double)comparisons / PerformanceTrials:0.0}, vector events={(double)events / PerformanceTrials:0.0} per Add");
                    }
                    ReportStorage(table, count);
                }
                finally
                {
                    vector.VectorChanged -= Changed;
                }
            }, sort: ProbeSort.Culture);
        }
        ReportLoad();
    }

    private async Task MeasureNamedValuesAsync()
    {
        Section("X. Named and whole culture-name refresh; drag policy disabled");
        ReportLoad();
        foreach (int count in new[] { 1000, 2000 })
        {
            await WithProbeAsync(count, async (table, rows) =>
            {
                table.CanReorder = false;
                table.SortInterval = TimeSpan.Zero;
                table.Sort = new(table.Columns[0]);
                ProbeRow moving = rows[count / 2];
                object[] named = { moving };
                string original = moving.Name;
                foreach (bool changed in new[] { false, true })
                {
                    foreach (bool whole in new[] { false, true })
                    {
                        Action refresh = whole ? table.RefreshView : () => table.RefreshView(named);
                        refresh();
                        List<Measurement> results = new();
                        int reads = 0;
                        long comparisons = 0;
                        for (int trial = 0; trial < PerformanceTrials; trial++)
                        {
                            moving.Name = changed ? "Render 999999" : original;
                            _sortKeyReads = 0;
                            long before = SortComparisons(table);
                            results.Add(await MeasureFrameAsync(refresh));
                            reads += _sortKeyReads;
                            comparisons += SortComparisons(table) - before;
                            moving.Name = original;
                            table.RefreshView(named);
                            await PerformanceFrameAsync();
                        }
                        ReportMeasurements($"{count} {(whole ? "whole" : "one named")} {(changed ? "changed" : "unchanged")} key", results);
                        W($"  key reads={(double)reads / PerformanceTrials:0.0}, actual comparisons={(double)comparisons / PerformanceTrials:0.0} per operation");
                    }
                }
                table.Sort = null;
                table.Selection = new(rows, rows[0]);
                List<Measurement> policy = new();
                int policies = 0;
                for (int trial = 0; trial < PerformanceTrials; trial++)
                {
                    _policyReads = 0;
                    policy.Add(await MeasureFrameAsync(() => table.RefreshView(named)));
                    policies += _policyReads;
                }
                ReportMeasurements($"{count} named policy-only refresh, all selected (packet commit included)", policy);
                W($"  interaction callbacks/operation={(double)policies / PerformanceTrials:0.0}");
                ReportStorage(table, count);
            }, sort: ProbeSort.Culture);
        }
        W("Inline allocations are current UI-thread bytes inside the publish only; host arrays and strings are prepared outside. Frame latency includes scheduling/layout and is separate from uninterrupted publish time.");
        ReportLoad();
    }

    private static long SortComparisons(Table table)
    {
        object? sorted = ReadMember(table, "_sortedOrder");
        return sorted is null ? 0 : (long)ReadMember(sorted, "Comparisons")!;
    }

    private static object? ReadMember(object owner, string name)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        return owner.GetType().GetField(name, flags)?.GetValue(owner)
            ?? owner.GetType().GetProperty(name, flags)?.GetValue(owner);
    }

    private async Task MeasureWorkersAsync()
    {
        Section("W. Queued first sorts: UI capture/commit, detached ordering and frame latency");
        ReportLoad();
        foreach (int count in new[] { 1000, 2000 })
        {
            foreach (var mode in new[] { (Sort: ProbeSort.Culture, Hierarchy: false),
                (Sort: ProbeSort.Number, Hierarchy: false), (Sort: ProbeSort.Culture, Hierarchy: true) })
            {
                await WithProbeAsync(count, async (table, rows) =>
                {
                    table.CanReorder = false;
                    Action<Column> activate = typeof(Table).GetMethod("ActivateSort",
                        BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(Column) }, null)!
                        .CreateDelegate<Action<Column>>(table);
                    List<Measurement> results = new();
                    List<double> captures = new();
                    List<double> commits = new();
                    List<double> ordering = new();
                    List<double> glyphs = new();
                    object? last = null;
                    for (int trial = -1; trial < PerformanceTrials; trial++)
                    {
                        table.Sort = null;
                        await PerformanceFrameAsync();
                        long allocation = GC.GetTotalAllocatedBytes(false);
                        long start = Stopwatch.GetTimestamp();
                        activate(table.Columns[0]);
                        object queued = ReadMember(table, "_queuedSort")!;
                        long glyphFrame = await PerformanceFrameAsync();
                        double glyph = Stopwatch.GetElapsedTime(start, glyphFrame).TotalMilliseconds;
                        await AwaitProbeAsync(table, () => ReadMember(table, "_queuedSort") is null);
                        double answer = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                        long allocated = GC.GetTotalAllocatedBytes(false) - allocation;
                        double capture = ((TimeSpan)ReadMember(queued, "CaptureTime")!).TotalMilliseconds;
                        double commit = ((TimeSpan)ReadMember(queued, "CommitTime")!).TotalMilliseconds;
                        object context = ReadMember(queued, "Work")
                            ?? throw new InvalidOperationException("The eligible queued sort did not use detached work.");
                        object sorting = ReadMember(context, "Sorting")!;
                        if (trial >= 0)
                        {
                            captures.Add(capture);
                            commits.Add(commit);
                            glyphs.Add(glyph);
                            ordering.Add(((TimeSpan)ReadMember(sorting, "Duration")!).TotalMilliseconds);
                            results.Add(new(Math.Max(capture, commit), allocated, answer));
                        }
                        last = context;
                    }
                    string label = $"{count} {mode.Sort}, hierarchy={mode.Hierarchy}";
                    ReportMeasurements(label + " queued operation", results);
                    W($"  UI capture median/max={Median(captures):0.0000}/{captures.Max():0.0000} ms; UI commit/rebase median/max={Median(commits):0.0000}/{commits.Max():0.0000} ms; worker ordering median/max={Median(ordering):0.0000}/{ordering.Max():0.0000} ms; glyph-frame callback latency median={Median(glyphs):0.000} ms (not physical-present time)");
                    W("  allocation is whole-process managed growth across the queued operation (includes worker/runtime work), not isolated UI allocations; block is max(UI capture, UI commit), not worker/task/frame elapsed");
                    ReportWorkerStorage(last!);
                    ReportStorage(table, count);
                    table.Sort = null;
                    activate(table.Columns[0]);
                    object superseded = ReadMember(table, "_queuedSort")!;
                    typeof(Table).GetMethod("StartWork", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(table, new[] { superseded });
                    activate(table.Columns[0]);
                    await AwaitProbeAsync(table, () => ReadMember(table, "_queuedSort") is null);
                    W($"  superseded first-sort request: final direction={table.Sort?.Direction}; previous request retained only for this report");
                    if (table.Sort?.Direction != SortDirection.Descending)
                    {
                        throw new InvalidOperationException("Superseded worker committed its old criterion.");
                    }
                    GC.KeepAlive(last);
                    GC.KeepAlive(superseded);
                }, hierarchy: mode.Hierarchy, sort: mode.Sort);
            }
        }
        ReportLoad();

        static double Median(List<double> values) => values.Order().ElementAt(values.Count / 2);
    }

    private void ReportWorkerStorage(object context)
    {
        object work = ReadMember(context, "Sorting")!;
        object payload = ReadMember(work, "_payload")!;
        long bytes = 0;
        HashSet<byte[]> collation = new(ReferenceEqualityComparer.Instance);
        foreach (string name in new[] { "Entries", "Indices" })
        {
            Array groups = (Array)ReadMember(payload, name)!;
            bytes += ArrayBytes(groups);
            foreach (Array array in groups)
            {
                long backing = ArrayBytes(array);
                bytes += backing;
                W($"  operation {name}: capacity={array.Length}, backing={backing} bytes, LOH={backing >= 85000}");
                foreach (object entry in array)
                {
                    if (ReadMember(entry, "Collation") is byte[] key)
                    {
                        collation.Add(key);
                    }
                }
            }
        }
        Array associations = (Array)ReadMember(work, "_groups")!;
        bytes += ArrayBytes(associations);
        foreach (object group in associations)
        {
            bytes += ArrayBytes((Array)ReadMember(group, "Rows")!);
            bytes += ReportMapBacking("operation captured keys", ReadMember(group, "Keys")!);
        }
        if (ReadMember(context, "Hierarchy") is { } hierarchy)
        {
            bytes += ReportMapBacking("operation parent groups", ReadMember(hierarchy, "_parents")!);
        }
        bytes += collation.Sum(ArrayBytes);
        W($"  operation/job backing={bytes} bytes; this report deliberately retains the completed queued context. Production releases it with the request; this is operation lifetime/peak backing, not permanent table retention. Excludes row/string objects, task/association objects, runtime/native caches.");
    }

    private long ReportMapBacking(string name, object owner)
    {
        object map = owner.GetType().GetMethod("CapturedFor")!.Invoke(owner, new[] { new object() })!;
        long bytes = 0;
        foreach (string field in new[] { "_entries", "_keys" })
        {
            if (ReadMember(map, field) is not IDictionary dictionary)
            {
                continue;
            }
            Array? entries = (Array?)ReadMember(dictionary, "_entries");
            Array? buckets = (Array?)ReadMember(dictionary, "_buckets");
            long backing = (entries is null ? 0 : ArrayBytes(entries)) + (buckets is null ? 0 : ArrayBytes(buckets));
            bytes += backing;
            W($"  {name}.{field}: count={dictionary.Count}, capacity={entries?.Length ?? 0}; backing={backing} bytes");
        }
        return bytes;
    }

    private static long ArrayBytes(Array array)
    {
        if (array.Length == 0)
        {
            return 0;
        }
        int size = (int)typeof(System.Runtime.CompilerServices.Unsafe).GetMethod("SizeOf")!
            .MakeGenericMethod(array.GetType().GetElementType()!).Invoke(null, null)!;
        return (3L * IntPtr.Size + (long)array.Length * size + IntPtr.Size - 1) / IntPtr.Size * IntPtr.Size;
    }

    private async Task ObserveLiveChangesAsync()
    {
        Section("L. Composed-window live mutation evidence; lead supplies WGC recording");
        await WithProbeAsync(1000, async (table, rows) =>
        {
            table.CanReorder = false;
            ObservableCollection<ProbeRow> source = new(rows);
            table.ItemsSource = source;
            table.Sort = new(table.Columns[0]);
            InvokeTable(table, "StopPointing");
            ListView list = FindDescendant<ListView>(table)!;
            await AwaitProbeAsync(table, IsReady);

            bool IsReady() => list.ItemsPanelRoot is ItemsStackPanel panel
                && panel.FirstVisibleIndex == 0 && table.VerticalOffset == 0;

            string prefix = Path.Combine(AppContext.BaseDirectory, $"performance-live-{Environment.ProcessId}");
            string ready = prefix + ".ready.json";
            string go = prefix + ".go";
            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance!);
            string phase = "ready";
            int frames = 0;
            long last = Stopwatch.GetTimestamp();
            double longestGap = 0;
            int events = 0;
            Exception? failure = null;
            IObservableVector<object> vector = (IObservableVector<object>)list.ItemsSource;
            void Changed(IObservableVector<object> sender, IVectorChangedEventArgs args)
            {
                events++;
                W($"L event utc={DateTimeOffset.UtcNow:O}, phase={phase}, kind={args.CollectionChange}, index={args.Index}, count={list.Items.Count}");
            }
            void Rendering(object? sender, object args)
            {
                if (failure is not null)
                {
                    return;
                }
                try
                {
                    if (phase is "ready" or "baseline" && !IsReady())
                    {
                        throw new InvalidOperationException($"The live scene lost its near-viewport premise before mutation; {ProbeState(table)}");
                    }
                    long now = Stopwatch.GetTimestamp();
                    double gap = Stopwatch.GetElapsedTime(last, now).TotalMilliseconds;
                    last = now;
                    longestGap = Math.Max(longestGap, gap);
                    frames++;
                    var containers = Containers();
                    W($"L frame utc={DateTimeOffset.UtcNow:O}, tick={now}, phase={phase}, gap={gap:0.000}ms, count={list.Items.Count}, containers={string.Join(';', containers.Select(pair => $"{pair.Key.RowId}:{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(pair.Value)}@{list.IndexFromContainer(pair.Value)}[{Bounds(pair.Value)}]"))}; {ProbeState(table)}");
                    if (containers.Count == 0 && list.Items.Count > 0)
                    {
                        throw new InvalidOperationException("The live scene lost every realized container.");
                    }
                }
                catch (Exception error)
                {
                    failure = error;
                }
            }
            vector.VectorChanged += Changed;
            CompositionTarget.Rendering += Rendering;
            try
            {
                File.WriteAllText(ready, System.Text.Json.JsonSerializer.Serialize(new
                {
                    Pid = Environment.ProcessId, Hwnd = hwnd.ToInt64(), Go = go,
                    TimeoutSeconds = 45, PhaseSeconds = 1.4,
                    Protocol = "Start winapp ui record --frames --fps 30 --json by HWND; require WGC; create Go only after stderr recording-started."
                }));
                W($"L READY={ready}; GO={go}; PID={Environment.ProcessId}; HWND={hwnd}; ready timeout=45s; each phase>=1.4s");
                Flush();
                long waiting = Stopwatch.GetTimestamp();
                while (!File.Exists(go))
                {
                    if (Stopwatch.GetElapsedTime(waiting) > TimeSpan.FromSeconds(45))
                    {
                        throw new TimeoutException("The live scene did not receive its recorder go marker.");
                    }
                    await PerformanceFrameAsync();
                    if (failure is not null)
                    {
                        throw failure;
                    }
                }
                await Phase("baseline", () => { });
                ProbeRow arrival = new(2001, "Render 000005.5 arrival");
                await Phase("notification-add", () => source.Insert(6, arrival),
                    expected: (source.Count + 1, arrival, true));
                ProbeRow removed = rows[8];
                await Phase("notification-remove", () => source.Remove(removed),
                    expected: (source.Count - 1, removed, false));
                ProbeRow[] membership = source.ToArray();
                membership[10] = new(2002, "Render 000009.5 whole");
                await Phase("whole-source-membership", () => table.ItemsSource = membership, continuity: true,
                    expected: (membership.Length, membership[10], true));
                await Phase("disjoint-descending-sort", () => table.Sort = new(table.Columns[0], SortDirection.Descending), disjoint: true);
                if (!ReferenceEquals(list.ReadLocalValue(ItemsControl.ItemContainerTransitionsProperty), DependencyProperty.UnsetValue))
                {
                    throw new InvalidOperationException("The disjoint scene did not restore the transition value source.");
                }
                ProbeRow restored = new(2003, "Render 000995.5 restored");
                membership = membership.Append(restored).ToArray();
                await Phase("restored-native-arrival", () => table.ItemsSource = membership,
                    expected: (membership.Length, restored, true));
                W($"L completed: Rendering callbacks={frames}, longest callback gap={longestGap:0.000}ms, vector events={events}. Proxy animation/no painted gap requires composed WGC frames; row transforms/base opacity are not animation proof.");
                Flush();
            }
            finally
            {
                CompositionTarget.Rendering -= Rendering;
                vector.VectorChanged -= Changed;
            }

            Dictionary<ProbeRow, ListViewItem> Containers()
            {
                Dictionary<ProbeRow, ListViewItem> containers = new(ReferenceEqualityComparer.Instance);
                if (list.ItemsPanelRoot is Panel panel)
                {
                    foreach (UIElement child in panel.Children)
                    {
                        int index = list.IndexFromContainer(child);
                        if (child is ListViewItem container && index >= 0
                            && ReferenceEquals(list.ContainerFromIndex(index), child))
                        {
                            ProbeRow item = (ProbeRow)list.Items[index];
                            if (!ReferenceEquals(list.ItemFromContainer(child), item))
                            {
                                throw new InvalidOperationException("A live container has stale row content.");
                            }
                            containers[item] = container;
                        }
                    }
                }
                return containers;
            }

            string Bounds(ListViewItem container)
            {
                Windows.Foundation.Rect bounds = container.TransformToVisual(list).TransformBounds(
                    new(0, 0, container.ActualWidth, container.ActualHeight));
                return $"{bounds.X:0.00},{bounds.Y:0.00},{bounds.Width:0.00},{bounds.Height:0.00}";
            }

            async Task Phase(string name, Action mutate, bool continuity = false, bool disjoint = false,
                (int Count, ProbeRow Item, bool Present)? expected = null)
            {
                Dictionary<ProbeRow, ListViewItem> before = Containers();
                ProbeRow[] visible = Visible();
                if (expected is { Present: false } removal
                    && !visible.Any(item => ReferenceEquals(item, removal.Item)))
                {
                    throw new InvalidOperationException($"The {name} removal target was not visible before mutation.");
                }
                phase = name;
                W($"L phase-start utc={DateTimeOffset.UtcNow:O}, tick={Stopwatch.GetTimestamp()}, phase={phase}, count={list.Items.Count}");
                long start = Stopwatch.GetTimestamp();
                InvokeTable(table, "StopPointing");
                mutate();
                if (expected is { } expectedRows
                    && (list.Items.Count != expectedRows.Count
                        || list.Items.Cast<object>().Any(item => ReferenceEquals(item, expectedRows.Item)) != expectedRows.Present))
                {
                    throw new InvalidOperationException($"The {phase} membership did not apply immediately; check pointer interference.");
                }
                while (Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(1.4))
                {
                    await PerformanceFrameAsync();
                    if (failure is not null)
                    {
                        throw failure;
                    }
                }
                Dictionary<ProbeRow, ListViewItem> after = Containers();
                int surviving = before.Keys.Count(after.ContainsKey);
                int reused = before.Count(pair => after.TryGetValue(pair.Key, out ListViewItem? container)
                    && ReferenceEquals(pair.Value, container));
                ProbeRow[] visibleAfter = Visible();
                if (expected is { Present: true } entered
                    && !visibleAfter.Any(item => ReferenceEquals(item, entered.Item)))
                {
                    throw new InvalidOperationException($"The {phase} arrival did not enter the visible region.");
                }
                int sharedVisible = visibleAfter.Count(row => visible.Contains(row, ReferenceEqualityComparer.Instance));
                W($"L phase-end utc={DateTimeOffset.UtcNow:O}, phase={phase}, surviving-realized-instances={surviving}, reused={reused}, visible-intersection={sharedVisible}; {ProbeState(table)}");
                if (continuity && reused != surviving)
                {
                    throw new InvalidOperationException("The unchanged run survivors lost their containers.");
                }
                if (disjoint && sharedVisible != 0)
                {
                    throw new InvalidOperationException("The reversal did not establish disjoint visible identities.");
                }
            }

            ProbeRow[] Visible()
            {
                var panel = (ItemsStackPanel)list.ItemsPanelRoot;
                return list.Items.Cast<ProbeRow>().Skip(Math.Max(0, panel.FirstVisibleIndex))
                    .Take(Math.Max(0, panel.LastVisibleIndex - panel.FirstVisibleIndex + 1)).ToArray();
            }
        }, sort: ProbeSort.Culture);
    }

    private async Task MeasureSortingAsync()
    {
        Section("T. Production culture-name sort: first sort, unchanged RefreshView, one changed key and one arrival");
        W($"comparer=StringComparer.CurrentCultureIgnoreCase; culture={System.Globalization.CultureInfo.CurrentCulture.Name}; names='Render 000000'..; comparisons count actual collation-byte/raw ordering comparisons, not equality checks");
        ReportLoad();
        foreach (int count in new[] { 1000, 2000 })
        {
            await WithProbeAsync(count, async (table, rows) =>
            {
                List<Measurement> first = new();
                List<Measurement> unchanged = new();
                List<Measurement> changed = new();
                List<Measurement> arrival = new();
                long firstComparisons = 0;
                long unchangedComparisons = 0;
                long changedComparisons = 0;
                long arrivalComparisons = 0;
                long keyReads = 0;
                table.SortInterval = TimeSpan.Zero;
                table.Sort = new(table.Columns[0]);
                table.Sort = null;
                for (int trial = 0; trial < PerformanceTrials; trial++)
                {
                    _sortKeyReads = 0;
                    first.Add(await MeasureFrameAsync(() => table.Sort = new(table.Columns[0])));
                    firstComparisons += Comparisons(table);
                    keyReads += _sortKeyReads;
                    table.Sort = null;
                }
                table.Sort = new(table.Columns[0]);
                table.RefreshView();
                for (int trial = 0; trial < PerformanceTrials; trial++)
                {
                    long before = Comparisons(table);
                    unchanged.Add(await MeasureFrameAsync(table.RefreshView));
                    unchangedComparisons += Comparisons(table) - before;
                }
                ProbeRow moving = rows[count / 2];
                string original = moving.Name;
                for (int trial = 0; trial < PerformanceTrials; trial++)
                {
                    moving.Name = "Render 999999";
                    long before = Comparisons(table);
                    changed.Add(await MeasureFrameAsync(table.RefreshView));
                    changedComparisons += Comparisons(table) - before;
                    moving.Name = original;
                    table.RefreshView();
                }
                table.SortInterval = TimeSpan.FromHours(1);
                ProbeRow added = new(count + 1, $"Render {count / 2:D6}.5");
                ProbeRow[] withArrival = rows.Append(added).ToArray();
                for (int trial = 0; trial < PerformanceTrials; trial++)
                {
                    long before = Comparisons(table);
                    arrival.Add(await MeasureFrameAsync(() => table.ItemsSource = withArrival));
                    arrivalComparisons += Comparisons(table) - before;
                    table.ItemsSource = rows;
                }
                ReportMeasurements($"{count} first synchronous Sort setter", first);
                ReportMeasurements($"{count} unchanged name RefreshView", unchanged);
                ReportMeasurements($"{count} one changed-name settle", changed);
                ReportMeasurements($"{count} one arrival inside interval", arrival);
                W($"  actual comparisons/operation: first={(double)firstComparisons / PerformanceTrials:0.0}, unchanged={(double)unchangedComparisons / PerformanceTrials:0.0}, changed={(double)changedComparisons / PerformanceTrials:0.0}, arrival={(double)arrivalComparisons / PerformanceTrials:0.0}; selector reads/first={(double)keyReads / PerformanceTrials:0.0}");
                ReportStorage(table, count);
            }, sort: ProbeSort.Culture);
        }
        W("100k gates: first-sort uninterrupted block >100ms decides worker; unchanged RefreshView >16ms decides changed-row overload. Extend measured growth, with n*log(n) first-sort comparison work and linear key/index passes; this name shape does not bound arbitrary strings or host comparers.");
        ReportLoad();

        static long Comparisons(Table table)
        {
            object workspace = typeof(Table).GetField("_sortedOrder", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(table)!;
            return (long)workspace.GetType().BaseType!.GetProperty("Comparisons", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(workspace)!;
        }
    }

    private async Task MeasurePublishesAsync(bool measureTimings = true)
    {
        Section(measureTimings ? "P. No-move publishes: host assignment and RefreshView measured separately"
            : "M. Retained map measurements and source-derived backing storage");
        ReportLoad();
        foreach (int count in new[] { 1000, 2000 })
        {
            GC.Collect();
            long before = GC.GetTotalMemory(false);
            await WithProbeAsync(count, async (table, rows) =>
            {
                ProbeRow[] first = rows.ToArray();
                ProbeRow[] second = rows.ToArray();
                table.ItemsSource = first;
                table.ItemsSource = second;
                table.RefreshView();
                await PerformanceFrameAsync();
                GC.Collect();
                long retained = GC.GetTotalMemory(false) - before;
                W($"rows={count} raw managed-heap delta={retained} bytes; unisolated process total includes host, WinUI/JIT caches and finalization; not table-retention scaling");
                object identity = typeof(Table).GetField("_identity", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(table)!;
                MeasureMapRetention(identity, rows[..1], false);
                MeasureMapRetention(identity, rows[..1], true);
                long ordinary = MeasureMapRetention(identity, rows, false);
                long captured = MeasureMapRetention(identity, rows, true);
                W($"  raw map-probe managed-heap deltas (unisolated): uncaptured={ordinary}, captured={captured}, difference={captured - ordinary} bytes; unrelated collection/finalization affects these values; use source-derived backing accounting for storage; old control's ~40 bytes/row is an estimate");
                table.SortInterval = TimeSpan.FromHours(1);
                foreach (bool sorted in new[] { false, true })
                {
                    table.Sort = sorted ? new(table.Columns[0]) : null;
                    table.ItemsSource = first;
                    table.ItemsSource = second;
                    table.RefreshView();
                    await PerformanceFrameAsync();
                    if (!measureTimings)
                    {
                        continue;
                    }
                    List<Measurement> assignments = new();
                    List<Measurement> refreshes = new();
                    int assignmentKeys = 0;
                    int refreshKeys = 0;
                    for (int trial = 0; trial < PerformanceTrials; trial++)
                    {
                        _keyReads = 0;
                        ProbeRow[] snapshot = trial % 2 == 0 ? first : second;
                        assignments.Add(await MeasureFrameAsync(() => table.ItemsSource = snapshot));
                        assignmentKeys += _keyReads;
                        _keyReads = 0;
                        refreshes.Add(await MeasureFrameAsync(table.RefreshView));
                        refreshKeys += _keyReads;
                    }
                    string mode = sorted ? "local name sort, settling" : "natural order";
                    ReportMeasurements($"{count} {mode} assignment", assignments);
                    ReportMeasurements($"{count} {mode} RefreshView", refreshes);
                    W($"  selector calls/assignment={(double)assignmentKeys / PerformanceTrials:0.0}, calls/RefreshView={(double)refreshKeys / PerformanceTrials:0.0}");
                }
                ReportStorage(table, count);
            });
        }
        ReportLoad();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static long MeasureMapRetention(object identity, ProbeRow[] rows, bool keepsKeys)
    {
        MethodInfo create = identity.GetType().GetMethod("CreateMap")!.MakeGenericMethod(typeof(int));
        GC.Collect();
        long before = GC.GetTotalMemory(false);
        object map = create.Invoke(identity, new object[] { keepsKeys })!;
        var add = map.GetType().GetMethod("TryAdd", BindingFlags.Instance | BindingFlags.NonPublic,
            null, new[] { typeof(object), typeof(int) }, null)!.CreateDelegate<Func<object, int, bool>>(map);
        for (int index = 0; index < rows.Length; index++)
        {
            add(rows[index], index);
        }
        GC.Collect();
        long retained = GC.GetTotalMemory(false) - before;
        GC.KeepAlive(add);
        GC.KeepAlive(map);
        return retained;
    }

    private void ReportStorage(Table table, int count)
    {
        object source = Read(table, "_source");
        object view = Read(table, "_view");
        object selection = Read(table, "_selection");
        object positions = view.GetType().GetProperty("Positions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        List<(string Name, object Owner)> maps = new()
        {
            ("source.accepted", Read(source, "_membership")), ("source.candidate", Read(source, "_candidate")),
            ("source.additions", Read(source, "_additions")), ("source.leavers", Read(source, "_leavers")),
            ("view", positions), ("view.candidate", Read(table, "_candidatePositions")),
            ("held", Read(table, "_held")), ("held.candidate", Read(table, "_candidateHeld")),
            ("pinned", Read(table, "_pinned")), ("released", Read(table, "_released")),
            ("selected", Read(selection, "_selected")), ("tracked", Read(selection, "_tracked")),
            ("selection.added", Read(selection, "_added")), ("selection.removed", Read(selection, "_removed")),
        };
        object? sorted = table.GetType().GetField("_sortedOrder", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(table);
        if (sorted is not null)
        {
            maps.Add(("sort", Read(sorted, "_positions")));
            maps.Add(("sort.candidate", Read(sorted, "_candidatePositions")));
        }
        long total = 0;
        foreach (var (name, owner) in maps)
        {
            total += ReportMapBacking(name, owner);
        }
        object backing = view.GetType().GetProperty("Items", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        foreach (var (name, list) in new[] { ("view rows", backing), ("kept order", Read(table, "_keptRows")),
            ("shown rows", Read(table, "_shownRows")), ("pin order", Read(table, "_pinOrder")) }
            .Concat(sorted is null ? Array.Empty<(string, object)>()
                : new[] { ("sorted rows", Read(sorted, "_rows")), ("sort candidate rows", Read(sorted, "_candidateRows")),
                    ("sort edits", Read(sorted, "_edits")), ("sort key updates", Read(sorted, "_keyUpdates")) }))
        {
            Array array = (Array)Read(list, name == "view rows" ? "_buffer" : "_items");
            int size = (int)typeof(System.Runtime.CompilerServices.Unsafe).GetMethod("SizeOf")!
                .MakeGenericMethod(array.GetType().GetElementType()!).Invoke(null, null)!;
            long bytes = Bytes(array.Length, size);
            total += bytes;
            W($"  {name}: count={list.GetType().GetProperty("Count")!.GetValue(list)}, capacity={array.Length} x {size}; backing={bytes} bytes; LOH={(bytes >= 85000)}");
        }
        if (sorted is not null)
        {
            HashSet<object> collation = new(ReferenceEqualityComparer.Instance);
            foreach (string field in new[] { "_entries", "_candidateEntries", "_captured", "_indices", "_survivors", "_places", "_targets" })
            {
                Array array = (Array)Read(sorted, field);
                Type element = array.GetType().GetElementType()!;
                int size = (int)typeof(System.Runtime.CompilerServices.Unsafe).GetMethod("SizeOf")!
                    .MakeGenericMethod(element).Invoke(null, null)!;
                long bytes = Bytes(array.Length, size);
                total += bytes;
                W($"  sort.{field}: capacity={array.Length} x {size}; backing={bytes} bytes; projected element backing 10k={Bytes(10000, size)}, 100k={Bytes(100000, size)}; LOH crossing={(85000 - 3 * IntPtr.Size + size - 1) / size} elements");
                if (element.GetProperty("Collation") is { } property)
                    foreach (object entry in array)
                        if (property.GetValue(entry) is byte[] key)
                            collation.Add(key);
            }
            long keyBytes = collation.Sum(key => Bytes(((byte[])key).Length, sizeof(byte)));
            total += keyBytes;
            W($"  sort.collation: distinct arrays={collation.Count}; backing={keyBytes} bytes");
        }
        W($"rows={count} source-derived retained backing={total} bytes ({(double)total / count:0.00}/row); actual capacities x CLR entry sizes plus aligned array headers; excludes map/list objects, host rows/arrays, visual/native state and runtime caches");

        static object Read(object owner, string name) => owner.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
        static long Bytes(int capacity, int size) => capacity == 0 ? 0
            : (3L * IntPtr.Size + (long)capacity * size + IntPtr.Size - 1) / IntPtr.Size * IntPtr.Size;
    }

    private async Task MeasureSelectionAsync(bool measureTimings = true)
    {
        Section(measureTimings ? "S. Selection model, packet publication and realized/pinned state"
            : "Q. Focused off-screen container and selection correctness");
        ReportLoad();
        foreach (int count in new[] { 1000, 2000 })
        {
            await WithProbeAsync(count, async (table, rows) =>
            {
                object model = typeof(Table).GetField("_selection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(table)!;
                var toggle = model.GetType().GetMethod("Toggle", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .CreateDelegate<Action<object>>(model);
                var navigate = model.GetType().GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .CreateDelegate<Action<object, bool, bool, IReadOnlyList<object>>>(model);
                var range = model.GetType().GetMethod("Range", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .CreateDelegate<Action<object, bool, IReadOnlyList<object>>>(model);
                var commit = typeof(Table).GetMethod("CommitSelection", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .CreateDelegate<Func<Selection>>(table);
                IReadOnlyList<object> view = (IReadOnlyList<object>)typeof(Table)
                    .GetProperty("View", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(table)!;

                if (measureTimings)
                {
                    table.Selection = new Selection(rows, rows[^1]);
                    toggle(rows[count / 2]);
                    commit();
                    toggle(rows[count / 2]);
                    commit();
                    await MeasureGestureAsync($"{count} Ctrl+click", () => toggle(rows[count / 2]), commit);
                    CheckContainerSelection(table, "Ctrl+click");

                    table.Selection = new Selection(new[] { rows[0] }, rows[0]);
                    range(rows[^1], false, view);
                    commit();
                    navigate(rows[^2], false, true, view);
                    commit();
                    navigate(rows[^1], false, true, view);
                    commit();
                    int end = count - 1;
                    await MeasureGestureAsync($"{count} Shift+arrow", () =>
                    {
                        end = end == count - 1 ? count - 2 : count - 1;
                        navigate(rows[end], false, true, view);
                    }, commit);
                    CheckContainerSelection(table, "continued Shift range");
                }

                ListView list = FindDescendant<ListView>(table)!;
                table.ScrollTo(0);
                await AwaitProbeAsync(table, () => list.ItemsPanelRoot is ItemsStackPanel panel
                    && panel.FirstVisibleIndex == 0 && list.ContainerFromIndex(0) is ListViewItem);
                var focused = (ListViewItem)list.ContainerFromIndex(0);
                bool focus = focused.Focus(FocusState.Keyboard);
                W($"rows={count} focus accepted={focus}, state={focused.FocusState}, offset={table.VerticalOffset:0.0}");
                if (!focus)
                    throw new InvalidOperationException("The focused-container probe could not focus row zero.");
                await PerformanceFrameAsync();
                table.ScrollTo(100000);
                await AwaitProbeAsync(table, () => list.ItemsPanelRoot is ItemsStackPanel panel
                    && panel.FirstVisibleIndex > count / 2 && table.VerticalOffset > 0);
                if (!ReferenceEquals(FocusManager.GetFocusedElement(table.XamlRoot), focused))
                    throw new InvalidOperationException("The far scroll did not retain row zero's focus.");
                table.Selection = new Selection(rows);
                toggle(rows[0]);
                commit();
                CheckContainerSelection(table, "pinned Ctrl+click", requiresPinned: true);
                navigate(rows[2], false, true, view);
                commit();
                CheckContainerSelection(table, "pinned Shift range", requiresPinned: true);
                table.Selection = new Selection(rows.Where((_, index) => index % 2 == 0));
                CheckContainerSelection(table, "scattered selection", requiresPinned: true);
                table.Selection = new Selection(new[] { rows[1], rows[^1] }, rows[^1]);
                CheckContainerSelection(table, "far-apart replacement", requiresPinned: true);
                var marquee = model.GetType().GetMethod("SetMarqueeSelection", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .CreateDelegate<Action<List<object>, IReadOnlyList<object>>>(model);
                marquee(new() { rows[0], rows[^1] }, view);
                commit();
                CheckContainerSelection(table, "marquee result", requiresPinned: true);
                InvokeTable(table, "SelectItem", rows[^1], false, false);
                CheckContainerSelection(table, "plain click", requiresPinned: true);
                table.ItemsSource = rows[..^1];
                CheckContainerSelection(table, "publish prunes current row", requiresPinned: true);
            });
        }
        ReportLoad();
    }

    private async Task MeasureGestureAsync(string label, Action model, Func<Selection> commit)
    {
        List<Measurement> models = new();
        List<Measurement> packets = new();
        for (int trial = 0; trial < PerformanceTrials; trial++)
        {
            models.Add(MeasureBlock(model));
            packets.Add(await MeasureFrameAsync(() => commit()));
        }
        ReportMeasurements(label + " model only", models);
        ReportMeasurements(label + " packet/commit", packets);
    }

    private async Task MeasureNativeEventsAsync()
    {
        Section("N. Plain ListView over sample IObservableVector; off-screen native event cost");
        ReportLoad();
        foreach (int count in new[] { 1000, 2000 })
        {
            NativeRows rows = new();
            for (int index = 0; index < count; index++)
            {
                rows.Add(new object());
            }
            ListView list = new() { ItemsSource = rows };
            await WithElementAsync(list, async () =>
            {
                object item = new();
                rows.Add(item);
                rows.RemoveAt(rows.Count - 1);
                await PerformanceFrameAsync();
                rows.RaisedTicks = 0;
                const int events = 512;
                long started = Stopwatch.GetTimestamp();
                Measurement native = MeasureBlock(() =>
                {
                    for (int index = 0; index < events / 2; index++)
                    {
                        rows.Add(item);
                        rows.RemoveAt(rows.Count - 1);
                    }
                });
                long nativeTicks = rows.RaisedTicks;
                await PerformanceFrameAsync();
                double frame = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                list.ItemsSource = null;
                rows.RaisedTicks = 0;
                Measurement own = MeasureBlock(() =>
                {
                    for (int index = 0; index < events / 2; index++)
                    {
                        rows.Add(item);
                        rows.RemoveAt(rows.Count - 1);
                    }
                });
                double perEvent = (nativeTicks - rows.RaisedTicks) * 1_000_000d / Stopwatch.Frequency / events;
                W($"rows={count} events={events} native-event={perEvent:0.000} us/event, total less collection={(native.Block - own.Block) * 1000 / events:0.000} us/event, native block={native.Block:0.000} ms, first frame={frame:0.000} ms, allocated={native.Allocated}, own={own.Allocated}");
                await PerformanceFrameAsync();
            });
        }
        ReportLoad();
    }

    private async Task CaptureBaselineAsync()
    {
        Section("V. Baseline rows, held and drag states, hierarchy");
        await SavePngAsync(Path.Combine(AppContext.BaseDirectory, "performance-rows.png"));
        await WithProbeAsync(32, async (table, rows) =>
        {
            table.Selection = new Selection(new[] { rows[1], rows[3] }, rows[1]);
            await PerformanceFrameAsync();
            await SavePngAsync(Path.Combine(AppContext.BaseDirectory, "performance-selection.png"), table);
            InvokeTable(table, "PointAt", rows[1]);
            table.ItemsSource = rows.Where(row => !ReferenceEquals(row, rows[1])).ToArray();
            await PerformanceFrameAsync();
            bool held = (bool)InvokeTable(table, "IsHeld", rows[1])!;
            W($"held fixture: held={held}, selection={table.Selection.Items.Count}");
            if (!held)
                throw new InvalidOperationException("The held-row scene did not establish a held row.");
            await SavePngAsync(Path.Combine(AppContext.BaseDirectory, "performance-held.png"), table);
            InvokeTable(table, "StopPointing");
            table.ItemsSource = rows;
            InvokeTable(table, "BeginDrag", rows[3]);
            await PerformanceFrameAsync();
            bool dragging = (bool)InvokeTable(table, "IsDragging", rows[3])!;
            W($"drag fixture: dragging={dragging}");
            if (!dragging)
                throw new InvalidOperationException("The drag scene did not establish a dragged row.");
            await SavePngAsync(Path.Combine(AppContext.BaseDirectory, "performance-drag.png"), table);
            InvokeTable(table, "CancelGesture");
        });
        await WithProbeAsync(4, async (table, _) =>
            await SavePngAsync(Path.Combine(AppContext.BaseDirectory, "performance-hierarchy.png"), table), hierarchy: true);
    }

    private static object? InvokeTable(Table table, string method, params object[] arguments) =>
        typeof(Table).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(table, arguments);

    private async Task WithProbeAsync(int count, Func<Table, ProbeRow[], Task> action, bool hierarchy = false,
        ProbeSort sort = ProbeSort.Ordinal)
    {
        ProbeRow[] rows = Enumerable.Range(0, count)
            .Select(index => new ProbeRow(index + 1, $"Render {index:D6}" )).ToArray();
        Column column = new()
        {
            DisplayName = "Render",
            Width = 360,
            CellTemplate = (DataTemplate)XamlReader.Load("<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><TextBlock Text=\"{Binding Name}\" /></DataTemplate>"),
        };
        Table table = new() { CanReorder = true, Background = Table.Background,
            BorderBrush = Table.BorderBrush, BorderThickness = Table.BorderThickness, RequestedTheme = Table.ActualTheme };
        table.Columns.Add(column);
        var schema = table.Schema<ProbeRow>().Key(row =>
        {
            _keyReads++;
            return row.RowId;
        }).CanInteract(row =>
        {
            _policyReads++;
            return row.RowId > 0;
        }).SortKey(column, row =>
        {
            _sortKeyReads++;
            return row.Name;
        }, sort == ProbeSort.Culture ? StringComparer.CurrentCultureIgnoreCase : StringComparer.Ordinal);
        if (sort == ProbeSort.Number)
        {
            schema.SortKey(column, row =>
            {
                _sortKeyReads++;
                return -row.RowId;
            });
        }
        if (hierarchy)
        {
            schema.Hierarchy(column, row => row.RowId == 1 ? rows[1..^1] : Array.Empty<ProbeRow>(),
                _ => true, (_, _) => { });
        }
        else
        {
            Column order = new() { DisplayName = "Order", Width = 64 };
            table.Columns.Add(order);
            schema.Order(order, row => (int)row.RowId, () => rows);
        }
        table.ItemsSource = hierarchy ? new[] { rows[0], rows[^1] } : rows;
        await WithElementAsync(table, () => action(table, rows));
    }

    private async Task WithElementAsync(FrameworkElement element, Func<Task> action)
    {
        Grid parent = (Grid)Table.Parent;
        Grid.SetRow(element, Grid.GetRow(Table));
        Table.Visibility = Visibility.Collapsed;
        parent.Children.Add(element);
        try
        {
            parent.UpdateLayout();
            await PerformanceFrameAsync();
            await action();
        }
        finally
        {
            parent.Children.Remove(element);
            Table.Visibility = Visibility.Visible;
            parent.UpdateLayout();
            await PerformanceFrameAsync();
            TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => drained.TrySetResult()))
            {
                throw new InvalidOperationException("The sample dispatcher is shutting down.");
            }
            await drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static Measurement MeasureBlock(Action action)
    {
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        action();
        return new(Stopwatch.GetElapsedTime(start).TotalMilliseconds,
            GC.GetAllocatedBytesForCurrentThread() - allocated, 0);
    }

    private static async Task<Measurement> MeasureFrameAsync(Action action)
    {
        long start = Stopwatch.GetTimestamp();
        Measurement block = MeasureBlock(action);
        await PerformanceFrameAsync();
        return block with { Frame = Stopwatch.GetElapsedTime(start).TotalMilliseconds };
    }

    private void ReportMeasurements(string label, List<Measurement> results)
    {
        Measurement median = results.OrderBy(result => result.Block).ElementAt(results.Count / 2);
        W($"{label}: block median={median.Block:0.0000} ms, max={results.Max(result => result.Block):0.0000} ms; allocated median={results.Select(result => result.Allocated).Order().ElementAt(results.Count / 2)} bytes; first frame median={results.Select(result => result.Frame).Order().ElementAt(results.Count / 2):0.000} ms");
    }

    private void CheckContainerSelection(Table table, string operation, bool requiresPinned = false)
    {
        ListView list = FindDescendant<ListView>(table)!;
        int mismatches = 0;
        int checkedRows = 0;
        int pinned = 0;
        if (list.ItemsPanelRoot is ItemsStackPanel panel)
        {
            foreach (UIElement child in panel.Children)
            {
                int index = list.IndexFromContainer(child);
                if (index < 0 || child is not ListViewItem container
                    || !ReferenceEquals(list.ContainerFromIndex(index), child))
                {
                    continue;
                }
                checkedRows++;
                if (index < panel.FirstCacheIndex || index > panel.LastCacheIndex)
                {
                    pinned++;
                }
                object item = list.ItemFromContainer(child);
                if (container.IsSelected != table.Selection.Items.Any(selected => ReferenceEquals(selected, item)))
                {
                    mismatches++;
                }
            }
        }
        W($"  {operation}: checked={checkedRows}, outside-cache={pinned}, selection mismatches={mismatches}; {ProbeState(table)}");
        if (checkedRows == 0 || mismatches != 0 || (requiresPinned && pinned == 0))
            throw new InvalidOperationException($"Container selection failed after {operation}.");
    }

    private static string ProbeState(Table table)
    {
        ListView list = FindDescendant<ListView>(table)!;
        var panel = list.ItemsPanelRoot as ItemsStackPanel;
        var focused = FocusManager.GetFocusedElement(table.XamlRoot) as ListViewItem;
        return $"offset={table.VerticalOffset:0.0}, visible={panel?.FirstVisibleIndex}..{panel?.LastVisibleIndex}, cache={panel?.FirstCacheIndex}..{panel?.LastCacheIndex}, focus={focused?.FocusState}, focused-index={(focused is null ? -1 : list.IndexFromContainer(focused))}";
    }

    private static async Task AwaitProbeAsync(Table table, Func<bool> ready)
    {
        long started = Stopwatch.GetTimestamp();
        while (!ready())
        {
            if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(5))
                throw new TimeoutException("The container probe did not settle: " + ProbeState(table));
            await PerformanceFrameAsync();
            table.UpdateLayout();
        }
    }

    private void ReportLoad()
    {
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.ProcessName is "dotnet" or "MSBuild" or "MsMpEng" or "pwsh" or "TableViewSample" or "testhost")
                        W($"load {DateTimeOffset.Now:O} {process.ProcessName} pid={process.Id}, cpu={process.TotalProcessorTime.TotalSeconds:0.000}s, working={process.WorkingSet64}");
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    W($"load pid={process.Id}: access denied");
                }
                catch (InvalidOperationException)
                {
                    W("load: process exited during snapshot");
                }
            }
        }
    }

    private static async Task<long> PerformanceFrameAsync()
    {
        TaskCompletionSource<long> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<object>? rendering = null;
        rendering = (_, _) => completion.TrySetResult(Stopwatch.GetTimestamp());
        CompositionTarget.Rendering += rendering;
        try
        {
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            CompositionTarget.Rendering -= rendering;
        }
    }

    private sealed class NativeRows : Collection<object>, IObservableVector<object>
    {
        internal long RaisedTicks { get; set; }
        public event VectorChangedEventHandler<object>? VectorChanged;

        protected override void InsertItem(int index, object item)
        {
            base.InsertItem(index, item);
            Raise(CollectionChange.ItemInserted, index);
        }

        protected override void RemoveItem(int index)
        {
            base.RemoveItem(index);
            Raise(CollectionChange.ItemRemoved, index);
        }

        private void Raise(CollectionChange change, int index)
        {
            NativeChange args = new(change, index);
            long start = Stopwatch.GetTimestamp();
            VectorChanged?.Invoke(this, args);
            RaisedTicks += Stopwatch.GetTimestamp() - start;
        }
    }

    private sealed class NativeChange(CollectionChange change, int index) : IVectorChangedEventArgs
    {
        public CollectionChange CollectionChange => change;
        public uint Index => (uint)index;
    }
}
