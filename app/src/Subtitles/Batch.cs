using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Subtitles;

internal sealed class Batch : UserControl, INotifyPropertyChanged
{
    private readonly Strings _text;
    private readonly Action<Exception, string> _report;
    private readonly IReadOnlySet<long>? _entries;
    private readonly ComboBox _scope = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _download = new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };
    private readonly TextBlock _status = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private Acquisition? _model;
    private string? _language;
    private bool _busy;
    private bool _refreshing;
    private long? _batchId;
    private int _count;
    private int _recount;
    private SettingsSection? _section;
    private SettingsRow? _scopeRow;
    private SettingsRow? _downloadRow;
    internal string Coverage
    {
        set
        {
            if (_section is not null)
                _section.Description = value;
        }
    }

    // A batch runs from Find until its manual jobs finish, so manual jobs kept
    // from an earlier session count as one.
    public bool Finding => _busy || _model?.Finding == true;

    // Each host draws one button for the batch: Find while none runs and Stop
    // while one does, so the pressed button stays enabled and keeps focus.
    public bool CanAct => Finding ? _model is not null : _model is { CanFind: true } && _count > 0;
    internal string ActionGlyph => Finding ? Lucide.Square : Lucide.Search;
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
        _report = report;
        _entries = entries;
        AutomationProperties.SetAutomationId(_scope, "SubtitleScope");
        AutomationProperties.SetAutomationId(_download, "SubtitleDownload");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
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
        if (_model == model)
            return;
        if (_model is not null)
            _model.Changed -= OnChanged;
        _model = model;
        if (_model is not null)
            _model.Changed += OnChanged;
        Refresh();
    }

    private void OnChanged(object? sender, EventArgs args) => DispatcherQueue.TryEnqueue(Refresh);

    internal void ShowSettings(ActionButton action)
    {
        ((Grid)((StackPanel)Content).Children[0]).Children.Clear();
        ((StackPanel)Content).Children.Clear();
        _scope.Width = _download.Width = 260;
        var choices = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        choices.Children.Add(_download);
        choices.Children.Add(action);
        _scopeRow = new SettingsRow { Content = _scope, Detail = _status };
        _downloadRow = new SettingsRow { Content = choices };
        _status.MinHeight = 20;
        _status.Style = (Style)Application.Current.Resources["TinyTorrentTitleDetailTextStyle"];
        _section = new SettingsSection
        {
            Style = (Style)Application.Current.Resources["TinyTorrentSettingsGroupStyle"],
            Content = new StackPanel { Children = { _scopeRow, _downloadRow } },
        };
        Content = _section;
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
        Present();
        _ = Recount();
    }

    // The count is a database read, so the text shows at once and the
    // number follows; a newer Refresh discards an older read's result.
    private async Task Recount()
    {
        var version = ++_recount;
        var count = _model is { } model
            ? await model.Count((SubtitleScope)Math.Max(0, _scope.SelectedIndex), _entries)
            : 0;
        if (version != _recount || count == _count)
            return;
        _count = count;
        Present();
    }

    private void Present()
    {
        _refreshing = true;
        var scope = Math.Max(0, _scope.SelectedIndex);
        var download = _download.SelectedIndex < 0 ? 2 : _download.SelectedIndex;
        if (_scopeRow is not null && _downloadRow is not null && _section is not null)
        {
            _scope.Header = _download.Header = null;
            _section.Header = T("library_search");
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
            _scope.ItemsSource = new[] { T("not_searched"), T("no_exact"), T("all_movies") };
            _download.ItemsSource = new[]
            {
                T("exact_only"),
                T("best_match"),
                _text.Format("subtitles", "up_to", 2),
                _text.Format("subtitles", "up_to", 3),
                _text.Format("subtitles", "up_to", 4),
            };
            _scope.SelectedIndex = scope;
            _download.SelectedIndex = download;
        }
        AutomationProperties.SetName(_scope, T("movies_to_search"));
        AutomationProperties.SetName(_download, T("matches_to_download"));
        _scope.IsEnabled = _download.IsEnabled = !_busy && _model?.Finding != true;
        _status.Text =
            _model is { Finding: true } finding ? Waiting(finding)
            : _model is { Found: { } found } completed && _batchId == completed.BatchId
                ? _text.Format("subtitles", "batch_found", found, completed.FindTotal)
            : _text.Format("subtitles", "to_search", _count);
        ToolTipService.SetToolTip(_status, _status.Text);
        _refreshing = false;
        PropertyChanged?.Invoke(this, new(nameof(CanAct)));
    }

    // A batch that waits for the supplier, the folder or the database names
    // the reason, so the wait is not mistaken for a search in progress.
    private string Waiting(Acquisition model) =>
        model.Failure switch
        {
            SubtitleFailure.None => T("searching"),
            SubtitleFailure.Quota when model.RetryAt is { } reset => _text.Format(
                "subtitles",
                "retry_at",
                _text.Time(reset)
            ),
            var failure => T("failure_" + failure.ToString().ToLowerInvariant()),
        };

    internal Task Act() => Finding ? Stop() : Find();

    private async Task Find()
    {
        if (!CanAct || Finding || _model is not { } model)
            return;
        var scope = (SubtitleScope)_scope.SelectedIndex;
        var guesses = _download.SelectedIndex;
        _busy = true;
        Refresh();
        try
        {
            await model.Find(scope, guesses, _entries);
            _batchId = model.BatchId;
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            _report(error, "failed_subtitle_find");
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    private async Task Stop()
    {
        if (_model is not { } model)
            return;
        try
        {
            await model.StopFinding();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            _report(error, "failed_subtitle_stop");
        }
        finally
        {
            Refresh();
        }
    }
}
