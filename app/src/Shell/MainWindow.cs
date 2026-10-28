using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.Storage.Pickers;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Settings;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Torrents;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.System;

namespace Syno.TinyTorrent.Shell;

public sealed partial class MainWindow : Window
{
    public MainViewModel Model { get; }
    public ICommand Back { get; }

    partial void ConfigureCapture();

    private AddDialog? _addDialog;
    private bool _allowClose;
    private bool _loaded;
    private readonly Dictionary<ICommand, KeyboardAccelerator> _shortcuts = [];

    // VirtualKey omits the Windows OEM comma code.
    private const VirtualKey Comma = (VirtualKey)0xBC;

    public MainWindow(Strings strings)
    {
        Model = new MainViewModel(strings, DispatcherQueue, new Navigation(PrepareNavigation));
#if CAPTURE
        Model.Features.CanStart = !IsCaptureReview;
#endif
        Back = new RelayCommand(GoBack, () => Model.Navigation.CanGoBack);
        SelectedMap = new RelayCommand(() => ShowConnections(false), () => CanShowMap(false));
        AllMap = new RelayCommand(() => ShowConnections(true), () => CanShowMap(true));
        InitializeComponent();
        Svg.Fit(TorrentsFace);
        Svg.Fit(LibraryFace);
        WorkspacePicker.Flyout = MenuItems.Flyout(() => WorkspaceMenu("WorkspacePicker"), () => Model.Text);
        WorkspacePicker.Flyout.Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft;
        LanguageButton.Flyout = MenuItems.Flyout(LanguageMenu, () => Model.Text);
        ConfigureWork();
        Model.Navigation.PropertyChanged += (_, _) => UpdateBack();
        ConfigureLibrary();
        PageContent.SizeChanged += Motion.Clip;
        WorkspaceContent.SizeChanged += Motion.Clip;
        SettingsContent.SizeChanged += Motion.Clip;
        FilterContent.SizeChanged += Motion.Clip;
        _pages.Show(Workspace);
        _workspaces.Show(TorrentContent);
        _filters.Show(Filters);
        Root.Unloaded += (_, _) => StopMotion();
        ConfigureCapture();
        ConfigureNotifications();
        Filters.ItemsSource = Model.Filters;
        Split.ValueChanged += (_, value) =>
        {
            _splitHeight = value;
            UpdateInspectorSize();
        };
        Split.ValueCommitted += OnSplitCommitted;
        InspectorSurface.SizeChanged += Motion.Clip;
        Workspace.SizeChanged += (_, _) => UpdateInspectorSize();
        ToolbarRow.SizeChanged += (_, _) => UpdateInspectorSize();
        StatusBar.SizeChanged += (_, _) => UpdateStatus();
        _uiSettings.TextScaleFactorChanged += OnTextScaling;
        FiltersPane.OpenRequested += OnFiltersOpen;
        FiltersPane.CloseRequested += OnFiltersClose;
        FiltersPane.CollapseRequested += OnFiltersCollapse;
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        SetTitleBar(Caption);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "TinyTorrent.ico"));
        Caption.SizeChanged += (_, _) => UpdateChrome();
        CaptionStart.SizeChanged += (_, _) => UpdateChrome();
        Menus.SizeChanged += (_, _) => UpdateChrome();
        LanguageButton.Loaded += (_, _) => UpdateChrome();
        LanguageButton.SizeChanged += (_, _) => UpdateChrome();
        ThemeButton.Loaded += (_, _) => UpdateChrome();
        ThemeButton.SizeChanged += (_, _) => UpdateChrome();
        SearchArea.SizeChanged += (_, _) => UpdateChrome();
        Search.SizeChanged += (_, _) => UpdateChrome();
        AppWindow.Changed += OnWindowChanged;
        Root.ActualThemeChanged += (_, _) =>
        {
            UpdateColors();
            RefreshPopups();
        };
        Model.PropertyChanged += OnModelChanged;
        Model.TextChanged += (_, _) => RefreshText();
        Model.Notifications.AnnouncementRequested += (_, message) =>
            FrameworkElementAutomationPeer
                .CreatePeerForElement(Torrents)
                ?.RaiseNotificationEvent(
                    AutomationNotificationKind.ActionCompleted,
                    AutomationNotificationProcessing.All,
                    message,
                    "command"
                );
        Model.SnapshotApplied += (_, change) =>
        {
            // Project already publishes changes to the ascending queue order.
            if (
                !change.Published
                && change.RowsChanged
                && Torrents.Sort is { } sort
                && (
                    sort.Column != QueueColumn
                    || sort.Direction != Syno.TableView.SortDirection.Ascending
                )
            )
                Torrents.RefreshView();
        };
        Model.RevealRequested += async (_, torrents) =>
        {
            if (_interaction is { } interaction)
                await interaction.Completion.Task;
            if (!await ShowTorrents())
                return;
            Torrents.ScrollIntoView(torrents[^1], torrents);
            await SelectTorrent(new Syno.TableView.Selection(torrents, torrents[^1]));
        };
        Model.ReleaseRequested += (_, torrents) => Torrents.Release(torrents);
        Model.SettingsRequested += async (_, target) => await ShowSettings(target);
        Model.TorrentsRequested += async (_, _) => await ShowTorrents();
        Model.AboutRequested += async (_, _) => await ShowAbout();
        Model.AddRequested += async (_, _) =>
        {
            try
            {
                await ShowAdd();
            }
            catch (Exception error)
            {
                Model.Notifications.Report(error);
            }
        };
        Model.FilesRequested += async (_, _) => await PickSources();
        // As in File Explorer, Shift with Remove's key, button or menu item
        // deletes permanently instead of through the Recycle Bin.
        Model.RemoveRequested += async (_, torrents) =>
            await ShowRemoval(
                torrents,
                Microsoft
                    .UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                    .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)
                    ? DeletionMode.Permanent
                    : DeletionMode.Recycle
            );
        Model.MergeRequested += async (_, _) => await ConfirmMerge();
        Model.DatabaseDeletionRequested += async (_, _) => await ConfirmDatabaseDeletion();
        Model.MoveRequested += async (_, torrents) => await ShowMove(torrents);
        Model.SpeedLimitRequested += async (_, torrents) => await ShowSpeedLimit(torrents);
        Model.OpenRequested += (_, args) => Open(args);
        Model.CopyRequested += (_, text) =>
        {
            try
            {
                var content = new DataPackage();
                content.SetText(text);
                Clipboard.SetContent(content);
            }
            catch (Exception error)
            {
                Model.Notifications.Report(error);
            }
        };
        Model.ActivateRequested += async (_, _) =>
        {
            // A window still waiting to appear answers when it appears.
            if (_cloaked)
                return;
#if CAPTURE
            if (IsCaptureReview)
            {
                await Model.ReplyActivation(false);
                return;
            }
#endif
            if (Model.IsClosing)
            {
                await Model.ReplyActivation(false);
                return;
            }
            BringToFront();
            await Model.ReplyActivation(true);
            if (Model.Page == WindowPage.Settings)
                await Model.Settings.ObserveRegistration();
        };
        Model.ShowRequested += async (_, _) => await ShowWhenReady();
        Model.CloseRequested += async (_, request) => await CloseWindow(request);
        Model.ConfirmExitRequested += async (_, _) => await ConfirmExit();
        Model.UnfinishedRequested += (_, _) => ShowDeferredUnfinished();
        // No limit sorts as the highest, so ascending lists limited torrents first.
        static long Rank(int limit) => limit > 0 ? limit : long.MaxValue;
        Torrents
            .Schema<Torrent>()
            .Key(row => row.TorrentId)
            .Order(
                QueueColumn,
                row => row.IsQueued ? row.Queue + 1 : (int?)null,
                () => Model.Torrents
            )
            .SortKey(NameColumn, row => row.Name, StringComparer.CurrentCultureIgnoreCase)
            .SortKey(SizeColumn, row => row.Size)
            .SortKey(ProgressColumn, row => row.Progress)
            .SortKey(StatusColumn, row => row.Status)
            .SortKey(DownColumn, row => row.DownloadRate)
            .SortKey(UpColumn, row => row.UploadRate)
            .SortKey(QueueColumn, row => row.QueueOrder)
            .SortKey(LimitColumn, row => (Rank(row.DownloadLimit), Rank(row.UploadLimit)))
            .SortKey(EtaColumn, row => row.Eta ?? double.PositiveInfinity)
            .SortKey(RatioColumn, row => row.Ratio)
            .SortKey(SeedsColumn, row => row.SwarmSeedCount)
            .SortKey(PeersColumn, row => row.SwarmLeecherCount)
            .SortKey(AddedColumn, row => row.Added)
            .SortKey(CompletedOnColumn, row => row.CompletedOn);
        Torrents.Sort = new Syno.TableView.Sort(QueueColumn);
        Torrents.Placeholder = Syno.TableView.Placeholder.Loading;
        Torrents.SelectionChanged += async (_, _) => await SelectTorrent();
        Torrents.ItemInvoked += (_, _) => Run(Model.Properties);
        Torrents.ItemContextRequested += (_, args) => ShowSelectionMenu(args.Target, args.Position);
        Torrents.ReorderRequested += async (_, args) =>
            await Model.Reorder([.. args.Items.Cast<Torrent>()], args.Before as Torrent);
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        UpdateMinimum(scale);
        AppWindow.Resize(new SizeInt32((int)(1040 * scale), (int)(680 * scale)));
        RememberBounds();
#if CAPTURE
        if (!IsCaptureReview)
#endif
            SetCloak(true);
        AppWindow.Closing += OnClosing;
        Closed += (_, _) =>
        {
            _uiSettings.TextScaleFactorChanged -= OnTextScaling;
            Model.Dispose();
        };
        AddShortcut(
            new() { Key = VirtualKey.O, Modifiers = VirtualKeyModifiers.Control },
            Model.Add
        );
        // Windows closes the window on Alt+F4 through OnClosing; this entry only
        // shows the key beside Close.
        _shortcuts.Add(
            Model.CloseWindow,
            new() { Key = VirtualKey.F4, Modifiers = VirtualKeyModifiers.Menu }
        );
        AddShortcut(
            new() { Key = VirtualKey.Q, Modifiers = VirtualKeyModifiers.Control },
            Model.Exit
        );
        AddShortcut(
            new()
            {
                Key = VirtualKey.P,
                Modifiers = VirtualKeyModifiers.Control,
                ScopeOwner = Torrents,
            },
            Model.Pause
        );
        AddShortcut(
            new()
            {
                Key = VirtualKey.S,
                Modifiers = VirtualKeyModifiers.Control,
                ScopeOwner = Torrents,
            },
            Model.Resume
        );
        AddShortcut(
            new()
            {
                Key = VirtualKey.S,
                Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu,
                ScopeOwner = Torrents,
            },
            Model.Force
        );
        AddShortcut(
            new() { Key = VirtualKey.M, Modifiers = VirtualKeyModifiers.Control },
            SelectedMap
        );
        AddShortcut(
            new()
            {
                Key = VirtualKey.M,
                Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift,
            },
            AllMap
        );
        AddShortcut(
            new()
            {
                Key = VirtualKey.R,
                Modifiers = VirtualKeyModifiers.Control,
                ScopeOwner = Torrents,
            },
            Model.Verify
        );
        AddShortcut(
            new()
            {
                Key = VirtualKey.V,
                Modifiers = VirtualKeyModifiers.Control,
                ScopeOwner = Torrents,
            },
            () => _ = PasteSources()
        );
        AddShortcut(
            new() { Key = VirtualKey.F, Modifiers = VirtualKeyModifiers.Control },
            FocusSearch
        );
        AddShortcut(
            new() { Key = VirtualKey.E, Modifiers = VirtualKeyModifiers.Control },
            FocusSearch
        );
        AddShortcut(
            new() { Key = VirtualKey.K, Modifiers = VirtualKeyModifiers.Control },
            FocusSearch
        );
        AddShortcut(
            new() { Key = Comma, Modifiers = VirtualKeyModifiers.Control },
            Model.ShowSettings
        );
        AddShortcut(new() { Key = VirtualKey.Left, Modifiers = VirtualKeyModifiers.Menu }, Back);
        AddShortcut(
            new()
            {
                Key = VirtualKey.O,
                Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift,
            },
            Model.AddMagnet
        );
        AddShortcut(
            new()
            {
                Key = VirtualKey.P,
                Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift,
            },
            Model.PauseAll
        );
        AddShortcut(
            new()
            {
                Key = VirtualKey.S,
                Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift,
            },
            Model.ResumeAll
        );
        AddShortcut(new() { Key = VirtualKey.Delete, ScopeOwner = Torrents }, Model.Remove);
        // Remove reads Shift itself, so this shortcut is not Remove's shown one.
        AddShortcut(
            new()
            {
                Key = VirtualKey.Delete,
                Modifiers = VirtualKeyModifiers.Shift,
                ScopeOwner = Torrents,
            },
            () => Run(Model.Remove)
        );
        AddShortcut(
            new()
            {
                Key = VirtualKey.Add,
                Modifiers = VirtualKeyModifiers.Control,
                ScopeOwner = Torrents,
            },
            Model.Up
        );
        AddShortcut(
            new()
            {
                Key = VirtualKey.Subtract,
                Modifiers = VirtualKeyModifiers.Control,
                ScopeOwner = Torrents,
            },
            Model.Down
        );
        AddShortcut(
            new()
            {
                Key = VirtualKey.Add,
                Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift,
                ScopeOwner = Torrents,
            },
            Model.Top
        );
        AddShortcut(
            new()
            {
                Key = VirtualKey.Subtract,
                Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift,
                ScopeOwner = Torrents,
            },
            Model.Bottom
        );
        Root.KeyDown += OnQueueKey;
        Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnMapKey), true);
        RefreshText();
    }

    private void BindAddDraft(
        FrameworkElement control,
        DependencyProperty property,
        string path,
        BindingMode mode = BindingMode.OneWay
    ) =>
        control.SetBinding(
            property,
            new Binding
            {
                Source = Model.AddDraft,
                Path = new PropertyPath(path),
                Mode = mode,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
            }
        );

    private static void Run(ICommand command)
    {
        if (command.CanExecute(null))
            command.Execute(null);
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (
            args.PropertyName
            is nameof(MainViewModel.FilterPane)
                or nameof(MainViewModel.HasInspector)
                or nameof(MainViewModel.IsInspectorCompact)
                or nameof(MainViewModel.IsInspectorMaximized)
                or nameof(MainViewModel.IsToolbarOpen)
            || Model.Inspector.Section != InspectorSection.Peers
            || !Model.Inspector.HasPeerScope
        )
            KeepMap();
        ((RelayCommand)SelectedMap).Refresh();
        ((RelayCommand)AllMap).Refresh();
        QueueStatus();
        QueueMenus();
        if (
            string.IsNullOrEmpty(args.PropertyName)
            || args.PropertyName == nameof(MainViewModel.ShowsTitleSpeeds)
        )
            UpdateChrome();
        if (args.PropertyName == nameof(MainViewModel.Page))
            UpdatePage();
        if (
            string.IsNullOrEmpty(args.PropertyName)
            || args.PropertyName == nameof(MainViewModel.FilterPane)
        )
        {
            Workspace.DisplayMode =
                Model.FilterPane == FilterPane.Compact
                    ? SplitViewDisplayMode.CompactInline
                    : SplitViewDisplayMode.Inline;
            FiltersPane.IsCompact = Model.FilterPane == FilterPane.Compact;
            FiltersPane.Visibility =
                Model.FilterPane == FilterPane.Hidden ? Visibility.Collapsed : Visibility.Visible;
            InspectorSurface.Margin = new Thickness(Model.IsFilterOpen ? 0 : 4, 0, 0, 0);
            if (
                args.PropertyName == nameof(MainViewModel.FilterPane)
                && Model.IsFilterOpen
                && Model.Page == WindowPage.Library
            )
                RefreshLibraryFilters();
        }
        if (Model.IsDisconnected)
            Reveal();
        if (
            string.IsNullOrEmpty(args.PropertyName)
            || args.PropertyName == nameof(MainViewModel.Theme)
        )
            Root.RequestedTheme = Model.Theme switch
            {
                "light" => ElementTheme.Light,
                "dark" => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        if (
            string.IsNullOrEmpty(args.PropertyName)
            || args.PropertyName == nameof(MainViewModel.IsLoading)
        )
            Torrents.Placeholder = Model.IsLoading
                ? Syno.TableView.Placeholder.Loading
                : Syno.TableView.Placeholder.Empty;
        if (
            string.IsNullOrEmpty(args.PropertyName)
            || args.PropertyName == nameof(MainViewModel.HasInspector)
            || args.PropertyName == nameof(MainViewModel.IsInspectorCompact)
            || args.PropertyName == nameof(MainViewModel.IsInspectorMaximized)
            || args.PropertyName == nameof(MainViewModel.IsToolbarOpen)
        )
            UpdateInspectorSize();
        if (
            string.IsNullOrEmpty(args.PropertyName)
            || args.PropertyName == nameof(MainViewModel.Filter)
        )
        {
            _refreshingFilters = true;
            Filters.SelectedItem = Filters
                .Items.OfType<FilterChoice>()
                .FirstOrDefault(choice => choice.Filter == Model.Filter);
            _refreshingFilters = false;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_loaded)
            return;
        _loaded = true;
        Root.XamlRoot.Changed += (_, _) => UpdateChrome();
        UpdateChrome();
        UpdateColors();
        Model.Start();
    }

    private void AddShortcut(KeyboardAccelerator accelerator, ICommand command)
    {
        _shortcuts.Add(command, accelerator);
        AddShortcut(
            accelerator,
            () => Run(command),
            canInvoke: command == SelectedMap || command == AllMap ? () => !HasEditorFocus() : null
        );
    }

    private void AddShortcut(
        KeyboardAccelerator accelerator,
        Action action,
        UIElement? target = null,
        Func<bool>? canInvoke = null
    )
    {
        var selection = accelerator.ScopeOwner == Torrents;
        if (selection)
            accelerator.ScopeOwner = Root;
        accelerator.Invoked += (_, args) =>
        {
            if (HasDialog || canInvoke?.Invoke() == false)
                return;
            if (selection && (Model.Page != WindowPage.Torrents || HasEditorFocus()))
                return;
            if (Root.XamlRoot?.Content is null)
                return;
            action();
            args.Handled = true;
        };
        var owner = target ?? Root;
        if (owner == Root)
            owner.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        owner.KeyboardAccelerators.Add(accelerator);
    }

    private async Task ShowAdd()
    {
        await Interact(async interaction =>
        {
            interaction.Restore = ShowAdd;
            _addDialog = new AddDialog(Model, Root.XamlRoot);
            _addDialog.DestinationRequested += async (_, _) => await PickDestination();
            _addDialog.AllowDrop = true;
            _addDialog.DragOver += OnDragOver;
            _addDialog.Drop += OnDrop;
            var dialog = new Dialog
            {
                Content = _addDialog,
                PrimaryGlyph = Syno.Lucide.CirclePlus,
                Footer = new DialogFooter.OptOut(Model.AddDraft.Confirmation),
            };
            dialog.SizeToContent();
            _addDialog.CloseRequested += (_, _) => dialog.Hide();
            Model.AddDraft.Open();
            BindAddDraft(
                dialog,
                ContentDialog.PrimaryButtonTextProperty,
                nameof(AddDraft.SubmitText)
            );
            BindAddDraft(dialog, Dialog.PrimaryToolTipProperty, nameof(AddDraft.SubmitToolTip));
            dialog.Opened += (_, _) => _addDialog?.FocusError();
            try
            {
                await ShowEditor(
                    interaction,
                    dialog,
                    Model.AddDraft,
                    () =>
                    {
                        AutomationProperties.SetName(dialog, Model.Text.Get("add", "title"));
                        _addDialog?.RefreshText();
                    },
                    submit: async () =>
                    {
                        var submitted = await Model.AddDraft.Submit();
                        if (!submitted)
                            _addDialog?.FocusError();
                        return submitted;
                    }
                );
            }
            finally
            {
                _addDialog?.Dispose();
                Model.AddDraft.Close();
                _addDialog = null;
            }
            return true;
        });
    }

    private async Task PickSources()
    {
        if (!Model.CanEdit)
            return;
        Model.IsPicking = true;
        try
        {
            var picker = new FileOpenPicker(AppWindow.Id);
            picker.FileTypeFilter.Add(".torrent");
            var files = await picker.PickMultipleFilesAsync();
            Model.IsPicking = false;
            if (files.Count > 0)
                await Model.AddSources(files.Select(file => file.Path), picked: true);
        }
        catch (Exception error)
        {
            Model.Notifications.Report(error);
        }
        finally
        {
            Model.IsPicking = false;
        }
    }

    private async Task PickDestination()
    {
        var folder = await PickFolder(Model.AddDraft.Destination);
        if (folder is not null)
            Model.AddDraft.Destination = folder;
    }

    // Opens at the first of `starts` that exists, as a field's Browse opens
    // at the folder the field names. The share check runs off the UI thread,
    // because an unreachable share takes seconds to answer.
    private async Task<string?> PickFolder(params string?[] starts)
    {
        if (!Model.CanEdit)
            return null;
        Model.IsPicking = true;
        try
        {
            var picker = new FolderPicker(AppWindow.Id);
            var start = await Task.Run(() =>
                starts.FirstOrDefault(folder =>
                    !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder)
                )
            );
            if (start is not null)
                picker.SuggestedFolder = start;
            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }
        catch (Exception error)
        {
            Model.Notifications.Report(error);
        }
        finally
        {
            Model.IsPicking = false;
        }
        return null;
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose)
            return;
        args.Cancel = true;
        await CloseWindow(CloseRequest.Close);
    }

    private void BringToFront()
    {
        if (
            AppWindow.Presenter is OverlappedPresenter
            {
                State: OverlappedPresenterState.Minimized
            } presenter
        )
            presenter.Restore();
        Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    // The engine decides whether Exit needs confirmation and asks here while
    // the window is open. A window that cannot show the prompt now lets the
    // engine ask natively instead.
    private async Task ConfirmExit()
    {
        if (_cloaked || HasDialog || Model.IsPicking || Model.IsClosing
#if CAPTURE
            || IsCaptureReview
#endif
        )
        {
            await Model.ReplyExit(ExitAnswer.Unavailable);
            return;
        }
        BringToFront();
        var answer = ExitAnswer.Cancelled;
        await Interact(async interaction =>
        {
            var preference = new DialogPreference(Model.Settings);
            var dialog = new Dialog
            {
                Glyph = Syno.Lucide.Power,
                PrimaryGlyph = Syno.Lucide.Power,
                Footer = new DialogFooter.OptOut(preference),
            };
            await Model.DeferClose();
            var choice = await ShowDialog(
                interaction,
                dialog,
                () =>
                {
                    dialog.Title = Model.Text.Get("exit", "title");
                    dialog.Content = Model.Text.Get("exit", "active");
                    dialog.PrimaryButtonText = Model.Text.Get("commands", "exit");
                }
            );
            if (choice == ContentDialogResult.Primary)
            {
                await preference.Save(Model.Settings.ConfirmExit);
                answer = ExitAnswer.Confirmed;
            }
            return true;
        });
        await Model.ReplyExit(answer);
    }

    private async Task ConfirmReset()
    {
        var confirmed = await Interact(async interaction =>
        {
            var dialog = new Dialog
            {
                Glyph = Syno.Lucide.RotateCcw,
                PrimaryGlyph = Syno.Lucide.RotateCcw,
            };
            var choice = await ShowDialog(
                interaction,
                dialog,
                () =>
                {
                    dialog.Title = Model.Text.Get("settings", "reset_all_title");
                    dialog.Content = DataConfirmation(
                        [
                            Model.Text.Get("settings", "reset_all_body"),
                            Model.Text.Get("settings", "reset_all_kept"),
                        ],
                        [Model.Text.Get("settings", "reset_all_caution")]
                    );
                    dialog.PrimaryButtonText = Model.Text.Get("settings", "reset_all_confirm");
                    dialog.PrimaryToolTip = Model.Text.Get("settings", "reset_all_tip");
                }
            );
            return choice == ContentDialogResult.Primary;
        });
        // A close that began while the dialog was open owns the window.
        if (!confirmed || Model.IsClosing)
            return;
        // The engine opens a new window, which starts as a new installation
        // does.
        await CloseWindow(CloseRequest.Reset);
    }

    // Orders the native steps around the model's: hiding, the open dialog,
    // draft prompts, placement and focus.
    private async Task CloseWindow(CloseRequest request)
    {
        var focused = FocusManager.GetFocusedElement(Root.XamlRoot) as Control;
        var focusName = focused?.Name;
        if (!await Model.BeginClose(request))
            return;
        DialogInteraction? suspended = null;
        try
        {
            // Close and Exit hide the window before any waiting, so closing never
            // leaves a frozen window on screen. Only a decision the person must
            // make keeps it visible, and a failed close shows it again.
            if (!HasDialog && !Model.HasDraft && !Model.IsPicking)
                AppWindow.Hide();
            await Model.DeferExit();
            if (
                _interaction is { IsDraftDecision: true } decision
                && !await decision.Completion.Task
            )
                return;
            if (!await Model.PrepareClose())
                return;
            suspended = _interaction;
            if (suspended is not null)
            {
                suspended.IsSuspended = true;
                suspended.Dialog?.Hide();
                await suspended.Completion.Task;
            }
            if (suspended?.ResolveDraft is { } resolve && !await resolve())
                return;
            while (Model.RemainingDraft is { } draft)
            {
                if (!AppWindow.IsVisible)
                    AppWindow.Show();
                await Model.DeferExit();
                if (!await ResolveRemainingDraft(draft))
                    return;
            }
            AppWindow.Hide();
            if (!await Model.FinishClose())
                return;
            if (request == CloseRequest.Reset)
                await ForgetPlacement();
            else
                await SavePlacement();
            _settingsPage?.Release();
            _allowClose = true;
            Close();
        }
        catch (Exception error)
        {
            Model.Notifications.Report(error);
        }
        finally
        {
            if (!_allowClose)
            {
                if (!AppWindow.IsVisible)
                    AppWindow.Show();
                await Model.CancelClose();
                if (suspended?.Restore is { } restore)
                {
                    if (!suspended.IsResolved)
                        _ = restore();
                }
                ShowDeferredUnfinished();
                ShowDeferredAdd();
                // A page the person is already on stays as they left it, and
                // Recover moves only to the error.
                if (!HasDialog && Model.Inspector.HasDraft && Model.Inspector.HasError)
                {
                    if (Model.Page != WindowPage.Torrents)
                        await ShowTorrents();
                    focused = (InspectorContent.Content as InspectorPane)?.Recover();
                }
                if (focused is { IsLoaded: true })
                    DispatcherQueue.TryEnqueue(
                        Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                        () => focused.Focus(FocusState.Programmatic)
                    );
                else if (!string.IsNullOrEmpty(focusName))
                {
                    var content = _fileDialog as FrameworkElement ?? _addDialog;
                    DispatcherQueue.TryEnqueue(
                        Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                        () =>
                            (content?.FindName(focusName) as Control)?.Focus(
                                FocusState.Programmatic
                            )
                    );
                }
            }
        }
    }

    private Task<bool> ResolveRemainingDraft(UnfinishedDraft draft)
    {
        switch (draft)
        {
            case UnfinishedDraft.Add:
                return ResolveDraft(Model.AddDraft);
            case UnfinishedDraft.Move:
                return ResolveDraft(Model.MoveDraft);
            case UnfinishedDraft.Inspector:
                // As with Settings, an edit that cannot be saved, because the
                // engine or the torrent is gone, closes without it instead of
                // trapping the person.
                if (Model.CanSave && Model.Inspector.IsAvailable)
                    return Model.Inspector.Depart();
                Model.Inspector.CancelDraft();
                return Task.FromResult(true);
            default:
                throw new ArgumentOutOfRangeException(nameof(draft));
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint window,
        int attribute,
        ref int value,
        int size
    );
}
