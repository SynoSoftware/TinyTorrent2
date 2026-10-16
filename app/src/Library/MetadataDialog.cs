using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Network;
using Syno.TinyTorrent.Shell;
using Windows.Globalization.NumberFormatting;

namespace Syno.TinyTorrent.Library;

internal sealed class MetadataDialog : UserControl, IDraft
{
    private readonly Browser _library;
    private readonly Entry _entry;
    private readonly IReadOnlyList<ProviderOption> _sources;
    private readonly ComboBox _source;
    private readonly HyperlinkButton _settings = new();
    private readonly ActionButton _fetch = new()
    {
        Glyph = Lucide.Download,
        VerticalAlignment = VerticalAlignment.Bottom,
    };
    private RequestBlock? _block;
    private RequestBlock? _detailBlock;
    private readonly AutoSuggestBox _input;
    private readonly NumberBox _year;
    private readonly FontIcon _broadenedIcon = new()
    {
        FontFamily = Lucide.Font,
        Glyph = Lucide.Search,
        Style = (Style)Application.Current.Resources["TinyTorrentStatusIconStyle"],
        Visibility = Visibility.Collapsed,
    };
    private readonly ListView _results = new()
    {
        ItemTemplate = (DataTemplate)
            Application.Current.Resources["TinyTorrentVideoChoiceTemplate"],
        ItemContainerStyle = (Style)
            Application.Current.Resources["TinyTorrentVideoChoiceItemStyle"],
        MaxHeight = 300,
        SelectionMode = ListViewSelectionMode.Single,
    };
    private readonly InfoBar _error = new()
    {
        Severity = InfoBarSeverity.Error,
        IsClosable = false,
    };
    private readonly ProgressBar _progress = new() { Opacity = 0 };
    private readonly TextBlock _state = new()
    {
        Visibility = Visibility.Collapsed,
        Style = (Style)Application.Current.Resources["TinyTorrentBodyTextStyle"],
    };
    private CancellationTokenSource? _request;
    private CancellationTokenSource? _protection;
    private Exception? _failure;
    private bool _broadened;
    private int _revision;
    public bool IsPending { get; private set; }
    public bool CanSubmit =>
        !IsPending && HasDraft && (_detailBlock is null || _detailBlock.CanForce);
    internal bool RequiresForce => _detailBlock?.CanForce == true;
    internal string SubmitText =>
        _library.Text.Get(
            RequiresForce ? "request_protection" : "dialog",
            RequiresForce ? "force_button" : "save"
        );
    internal string SubmitTip =>
        _detailBlock is { CanForce: true } block
            ? _library.Text.Protection(block)
            : _library.Text.Get("library", "save_metadata_tip");
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
        _input = new AutoSuggestBox { Text = FileName.Interpret(entry.Name).Title };
        _year = new NumberBox
        {
            Value = entry.Year ?? double.NaN,
            Minimum = 1000,
            Maximum = 9999,
            MinWidth = 88,
            NumberFormatter = new DecimalFormatter { FractionDigits = 0, IsGrouped = false },
        };
        _error.ActionButton = _settings;
        AutomationProperties.SetAutomationId(_source, "MetadataSource");
        AutomationProperties.SetAutomationId(_input, "MetadataSearch");
        AutomationProperties.SetAutomationId(_year, "MetadataYear");
        AutomationProperties.SetAutomationId(_fetch, "MetadataFetch");
        AutomationProperties.SetAutomationId(_results, "MetadataMatches");
        AutomationProperties.SetLiveSetting(_state, AutomationLiveSetting.Polite);
        AutomationProperties.SetAccessibilityView(_broadenedIcon, AccessibilityView.Raw);
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
                new ColumnDefinition { Width = GridLength.Auto },
            },
            Children = { _input, _year, _fetch },
        };
        Grid.SetColumn(_year, 1);
        Grid.SetColumn(_fetch, 2);
        var search = new StackPanel { Spacing = 4, Children = { query, _progress } };
        var status = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
            },
            Children = { _state, _broadenedIcon },
        };
        Grid.SetColumn(_broadenedIcon, 1);
        Content = new StackPanel
        {
            Spacing = 12,
            MinWidth = 460,
            Children = { _source, search, status, _results, _error },
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
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
                await Changed();
        };
        _year.ValueChanged += async (_, _) => await Changed();
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
        _year.Header = _library.Text.Get("library", "year");
        AutomationProperties.SetName(_year, _library.Text.Get("library", "year"));
        ToolTipService.SetToolTip(_year, _library.Text.Get("library", "year_tip"));
        AutomationProperties.SetHelpText(_year, _library.Text.Get("library", "year_tip"));
        AutomationProperties.SetName(_input, _input.PlaceholderText);
        AutomationProperties.SetName(_results, _library.Text.Get("library", "matches"));
        _error.Message = _failure is null ? string.Empty : _library.Error(_failure);
        _error.Severity =
            _failure is RequestBlockedException ? InfoBarSeverity.Warning : InfoBarSeverity.Error;
        _error.IsOpen = _failure is not null;
        var count = (_results.ItemsSource as IReadOnlyList<VideoChoice>)?.Count;
        var state =
            IsPending ? _library.Text.Get("library", "saving_metadata")
            : _request is not null
                ? _library.Text.Format(
                    "library",
                    "searching_metadata",
                    Source?.Name ?? string.Empty
                )
            : _failure is not null || count is null ? string.Empty
            : count == 0 ? _library.Text.Get("library", "no_metadata_matches")
            : _library.Text.Format("library", count == 1 ? "match_one" : "match_other", count);
        if (_broadened && state.Length > 0 && _request is null && _failure is null)
            state = _library.Text.Format("library", "title_only_matches", state);
        var broadenedTip = Year is { } year
            ? _library.Text.Format("library", "title_only_tip", $"{_input.Text} ({year})")
            : string.Empty;
        _broadenedIcon.Visibility = _broadened ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(_broadenedIcon, broadenedTip);
        if (_state.Text != state)
        {
            _state.Text = state;
            _state.Visibility = state.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            FrameworkElementAutomationPeer
                .CreatePeerForElement(_state)
                ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
        ToolTipService.SetToolTip(_state, _broadened ? broadenedTip : state);
        AutomationProperties.SetHelpText(_state, _broadened ? broadenedTip : state);
        _fetch.Text =
            _block?.CanForce == true
                ? _library.Text.Get("request_protection", "force_button")
                : _library.Text.Get("library", "fetch");
        _fetch.IsEnabled =
            _request is null
            && Source is not null
            && !string.IsNullOrWhiteSpace(_input.Text)
            && (_block is null || _block.CanForce);
        var fetchTip = _block is null
            ? _library.Text.Format("library", "fetch_metadata_tip", Source?.Name ?? string.Empty)
            : _library.Text.Protection(_block);
        ToolTipService.SetToolTip(_fetch, fetchTip);
        AutomationProperties.SetHelpText(_fetch, fetchTip);
    }

    private ProviderOption? Source =>
        _source.SelectedIndex >= 0 ? _sources[_source.SelectedIndex] : null;

    private int? Year => double.IsNaN(_year.Value) ? null : (int)Math.Round(_year.Value);

    private async Task Changed()
    {
        Cancel();
        _block = null;
        _failure = null;
        _broadened = false;
        _fetch.IsEnabled = false;
        _results.ItemsSource = null;
        if (string.IsNullOrWhiteSpace(_input.Text))
            RefreshText();
        else
        {
            using var protection = new CancellationTokenSource();
            _protection = protection;
            try
            {
                await ReadProtection(protection.Token);
            }
            finally
            {
                if (ReferenceEquals(_protection, protection))
                    _protection = null;
            }
        }
    }

    private async Task ReadProtection(
        CancellationToken cancellation = default,
        bool automatic = false
    )
    {
        if (Source is not { } source || string.IsNullOrWhiteSpace(_input.Text))
            return;
        var query = _input.Text;
        var year = Year;
        var revision = _revision;
        RequestBlock? block;
        try
        {
            block = await _library.Protection(
                query,
                _entry.Name,
                source,
                cancellation,
                year,
                automatic
            );
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Metadata search protection read cancelled ({source.ProviderId})."
            );
            return;
        }
        catch (Exception error)
        {
            if (!cancellation.IsCancellationRequested && revision == _revision)
                Report(error);
            return;
        }
        if (cancellation.IsCancellationRequested || revision != _revision)
            return;
        _block = block;
        if (
            _failure is RequestBlockedException
            || block is not null && _results.ItemsSource is null
        )
            _failure = block is null ? null : new RequestBlockedException(block);
        RefreshText();
    }

    internal async Task Find(bool user = false)
    {
        if (Source is not { } source || string.IsNullOrWhiteSpace(_input.Text))
            return;
        var scope =
            user && _block?.CanForce == true ? FetchScope.Missing
            : user && _entry.HasMetadata ? FetchScope.All
            : FetchScope.New;
        var query = _input.Text;
        var year = Year;
        Cancel();
        using var request = new CancellationTokenSource();
        _failure = null;
        _broadened = false;
        _results.ItemsSource = null;
        Track(request);
        try
        {
            if (!user)
                await ReadProtection(request.Token, automatic: true);
            request.Token.ThrowIfCancellationRequested();
            var answer = await _library.FindVideo(
                query,
                _entry.Name,
                source,
                request.Token,
                scope,
                year,
                automatic: !user
            );
            if (!request.IsCancellationRequested)
            {
                _failure = null;
                _broadened = answer.Broadened;
                _results.ItemsSource = answer.Choices;
            }
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine($"Metadata search cancelled ({source.ProviderId}).");
        }
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
        IsPending = true;
        _failure = null;
        Track(request);
        _source.IsEnabled = _input.IsEnabled = _year.IsEnabled = _results.IsEnabled = false;
        PropertyChanged?.Invoke(this, new(nameof(CanSubmit)));
        try
        {
            await _library.SaveMetadata(
                _entry,
                choice,
                source,
                request.Token,
                RequiresForce ? FetchScope.Missing : FetchScope.New
            );
            return true;
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine($"Metadata save cancelled ({source.ProviderId}).");
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
            _source.IsEnabled = _input.IsEnabled = _year.IsEnabled = _results.IsEnabled = true;
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

    internal void Cancel()
    {
        _revision++;
        _request?.Cancel();
        _protection?.Cancel();
    }
}
