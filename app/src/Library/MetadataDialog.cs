using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;
using Syno.TinyTorrent.Views;
using Syno.TinyTorrent.Controls;

namespace Syno.TinyTorrent.Library;

internal sealed class MetadataDialog : UserControl, IDraft
{
    private readonly Browser _library;
    private readonly Entry _entry;
    private readonly IReadOnlyList<ProviderOption> _sources;
    private readonly ComboBox _source;
    private readonly HyperlinkButton _settings = new();
    private readonly ActionButton _fetch = new() { Glyph = Lucide.Download, VerticalAlignment = VerticalAlignment.Bottom };
    private RequestBlock? _block;
    private RequestBlock? _detailBlock;
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
    public bool CanSubmit => !IsPending && HasDraft && (_detailBlock is null || _detailBlock.CanForce);
    internal bool RequiresForce => _detailBlock?.CanForce == true;
    internal string SubmitText => _library.Text.Get(RequiresForce ? "request_protection" : "dialog", RequiresForce ? "force_button" : "save");
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
        _sources = library.DownloadProviders;
        _source = new ComboBox
        {
            ItemsSource = _sources.Select(option => option.Name).ToArray(),
            SelectedIndex = _sources.ToList().IndexOf(source),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _input = new AutoSuggestBox
        {
            Text = FileName.Interpret(entry.Name).Title,
        };
        _error.ActionButton = _settings;
        AutomationProperties.SetAutomationId(_source, "MetadataSource");
        AutomationProperties.SetAutomationId(_input, "MetadataSearch");
        AutomationProperties.SetAutomationId(_fetch, "MetadataFetch");
        AutomationProperties.SetAutomationId(_results, "MetadataMatches");
        AutomationProperties.SetLiveSetting(_empty, AutomationLiveSetting.Polite);
        AutomationProperties.SetAccessibilityView(
            _progress,
            Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw
        );
        var query = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
            },
            Children = { _input, _fetch },
        };
        Grid.SetColumn(_fetch, 1);
        var search = new StackPanel { Spacing = 4, Children = { query, _progress } };
        Content = new StackPanel
        {
            Spacing = 12,
            MinWidth = 380,
            Children = { _source, search, _results, _empty, _error },
        };
        _source.SelectionChanged += async (_, _) => await Find();
        _settings.Click += (_, _) => SettingsRequested?.Invoke(this, new(SettingsCategory.Network));
        _fetch.Click += async (_, _) => await Find(true);
        _input.QuerySubmitted += async (_, _) =>
        {
            if (_fetch.IsEnabled)
                await Find(true);
        };
        _input.TextChanged += async (_, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
                return;
            Cancel();
            _block = null;
            _fetch.IsEnabled = false;
            _empty.Visibility = Visibility.Collapsed;
            _results.ItemsSource = null;
            await ReadProtection();
        };
        _results.SelectionChanged += (_, _) =>
        {
            _detailBlock = null;
            PropertyChanged?.Invoke(this, new(nameof(CanSubmit)));
        };
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
        _input.Header = _library.Text.Get("library", "query");
        AutomationProperties.SetName(_input, _input.PlaceholderText);
        AutomationProperties.SetName(_results, _library.Text.Get("library", "matches"));
        _error.Message = _failure is null ? string.Empty : _library.Error(_failure);
        _error.IsOpen = _failure is not null;
        _empty.Text = _library.Text.Get("library", "no_metadata_matches");
        _fetch.Text = _block?.CanForce == true ? _library.Text.Get("request_protection", "force_button")
            : _library.Text.Get("library", "fetch");
        _fetch.IsEnabled = _request is null && Source is not null
            && !string.IsNullOrWhiteSpace(_input.Text) && (_block is null || _block.CanForce);
        var fetchTip = _block is null
            ? _library.Text.Format("library", "fetch_metadata_tip", Source?.Name ?? string.Empty)
            : _library.Text.Protection(_block);
        ToolTipService.SetToolTip(_fetch, fetchTip);
        AutomationProperties.SetHelpText(_fetch, fetchTip);
    }

    private ProviderOption? Source =>
        _source.SelectedIndex >= 0 ? _sources[_source.SelectedIndex] : null;

    private async Task ReadProtection(CancellationToken cancellation = default)
    {
        if (Source is not { } source || string.IsNullOrWhiteSpace(_input.Text))
            return;
        var query = _input.Text;
        RequestBlock? block;
        try
        {
            block = await _library.Protection(query, _entry.Name, source, cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
        catch (Exception error)
        {
            if (!cancellation.IsCancellationRequested && Source == source && _input.Text == query)
                Report(error);
            return;
        }
        if (cancellation.IsCancellationRequested || Source != source || _input.Text != query)
            return;
        _block = block;
        if (block is not null || _failure is RequestBlockedException)
            _failure = block is null ? null : new RequestBlockedException(block);
        RefreshText();
    }

    internal async Task Find(bool user = false)
    {
        if (Source is not { } source || string.IsNullOrWhiteSpace(_input.Text))
            return;
        var scope = user && _block?.CanForce == true ? FetchScope.Missing
            : user && _entry.HasMetadata ? FetchScope.All : FetchScope.New;
        var query = _input.Text;
        Cancel();
        using var request = new CancellationTokenSource();
        Track(request);
        try
        {
            if (!user)
                await ReadProtection(request.Token);
            request.Token.ThrowIfCancellationRequested();
            _empty.Visibility = Visibility.Collapsed;
            _results.ItemsSource = null;
            Report(null);
            var choices = await _library.FindVideo(query, _entry.Name, source, request.Token, scope);
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
            {
                if (!request.IsCancellationRequested)
                    await ReadProtection(request.Token);
                if (ReferenceEquals(_request, request))
                    Track(null);
            }
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
            await _library.SaveMetadata(_entry, choice, source, request.Token,
                RequiresForce ? FetchScope.Missing : FetchScope.New);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception error)
        {
            if (!request.IsCancellationRequested)
            {
                _detailBlock = (error as RequestBlockedException)?.Block;
                Report(error);
            }
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
        RefreshText();
    }

    internal void Cancel() => _request?.Cancel();
}
