using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Syno.TinyTorrent;

public sealed partial class MainWindow : Window
{
    public MainViewModel Model { get; }

    private readonly FontIcon _themeIcon = new() { FontFamily = Syno.Lucide.Font, FontSize = 16 };
    private ContentDialog? _addDialog;
    private ContentDialog? _closePrompt;
    private AddForm? _form;
    private TaskCompletionSource? _dialogClosed;
    private bool _allowClose;
    private bool _closing;
    private bool _engineExit;
    private bool _loaded;

    public MainWindow(Strings strings)
    {
        Model = new MainViewModel(strings, DispatcherQueue);
        InitializeComponent();
        ConfigureCapture();
        Filters.ItemsSource = Model.Filters;
        Split.ValueChanged += (_, value) => { _splitHeight = value; UpdateInspectorSize(); };
        TorrentWorkspace.SizeChanged += (_, _) => UpdateInspectorSize();
        FiltersClose.Content = new FontIcon { FontFamily = Syno.Lucide.Font, Glyph = Syno.Lucide.X, FontSize = 16 };
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        SetTitleBar(Caption);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "TinyTorrent.ico"));
        AddButton.Content = new FontIcon { FontFamily = Syno.Lucide.Font, Glyph = Syno.Lucide.Plus, FontSize = 16 };
        PauseButton.Content = new FontIcon { FontFamily = Syno.Lucide.Font, Glyph = Syno.Lucide.Pause, FontSize = 16 };
        ResumeButton.Content = new FontIcon { FontFamily = Syno.Lucide.Font, Glyph = Syno.Lucide.Play, FontSize = 16 };
        ExitButton.Content = new FontIcon { FontFamily = Syno.Lucide.Font, Glyph = Syno.Lucide.Power, FontSize = 16 };
        ThemeButton.Content = _themeIcon;
        MagnetButton.Content = new FontIcon { FontFamily = Syno.Lucide.Font, Glyph = Syno.Lucide.Link, FontSize = 16 };
        OverflowButton.Content = new FontIcon { FontFamily = Syno.Lucide.Font, Glyph = Syno.Lucide.Ellipsis, FontSize = 16 };
        Caption.SizeChanged += (_, _) => UpdateChrome();
        CaptionActions.SizeChanged += (_, _) => UpdateChrome();
        SearchArea.SizeChanged += (_, _) => UpdateChrome();
        FilterButton.SizeChanged += (_, _) => UpdateChrome();
        HomeButton.SizeChanged += (_, _) => UpdateChrome();
        AppWindow.Changed += OnWindowChanged;
        Root.ActualThemeChanged += (_, _) => { UpdateColors(); RefreshDialogs(); };
        Model.PropertyChanged += OnModelChanged;
        Model.TextChanged += (_, _) => RefreshText();
        Model.AnnouncementRequested += (_, message) =>
            FrameworkElementAutomationPeer.CreatePeerForElement(Torrents)?.RaiseNotificationEvent(
                AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.All, message, "command");
        Model.SnapshotApplied += (_, queueChanged) =>
        {
            if (queueChanged || Torrents.Sort is { Column: var column } && column != QueueColumn) Torrents.RefreshView();
        };
        Model.RevealRequested += async (_, torrent) =>
        {
            if (_addDialog is not null && _dialogClosed is { } closed) await closed.Task;
            if (!await ShowTorrents()) return;
            Torrents.Selection = new Syno.TableView.Selection([torrent], torrent);
            Torrents.ScrollIntoView(torrent);
        };
        Model.PreferencesRequested += async (_, target) => await ShowPreferences(target);
        Model.TorrentsRequested += async (_, _) => await ShowTorrents();
        Model.AboutRequested += async (_, _) => await ShowAbout();
        Model.AddRequested += async (_, _) =>
        {
            try { await ShowAdd(); }
            catch (Exception error) { Model.Report(error); }
        };
        Model.FilesRequested += async (_, _) => await PickSources();
        Model.RemoveRequested += async (_, torrents) => await ConfirmRemove(torrents);
        Model.LimitsRequested += async (_, _) => await ShowLimits();
        Model.OpenRequested += (_, path) =>
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception error) { Model.Report(error); }
        };
        Model.CopyRequested += (_, text) =>
        {
            try { var content = new DataPackage(); content.SetText(text); Clipboard.SetContent(content); }
            catch (Exception error) { Model.Report(error); }
        };
        Model.ActivateRequested += async (_, _) =>
        {
            if (_closing) { await Model.Activated(false); return; }
            if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
                presenter.Restore();
            Activate();
            SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
            await Model.Activated(true);
            if (Model.Page == WindowPage.Preferences) await Model.Preferences.ObserveRegistration();
        };
        Model.CloseRequested += async (_, engineExit) => await CloseWindow(engineExit);
        RefreshText();
        Torrents.Schema<Torrent>().Key(row => row.TorrentId).CanReorder(row => row.Queue >= 0)
            .SortKey(NameColumn, row => row.Name, StringComparer.CurrentCultureIgnoreCase)
            .SortKey(SizeColumn, row => row.Size).SortKey(ProgressColumn, row => row.Progress)
            .SortKey(StatusColumn, row => row.Status).SortKey(DownColumn, row => row.DownloadRate)
            .SortKey(UpColumn, row => row.UploadRate).SortKey(QueueColumn, row => row.QueueOrder)
            .SortKey(EtaColumn, row => row.DownloadRate <= 0 ? double.PositiveInfinity : row.Remaining / row.DownloadRate)
            .SortKey(RatioColumn, row => row.Ratio)
            .SortKey(PeersColumn, row => row.Seeds).SortKey(AddedColumn, row => row.Added);
        Torrents.Sort = new Syno.TableView.Sort(QueueColumn);
        Torrents.Placeholder = Syno.TableView.Placeholder.Loading;
        Torrents.SelectionChanged += async (_, _) => await SelectTorrent();
        Torrents.ItemInvoked += (_, _) => Run(Model.Properties);
        Torrents.ItemContextRequested += (_, args) => ShowSelectionMenu(args.Target, args.Position, true);
        Torrents.ReorderRequested += async (_, args) => await Model.Reorder(args.Items.Cast<Torrent>(), args.Before as Torrent);
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        UpdateMinimum(scale);
        AppWindow.Resize(new SizeInt32((int)(1040 * scale), (int)(680 * scale)));
        RememberBounds();
        AppWindow.Closing += OnClosing;
        Closed += (_, _) => Model.Dispose();
        AddShortcut(new() { Key = VirtualKey.O, Modifiers = VirtualKeyModifiers.Control }, () => Run(Model.Add), AddButton);
        AddShortcut(new() { Key = VirtualKey.W, Modifiers = VirtualKeyModifiers.Control }, () => _ = CloseWindow(false));
        AddShortcut(new() { Key = VirtualKey.Q, Modifiers = VirtualKeyModifiers.Control }, () => Run(Model.Exit), ExitButton);
        AddShortcut(new() { Key = VirtualKey.P, Modifiers = VirtualKeyModifiers.Control, ScopeOwner = Torrents }, () => Run(Model.Pause), PauseButton);
        AddShortcut(new() { Key = VirtualKey.S, Modifiers = VirtualKeyModifiers.Control, ScopeOwner = Torrents }, () => Run(Model.Resume), ResumeButton);
        AddShortcut(new() { Key = VirtualKey.M, Modifiers = VirtualKeyModifiers.Control, ScopeOwner = Torrents }, () => Run(Model.Force));
        AddShortcut(new() { Key = VirtualKey.R, Modifiers = VirtualKeyModifiers.Control, ScopeOwner = Torrents }, () => Run(Model.Verify));
        AddShortcut(new() { Key = VirtualKey.V, Modifiers = VirtualKeyModifiers.Control, ScopeOwner = Torrents }, () => _ = PasteSources());
        AddShortcut(new() { Key = VirtualKey.F, Modifiers = VirtualKeyModifiers.Control }, () => Search.Focus(FocusState.Keyboard));
        AddShortcut(new() { Key = VirtualKey.E, Modifiers = VirtualKeyModifiers.Control }, () => Search.Focus(FocusState.Keyboard));
        AddShortcut(new() { Key = VirtualKey.K, Modifiers = VirtualKeyModifiers.Control }, () => Search.Focus(FocusState.Keyboard));
        AddShortcut(new() { Key = VirtualKey.O, Modifiers = VirtualKeyModifiers.Menu }, () => Run(Model.ShowPreferences));
        AddShortcut(new() { Key = VirtualKey.O, Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift }, () => Run(Model.AddMagnet));
        AddShortcut(new() { Key = VirtualKey.P, Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift }, () => Run(Model.PauseAll));
        AddShortcut(new() { Key = VirtualKey.S, Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift }, () => Run(Model.ResumeAll));
        AddShortcut(new() { Key = VirtualKey.Delete, ScopeOwner = Torrents }, () => Run(Model.Remove));
        AddShortcut(new() { Key = VirtualKey.Add, Modifiers = VirtualKeyModifiers.Control, ScopeOwner = Torrents }, () => Run(Model.Up));
        AddShortcut(new() { Key = VirtualKey.Subtract, Modifiers = VirtualKeyModifiers.Control, ScopeOwner = Torrents }, () => Run(Model.Down));
        AddShortcut(new() { Key = VirtualKey.Add, Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, ScopeOwner = Torrents }, () => Run(Model.Top));
        AddShortcut(new() { Key = VirtualKey.Subtract, Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, ScopeOwner = Torrents }, () => Run(Model.Bottom));
        Root.KeyDown += OnQueueKey;
    }

    private void Bind(FrameworkElement control, DependencyProperty property, string path,
        BindingMode mode = BindingMode.OneWay) => control.SetBinding(property, new Binding
        {
            Source = Model.Draft, Path = new PropertyPath(path), Mode = mode,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
        });

    private static void Run(ICommand command)
    {
        if (command.CanExecute(null)) command.Execute(null);
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainViewModel.Page)) UpdatePage();
        if (_placementPath is null && Model.DataDirectory is { } directory) _placementRead = RestorePlacement(directory);
        if (string.IsNullOrEmpty(args.PropertyName) || args.PropertyName == nameof(MainViewModel.Theme))
            Root.RequestedTheme = Model.Theme switch { "light" => ElementTheme.Light, "dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        if (string.IsNullOrEmpty(args.PropertyName) || args.PropertyName == nameof(MainViewModel.IsLoading))
            Torrents.Placeholder = Model.IsLoading ? Syno.TableView.Placeholder.Loading : Syno.TableView.Placeholder.Empty;
        if (string.IsNullOrEmpty(args.PropertyName) || args.PropertyName == nameof(MainViewModel.HasInspector)) UpdateInspectorSize();
        if (string.IsNullOrEmpty(args.PropertyName) || args.PropertyName == nameof(MainViewModel.Filter))
        {
            _refreshingFilters = true;
            Filters.SelectedItem = Filters.Items.OfType<FilterChoice>().FirstOrDefault(choice => choice.Filter == Model.Filter);
            _refreshingFilters = false;
            AutomationProperties.SetName(FilterButton, Model.FilterLabel);
            ToolTipService.SetToolTip(FilterButton, Model.FilterLabel);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_loaded) return;
        _loaded = true;
        Root.XamlRoot.Changed += (_, _) => UpdateChrome();
        UpdateChrome();
        UpdateColors();
        Model.Start();
    }

    private void AddShortcut(KeyboardAccelerator accelerator, Action action, UIElement? target = null)
    {
        var selection = accelerator.ScopeOwner == Torrents;
        if (selection) accelerator.ScopeOwner = Root;
        accelerator.Invoked += (_, args) =>
        {
            if (HasDialog && accelerator.Key is not (VirtualKey.W or VirtualKey.Q)) return;
            if (selection && (Model.Page != WindowPage.Torrents || HasEditorFocus())) return;
            if (Root.XamlRoot?.Content is null) return;
            action();
            args.Handled = true;
        };
        var owner = target ?? Root;
        if (owner == Root) owner.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        owner.KeyboardAccelerators.Add(accelerator);
    }

    private async Task ShowAdd()
    {
        if (HasDialog) return;
        _form = new AddForm(Model);
        _form.FilesRequested += async (_, _) => await PickSources();
        _form.DestinationRequested += async (_, _) => await PickDestination();
        _form.AllowDrop = true;
        _form.DragOver += OnDragOver;
        _form.Drop += OnDrop;
        var body = new ScrollViewer { Content = _form, Width = Math.Min(816, Root.ActualWidth - 80), MaxHeight = Math.Max(220, Root.ActualHeight - 180), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Content = body, DefaultButton = ContentDialogButton.Primary };
        dialog.Resources["ContentDialogMaxWidth"] = 864d;
        _addDialog = dialog;
        Model.IsAddOpen = true;
        RefreshText();
        Bind(dialog, ContentDialog.IsPrimaryButtonEnabledProperty, nameof(AddDraft.CanSubmit));
        Bind(dialog, ContentDialog.PrimaryButtonTextProperty, nameof(AddDraft.SubmitText));
        _dialogClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.PrimaryButtonClick += OnSubmit;
        dialog.Closing += (_, args) => { if ((Model.Draft.IsSubmitting || Model.IsPicking) && !_closing) args.Cancel = true; };
        var completed = false;
        try { await dialog.ShowAsync(); completed = true; }
        catch (Exception error) { Model.Report(error); }
        finally
        {
            body.Content = null;
            _form = null;
            _addDialog = null;
            Model.IsAddOpen = false;
            _dialogClosed.TrySetResult();
        }
        if (!_closing && completed)
        {
            try { await Model.Draft.Cancel(); }
            catch (Exception error) { Model.Report(error); }
        }
    }

    private async Task PickSources()
    {
        if (Model.IsBusy || Model.IsPicking) return;
        Model.IsPicking = true;
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".torrent");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var files = await picker.PickMultipleFilesAsync();
            Model.IsPicking = false;
            if (files.Count > 0) await Model.AddSources(files.Select(file => file.Path));
        }
        catch (Exception error) { Model.Report(error); }
        finally { Model.IsPicking = false; }
    }

    private async Task PickDestination()
    {
        if (Model.IsBusy || Model.IsPicking) return;
        Model.IsPicking = true;
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            Model.IsPicking = false;
            if (folder is not null) Model.Draft.Destination = folder.Path;
        }
        catch (Exception error) { Model.Report(error); }
        finally { Model.IsPicking = false; }
    }

    private async void OnSubmit(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        var deferral = args.GetDeferral();
        try
        {
            args.Cancel = !await Model.Draft.Submit();
            if (args.Cancel) _form?.FocusMagnetError();
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        await CloseWindow(false);
    }

    private async Task CloseWindow(bool engineExit)
    {
        _engineExit |= engineExit;
        if (_closing) return;
        _closing = true;
        var wasOpen = false;
        var hadLimits = false;
        var keepDraft = false;
        try
        {
            if (!Model.CanClose)
            {
                var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnIdle(object? sender, PropertyChangedEventArgs args)
                {
                    if (Model.CanClose) idle.TrySetResult();
                }
                Model.PropertyChanged += OnIdle;
                try
                {
                    if (Model.CanClose) idle.TrySetResult();
                    await idle.Task;
                }
                finally { Model.PropertyChanged -= OnIdle; }
            }
            wasOpen = _addDialog is not null;
            hadLimits = _limitsDialog is not null;
            var hadRemoval = _removeDialog is not null;
            _addDialog?.Hide();
            _limitsDialog?.Hide();
            _removeDialog?.Hide();
            if (wasOpen && _dialogClosed is not null) await _dialogClosed.Task;
            if (hadLimits && _limitsClosed is not null) await _limitsClosed.Task;
            if (hadRemoval && _removeClosed is not null) await _removeClosed.Task;
            if (Model.HasDraft)
            {
                if (!await ConfirmDiscard())
                {
                    if (_engineExit) await Model.CancelClose();
                    keepDraft = true;
                    return;
                }
                await Model.CancelDraft();
            }
            await SavePlacement();
            await Model.Close(_engineExit);
            _allowClose = true;
            Close();
        }
        catch (Exception error) { Model.Report(error); }
        finally
        {
            _closing = false;
            _engineExit = false;
            if (keepDraft)
            {
                await Model.Activated(true);
                if (hadLimits) _ = ShowLimits(false);
                else if (wasOpen || Model.Draft.Sources.Count > 0 || Model.Draft.EditingMagnet) _ = ShowAdd();
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);
}
