using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;
using Syno.TinyTorrent.Views;

namespace Syno.TinyTorrent.Subtitles;

// Lists the supplier's subtitles for one video, for the person to choose one
// when automatic matching found none for the exact release.
internal sealed class ChoiceDialog : UserControl, IDraft
{
    private readonly Acquisition _model;
    private readonly long _entryId;
    private readonly ComboBox _language;
    private RetryCommand? _command;
    private RequestBlock? _block;
    private readonly ListView _results = new()
    {
        DisplayMemberPath = nameof(SubtitleCandidate.Label),
        MaxHeight = 300,
        SelectionMode = ListViewSelectionMode.Single,
    };
    private readonly TextBlock _state = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _description = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new();
    private readonly InfoBar _error = new()
    {
        Severity = InfoBarSeverity.Error,
        IsClosable = false,
    };
    private CancellationTokenSource? _request;
    private Exception? _failure;
    private Strings Text { get; }
    public bool IsPending { get; private set; }
    internal bool IsSearching => _request is not null && !IsPending;
    internal bool HasResults => !IsSearching && !TargetChanged
        && _results.ItemsSource is IReadOnlyList<SubtitleCandidate> { Count: > 0 };
    private bool TargetChanged => _failure is SubtitleException { Reason: SubtitleFailure.Changed };
    public bool CanSubmit => !IsPending && !IsSearching && HasDraft;
    public bool HasDraft => !TargetChanged && _results.SelectedItem is SubtitleCandidate;
    internal bool CanSearch => !IsSearching && !IsPending && !TargetChanged
        && _language.SelectedItem is ComboBoxItem && (_block is null || _block.CanForce);
    internal string SearchHint => _block is null ? Text.Get("subtitles", "search_again_hint") : Text.Protection(_block);
    public event PropertyChangedEventHandler? PropertyChanged;

    internal ChoiceDialog(Strings text, Acquisition model, long entryId)
    {
        Text = text;
        _model = model;
        _entryId = entryId;
        var languages = model
            .EffectiveLanguages.Where(language => model.Supplier?.Supports(language) == true)
            .ToArray();
        _language = new ComboBox
        {
            ItemsSource = languages
                .Select(tag => new ComboBoxItem { Tag = tag, Content = text.LanguageName(tag) })
                .ToArray(),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            // One wanted language needs no choice.
            Visibility = languages.Length > 1 ? Visibility.Visible : Visibility.Collapsed,
        };
        AutomationProperties.SetAutomationId(_language, "SubtitleChoiceLanguage");
        AutomationProperties.SetAutomationId(_results, "SubtitleChoices");
        AutomationProperties.SetLiveSetting(_state, AutomationLiveSetting.Polite);
        _state.Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"];
        Content = new StackPanel
        {
            Spacing = 12,
            Children = { _language, _state, _description, _progress, _results, _error },
        };
        _language.SelectionChanged += async (_, _) => await Find();
        _results.SelectionChanged += (_, _) =>
            PropertyChanged?.Invoke(this, new(nameof(CanSubmit)));
    }

    internal void RefreshText()
    {
        var busy = IsSearching || IsPending;
        var empty = !busy && _failure is null
            && _results.ItemsSource is IReadOnlyList<SubtitleCandidate> { Count: 0 };
        foreach (var item in _language.Items.OfType<ComboBoxItem>())
            item.Content = Text.LanguageName((string)item.Tag);
        _language.Header = Text.Get("subtitles", "language");
        AutomationProperties.SetName(_language, Text.Get("subtitles", "language"));
        AutomationProperties.SetName(_results, Text.Get("subtitles", "candidates"));
        _state.Text = IsPending ? Text.Get("subtitles", "downloading_choice")
            : IsSearching ? Text.Get("subtitles", "searching")
            : empty ? Text.Get("subtitles", "no_candidates")
            : HasResults ? Text.Get("subtitles", "select_candidate")
            : string.Empty;
        _state.Visibility = _state.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _description.Text = !busy && _failure is null && _block is { CanForce: false }
            ? Text.Protection(_block)
            : empty ? Text.Get("subtitles", _language.Items.Count > 1 ? "no_candidates_languages" : "no_candidates_hint")
            : string.Empty;
        _description.Visibility = _description.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _progress.IsIndeterminate = busy;
        _progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(_progress, _state.Text);
        _results.Visibility = HasResults ? Visibility.Visible : Visibility.Collapsed;
        _results.IsEnabled = !busy && !TargetChanged;
        _language.IsEnabled = !IsPending && !TargetChanged;
        _error.Message = _failure is null ? string.Empty : Text.Error(_failure);
        _error.IsOpen = _failure is not null;
        _error.Visibility = _failure is not null ? Visibility.Visible : Visibility.Collapsed;
        PropertyChanged?.Invoke(this, new(string.Empty));
    }

    internal async Task Find(bool user = false)
    {
        if (IsPending)
            return;
        if (_language.SelectedItem is not ComboBoxItem { Tag: string language })
        {
            _failure = new SubtitleException(SubtitleFailure.Unconfigured);
            RefreshText();
            return;
        }
        var command = user && _block?.CanForce == true ? new RetryCommand(DateTimeOffset.UtcNow.UtcTicks) : null;
        _command = command;
        Cancel();
        using var request = new CancellationTokenSource();
        _request = request;
        _results.ItemsSource = null;
        _failure = null;
        _block = null;
        RefreshText();
        try
        {
            if (!user)
            {
                var block = await _model.Protection(_entryId, language, request.Token);
                request.Token.ThrowIfCancellationRequested();
                _block = block;
            }
            var candidates = await _model.Candidates(_entryId, language, request.Token, command);
            if (!request.IsCancellationRequested)
                _results.ItemsSource = candidates;
        }
        catch (Exception error)
        {
            if (!request.IsCancellationRequested)
                _failure = Explain(error);
        }
        finally
        {
            if (ReferenceEquals(_request, request))
            {
                if (!request.IsCancellationRequested)
                {
                    try
                    {
                        var block = await _model.Protection(_entryId, language, request.Token);
                        if (!request.IsCancellationRequested)
                            _block = block;
                    }
                    catch (Exception error)
                    {
                        if (!request.IsCancellationRequested)
                            _failure = Explain(error);
                    }
                }
                if (ReferenceEquals(_request, request))
                {
                    _request = null;
                    RefreshText();
                }
            }
        }
    }

    public async Task<bool> Submit()
    {
        if (!CanSubmit || _results.SelectedItem is not SubtitleCandidate candidate)
            return false;
        Cancel();
        using var request = new CancellationTokenSource();
        _request = request;
        IsPending = true;
        _failure = null;
        RefreshText();
        var command = _command;
        _command = null;
        try
        {
            await _model.Choose(_entryId, candidate, request.Token, command);
            return true;
        }
        catch (Exception error)
        {
            if (!request.IsCancellationRequested)
            {
                if (error is RequestBlockedException { Block.Kind: not RequestBlockKind.Unusable })
                    _command = command;
                _failure = Explain(error);
                _block = (error as RequestBlockedException)?.Block ?? _block;
            }
            return false;
        }
        finally
        {
            IsPending = false;
            if (ReferenceEquals(_request, request))
                _request = null;
            RefreshText();
        }
    }

    internal void Cancel() => _request?.Cancel();

    // A cancellation the dialog did not start means a settings change or the
    // video leaving its torrent stopped the request.
    private static Exception Explain(Exception error) =>
        error is OperationCanceledException
            ? new SubtitleException(SubtitleFailure.Changed)
            : error;
}
