using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;
using Syno.TinyTorrent.Views;

namespace Syno.TinyTorrent.Library;

internal sealed class MetadataDialog : UserControl, IDraft
{
    private readonly Browser _library;
    private readonly Entry _entry;
    private readonly IReadOnlyList<ProviderOption> _sources;
    private readonly ComboBox _source;
    private readonly HyperlinkButton _settings = new();
    private readonly AutoSuggestBox _input;
    private readonly ListView _results = new()
    {
        DisplayMemberPath = "Label",
        MaxHeight = 300,
        SelectionMode = ListViewSelectionMode.Single,
    };
    private readonly InfoBar _error = new()
    {
        Severity = InfoBarSeverity.Error,
        IsClosable = false,
    };
    private readonly ProgressBar _progress = new() { Opacity = 0 };
    private readonly TextBlock _empty = new()
    {
        Visibility = Visibility.Collapsed,
        TextWrapping = TextWrapping.Wrap,
    };
    private CancellationTokenSource? _request;
    private Exception? _failure;
    public bool IsPending { get; private set; }
    public bool CanSubmit => !IsPending && HasDraft;
    public bool HasDraft => _results.SelectedItem is VideoChoice;
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Raised when the person follows the error's Settings link. The owner
    /// closes the dialog and opens Settings at the target.
    /// </summary>
    internal event EventHandler<SettingTarget>? SettingsRequested;

    internal MetadataDialog(Browser library, Entry entry, ProviderOption source)
    {
        _library = library;
        _entry = entry;
        // The source applies to this dialog only and starts at the one the person chose.
        _sources = library.Providers;
        _source = new ComboBox
        {
            ItemsSource = _sources.Select(option => option.Label(library.Text)).ToArray(),
            SelectedIndex = _sources.ToList().IndexOf(source),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _input = new AutoSuggestBox
        {
            Text = FileName.Interpret(entry.Name).Title,
            QueryIcon = new SymbolIcon(Symbol.Find),
        };
        _error.ActionButton = _settings;
        AutomationProperties.SetAutomationId(_source, "MetadataSource");
        AutomationProperties.SetAutomationId(_input, "MetadataSearch");
        AutomationProperties.SetAutomationId(_results, "MetadataMatches");
        AutomationProperties.SetLiveSetting(_empty, AutomationLiveSetting.Polite);
        AutomationProperties.SetAccessibilityView(
            _progress,
            Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw
        );
        var search = new StackPanel { Spacing = 4, Children = { _input, _progress } };
        Content = new StackPanel
        {
            Spacing = 12,
            MinWidth = 380,
            Children = { _source, search, _results, _empty, _error },
        };
        _source.SelectionChanged += async (_, _) => await Find();
        _settings.Click += (_, _) => SettingsRequested?.Invoke(this, new(SettingsCategory.Network));
        _input.QuerySubmitted += async (_, _) => await Find();
        _input.TextChanged += (_, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
                return;
            Cancel();
            _empty.Visibility = Visibility.Collapsed;
            _results.ItemsSource = null;
        };
        _results.SelectionChanged += (_, _) =>
            PropertyChanged?.Invoke(this, new(nameof(CanSubmit)));
    }

    internal void RefreshText()
    {
        _source.Header = _library.Text.Get("library", "provider");
        AutomationProperties.SetName(_source, _library.Text.Get("library", "provider"));
        _settings.Content = _library.Text.Get("commands", "settings");
        // Only a route failure has a fix in Settings; other failures need
        // another source or a later try.
        _settings.Visibility =
            _failure is RouteException ? Visibility.Visible : Visibility.Collapsed;
        _input.PlaceholderText = _library.Text.Get("library", "search_title");
        AutomationProperties.SetName(_input, _input.PlaceholderText);
        AutomationProperties.SetName(_results, _library.Text.Get("library", "matches"));
        _error.Message = _failure is null ? string.Empty : _library.Error(_failure);
        _error.IsOpen = _failure is not null;
        _empty.Text = _library.Text.Get("library", "no_metadata_matches");
    }

    private ProviderOption? Source =>
        _source.SelectedIndex >= 0 ? _sources[_source.SelectedIndex] : null;

    internal async Task Find()
    {
        if (Source is not { } source)
            return;
        Cancel();
        _empty.Visibility = Visibility.Collapsed;
        using var request = new CancellationTokenSource();
        Track(request);
        _results.ItemsSource = null;
        Report(null);
        try
        {
            var choices = await _library.FindVideo(_input.Text, source, request.Token);
            if (!request.IsCancellationRequested)
            {
                _results.ItemsSource = choices;
                _empty.Visibility = choices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                if (choices.Count == 0)
                    FrameworkElementAutomationPeer.CreatePeerForElement(_empty)
                        ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!request.IsCancellationRequested)
                Report(error);
        }
        finally
        {
            if (ReferenceEquals(_request, request))
                Track(null);
        }
    }

    public async Task<bool> Submit()
    {
        if (
            _results.SelectedItem is not VideoChoice choice
            || Source is not { } source
            || IsPending
        )
            return false;
        Cancel();
        using var request = new CancellationTokenSource();
        Track(request);
        IsPending = true;
        _source.IsEnabled = _input.IsEnabled = _results.IsEnabled = false;
        PropertyChanged?.Invoke(this, new(nameof(CanSubmit)));
        try
        {
            await _library.SaveMetadata(_entry, choice, source, request.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception error)
        {
            if (!request.IsCancellationRequested)
                Report(error);
            return false;
        }
        finally
        {
            IsPending = false;
            _source.IsEnabled = _input.IsEnabled = _results.IsEnabled = true;
            if (ReferenceEquals(_request, request))
                Track(null);
            PropertyChanged?.Invoke(this, new(nameof(CanSubmit)));
        }
    }

    private void Report(Exception? error)
    {
        _failure = error;
        RefreshText();
    }

    private void Track(CancellationTokenSource? request)
    {
        _request = request;
        _progress.IsIndeterminate = request is not null;
        // Opacity, not Visibility, keeps the bar's space while idle, so the
        // list and buttons stay put when a request starts.
        _progress.Opacity = request is null ? 0 : 1;
    }

    internal void Cancel() => _request?.Cancel();
}
