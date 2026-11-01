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

    public sealed record ProbeRow(long RowId, string Name);
    private sealed record Measurement(double Block, long Allocated, double Frame);

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
        (string Name, object Owner)[] maps =
        {
            ("source.accepted", Read(source, "_membership")), ("source.candidate", Read(source, "_candidate")),
            ("view", positions), ("view.candidate", Read(table, "_candidatePositions")),
            ("held", Read(table, "_held")), ("held.candidate", Read(table, "_candidateHeld")),
            ("pinned", Read(table, "_pinned")), ("released", Read(table, "_released")),
            ("selected", Read(selection, "_selected")), ("tracked", Read(selection, "_tracked")),
            ("selection.added", Read(selection, "_added")), ("selection.removed", Read(selection, "_removed")),
        };
        long total = 0;
        foreach (var (name, owner) in maps)
        {
            object map = owner.GetType().GetProperty("Captured")!.GetValue(owner)!;
            long bytes = 0;
            string capacities = "";
            foreach (string field in new[] { "_entries", "_keys" })
            {
                if (map.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(map) is not IDictionary dictionary)
                    continue;
                var entries = (Array?)dictionary.GetType().GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dictionary);
                var buckets = (Array?)dictionary.GetType().GetField("_buckets", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dictionary);
                int size = entries is null ? 0 : (int)typeof(System.Runtime.CompilerServices.Unsafe)
                    .GetMethod("SizeOf")!.MakeGenericMethod(entries.GetType().GetElementType()!).Invoke(null, null)!;
                bytes += Bytes(entries?.Length ?? 0, size) + Bytes(buckets?.Length ?? 0, sizeof(int));
                capacities += $" {field} count={dictionary.Count}, capacity={entries?.Length ?? 0} x {size}";
            }
            total += bytes;
            W($"  {name}:{capacities}; backing={bytes} bytes");
        }
        object backing = view.GetType().GetProperty("Items", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        foreach (var (name, list) in new[] { ("view rows", backing), ("kept order", Read(table, "_keptRows")),
            ("shown rows", Read(table, "_shownRows")), ("pin order", Read(table, "_pinOrder")) })
        {
            int capacity = (int)list.GetType().GetProperty("Capacity")!.GetValue(list)!;
            long bytes = Bytes(capacity, IntPtr.Size);
            total += bytes;
            W($"  {name}: count={((ICollection)list).Count}, capacity={capacity} x {IntPtr.Size}; backing={bytes} bytes");
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

    private async Task WithProbeAsync(int count, Func<Table, ProbeRow[], Task> action, bool hierarchy = false)
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
        }).SortKey(column, row => row.Name, StringComparer.Ordinal);
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

    private static async Task PerformanceFrameAsync()
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<object>? rendering = null;
        rendering = (_, _) => completion.TrySetResult();
        CompositionTarget.Rendering += rendering;
        try
        {
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
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
