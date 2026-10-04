using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Syno.TableViewSample.Jobs;

/// <summary>
/// The sample's first consumer: a render farm's job list over <see cref="Feed"/>, which
/// publishes a whole snapshot every second the way a polling host does. It is also the host the
/// measurement sections in the other half of this class drive, because a synthetic feed is the only
/// thing in the repository that can put twenty thousand changing rows through the control.
/// </summary>
public sealed partial class JobsPage : Page
{
    /// <summary>
    /// The size of the list every figure the specification quotes was taken on, so a default launch
    /// is directly comparable with sections 5.3, 9 and 20 rather than nearly comparable.
    /// </summary>
    private const int DefaultRowCount = 2002;

    /// <summary>Present only while an agent measures the control. A packaged launch inherits no
    /// arguments, so the switch is also a file; this project is unpackaged, so prefer the
    /// argument.</summary>
    private static readonly string MeasureFlagPath =
        System.IO.Path.Combine(AppContext.BaseDirectory, "sample-measure.flag");

    private readonly StringBuilder _log = new();
    private Feed? _feed;
    private double _scale = 1.0;
    private bool _finished;

    public JobsPage()
    {
        InitializeComponent();

        // The row type, stated once. A job the farm has not confirmed renders and nothing else:
        // it cannot be selected, invoked, context-clicked, or joined to a packet. Each column
        // joins to its sort key through the field the XAML compiler generates for x:Name, so
        // renaming a column is a build break.
        Table.Schema<Job>()
            .Key(row => row.Id)
            .CanInteract(row => !row.IsPending)
            .Sort(IndexColumn, row => row.Index)
            .Sort(NameColumn, row => row.Name)
            .Sort(StateColumn, row => (int)row.State)
            .Sort(ProgressColumn, row => row.Progress)
            .Sort(ThroughputColumn, row => row.ActiveRate)
            .Sort(WorkersColumn, row => row.WorkersBusy)
            .Sort(YieldColumn, row => row.Yield)
            .Sort(RemainingColumn, row => row.Remaining?.TotalSeconds ?? double.MaxValue)
            .Sort(SubmittedColumn, row => row.SubmittedText)
            .Sort(FinishedColumn, row => row.FinishedOrder);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        // The accent is a resource key the cell resolves, so a theme change alters what the key
        // names without changing the key. Nothing would ask for it again on its own.
        ActualThemeChanged += (_, _) => InvalidateStateAccents();
    }

    private void InvalidateStateAccents()
    {
        if (_feed is null)
        {
            return;
        }

        foreach (Job row in _feed.Rows)
        {
            row.InvalidateStateAccent();
        }
    }

    /// <summary>How many rows the feed holds. The diagnostics report counts against it.</summary>
    private int RowCount => _feed?.Rows.Count ?? 0;

    /// <summary>
    /// Snapshots handed to the table since the page loaded. The settling measurement needs it: a
    /// window in which nothing was published says nothing about how often the table reordered.
    /// </summary>
    private int Publishes { get; set; }

    private void W(string s) => _log.AppendLine(s);

    private void Section(string s)
    {
        // Flushed per section: a crash later in the pass must not cost the findings so far.
        Flush();
        W("");
        W("========================================================================");
        W(s);
        W("========================================================================");
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        _scale = XamlRoot?.RasterizationScale ?? 1.0;

        _feed = new Feed(DispatcherQueue, RequestedRowCount());
        _feed.Ticked += (_, _) => Publish();
        _feed.TickFailed += (_, error) => Status.Text = "the feed stopped: " + error.Message;

        // Closing the window does not always unload its content first, so the ticker is stopped
        // from both signals. Whichever arrives first stops it.
        if (MainWindow.Instance is Window window)
        {
            window.Closed += (_, _) => _feed?.Stop();
        }

        try
        {
            Publish();
        }
        catch (Exception ex)
        {
            Status.Text = "ItemsSource threw: " + ex.Message;
            W("ItemsSource assignment THREW " + ex);
        }

        _feed.Start();

        if (!MeasuringThisLaunch())
        {
            return;
        }

        DispatcherQueueTimer watchdog = DispatcherQueue.CreateTimer();
        watchdog.Interval = TimeSpan.FromMinutes(10);
        watchdog.IsRepeating = false;
        watchdog.Tick += (_, _) => { W("\n!!! WATCHDOG FIRED !!!"); Finish(); };
        watchdog.Start();

        try
        {
            await RunDiagnosticsAsync();
        }
        catch (Exception ex)
        {
            W("\n*** DIAGNOSTICS THREW: " + ex);
        }

        watchdog.Stop();
        Finish();
    }

    /// <summary>
    /// Stop the feed with the page. Its timer belongs to the dispatcher, not to this page, so
    /// nothing else stops it: it goes on ticking into a tree that is being taken apart.
    /// </summary>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Unloaded -= OnUnloaded;
        _feed?.Stop();
    }

    /// <summary>The host's publish: a new snapshot of the feed, which is what a poll produces.</summary>
    private void Publish()
    {
        Publishes++;

        List<Job> snapshot = _feed is null ? new() : new(_feed.Rows);
        Table.ItemsSource = snapshot;
        Status.Text = $"{snapshot.Count} rows, {Publishes} publishes";
    }

    /// <summary>
    /// The row count this launch asked for, from <c>--rows:20000</c>. That argument is how a person
    /// drives the table at scale by hand: the feed generates that many rows and ticks all of them.
    /// </summary>
    private static int RequestedRowCount()
    {
        foreach (string argument in Environment.GetCommandLineArgs())
        {
            if (argument.StartsWith("--rows:", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(argument[7..], out int rows) && rows > 0)
            {
                return rows;
            }
        }

        return DefaultRowCount;
    }

    /// <summary>
    /// Whether this launch is a measurement. <c>--measure</c> runs the whole pass;
    /// <c>--measure:K</c> or <c>--measure:H,J</c> runs those sections and leaves the rest out.
    /// </summary>
    /// <remarks>
    /// The file switch has a trap the argument does not: it belongs to the machine rather than to a
    /// launch, so one left behind turns the next launch into a measurement — the window resizes
    /// itself, the table sorts itself, and the app closes at the end, which reads exactly like a
    /// hang. Ask with the argument.
    /// </remarks>
    private bool MeasuringThisLaunch()
    {
        bool asked = File.Exists(MeasureFlagPath);

        foreach (string argument in Environment.GetCommandLineArgs())
        {
            if (!argument.StartsWith("--measure", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            asked = true;

            int colon = argument.IndexOf(':');
            if (colon >= 0)
            {
                _only = new HashSet<string>(
                    argument[(colon + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries),
                    StringComparer.OrdinalIgnoreCase);
            }
        }

        return asked;
    }

    private void OnDetailsClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Job row })
        {
            Status.Text = $"{RowCount} rows — last button: row {row.Index}";
        }
    }

    private void Flush()
    {
        try
        {
            File.WriteAllText(ResultsPath, _log.ToString());
        }
        catch
        {
            // the results file is diagnostics only
        }
    }

    private static readonly string ResultsPath = System.IO.Path.Combine(AppContext.BaseDirectory, "sample-results.txt");

    private void Finish()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        _feed?.Stop();
        Flush();

        try
        {
            File.WriteAllText(
                Path.Combine(AppContext.BaseDirectory, "sample-results.txt"), _log.ToString());
        }
        catch
        {
            // the copy beside the executable is a convenience
        }

        Application.Current.Exit();
    }
}
