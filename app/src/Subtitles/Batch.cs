using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Shared;

namespace Syno.TinyTorrent.Subtitles;

internal sealed class Batch : UserControl, INotifyPropertyChanged
{
    private readonly Strings _text;
    private readonly Bridge _bridge;
    private readonly IReadOnlySet<long>? _entries;
    private readonly ComboBox _scope = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _download = new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };
    private readonly TextBlock _status = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private string? _language;
    private bool _busy;
    private bool _refreshing;
    private long? _batchId;
    private int[] _counts = [0, 0, 0];
    private int Count => _counts[Math.Max(0, _scope.SelectedIndex)];
    private int _recount;
    private bool _reading;
    private SettingsRow? _coverage;
    private SettingsRow? _scopeRow;
    private SettingsRow? _downloadRow;
    internal string Coverage
    {
        set
        {
            if (_coverage is not null)
                _coverage.Header = value;
        }
    }

    // A batch runs from Find until its manual jobs finish, so manual jobs kept
    // from an earlier session count as one.
    public bool Finding => _busy || _bridge.Model?.Finding == true;

    // Each host draws one button for the batch: Find while none runs and Stop
    // while one does, so the pressed button stays enabled and keeps focus.
    public bool CanAct =>
        Finding ? _bridge.Model is not null : _bridge.Model is { CanFind: true } && Count > 0;
    internal string ActionGlyph => Finding ? Segoe.Square : Segoe.Search;
    internal string ActionText => T(Finding ? "stop" : "find");
    internal string ActionHint => T(Finding ? "stop_hint" : "batch_hint");
    public event PropertyChangedEventHandler? PropertyChanged;

    internal Batch(
        Strings text,
        Action<Exception, string> report,
        IReadOnlySet<long>? entries = null
    )
    {
        _text = text;
        _bridge = new Bridge(DispatcherQueue, Refresh, report);
        _entries = entries;
        AutomationProperties.SetAutomationId(_scope, "SubtitleScope");
        AutomationProperties.SetAutomationId(_download, "SubtitleDownload");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        Trimming.SetShowsToolTip(_status, true);
        for (var index = 0; index < _counts.Length; index++)
            _scope.Items.Add(new ComboBoxItem());
        _scope.SelectedIndex = 0;
        var choices = new Grid { ColumnSpacing = 12 };
        choices.ColumnDefinitions.Add(new ColumnDefinition());
        choices.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(_download, 1);
        choices.Children.Add(_scope);
        choices.Children.Add(_download);
        Content = new StackPanel { Spacing = 8, Children = { choices, _status } };
        _scope.SelectionChanged += OnChoice;
        _download.SelectionChanged += OnChoice;
        Refresh();
    }

    internal void Attach(Acquisition? model)
    {
        if (!_bridge.Attach(model))
        {
            return;
        }
        _counts = [0, 0, 0];
        _batchId = null;
        Refresh();
    }

    internal void SetVisible(bool visible)
    {
        if (_bridge.SetVisible(visible))
        {
            Refresh();
        }
    }

    internal void ShowSettings(ActionButton action)
    {
        ((Grid)((StackPanel)Content).Children[0]).Children.Clear();
        ((StackPanel)Content).Children.Clear();
        _scope.Width = _download.Width = 260;
        var choices = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        choices.Children.Add(_download);
        choices.Children.Add(action);
        _scopeRow = new SettingsRow { Content = _scope };
        _downloadRow = new SettingsRow { Content = choices, BorderThickness = new Thickness(0) };
        _coverage = new SettingsRow { BorderThickness = new Thickness(0) };
        Content = new StackPanel
        {
            Children = { _coverage, _scopeRow, _downloadRow },
        };
        Present();
    }

    private void OnChoice(object sender, SelectionChangedEventArgs args)
    {
        if (_refreshing)
            return;
        _batchId = null;
        Refresh();
    }

    private string T(string key) => _text.Get("subtitles", key);

    internal void Refresh()
    {
        _recount++;
        if (!_bridge.IsVisible)
            return;
        Present();
        if (!_reading)
            _ = Recount();
    }

    private async Task Recount()
    {
        _reading = true;
        try
        {
            while (_bridge.IsVisible && _bridge.Model is { } model)
            {
                var version = _recount;
                var counts = new int[3];
                for (var scope = 0; scope < counts.Length && version == _recount; scope++)
                    counts[scope] = await model.Count((SubtitleScope)scope, _entries);
                if (version != _recount)
                    continue;
                _counts = counts;
                Present();
                break;
            }
        }
        finally
        {
            _reading = false;
        }
    }

    private void Present()
    {
        _refreshing = true;
        var download = _download.SelectedIndex < 0 ? 2 : _download.SelectedIndex;
        if (_scopeRow is not null && _downloadRow is not null)
        {
            _scope.Header = _download.Header = null;
            _scopeRow.Header = T("movies_to_search");
            _downloadRow.Header = T("matches_to_download");
            _downloadRow.Description = T("matches_hint");
        }
        else
        {
            _scope.Header = T("movies");
            _download.Header = T("download");
        }
        if (_language != _text.Language)
        {
            _language = _text.Language;
            // A choice's index is the guesses Find takes, so the last choice
            // is the best match and every extra one.
            var upTo = Enumerable
                .Range(2, Acquisition.MaximumExtras)
                .Select(count => _text.Format("subtitles", "up_to", count));
            _download.ItemsSource = new[] { T("exact_only"), T("best_match") }.Concat(upTo).ToArray();
            _download.SelectedIndex = download;
        }
        var keys = new[] { "new", "missing_data", "all" };
        for (var index = 0; index < keys.Length; index++)
            ((ComboBoxItem)_scope.Items[index]).Content = _text.Format(
                "filters",
                "count",
                _text.Get("request_protection", keys[index]),
                _counts[index]
            );
        AutomationProperties.SetName(_scope, T("movies_to_search"));
        AutomationProperties.SetName(_download, T("matches_to_download"));
        _scope.IsEnabled = _download.IsEnabled = !_busy && _bridge.Model?.Finding != true;
        _status.Text =
            _bridge.Model is { Finding: true } finding ? Waiting(finding)
            : _bridge.Model is { Found: { } found } completed && _batchId == completed.BatchId
                ? _text.Format("subtitles", "batch_found", found, completed.FindTotal)
            : _text.Format("subtitles", "to_search", Count);
        if (_scopeRow is not null)
            _scopeRow.Description = _status.Text;
        _refreshing = false;
        PropertyChanged?.Invoke(this, new(nameof(CanAct)));
    }

    // A batch that waits for the supplier, the folder or the database names
    // the reason, so the wait is not mistaken for a search in progress.
    private string Waiting(Acquisition model) =>
        model.Failure switch
        {
            SubtitleFailure.None when model.Blocked > 0 => T("blocked"),
            SubtitleFailure.None => T("searching"),
            SubtitleFailure.Quota when model.RetryAt is { } reset => _text.Format(
                "subtitles",
                "retry_at",
                _text.Time(reset)
            ),
            var failure => SubtitleException.Describe(_text, failure),
        };

    internal Task Act() => Finding ? Stop() : Find();

    private async Task Find()
    {
        if (!CanAct || Finding)
        {
            return;
        }
        var scope = (SubtitleScope)_scope.SelectedIndex;
        var guesses = _download.SelectedIndex;
        _busy = true;
        await _bridge.Run(
            async model =>
            {
                try
                {
                    await model.Find(scope, guesses, _entries);
                    _batchId = model.BatchId;
                }
                finally
                {
                    _busy = false;
                }
            },
            "failed_subtitle_find"
        );
    }

    private Task Stop() => _bridge.Run(model => model.StopFinding(), "failed_subtitle_stop");
}
