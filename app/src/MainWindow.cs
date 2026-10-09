using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;
using Syno.TinyTorrent.Views;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace Syno.TinyTorrent;

public sealed partial class MainWindow : Window
{
    public MainViewModel Model { get; }
    public ICommand Back { get; }
    internal static bool IsCaptureReview { get; }

    partial void ConfigureCapture();

    private AddDialog? _addDialog;
    private bool _allowClose;
    private bool _exiting;
    private bool _loaded;
    private readonly Dictionary<ICommand, KeyboardAccelerator> _shortcuts = [];

    // VirtualKey omits the Windows OEM comma code.
    private const VirtualKey Comma = (VirtualKey)0xBC;

    public MainWindow(Strings strings)
    {
        Model = new MainViewModel(strings, DispatcherQueue);
        Back = new RelayCommand(GoBack, () => true);
        InitializeComponent();
        Root.Unloaded += (_, _) => _motion.Stop();
        ConfigureCapture();
        ConfigureNotifications();
        Filters.ItemsSource = Model.Filters;
        Split.ValueChanged += (_, value) =>
        {
            _splitHeight = value;
            UpdateInspectorSize();
        };
        Workspace.SizeChanged += (_, _) => UpdateInspectorSize();
        Toolbar.SizeChanged += (_, _) => UpdateInspectorSize();
        StatusBar.SizeChanged += (_, _) => UpdateStatus();
        FilterLabel.RegisterPropertyChangedCallback(
            TextBlock.TextProperty,
            (_, _) => UpdateStatus()
        );
        _uiSettings.TextScaleFactorChanged += OnTextScaling;
        FiltersClose.Content = new FontIcon
        {
            FontFamily = Syno.Lucide.Font,
            Glyph = Syno.Lucide.X,
            FontSize = 16,
        };
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        SetTitleBar(Caption);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "TinyTorrent.ico"));
        Caption.SizeChanged += (_, _) => UpdateChrome();
        CaptionStart.SizeChanged += (_, _) => UpdateChrome();
        Menus.SizeChanged += (_, _) => UpdateChrome();
        AddButtons.SizeChanged += (_, _) => UpdateChrome();
        ThemeButton.Loaded += (_, _) => UpdateChrome();
        ThemeButton.SizeChanged += (_, _) => UpdateChrome();
        SearchArea.SizeChanged += (_, _) => UpdateChrome();
        Search.SizeChanged += (_, _) => UpdateChrome();
        AppWindow.Changed += OnWindowChanged;
        Root.ActualThemeChanged += (_, _) =>
        {
            UpdateColors();
            RefreshDialogs();
        };
        Model.PropertyChanged += OnModelChanged;
        Model.TextChanged += (_, _) => RefreshText();
        Model.AnnouncementRequested += (_, message) =>
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
            if (
                !change.Published
                && (
                    change.EligibilityChanged
                    || Torrents.Sort is { Column: var column } && column != QueueColumn
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
            foreach (var torrent in torrents)
                Torrents.ScrollIntoView(torrent);
            if (await SelectTorrent(new Syno.TableView.Selection(torrents, torrents[^1])))
                Torrents.ScrollIntoView(torrents[^1]);
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
                Model.Report(error);
            }
        };
        Model.FilesRequested += async (_, _) => await PickSources();
        Model.RemoveRequested += async (_, torrents) => await ConfirmRemove(torrents);
        Model.MergeRequested += async (_, _) => await ConfirmMerge();
        Model.MoveRequested += async (_, torrents) => await ShowFiles(torrents, FileAction.Move);
        Model.DeleteRequested += async (_, torrents) =>
            await ShowFiles(torrents, FileAction.Delete);
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
                Model.Report(error);
            }
        };
        Model.ActivateRequested += async (_, _) =>
        {
            // A window still waiting to appear answers when it appears.
            if (_cloaked)
                return;
            if (IsCaptureReview)
            {
                await Model.ReplyActivation(false);
                return;
            }
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
        Model.CloseRequested += async (_, exiting) => await CloseWindow(exiting);
        Model.ConfirmExitRequested += async (_, _) => await ConfirmExit();
        // No limit sorts as the highest, so ascending lists limited torrents first.
        static long Rank(int limit) => limit > 0 ? limit : long.MaxValue;
        Torrents
            .Schema<Torrent>()
            .Key(row => row.TorrentId)
            .CanReorder(row => row.Queue >= 0)
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
            .SortKey(SeedsColumn, row => row.SeedCount)
            .SortKey(PeersColumn, row => row.LeecherCount)
            .SortKey(AddedColumn, row => row.Added);
        Torrents.Sort = new Syno.TableView.Sort(QueueColumn);
        Torrents.Placeholder = Syno.TableView.Placeholder.Loading;
        Torrents.SelectionChanged += async (_, _) => await SelectTorrent();
        Torrents.ItemInvoked += (_, _) => Run(Model.Properties);
        Torrents.ItemContextRequested += (_, args) => ShowSelectionMenu(args.Target, args.Position);
        Torrents.ReorderRequested += async (_, args) =>
            await Model.Reorder(args.Items.Cast<Torrent>(), args.Before as Torrent);
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        UpdateMinimum(scale);
        AppWindow.Resize(new SizeInt32((int)(1040 * scale), (int)(680 * scale)));
        RememberBounds();
        if (!IsCaptureReview)
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
                Key = VirtualKey.M,
                Modifiers = VirtualKeyModifiers.Control,
                ScopeOwner = Torrents,
            },
            Model.Force
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
        AddShortcut(
            new() { Key = VirtualKey.Left, Modifiers = VirtualKeyModifiers.Menu },
            Back
        );
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
        AddShortcut(
            new()
            {
                Key = VirtualKey.Delete,
                Modifiers = VirtualKeyModifiers.Shift,
                ScopeOwner = Torrents,
            },
            Model.DeleteFiles
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
        RefreshText();
    }

    private void Bind(
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
            string.IsNullOrEmpty(args.PropertyName)
            || args.PropertyName == nameof(MainViewModel.ShowsTitleSpeeds)
        )
            UpdateChrome();
        if (args.PropertyName == nameof(MainViewModel.Page))
            UpdatePage();
        if (Model.CanRestart)
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
        AddShortcut(accelerator, () => Run(command));
    }

    private void AddShortcut(
        KeyboardAccelerator accelerator,
        Action action,
        UIElement? target = null
    )
    {
        var selection = accelerator.ScopeOwner == Torrents;
        if (selection)
            accelerator.ScopeOwner = Root;
        accelerator.Invoked += (_, args) =>
        {
            if (HasDialog)
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
        if (HasDialog || Model.IsClosing)
            return;
        await Interact(async interaction =>
        {
            interaction.Restore = () => !interaction.IsResolved ? ShowAdd() : Task.CompletedTask;
            interaction.ResolveDraft = async () =>
            {
                if (!Model.AddDraft.HasChanges)
                    return true;
                return interaction.IsResolved = await ResolveDraft(
                    "add",
                    Model.AddDraft.Submit,
                    Model.AddDraft.Cancel
                );
            };
            _addDialog = new AddDialog(Model, Root.XamlRoot);
            _addDialog.DestinationRequested += async (_, _) => await PickDestination();
            _addDialog.AllowDrop = true;
            _addDialog.DragOver += OnDragOver;
            _addDialog.Drop += OnDrop;
            var neverShow = new CheckBox();
            AutomationProperties.SetAutomationId(neverShow, "NeverShow");
            Bind(
                neverShow,
                ToggleButton.IsCheckedProperty,
                nameof(AddDraft.NeverShow),
                BindingMode.TwoWay
            );
            Bind(neverShow, Control.IsEnabledProperty, nameof(AddDraft.CanEdit));
            var dialog = new Dialog
            {
                XamlRoot = Root.XamlRoot,
                Content = _addDialog,
                Footer = neverShow,
                DefaultButton = ContentDialogButton.Primary,
                PrimaryGlyph = Syno.Lucide.CirclePlus,
            };
            _addDialog.CloseRequested += (_, _) => dialog.Hide();
            dialog.Resources["ContentDialogMaxWidth"] = double.PositiveInfinity;
            dialog.Resources["ContentDialogMaxHeight"] = double.PositiveInfinity;
            Model.IsAddOpen = true;
            Bind(dialog, ContentDialog.IsPrimaryButtonEnabledProperty, nameof(AddDraft.CanSubmit));
            Bind(dialog, ContentDialog.PrimaryButtonTextProperty, nameof(AddDraft.SubmitText));
            Bind(dialog, Dialog.PrimaryToolTipProperty, nameof(AddDraft.SubmitToolTip));
            dialog.PrimaryButtonClick += async (_, args) =>
            {
                await Submit(interaction, args, Model.AddDraft.Submit);
                if (args.Cancel)
                    _addDialog?.FocusError();
            };
            dialog.Opened += (_, _) => _addDialog?.FocusError();
            dialog.Closing += (_, args) =>
            {
                if ((Model.AddDraft.IsSubmitting || Model.IsPicking) && !Model.IsClosing)
                    args.Cancel = true;
            };
            var completed = false;
            try
            {
                await ShowDialog(
                    interaction,
                    dialog,
                    () =>
                    {
                        AutomationProperties.SetName(dialog, Model.Text.Get("add", "title"));
                        dialog.CloseButtonText = Model.Text.Get("add", "cancel");
                        neverShow.Content = Model.Text.Get("add", "never_show");
                        _addDialog?.RefreshText();
                    }
                );
                interaction.IsResolved |= !Model.IsClosing;
                completed = true;
            }
            finally
            {
                _addDialog?.Dispose();
                dialog.Content = null;
                Model.IsAddOpen = false;
                if (!Model.IsClosing && completed)
                {
                    try
                    {
                        await Model.AddDraft.Cancel();
                    }
                    catch (Exception error)
                    {
                        Model.Report(error);
                    }
                }
                _addDialog = null;
            }
            return completed;
        });
    }

    private async Task PickSources()
    {
        if (!Model.CanEdit)
            return;
        Model.IsPicking = true;
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".torrent");
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker,
                WinRT.Interop.WindowNative.GetWindowHandle(this)
            );
            var files = await picker.PickMultipleFilesAsync();
            Model.IsPicking = false;
            if (files.Count > 0)
                await Model.AddPicked(files.Select(file => file.Path));
        }
        catch (Exception error)
        {
            Model.Report(error);
        }
        finally
        {
            Model.IsPicking = false;
        }
    }

    private async Task PickDestination()
    {
        var folder = await PickFolder();
        if (folder is not null)
            Model.AddDraft.Destination = folder;
    }

    private async Task<string?> PickFolder()
    {
        if (!Model.CanEdit)
            return null;
        Model.IsPicking = true;
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker,
                WinRT.Interop.WindowNative.GetWindowHandle(this)
            );
            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }
        catch (Exception error)
        {
            Model.Report(error);
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
        await CloseWindow(exiting: false);
    }

    private void OnDismissError(InfoBar sender, object args) => Model.ClearError();

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
        if (_cloaked || IsCaptureReview || HasDialog || Model.IsPicking || Model.IsClosing)
        {
            await Model.ReplyExit(ExitAnswer.Unavailable);
            return;
        }
        BringToFront();
        var answer = ExitAnswer.Cancelled;
        await Interact(async interaction =>
        {
            var dialog = new Dialog
            {
                XamlRoot = Root.XamlRoot,
                DefaultButton = ContentDialogButton.Primary,
                Glyph = Syno.Lucide.Power,
                PrimaryGlyph = Syno.Lucide.Power,
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
                    dialog.CloseButtonText = Model.Text.Get("add", "cancel");
                }
            );
            if (choice == ContentDialogResult.Primary)
                answer = ExitAnswer.Confirmed;
            return true;
        });
        await Model.ReplyExit(answer);
    }

    private async Task CloseWindow(bool exiting)
    {
        var focused = FocusManager.GetFocusedElement(Root.XamlRoot) as Control;
        var focusName = focused?.Name;
        _exiting |= exiting;
        if (!Model.BeginClose())
        {
            if (exiting)
                await Model.DeferClose();
            return;
        }
        DialogInteraction? suspended = null;
        try
        {
            // Unfinished input can keep the window open. Exit stays visible until
            // the engine accepts it, so a pending decision cannot look complete.
            if (
                !_exiting
                && !HasDialog
                && !Model.HasDraft
                && !Model.Settings.Schedule.HasDraft
                && !Model.IsPicking
            )
                AppWindow.Hide();
            if (_exiting)
                await Model.DeferClose();
            if (
                _interaction is { IsDraftDecision: true } decision
                && !await decision.Completion.Task
            )
                return;
            if (!Model.CanClose)
            {
                if (_exiting && Model.IsPicking)
                    await Model.DeferClose();
                var idle = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );
                void OnIdle(object? sender, PropertyChangedEventArgs args)
                {
                    if (Model.CanClose)
                        idle.TrySetResult();
                }
                Model.PropertyChanged += OnIdle;
                try
                {
                    if (Model.CanClose)
                        idle.TrySetResult();
                    await idle.Task;
                }
                finally
                {
                    Model.PropertyChanged -= OnIdle;
                }
            }
            if (!await Model.Settings.PrepareLeave())
                return;
            suspended = _interaction;
            suspended?.Dialog?.Hide();
            if (suspended is not null)
                await suspended.Completion.Task;
            if (suspended?.ResolveDraft is { } resolve && !await resolve())
                return;
            while (Model.HasDraft)
            {
                if (!AppWindow.IsVisible)
                    AppWindow.Show();
                if (_exiting)
                    await Model.DeferClose();
                if (!await ResolveRemainingDraft())
                    return;
            }
            if (!_exiting)
                AppWindow.Hide();
            await SavePlacement();
            await Model.Close(_exiting);
            _allowClose = true;
            Close();
        }
        catch (Exception error)
        {
            Model.Report(error);
        }
        finally
        {
            if (!_allowClose && !AppWindow.IsVisible)
                AppWindow.Show();
            if (!_allowClose && _exiting)
            {
                try
                {
                    await Model.CancelClose();
                }
                catch (Exception failure)
                {
                    Model.Report(failure);
                }
            }
            _exiting = false;
            if (!_allowClose)
                Model.EndClose();
            if (!_allowClose)
            {
                if (suspended?.Restore is { } restore)
                {
                    _ = restore();
                    await Model.ReplyActivation(true);
                }
                ShowDeferredAdd();
                // A page the person is already on stays as they left it, and
                // Recover moves only to the error.
                if (!HasDialog && Model.Settings.HasError)
                {
                    if (Model.Page != WindowPage.Settings)
                        await ShowSettings(new(SettingsCategory.General));
                    focused = _settingsPage?.Recover(focusName);
                }
                else if (!HasDialog && Model.Inspector.HasDraft && Model.Inspector.HasError)
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

    private Task<bool> ResolveRemainingDraft()
    {
        if (Model.AddDraft.HasChanges)
            return ResolveDraft("add", Model.AddDraft.Submit, Model.AddDraft.Cancel);
        if (Model.FileDraft.HasChanges)
            return ResolveDraft(
                "move",
                Model.FileDraft.Submit,
                () =>
                {
                    Model.FileDraft.Cancel();
                    return Task.CompletedTask;
                }
            );
        if (!Model.Inspector.HasDraft)
            return Task.FromResult(true);
        // As with Settings, an edit that cannot be saved, because the engine or
        // the torrent is gone, closes without it instead of trapping the person.
        if (Model.CanSave && Model.Inspector.IsAvailable)
            return Model.Inspector.Depart();
        Model.Inspector.CancelDraft();
        return Task.FromResult(true);
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
