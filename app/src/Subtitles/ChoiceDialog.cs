using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
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
    private readonly ListView _results = new()
    {
        DisplayMemberPath = nameof(SubtitleCandidate.Label),
        MaxHeight = 300,
        SelectionMode = ListViewSelectionMode.Single,
    };
    private readonly TextBlock _state = new() { TextWrapping = TextWrapping.Wrap };
    private readonly InfoBar _error = new()
    {
        Severity = InfoBarSeverity.Error,
        IsClosable = false,
    };
    private CancellationTokenSource? _request;
    private Exception? _failure;
    private bool _searching;
    private Strings Text { get; }
    public bool IsPending { get; private set; }
    public bool CanSubmit => !IsPending && HasDraft;
    public bool HasDraft => _results.SelectedItem is SubtitleCandidate;
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
                .Select(tag => new Choice(tag, text.LanguageName(tag)))
                .ToArray(),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            // One wanted language needs no choice.
            Visibility = languages.Length > 1 ? Visibility.Visible : Visibility.Collapsed,
        };
        AutomationProperties.SetAutomationId(_language, "SubtitleChoiceLanguage");
        AutomationProperties.SetAutomationId(_results, "SubtitleChoices");
        Content = new StackPanel
        {
            Spacing = 12,
            MinWidth = 380,
            Children = { _language, _state, _results, _error },
        };
        _language.SelectionChanged += async (_, _) => await Find();
        _results.SelectionChanged += (_, _) =>
            PropertyChanged?.Invoke(this, new(nameof(CanSubmit)));
    }

    internal void RefreshText()
    {
        _language.Header = Text.Get("subtitles", "language");
        AutomationProperties.SetName(_language, Text.Get("subtitles", "language"));
        AutomationProperties.SetName(_results, Text.Get("subtitles", "candidates"));
        _state.Text =
            _searching ? Text.Get("subtitles", "searching")
            : _failure is null
            && _results.ItemsSource is IReadOnlyList<SubtitleCandidate> { Count: 0 }
                ? Text.Get("subtitles", "no_candidates")
            : string.Empty;
        _state.Visibility = _state.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _error.Message = _failure is null ? string.Empty : Text.Error(_failure);
        _error.IsOpen = _failure is not null;
    }

    internal async Task Find()
    {
        if (_language.SelectedItem is not Choice language)
            return;
        Cancel();
        using var request = new CancellationTokenSource();
        _request = request;
        _results.ItemsSource = null;
        _failure = null;
        _searching = true;
        RefreshText();
        try
        {
            var candidates = await _model.Candidates(_entryId, language.Tag, request.Token);
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
                _request = null;
                _searching = false;
                RefreshText();
            }
        }
    }

    public async Task<bool> Submit()
    {
        if (_results.SelectedItem is not SubtitleCandidate candidate || IsPending)
            return false;
        Cancel();
        using var request = new CancellationTokenSource();
        _request = request;
        IsPending = true;
        _language.IsEnabled = _results.IsEnabled = false;
        PropertyChanged?.Invoke(this, new(nameof(CanSubmit)));
        try
        {
            await _model.Choose(_entryId, candidate, request.Token);
            return true;
        }
        catch (Exception error)
        {
            if (!request.IsCancellationRequested)
                _failure = Explain(error);
            RefreshText();
            return false;
        }
        finally
        {
            IsPending = false;
            _language.IsEnabled = _results.IsEnabled = true;
            if (ReferenceEquals(_request, request))
                _request = null;
            PropertyChanged?.Invoke(this, new(nameof(CanSubmit)));
        }
    }

    internal void Cancel() => _request?.Cancel();

    // A cancellation the dialog did not start means a settings change or the
    // video leaving its torrent stopped the request.
    private static Exception Explain(Exception error) =>
        error is OperationCanceledException
            ? new SubtitleException(SubtitleFailure.Changed)
            : error;

    private sealed record Choice(string Tag, string Name)
    {
        public override string ToString() => Name;
    }
}
