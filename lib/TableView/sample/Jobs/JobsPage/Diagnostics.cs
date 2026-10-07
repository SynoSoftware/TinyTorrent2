using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Syno.TableView;
using Syno.TableView.Header;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Syno.TableViewSample.Jobs;

/// <summary>
/// The measurement pass that produces sample-results.txt. Sections A to G check what the control
/// renders; H to K drive it at scale and produce the figures specification sections 5.3, 9 and 20
/// quote. It runs only when this launch asked for it, so a person opening the sample never sees it.
/// </summary>
/// <remarks>
/// Deliberately hosted here rather than in the original host. Every claim below is about the control
/// — how many notifications a reorder raises, how long it takes, how often a sort is allowed to
/// move rows, whether a container ever shows the wrong row — and none of it is about that host's rows. The
/// row type it drives is the sample's own, which is what keeps the measurements alive across a
/// rewrite of any host.
/// </remarks>
public sealed partial class JobsPage
{
    /// <summary>Notifications the hosted list has been sent since the last reset.</summary>
    private int _notifications;

    /// <summary>Containers the hosted list has prepared since the last reset.</summary>
    private int _realizations;

    private ListView? _counted;

    /// <summary>
    /// Gen 0 and gen 2 collections that happened inside the last <see cref="Time"/>. A reconcile
    /// allocates a dictionary and three integer arrays the length of the view, so a collection
    /// landing inside a timed sort is a real candidate for why one sort costs three times another.
    /// </summary>
    private string _collections = "0/0";

    /// <summary>
    /// The sections this launch was asked for, or null for all of them. A whole pass takes minutes,
    /// during which the app resizes its own window, sorts itself and scrolls itself, which is
    /// indistinguishable from a hang to anyone watching. Asking for one section should cost seconds.
    /// </summary>
    private HashSet<string>? _only;

    /// <summary>Whether this launch asked for something other than <paramref name="section"/>.</summary>
    private bool Skip(string section) => _only is not null && !_only.Contains(section);

    // ------------------------------------------------------------ diagnostics

    private async Task RunDiagnosticsAsync()
    {
        SizeWindow();

        // Every section below wants a still table; the two that want a live feed start it
        // themselves. A tick landing inside a timed sort is exactly the contamination that made two
        // earlier sections disagree about the same reversal.
        _feed?.Stop();
        await Settle(700);

        W($"Sample measurement pass {DateTime.Now:O}");
        W($"  {Configuration} {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}, " +
          $"RasterizationScale={_scale}, {RowCount} rows, every duration measured by {nameof(Time)}");

        // Counting notifications is on for the whole pass rather than switched on around the
        // operations that report a count, so its cost is in every figure equally and cancels out of
        // every comparison between them.
        AttachCounters();

        if (!Skip("A"))
        {
            Section("A. Did anything render at all?");
            await ReportRenderAsync();
        }

        if (!Skip("F"))
        {
            Section("F. RenderTargetBitmap of the table");
            await SavePngAsync(System.IO.Path.Combine(AppContext.BaseDirectory, "sample-table.png"));
            await ReportBitmapAsync();
        }

        if (!Skip("G"))
        {
            Section("G. Platform spacing resources available to a header cell");
            ProbeResources();
        }

        if (!Skip("H"))
        {
            Section("H. What one full reversal costs (specification 20)");
            await MeasureReversalAsync();
        }

        if (!Skip("I"))
        {
            Section("I. The same reversal at ten times the rows (specification 20)");
            await ProbeScaleAsync();
        }

        if (!Skip("J"))
        {
            Section("J. The list is told only about the rows it holds (specification 5.3)");
            await ProbeQuietPlacementAsync();
        }

        if (!Skip("K"))
        {
            Section("K. What the settling interval buys (specification 9)");
            await MeasureSettlingAsync();
        }

        DetachCounters();
    }

    // --------------------------------------------------------- sections A–G

    /// <summary>
    /// What the feed actually generated. A synthetic source exists so that every cell template has
    /// something to draw; one that quietly stopped producing a state would leave the page looking
    /// empty while every other measurement in this file still passed. This is the check that would
    /// notice, and it is also the answer to "which states does the sample exercise".
    /// </summary>
    private void ReportFeedSpread()
    {
        IReadOnlyList<Job> rows = _feed?.Rows ?? Array.Empty<Job>();
        if (rows.Count == 0)
        {
            W("the feed generated no rows");
            return;
        }

        foreach (IGrouping<JobState, Job> group in rows.GroupBy(r => r.State).OrderBy(g => g.Key))
        {
            W($"  {group.Key,-16} x{group.Count()}");
        }

        W($"  rows carrying a fault  = {rows.Count(r => r.HasFault)}");
        W($"  non-interactive rows   = {rows.Count(r => r.IsPending)}");
        W($"  progress {rows.Min(r => r.Progress):0.0}% to {rows.Max(r => r.Progress):0.0}%, " +
          $"{rows.Count(r => r.Progress >= 100)} complete and {rows.Count(r => r.Progress == 0)} at zero");
        W($"  in rate {rows.Max(r => r.InRate) / 1_000_000:0.0} MB/s at most, " +
          $"out rate {rows.Max(r => r.OutRate) / 1_000_000:0.0} MB/s at most, " +
          $"{rows.Count(r => r.InRate == 0 && r.OutRate == 0)} rows moving nothing");
        W($"  workers busy {rows.Min(r => r.WorkersBusy)} to {rows.Max(r => r.WorkersBusy)} " +
          $"of pools {rows.Min(r => r.WorkersTotal)} to {rows.Max(r => r.WorkersTotal)}");
        W($"  yield {rows.Min(r => r.Yield):0.00} to {rows.Max(r => r.Yield):0.00}");
        W($"  no estimate on {rows.Count(r => r.Remaining is null)} rows, " +
          $"not finished on {rows.Count(r => r.Finished is null)}");
        W($"  job names {rows.Min(r => r.Name.Length)} to {rows.Max(r => r.Name.Length)} characters");
    }

    private async Task ReportRenderAsync()
    {
        W("what the feed generated:");
        ReportFeedSpread();
        W($"Table ActualWidth={Table.ActualWidth:0.##} ActualHeight={Table.ActualHeight:0.##}");

        ListView? list = FindDescendant<ListView>(Table);
        Strip? strip = FindDescendant<Strip>(Table);
        W($"header strip found = {strip is not null}");
        W($"hosted ListView found = {list is not null}");
        if (list is not null)
        {
            W($"ListView ActualWidth={list.ActualWidth:0.##} SelectionMode={list.SelectionMode} " +
              $"IsMultiSelectCheckBoxEnabled={list.IsMultiSelectCheckBoxEnabled} Items={list.Items.Count}");
        }

        List<Cell> header = new();
        if (strip is not null)
        {
            FindAll(strip, header);
        }

        W($"header cells realized = {header.Count}");

        List<ListViewItem> containers = new();
        if (list is not null)
        {
            FindAll(list, containers);
        }

        W($"realized ListViewItem containers = {containers.Count} of {RowCount} rows");

        ScrollViewer? sv = list is null ? null : FindDescendant<ScrollViewer>(list);
        if (sv is not null)
        {
            W($"inner ScrollViewer HorizontalScrollMode={sv.HorizontalScrollMode} " +
              $"ExtentWidth={sv.ExtentWidth:0.##} ViewportWidth={sv.ViewportWidth:0.##} " +
              $"ExtentHeight={sv.ExtentHeight:0.##} ViewportHeight={sv.ViewportHeight:0.##}");
        }

        Section("B. Content origin and vertical pitch of the row containers");
        ReportRowOrigin(containers);
        ReportRowPitch(list, containers);

        Section("C. Header cell boundaries vs row cell boundaries");
        await SavePngAsync(System.IO.Path.Combine(AppContext.BaseDirectory, "sample-boundaries.png"));
        ReportBoundaries("loaded", header, containers);
    }

    // ------------------------------------------------------ sections H–K

    /// <summary>
    /// Specification 20: a reorder raises notifications for membership changes and for the positions
    /// the list holds a container for, and for nothing else, so its count is bounded by the realized
    /// set rather than by the row count. This is the "after" half of the figure that section quotes.
    /// </summary>
    private async Task MeasureReversalAsync()
    {
        ListView? list = FindDescendant<ListView>(Table);
        if (list is null)
        {
            W("  no hosted list");
            return;
        }

        Microsoft.UI.Xaml.Media.Animation.TransitionCollection? live = list.ItemContainerTransitions;

        (string Label, bool Motion)[] cases =
        {
            ("index reversal, row transitions on", true),
            ("index reversal, row transitions off", false),
        };

        foreach ((string label, bool motion) in cases)
        {
            if (!motion)
            {
                list.ItemContainerTransitions =
                    new Microsoft.UI.Xaml.Media.Animation.TransitionCollection();
            }

            List<double> totals = new();
            List<double> changes = new();
            int sent = 0;
            int perRow = 0;
            string collected = "0/0";

            // Five trials, not one. A single sample of this operation moved by a factor of two
            // between runs on a machine at about forty percent load, so one number is not a
            // measurement; the spread reported below is.
            for (int trial = 0; trial < 5; trial++)
            {
                Table.Sort = new Sort(IndexColumn);
                await Settle(500);

                List<object> before = Order(list);
                _notifications = 0;
                (double changed, double laidOut) = Time(
                    () => Table.Sort = new Sort(IndexColumn, SortDirection.Descending));

                totals.Add(changed + laidOut);
                changes.Add(changed);
                sent = _notifications;
                perRow = PerRowNotifications(before, list);
                collected = _collections;
                await Settle(500);
            }

            // The container count comes from a pass of its own, so that no reported duration is
            // measured with a ContainerContentChanging subscriber attached.
            Table.Sort = new Sort(IndexColumn);
            await Settle(500);
            (int realized, double _) = Realizations(
                () => Table.Sort = new Sort(IndexColumn, SortDirection.Descending));
            await Settle(500);

            totals.Sort();
            changes.Sort();
            W($"  {label}, {RowCount} rows:");
            W($"    {sent} notifications, a container was prepared {realized} times, " +
              $"{collected} gen0/gen2 collections in the last trial");
            W($"    every position moved, so a reconcile announcing each of them would have raised " +
              $"a computed {perRow}");

            // The collection change on its own is the half specification 20 records as 223 ms
            // falling to 13 ms — on a host that no longer exists, so this feed's own numbers are
            // the current ones; the layout it causes is reported beside it, never added into it.
            W($"    collection change: median {changes[changes.Count / 2]:0} ms, " +
              $"{changes[0]:0} to {changes[^1]:0} ms across {changes.Count} trials");
            W($"    change plus layout: median {totals[totals.Count / 2]:0} ms, " +
              $"{totals[0]:0} to {totals[^1]:0} ms across {totals.Count} trials");

            if (!motion)
            {
                list.ItemContainerTransitions = live;
            }
        }

        Table.Sort = null;
        await Settle(400);
    }

    /// <summary>
    /// The claim the whole reconcile rests on: what a reorder costs follows the number of rows on
    /// the screen and not the number of rows in the table. Every other figure here is taken at the
    /// page's own row count, which cannot tell a cost that scales from one that does not. This runs
    /// the same reversal against ten times the rows, and then ticks all of them.
    /// </summary>
    private async Task ProbeScaleAsync()
    {
        ListView? list = FindDescendant<ListView>(Table);

        async Task Reversal(string what, IReadOnlyList<Job> rows)
        {
            Table.ItemsSource = new List<Job>(rows);
            Table.Sort = new Sort(IndexColumn);
            await Settle(900);

            List<object> before = Order(list);
            _notifications = 0;
            (double changed, double laidOut) = Time(
                () => Table.Sort = new Sort(IndexColumn, SortDirection.Descending));

            W($"  {what}: {rows.Count} rows, {_notifications} notifications " +
              $"(against {PerRowNotifications(before, list)} for a reconcile that announced every " +
              $"moved row), {_collections} collections, changed in {changed:0} ms, laid out in " +
              $"{laidOut:0} ms, {changed + laidOut:0} ms in all");
        }

        Feed? bigger = null;
        try
        {
            await Reversal("as the page runs", _feed!.Rows);

            Stopwatch built = Stopwatch.StartNew();
            bigger = new Feed(DispatcherQueue, 20000);
            W($"  a 20,000-row feed generated in {built.Elapsed.TotalMilliseconds:0} ms");

            await Reversal("ten times the rows", bigger.Rows);

            // Ticking them is the other half of "reaches 20,000 rows": a source that size has to
            // move, or the reversal above is a measurement over a static list.
            Table.Sort = new Sort(ThroughputColumn, SortDirection.Descending);
            await Settle(600);

            int ticks = 0;
            double worst = 0;
            double total = 0;
            EventHandler onTick = (_, _) =>
            {
                Stopwatch publish = Stopwatch.StartNew();
                Table.ItemsSource = new List<Job>(bigger!.Rows);
                Table.UpdateLayout();
                double ms = publish.Elapsed.TotalMilliseconds;
                total += ms;
                worst = Math.Max(worst, ms);
                ticks++;
            };

            bigger.Ticked += onTick;
            _notifications = 0;
            bigger.Start();
            await Task.Delay(8000);
            bigger.Stop();
            bigger.Ticked -= onTick;

            W($"  ticking 20,000 rows sorted by throughput: {ticks} ticks in 8 s, " +
              $"{_notifications} notifications in all, publish plus layout averaged " +
              $"{(ticks == 0 ? 0 : total / ticks):0} ms and peaked at {worst:0} ms");
        }
        catch (Exception ex)
        {
            W($"  the larger feed failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            bigger?.Stop();
            Table.Sort = null;
            Publish();
            await Settle(900);
        }
    }

    /// <summary>
    /// Specification 5.3's falsifiable half: the list keeps nothing for a position it has not
    /// realized, so a reorder can be announced only where it holds a container and every other
    /// position changes quietly. If that is wrong, a container shows a row the view does not have
    /// at its index, and every case below is a different way of asking whether one does.
    /// </summary>
    private async Task ProbeQuietPlacementAsync()
    {
        ListView? list = FindDescendant<ListView>(Table);
        if (list?.ItemsSource is not System.Collections.IList view
            || list.ItemsPanelRoot is not Panel panel)
        {
            W("  no hosted list to probe");
            return;
        }

        ScrollViewer? scroller = FindDescendant<ScrollViewer>(list);

        string Disagreements()
        {
            int inspected = 0;
            List<string> wrong = new();

            foreach (UIElement child in panel.Children)
            {
                int index = list.IndexFromContainer(child);
                if (index < 0 || child is not ListViewItem container)
                {
                    continue;
                }

                inspected++;
                object? shown = container.Content;
                object? expected = index < view.Count ? view[index] : null;
                if (!ReferenceEquals(shown, expected))
                {
                    wrong.Add($"{index} shows {(shown as Job)?.Name ?? "nothing"} but the view " +
                              $"has {(expected as Job)?.Name ?? "nothing"}");
                }
            }

            return wrong.Count == 0
                ? $"{inspected} containers, all showing the view's row"
                : $"{inspected} containers, {wrong.Count} wrong: {string.Join("; ", wrong.Take(4))}";
        }

        string Fill()
        {
            double viewport = scroller?.ViewportHeight ?? list.ActualHeight;
            double reached = 0;
            int held = 0;

            foreach (UIElement child in panel.Children)
            {
                if (child is not FrameworkElement row || list.IndexFromContainer(child) < 0)
                {
                    continue;
                }

                held++;
                reached = Math.Max(
                    reached,
                    row.TransformToVisual(list)
                        .TransformBounds(new Rect(0, 0, row.ActualWidth, row.ActualHeight))
                        .Bottom);
            }

            return $"{held} containers reaching {reached:0} of the {viewport:0} the viewport shows" +
                   (reached >= viewport - 1 ? string.Empty : " — the foot of the viewport is empty");
        }

        Table.Sort = new Sort(IndexColumn);
        await Settle(600);

        double rowHeight = RowHeight(list, panel);
        W($"  row height taken from a realized container = {rowHeight:0.##} DIP");

        // 0. The mechanism, before any symptom. Scroll away from the top so the realized run sits
        //    well above index 0, remove the row at 0, and ask a container what index it is at before
        //    any layout has run. Dropped by one means the panel updates its map inside the
        //    notification, so two reconciles in one callback cannot read a stale set and no forced
        //    layout is needed.
        scroller?.ChangeView(null, 700 * rowHeight, null, disableAnimation: true);
        list.UpdateLayout();
        await Settle(400);

        UIElement? sample = panel.Children.FirstOrDefault(c => list.IndexFromContainer(c) > 0);
        if (sample is not null && view.Count > 1)
        {
            int before = list.IndexFromContainer(sample);
            object first = view[0]!;
            view.RemoveAt(0);
            int after = list.IndexFromContainer(sample);
            view.Insert(0, first);

            W($"  0. a container at {before}; after removing the row at 0, and before any layout, " +
              $"it reports {after} — the panel's map is " +
              (after == before - 1 ? "updated inside the notification" : "deferred to the next measure"));
        }
        else
        {
            W("  0. no container above index 0 to ask");
        }

        await Settle(400);

        // 1. The reversal itself.
        Table.Sort = new Sort(IndexColumn);
        await Settle(600);
        _notifications = 0;
        (double changed, double laidOut) = Time(
            () => Table.Sort = new Sort(IndexColumn, SortDirection.Descending));

        W($"  1. a full reversal: {_notifications} notifications, {_collections} collections, " +
          $"changed in {changed:0} ms and laid out in {laidOut:0} ms");
        W($"     {Disagreements()}");

        // Whether the rows reach the bottom of the viewport is a different question from whether the
        // containers that exist are right, and it is the failure this design can have: rows placed
        // without a notification give the panel no reason to re-examine what it holds, so the foot
        // of the viewport stays empty until something else pokes it. Asked immediately, then after a
        // settle: if the first answer is short and the second is not, the panel was told too late.
        W($"     {Fill()}");

        // Uneven vertical gaps were reported on this page. A reorder animates every container it
        // moved into its new place, so the moment to ask is inside that animation and again once it
        // has run: uneven then even names the reposition transition, uneven in both names the
        // layout, and even in both means nothing here produces them.
        List<ListViewItem> moved = new();
        FindAll(list, moved);
        W("     row pitch inside the reposition animation:");
        ReportRowPitch(list, moved);

        await Settle(1200);
        W($"     after settling: {Fill()}");
        W("     row pitch once the animation has run:");
        ReportRowPitch(list, moved);

        // 2. Far scrolls, where a quietly placed row would first be seen. Asked after a second,
        //    not after 400 ms: a scroll of two thousand rows realizes two dozen containers with
        //    eleven cells each, and asked too early the answer is that some hold the row they held
        //    before and some hold nothing — which is realization in progress, not a placement
        //    defect, and reporting it as one would be worse than not asking.
        int last = view.Count - 1;
        bool firstScroll = true;
        foreach (int row in new[] { last / 3, last * 2 / 3, last - 12 })
        {
            scroller?.ChangeView(null, row * rowHeight, null, disableAnimation: true);
            list.UpdateLayout();

            if (firstScroll)
            {
                // The other place uneven gaps can come from, and the one a person actually sees: a
                // container arrives before its cells do. Asked here it is holding whatever height
                // it has this frame; asked after the settle it is holding the height its cells ask
                // for. Two answers that differ name the arrival, not the layout.
                firstScroll = false;
                List<ListViewItem> arriving = new();
                FindAll(list, arriving);
                W("  2. row pitch while the scrolled-to rows are still arriving:");
                ReportRowPitch(list, arriving);
            }

            await Settle(1000);
            W($"  2. after scrolling to about row {row}: {Disagreements()}");
        }

        List<ListViewItem> settled = new();
        FindAll(list, settled);
        W("  2. row pitch once they have:");
        ReportRowPitch(list, settled);

        // 3. The row a scroll request brings in.
        scroller?.ChangeView(null, 0, null, disableAnimation: true);
        await Settle(400);
        int wantedIndex = view.Count * 3 / 4;
        if (wantedIndex > 0)
        {
            object wanted = view[wantedIndex]!;
            list.ScrollIntoView(wanted);
            list.ScrollIntoView(wanted);
            list.UpdateLayout();
            await Settle(1000);

            object? arrived = (list.ContainerFromItem(wanted) as ListViewItem)?.Content;
            W($"  3. ScrollIntoView of view index {wantedIndex} arrived holding " +
              $"{(ReferenceEquals(arrived, wanted) ? "that row" : "something else")}; {Disagreements()}");
        }

        // 4. Two reconciles before one layout, which is a publish and the settle timer landing in
        //    the same dispatcher callback as a sort.
        scroller?.ChangeView(null, 700 * rowHeight, null, disableAnimation: true);
        list.UpdateLayout();
        await Settle(400);

        Table.Sort = new Sort(IndexColumn);
        Table.Sort = new Sort(IndexColumn, SortDirection.Descending);

        // Asserting between the two reconciles and their layout finds no containers to assert on:
        // the list answers no index for any of them until it has laid out. That is a fact about when
        // the question can be asked, not a pass, so this says so rather than reporting nothing wrong
        // out of nothing checked.
        W($"  4. two reconciles in one callback, before their layout: {Disagreements()} — nothing " +
          "can be asked of the list here, it answers no index until it lays out");
        list.UpdateLayout();
        await Settle(400);
        W($"     after the layout, which is where this case is decided: {Disagreements()}");

        // 5. The same thing with the host publishing underneath it.
        Table.Sort = new Sort(ThroughputColumn, SortDirection.Descending);
        TimeSpan restore = Table.SortInterval;
        Table.SortInterval = TimeSpan.Zero;
        _feed!.Start();
        for (int second = 0; second < 15; second++)
        {
            await Task.Delay(1000);
            string state = Disagreements();
            if (!state.EndsWith("showing the view's row", StringComparison.Ordinal))
            {
                W($"  5. second {second} under a live publish: {state}");
            }
        }

        _feed.Stop();
        W("  5. fifteen seconds sorted by throughput with settling off, publishing every second: " +
          Disagreements());

        Table.SortInterval = restore;
        Table.Sort = null;
        scroller?.ChangeView(null, 0, null, disableAnimation: true);
        await Settle(600);
    }

    /// <summary>
    /// Specification 9: while a sort is applied the view converges on the sorted order within a
    /// bounded interval, so a host that publishes every second reorders once per interval instead of
    /// once per publish. Counted per reorder for the benefit and per publish for the cost, because
    /// per second measures only how often the host happened to publish.
    /// </summary>
    private async Task MeasureSettlingAsync()
    {
        ListView? list = FindDescendant<ListView>(Table);
        if (list?.ItemsSource is not INotifyCollectionChanged feed)
        {
            W("  the list's source raises no notifications to count");
            return;
        }

        TimeSpan original = Table.SortInterval;

        // Specification 9's claim, in the unit it states it in. With settling off every publish
        // reorders, so per publish and per reorder are the same number; taken as one publish rather
        // than averaged over a window so that the per-row comparison beside it is that publish and
        // not a different one. The 2,158 that section used to quote has been removed from it: it
        // was taken against a reconcile that announced every row it moved, which is the shape of
        // the second number here, and that implementation is gone.
        Table.SortInterval = TimeSpan.Zero;
        Table.Sort = new Sort(ThroughputColumn, SortDirection.Descending);
        await Settle(600);

        List<object> beforePublish = Order(list);
        _notifications = 0;
        _feed!.Tick();
        Publish();
        Table.UpdateLayout();

        W($"  one publish, sorted by Throughput with settling off, {RowCount} rows: " +
          $"{_notifications} collection notifications, against a computed " +
          $"{PerRowNotifications(beforePublish, list)} for a reconcile announcing every moved row");

        foreach ((Column column, TimeSpan settle) in new[]
        {
            (ThroughputColumn, original),
            (ThroughputColumn, TimeSpan.Zero),
            (NameColumn, original),
        })
        {
            Table.SortInterval = settle;
            Table.Sort = new Sort(column, SortDirection.Descending);
            await Settle(600);

            // One publish either reorders the whole view or is held, so counting the publishes that
            // reordered is the measure. Per publish is the wrong unit and per second is too: the
            // first divides out the very publishes settling makes free, and the second is hostage to
            // how often the feed happened to publish. The window is reported as the clock measured
            // it, not as the delays asked for, because a hundred sampling awaits on a busy UI thread
            // overrun by half — labelling it fifteen seconds put eight reorders inside a window that
            // was really twenty-two, which reads as the cap failing when it held.
            int reorders = 0;
            int sinceLast = 0;
            List<int> perReorder = new();
            NotifyCollectionChangedEventHandler burst = (_, _) => sinceLast++;

            _notifications = 0;
            int publishedBefore = Publishes;
            int seenPublishes = publishedBefore;
            feed.CollectionChanged += burst;
            _feed!.Start();
            Stopwatch window = Stopwatch.StartNew();

            while (window.Elapsed < TimeSpan.FromSeconds(15))
            {
                await Task.Delay(100);
                if (Publishes == seenPublishes)
                {
                    continue;
                }

                seenPublishes = Publishes;
                if (sinceLast > 0)
                {
                    reorders++;
                    perReorder.Add(sinceLast);
                }

                sinceLast = 0;
            }

            double seconds = window.Elapsed.TotalSeconds;
            _feed.Stop();
            feed.CollectionChanged -= burst;

            int published = Publishes - publishedBefore;
            W($"  sorted by {column.DisplayName}, settle {settle.TotalSeconds:0.#}s: {published} publishes " +
              $"in {seconds:0.0} s, {reorders} of them reordered the view" +
              (reorders == 0 ? string.Empty : $", one every {seconds / reorders:0.0} s") +
              $", {_notifications} notifications in all" +
              (perReorder.Count == 0
                  ? string.Empty
                  : $", {perReorder.Sum() / perReorder.Count} per reorder"));
        }

        Table.SortInterval = original;
        Table.Sort = null;
        await Settle(400);
    }

    // ---------------------------------------------------------- measurement

#if DEBUG
    private const string Configuration = "Debug";
#else
    private const string Configuration = "Release";
#endif

    /// <summary>
    /// The one stopwatch in this file. Every duration reported anywhere is this pair, so two
    /// sections timing the same operation cannot disagree because one measured a different span.
    /// The first number is the collection change alone; the second is the layout pass it caused,
    /// and never the two added together.
    /// </summary>
    private (double Mutation, double Layout) Time(Action operation)
    {
        // Empty the heap first. A gen 2 collection landing inside a timed sort is worth more than
        // the sort, and that is the whole of why two sections once reported the same reversal as
        // 141 ms and 700 ms: the one that ran early in the pass collected nothing inside its
        // measurement and the one that ran late collected twice. This does not remove a cost the
        // owner pays; it removes it from the comparison between two designs, and the counts reported
        // beside every figure say whether it worked.
        //
        // Collect only. Never WaitForPendingFinalizers here: this runs on the UI thread, WinRT
        // objects have finalizers that marshal back to the UI thread, and waiting for them from the
        // thread they are waiting for is a deadlock with no timeout.
        GC.Collect();

        int gen0 = GC.CollectionCount(0);
        int gen2 = GC.CollectionCount(2);

        Stopwatch clock = Stopwatch.StartNew();
        operation();
        double mutation = clock.Elapsed.TotalMilliseconds;
        Table.UpdateLayout();

        _collections = $"{GC.CollectionCount(0) - gen0}/{GC.CollectionCount(2) - gen2}";
        return (mutation, clock.Elapsed.TotalMilliseconds - mutation);
    }

    /// <summary>
    /// Counting notifications is on for the whole pass: it is one increment per notification and it
    /// costs every figure the same. Counting container preparations is not, and must never be: see
    /// <see cref="Realizations"/>.
    /// </summary>
    private void AttachCounters()
    {
        _counted = FindDescendant<ListView>(Table);
        if (_counted?.ItemsSource is INotifyCollectionChanged feed)
        {
            feed.CollectionChanged += OnCountedNotification;
        }
    }

    private void DetachCounters()
    {
        if (_counted?.ItemsSource is INotifyCollectionChanged feed)
        {
            feed.CollectionChanged -= OnCountedNotification;
        }

        _counted = null;
    }

    /// <summary>
    /// Runs <paramref name="work"/> with a <c>ContainerContentChanging</c> subscriber attached and
    /// reports how many containers it prepared. Counts come from their own pass so that no reported
    /// duration is measured with this subscriber attached.
    /// </summary>
    private (int Realized, double Watched) Realizations(Action work)
    {
        _realizations = 0;
        if (_counted is null)
        {
            work();
            return (0, 0);
        }

        _counted.ContainerContentChanging += OnCountedRealization;
        (double mutation, double layout) = Time(work);
        _counted.ContainerContentChanging -= OnCountedRealization;
        return (_realizations, mutation + layout);
    }

    private void OnCountedNotification(object? sender, NotifyCollectionChangedEventArgs e) =>
        _notifications++;

    private void OnCountedRealization(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue)
        {
            _realizations++;
        }
    }

    /// <summary>The view's current order, as the list holds it.</summary>
    private static List<object> Order(ListView? list) =>
        list?.ItemsSource is System.Collections.IList view ? view.Cast<object>().ToList() : new();

    /// <summary>
    /// How many notifications a reconcile that announced every moved row would have raised: one
    /// removal and one insertion each. Specification 20 quotes both halves of that comparison, and
    /// the implementation that produced the first half is gone, so it is counted from the order the
    /// operation actually changed rather than measured.
    /// </summary>
    private static int PerRowNotifications(IReadOnlyList<object> before, ListView? list)
    {
        List<object> after = Order(list);
        int moved = 0;
        for (int i = 0; i < before.Count && i < after.Count; i++)
        {
            if (!ReferenceEquals(before[i], after[i]))
            {
                moved++;
            }
        }

        return moved * 2;
    }

    /// <summary>A scroll offset in rows needs the height of one, and nothing publishes it.</summary>
    private static double RowHeight(ListView list, Panel panel)
    {
        foreach (UIElement child in panel.Children)
        {
            if (child is FrameworkElement row && list.IndexFromContainer(child) >= 0
                && row.ActualHeight > 0)
            {
                return row.ActualHeight;
            }
        }

        return 40;
    }

    private void SizeWindow()
    {
        try
        {
            MainWindow? w = MainWindow.Instance;
            if (w is null)
            {
                W("MainWindow.Instance is null; window not resized");
                return;
            }

            w.AppWindow.ResizeClient(new Windows.Graphics.SizeInt32(
                (int)Math.Ceiling(1400 * _scale), (int)Math.Ceiling(820 * _scale)));
            w.Activate();
        }
        catch (Exception ex)
        {
            W("resize failed: " + ex.Message);
        }
    }

    private async Task Settle(int ms)
    {
        UpdateLayout();
        await Task.Delay(ms);
        UpdateLayout();
        await Task.Delay(60);
    }

    private void ReportRowOrigin(List<ListViewItem> containers)
    {
        if (containers.Count == 0)
        {
            W("no realized container to measure");
            return;
        }

        ListViewItem item = containers[0];
        CellsPanel? panel = FindDescendant<CellsPanel>(item);
        W($"container[0] width={item.ActualWidth:0.##} height={item.ActualHeight:0.##} " +
          $"padding={item.Padding} x-in-table={XOf(item):0.##}");
        if (panel is not null)
        {
            W($"row cells panel x-in-container={XOf(panel, item):0.##} " +
              $"width={panel.ActualWidth:0.##} children={panel.Children.Count}");
        }
    }

    /// <summary>
    /// Whether the rows are evenly spaced. Uneven vertical gaps were reported on this page, and a
    /// row band has two parts that can produce them independently: the container's own height,
    /// which the tallest cell in it decides, and the gap the panel leaves between one container and
    /// the next. Reporting the distinct values of each says which of the two it is, or that neither
    /// moved.
    /// </summary>
    private void ReportRowPitch(ListView? list, List<ListViewItem> containers)
    {
        if (list is null || containers.Count < 2)
        {
            W("  fewer than two realized containers to compare");
            return;
        }

        // Only the containers the list currently places. A recycled one is still in the visual tree
        // and still a ListViewItem, holding no row and measuring the container's 40 DIP minimum
        // instead of the 44 its cells ask for. Counting those reported two row heights and gaps of
        // minus forty — a table that is laid out correctly, described as broken.
        List<(double Top, double Height)> bands = containers
            .Where(item => list.IndexFromContainer(item) >= 0)
            .Select(item => (Top: YOf(item, list), item.ActualHeight))
            .Where(band => !double.IsNaN(band.Top) && band.ActualHeight > 0)
            .OrderBy(band => band.Top)
            .ToList();

        // The question is about the rows a person sees, so the ones the panel has placed outside
        // the viewport are counted and then left out. A container still sitting at the offset it
        // held before a scroll is a gap of tens of thousands of DIPs to nothing anybody can see,
        // and averaging it in with the visible run answers a question nobody asked.
        List<(double Top, double Height)> shown = bands
            .Where(band => band.Top > -band.Height && band.Top < list.ActualHeight)
            .ToList();

        List<double> gaps = new();
        for (int i = 1; i < shown.Count; i++)
        {
            gaps.Add(Math.Round(shown[i].Top - (shown[i - 1].Top + shown[i - 1].Height), 3));
        }

        W($"  {shown.Count} containers inside the viewport, " +
          $"{bands.Count - shown.Count} placed outside it");
        W($"  row heights: {Distinct(shown.Select(b => b.Height))}");
        W($"  gaps between them: {Distinct(gaps)}");
        W($"  pitch (top to top): {Distinct(Pitches(shown))}");
    }

    private static IEnumerable<double> Pitches(List<(double Top, double Height)> bands)
    {
        for (int i = 1; i < bands.Count; i++)
        {
            yield return Math.Round(bands[i].Top - bands[i - 1].Top, 3);
        }
    }

    /// <summary>Every distinct value with how many times it occurred. One entry is uniform.</summary>
    private static string Distinct(IEnumerable<double> values) => string.Join(
        ", ",
        values
            .GroupBy(v => Math.Round(v, 3))
            .OrderBy(g => g.Key)
            .Select(g => $"{g.Key:0.###} x{g.Count()}"));

    private void ReportBoundaries(string label, List<Cell> header, List<ListViewItem> containers)
    {
        if (header.Count == 0)
        {
            W("no header cells");
            return;
        }

        double[] headerX = header.Select(c => XOf(c)).ToArray();
        W($"{label}: header cell left edges (DIP, relative to Table)");
        for (int i = 0; i < header.Count; i++)
        {
            TextBlock? text = FindDescendant<TextBlock>(header[i]);
            string where = text is null ? "no label" : $"label x={XOf(text):0.##}";
            W($"  header[{i}] '{AutomationProperties.GetName(header[i])}' x={headerX[i]:0.##} " +
              $"w={header[i].ActualWidth:0.##} padding={header[i].Padding} {where}");
        }

        int measured = 0;
        foreach (ListViewItem item in containers)
        {
            CellsPanel? panel = FindDescendant<CellsPanel>(item);
            if (panel is null || panel.Children.Count == 0)
            {
                continue;
            }

            List<double> deltas = new();
            for (int i = 0; i < panel.Children.Count && i < headerX.Length; i++)
            {
                if (panel.Children[i] is FrameworkElement cell)
                {
                    deltas.Add(XOf(cell) - headerX[i]);
                }
            }

            TextBlock? firstText = FindDescendant<TextBlock>(panel);
            W($"  row container {measured}: cell-vs-header delta = [" +
              string.Join(", ", deltas.Select(d => d.ToString("0.###", CultureInfo.InvariantCulture))) + "]" +
              (firstText is null ? "" : $"  first cell text x={XOf(firstText):0.##}"));

            if (++measured == 3)
            {
                break;
            }
        }

        if (measured == 0)
        {
            W("  no row cell panel found to compare");
        }
    }

    private async Task SavePngAsync(string path)
    {
        try
        {
            RenderTargetBitmap rtb = new();
            await rtb.RenderAsync(Table);
            IBuffer buffer = await rtb.GetPixelsAsync();
            byte[] px = new byte[buffer.Length];
            DataReader.FromBuffer(buffer).ReadBytes(px);

            InMemoryRandomAccessStream stream = new();
            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                (uint)rtb.PixelWidth,
                (uint)rtb.PixelHeight,
                96 * _scale,
                96 * _scale,
                px);
            await encoder.FlushAsync();

            byte[] file = new byte[stream.Size];
            DataReader reader = new(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(file);
            File.WriteAllBytes(path, file);
            W($"wrote {path} ({file.Length} bytes, {rtb.PixelWidth}x{rtb.PixelHeight})");
        }
        catch (Exception ex)
        {
            W($"png {path} failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task ReportBitmapAsync()
    {
        try
        {
            RenderTargetBitmap rtb = new();
            await rtb.RenderAsync(Table);
            IBuffer buffer = await rtb.GetPixelsAsync();
            byte[] px = new byte[buffer.Length];
            DataReader.FromBuffer(buffer).ReadBytes(px);
            int w = rtb.PixelWidth;
            int h = rtb.PixelHeight;
            W($"bitmap {w}x{h} px");

            if (w == 0 || h == 0)
            {
                W("bitmap is empty");
                return;
            }

            Dictionary<uint, int> histogram = new();
            for (int i = 0; i + 3 < px.Length; i += 4)
            {
                uint key = (uint)(px[i] | (px[i + 1] << 8) | (px[i + 2] << 16));
                histogram[key] = histogram.TryGetValue(key, out int n) ? n + 1 : 1;
            }

            W($"distinct colours = {histogram.Count}");
            foreach (KeyValuePair<uint, int> entry in histogram.OrderByDescending(p => p.Value).Take(5))
            {
                W($"  #{entry.Key:X6} x{entry.Value}");
            }

            int opaque = 0;
            for (int i = 3; i < px.Length; i += 4)
            {
                if (px[i] != 0)
                {
                    opaque++;
                }
            }

            W($"pixels with alpha > 0 = {opaque} of {w * h}");

            for (int y = 8; y < h && y < (int)(200 * _scale); y += (int)(10 * _scale))
            {
                W($"  scanline y={y}: " + Runs(px, w, y));
            }
        }
        catch (Exception ex)
        {
            W("RenderTargetBitmap failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static string Runs(byte[] px, int w, int y)
    {
        if (y < 0 || (y + 1) * w * 4 > px.Length)
        {
            return "(row out of range)";
        }

        StringBuilder sb = new();
        uint last = 0xFFFFFFFF;
        int runs = 0;
        for (int x = 0; x < w; x++)
        {
            int i = ((y * w) + x) * 4;
            uint key = (uint)(px[i] | (px[i + 1] << 8) | (px[i + 2] << 16));
            if (key != last)
            {
                if (runs < 12)
                {
                    sb.Append($"x={x}:#{key:X6} ");
                }

                runs++;
                last = key;
            }
        }

        sb.Append($"(total runs {runs})");
        return sb.ToString();
    }

    /// <summary>
    /// Enumerate every platform resource whose value is a Thickness and whose key mentions a
    /// list, item, header, or cell. This answers whether the platform already publishes a padding
    /// the header cell could adopt instead of inventing one.
    /// </summary>
    private void ProbeResources()
    {
        List<string> hits = new();
        int scanned = 0;

        void Scan(ResourceDictionary dictionary, string origin)
        {
            try
            {
                foreach (KeyValuePair<object, object> entry in dictionary)
                {
                    scanned++;
                    if (entry.Key is not string key || entry.Value is not Thickness thickness)
                    {
                        continue;
                    }

                    string lower = key.ToLowerInvariant();
                    if (lower.Contains("list") || lower.Contains("item") || lower.Contains("header")
                        || lower.Contains("cell") || lower.Contains("grid"))
                    {
                        hits.Add($"  {key,-44} = {thickness}   [{origin}]");
                    }
                }
            }
            catch (Exception ex)
            {
                W($"  scanning {origin} threw {ex.GetType().Name}");
            }

            foreach (ResourceDictionary merged in dictionary.MergedDictionaries)
            {
                Scan(merged, origin + "/merged");
            }

            foreach (KeyValuePair<object, object> theme in dictionary.ThemeDictionaries)
            {
                if (theme.Value is ResourceDictionary themed)
                {
                    Scan(themed, origin + "/" + theme.Key);
                }
            }
        }

        Scan(Application.Current.Resources, "app");
        W($"scanned {scanned} resource entries; Thickness keys mentioning list/item/header/cell/grid:");
        foreach (string hit in hits.Distinct().OrderBy(h => h, StringComparer.Ordinal))
        {
            W(hit);
        }

        if (hits.Count == 0)
        {
            W("  (none)");
        }
    }

    // ------------------------------------------------------------- utilities

    private double XOf(FrameworkElement element) => XOf(element, Table);

    private static double XOf(FrameworkElement element, UIElement relativeTo) =>
        OriginOf(element, relativeTo).X;

    private static double YOf(FrameworkElement element, UIElement relativeTo) =>
        OriginOf(element, relativeTo).Y;

    private static Point OriginOf(FrameworkElement element, UIElement relativeTo)
    {
        try
        {
            return element.TransformToVisual(relativeTo).TransformPoint(new Point(0, 0));
        }
        catch
        {
            return new Point(double.NaN, double.NaN);
        }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T t)
            {
                return t;
            }

            T? deeper = FindDescendant<T>(child);
            if (deeper is not null)
            {
                return deeper;
            }
        }

        return null;
    }

    private static void FindAll<T>(DependencyObject root, List<T> into) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T t)
            {
                into.Add(t);
            }

            FindAll(child, into);
        }
    }
}
