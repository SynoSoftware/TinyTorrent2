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
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;
using Syno.TinyTorrent.Views;

namespace Syno.TinyTorrent;

public sealed partial class MainWindow : Window
{
    public MainViewModel Model { get; }

    private AddForm? _form;
    private bool _allowClose;
    private bool _engineExit;
    private bool _loaded;
    private readonly Dictionary<ICommand, KeyboardAccelerator> _shortcuts = [];

    public MainWindow(Strings strings)
    {
        Model = new MainViewModel(strings, DispatcherQueue);
        InitializeComponent();
        ConfigureCapture();
        Filters.ItemsSource = Model.Filters;
        Split.ValueChanged += (_, value) => { _splitHeight = value; UpdateInspectorSize(); };
        Workspace.SizeChanged += (_, _) => UpdateInspectorSize();
        Root.SizeChanged += (_, args) =>
        {
            var narrow = args.NewSize.Width < 960;
            Grid.SetRow(Incoming, narrow ? 1 : 0);
            Grid.SetColumn(Incoming, narrow ? 0 : 3);
            Grid.SetColumnSpan(Incoming, narrow ? 4 : 1);
            Incoming.Margin = new Thickness(0, narrow ? 8 : 0, 0, 0);
            Incoming.TextAlignment = narrow ? TextAlignment.Left : TextAlignment.Right;
        };
        FiltersClose.Content = new FontIcon { FontFamily = Syno.Lucide.Font, Glyph = Syno.Lucide.X, FontSize = 16 };
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "TinyTorrent.ico"));
        Caption.SizeChanged += (_, _) => UpdateChrome();
        Menus.SizeChanged += (_, _) => UpdateChrome();
        SearchArea.SizeChanged += (_, _) => UpdateChrome();
        Search.SizeChanged += (_, _) => UpdateChrome();
        AppWindow.Changed += OnWindowChanged;
        Root.ActualThemeChanged += (_, _) => { UpdateColors(); RefreshDialogs(); };
        Model.PropertyChanged += OnModelChanged;
        Model.TextChanged += (_, _) => RefreshText();
        Model.AnnouncementRequested += (_, message) =>
            FrameworkElementAutomationPeer.CreatePeerForElement(Torrents)?.RaiseNotificationEvent(
                AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.All, message, "command");
        Model.SnapshotApplied += (_, change) =>
        {
            if (!change.Published && (change.EligibilityChanged || Torrents.Sort is { Column: var column } && column != QueueColumn))
                Torrents.RefreshView();
        };
        Model.RevealRequested += async (_, torrent) =>
        {
            if (_interaction is { } interaction) await interaction.Completion.Task;
            if (!await ShowTorrents()) return;
            if (await SelectTorrent(new Syno.TableView.Selection([torrent], torrent))) Torrents.ScrollIntoView(torrent);
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
        Model.MoveRequested += async (_, torrents) => await ShowFiles(torrents, FileAction.Move);
        Model.DeleteRequested += async (_, torrents) => await ShowFiles(torrents, FileAction.Delete);
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
            // A window still waiting to appear answers when it appears.
            if (_cloaked) return;
            if (IsCaptureReview) { await Model.Activated(false); return; }
            if (Model.IsClosing) { await Model.Activated(false); return; }
            if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
                presenter.Restore();
            Activate();
            SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
            await Model.Activated(true);
            if (Model.Page == WindowPage.Preferences) await Model.Preferences.ObserveRegistration();
        };
        Model.ShowRequested += async (_, _) => await ShowWhenReady();
        Model.CloseRequested += async (_, engineExit) => await CloseWindow(engineExit);
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
        Torrents.ItemContextRequested += (_, args) => ShowSelectionMenu(args.Target, args.Position);
        Torrents.ReorderRequested += async (_, args) => await Model.Reorder(args.Items.Cast<Torrent>(), args.Before as Torrent);
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        UpdateMinimum(scale);
        AppWindow.Resize(new SizeInt32((int)(1040 * scale), (int)(680 * scale)));
        RememberBounds();
        if (!IsCaptureReview) SetCloak(true);
        AppWindow.Closing += OnClosing;
        Closed += (_, _) => Model.Dispose();
        AddShortcut(new() { Key = VirtualKey.O, Modifiers = VirtualKeyModifiers.Control }, Model.Add);
        AddShortcut(new() { Key = VirtualKey.W, Modifiers = VirtualKeyModifiers.Control }, () => _ = CloseWindow(engineExit: false));
        AddShortcut(new() { Key = VirtualKey.Q, Modifiers = VirtualKeyModifiers.Control }, Model.Exit);
        AddShortcut(new() { Key = VirtualKey.P, Modifiers = VirtualKeyModifiers.Control, ScopeOwner = Torrents }, Model.Pause);
        AddShortcut(new() { Key = VirtualKey.S, Modifiers = VirtualKeyModifiers.Control, ScopeOwner = Torrents }, Model.Resume);
        AddShortcut(new() { Key = VirtualKey.M, Modifiers = VirtualKeyModifiers.Control, ScopeOwner = Torrents }, Model.Force);
        AddShortcut(new() { Key = VirtualKey.R, Modifiers = VirtualKeyModifiers.Control, ScopeOwner = Torrents }, Model.Verify);
        AddShortcut(new() { Key = VirtualKey.V, Modifiers = VirtualKeyModifiers.Control, ScopeOwner = Torrents }, () => _ = PasteSources());
        AddShortcut(new() { Key = VirtualKey.F, Modifiers = VirtualKeyModifiers.Control }, FocusSearch);
        AddShortcut(new() { Key = VirtualKey.E, Modifiers = VirtualKeyModifiers.Control }, FocusSearch);
        AddShortcut(new() { Key = VirtualKey.K, Modifiers = VirtualKeyModifiers.Control }, FocusSearch);
        AddShortcut(new() { Key = VirtualKey.O, Modifiers = VirtualKeyModifiers.Menu }, Model.ShowPreferences);
        AddShortcut(new() { Key = VirtualKey.Left, Modifiers = VirtualKeyModifiers.Menu }, Model.ShowTorrents);
        AddShortcut(new() { Key = VirtualKey.O, Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift }, Model.AddMagnet);
        AddShortcut(new() { Key = VirtualKey.P, Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift }, Model.PauseAll);
        AddShortcut(new() { Key = VirtualKey.S, Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift }, Model.ResumeAll);
        AddShortcut(new() { Key = VirtualKey.Delete, ScopeOwner = Torrents }, Model.Remove);
        AddShortcut(new() { Key = VirtualKey.Delete, Modifiers = VirtualKeyModifiers.Shift, ScopeOwner = Torrents }, Model.DeleteFiles);
        AddShortcut(new() { Key = VirtualKey.Add, Modifiers = VirtualKeyModifiers.Control, ScopeOwner = Torrents }, Model.Up);
        AddShortcut(new() { Key = VirtualKey.Subtract, Modifiers = VirtualKeyModifiers.Control, ScopeOwner = Torrents }, Model.Down);
        AddShortcut(new() { Key = VirtualKey.Add, Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, ScopeOwner = Torrents }, Model.Top);
        AddShortcut(new() { Key = VirtualKey.Subtract, Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, ScopeOwner = Torrents }, Model.Bottom);
        Root.KeyDown += OnQueueKey;
        RefreshText();
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
        if (Model.CanRestart) Reveal();
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

    private void AddShortcut(KeyboardAccelerator accelerator, ICommand command)
    {
        _shortcuts.Add(command, accelerator);
        AddShortcut(accelerator, () => Run(command));
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
        if (HasDialog || Model.IsClosing) return;
        await Interact(async interaction =>
        {
            interaction.Restore = () => !interaction.IsResolved ? ShowAdd() : Task.CompletedTask;
            interaction.ResolveDraft = async () =>
            {
                if (!Model.Draft.HasChanges) return true;
                return interaction.IsResolved = await ResolveDraft(Model.Draft.Submit, Model.Draft.Cancel);
            };
            _form = new AddForm(Model);
            _form.FilesRequested += async (_, _) => await PickSources();
            _form.DestinationRequested += async (_, _) => await PickDestination();
            _form.AllowDrop = true;
            _form.DragOver += OnDragOver;
            _form.Drop += OnDrop;
            _form.Width = Math.Min(960, Root.ActualWidth - 80);
            _form.Height = Math.Clamp(Root.ActualHeight - 260, 280, 540);
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Content = _form, DefaultButton = ContentDialogButton.Primary };
            dialog.Resources["ContentDialogMaxWidth"] = 1008d;
            Model.IsAddOpen = true;
            Bind(dialog, ContentDialog.IsPrimaryButtonEnabledProperty, nameof(AddDraft.CanSubmit));
            Bind(dialog, ContentDialog.PrimaryButtonTextProperty, nameof(AddDraft.SubmitText));
            dialog.PrimaryButtonClick += OnSubmit;
            dialog.Closing += (_, args) => { if ((Model.Draft.IsSubmitting || Model.IsPicking) && !Model.IsClosing) args.Cancel = true; };
            var completed = false;
            try
            {
                await ShowDialog(interaction, dialog, () =>
                {
                    dialog.Title = Model.Text.Get("add", "title");
                    dialog.CloseButtonText = Model.Text.Get("add", "cancel");
                    _form?.RefreshText();
                });
                interaction.IsResolved |= !Model.IsClosing;
                completed = true;
            }
            finally
            {
                dialog.Content = null;
                Model.IsAddOpen = false;
                if (!Model.IsClosing && completed)
                {
                    try { await Model.Draft.Cancel(); }
                    catch (Exception error) { Model.Report(error); }
                }
                _form = null;
            }
            return completed;
        });
    }

    private async Task PickSources()
    {
        if (!Model.CanEdit) return;
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
        var folder = await PickFolder();
        if (folder is not null) Model.Draft.Destination = folder;
    }

    private async Task<string?> PickFolder()
    {
        if (!Model.CanEdit) return null;
        Model.IsPicking = true;
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }
        catch (Exception error) { Model.Report(error); }
        finally { Model.IsPicking = false; }
        return null;
    }

    private async void OnSubmit(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        var deferral = args.GetDeferral();
        try
        {
            args.Cancel = !await Model.Draft.Submit();
            if (!args.Cancel && _interaction is { } interaction) interaction.IsResolved = true;
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
        await CloseWindow(engineExit: false);
    }

    private void OnDismissError(InfoBar sender, object args) => Model.ClearError();

    private async Task CloseWindow(bool engineExit)
    {
        var focused = FocusManager.GetFocusedElement(Root.XamlRoot) as Control;
        var focusName = focused?.Name;
        _engineExit |= engineExit;
        if (!Model.BeginClose())
        {
            if (engineExit) await Model.DeferClose();
            return;
        }
        DialogInteraction? suspended = null;
        try
        {
            if (_engineExit) await Model.DeferClose();
            if (_interaction is { IsDraftDecision: true } decision && !await decision.Completion.Task) return;
            if (!await Model.Preferences.PrepareLeave()) return;
            if (!Model.CanClose)
            {
                if (_engineExit && Model.IsPicking) await Model.DeferClose();
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
            suspended = _interaction;
            suspended?.Dialog?.Hide();
            if (suspended is not null) await suspended.Completion.Task;
            if (suspended?.ResolveDraft is { } resolve && !await resolve()) return;
            while (Model.HasDraft)
            {
                if (_engineExit) await Model.DeferClose();
                if (!await ResolveRemainingDraft()) return;
            }
            await SavePlacement();
            await Model.Close(_engineExit);
            _allowClose = true;
            Close();
        }
        catch (Exception error)
        {
            Model.Report(error);
        }
        finally
        {
            if (!_allowClose && _engineExit)
            {
                try { await Model.CancelClose(); }
                catch (Exception failure) { Model.Report(failure); }
            }
            _engineExit = false;
            if (!_allowClose) Model.EndClose();
            if (!_allowClose)
            {
                if (suspended?.Restore is { } restore)
                {
                    _ = restore();
                    await Model.Activated(true);
                }
                ShowDeferredAdd();
                if (!HasDialog && Model.Preferences.HasDraft && Model.Preferences.Schedule.HasScheduleError)
                {
                    await ShowPreferences(new(PreferenceSection.Schedule));
                    var scheduler = (_preferencesForm?.FindName("ScheduleContent") as ContentControl)?.Content as FrameworkElement;
                    focused = scheduler?.FindName(focusName ?? "StartTime") as Control ?? scheduler?.FindName("StartTime") as Control;
                }
                else if (!HasDialog && Model.Inspector.HasDraft && Model.Inspector.HasError)
                {
                    Model.Inspector.Select(Model.Inspector.IsEditingTrackers ? InspectorSection.Trackers : InspectorSection.Files);
                    await ShowTorrents();
                    focused = (InspectorContent.Content as FrameworkElement)?.FindName(
                        Model.Inspector.IsEditingTrackers ? "TrackerInput" : "RetryFiles") as Control;
                }
                if (focused is { IsLoaded: true })
                    DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => focused.Focus(FocusState.Programmatic));
                else if (!string.IsNullOrEmpty(focusName))
                {
                    var form = _filesForm as FrameworkElement ?? _form;
                    DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                        () => (form?.FindName(focusName) as Control)?.Focus(FocusState.Programmatic));
                }
            }
        }
    }

    private Task<bool> ResolveRemainingDraft()
    {
        if (Model.Draft.HasChanges) return ResolveDraft(Model.Draft.Submit, Model.Draft.Cancel);
        if (Model.Files.HasDraft) return ResolveDraft(Model.Files.Submit, () => { Model.Files.Cancel(); return Task.CompletedTask; });
        if (Model.Preferences.HasDraft) return ResolveDraft(Model.Preferences.Schedule.CommitPeriod, () =>
            { Model.Preferences.Schedule.CancelDraft(); return Task.CompletedTask; });
        if (Model.Inspector.HasDraft) return ResolveDraft(Model.Inspector.SaveDraft, () =>
            { Model.Inspector.CancelDraft(); return Task.CompletedTask; });
        return Task.FromResult(true);
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}
