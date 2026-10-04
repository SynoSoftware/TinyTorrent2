using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.System;

namespace Syno.TinyTorrent;

public sealed partial class MainWindow : Window
{
    public MainViewModel Model { get; }

    private readonly TextBox _source = new() { IsReadOnly = true };
    private readonly TextBox _destination = new();
    private readonly CheckBox _paused = new();
    private readonly TextBlock _preview = new() { TextWrapping = TextWrapping.Wrap };
    private readonly InfoBar _addError = new() { IsClosable = false };
    private readonly FontIcon _themeIcon = new() { FontFamily = Syno.Lucide.Font, FontSize = 16 };
    private ContentDialog? _addDialog;
    private ContentDialog? _closePrompt;
    private Button? _sourceButton;
    private Button? _destinationButton;
    private TaskCompletionSource? _dialogClosed;
    private bool _allowClose;
    private bool _closing;
    private bool _loaded;

    public MainWindow(Strings strings)
    {
        Model = new MainViewModel(strings, DispatcherQueue);
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        SetTitleBar(Caption);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "TinyTorrent.ico"));
        AddButton.Content = new FontIcon { FontFamily = Syno.Lucide.Font, Glyph = Syno.Lucide.Plus, FontSize = 16 };
        PauseButton.Content = new FontIcon { FontFamily = Syno.Lucide.Font, Glyph = Syno.Lucide.Pause, FontSize = 16 };
        ResumeButton.Content = new FontIcon { FontFamily = Syno.Lucide.Font, Glyph = Syno.Lucide.Play, FontSize = 16 };
        ExitButton.Content = new FontIcon { FontFamily = Syno.Lucide.Font, Glyph = Syno.Lucide.Power, FontSize = 16 };
        ThemeButton.Content = _themeIcon;
        Caption.SizeChanged += (_, _) => UpdateChrome();
        CaptionActions.SizeChanged += (_, _) => UpdateChrome();
        AppWindow.Changed += (_, _) => UpdateChrome();
        Root.ActualThemeChanged += (_, _) => { UpdateColors(); RefreshDialogs(); };
        Model.PropertyChanged += OnModelChanged;
        Model.TextChanged += (_, _) => RefreshText();
        Model.SnapshotApplied += (_, _) => Torrents.RefreshView();
        Model.RevealRequested += (_, torrent) =>
        {
            Torrents.Selection = new Syno.TableView.Selection([torrent], torrent);
            Torrents.ScrollIntoView(torrent);
        };
        Model.AddRequested += async (_, _) =>
        {
            try { await ShowAdd(); }
            catch (Exception error) { Model.Report(error); }
        };
        Model.ActivateRequested += (_, _) =>
        {
            if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
                presenter.Restore();
            Activate();
        };
        Model.CloseRequested += async (_, engineExit) => await CloseWindow(engineExit);
        RefreshText();
        Torrents.Schema<Torrent>().Key(row => row.TorrentId)
            .SortKey(NameColumn, row => row.Name, StringComparer.CurrentCultureIgnoreCase)
            .SortKey(SizeColumn, row => row.Size).SortKey(ProgressColumn, row => row.Progress)
            .SortKey(StatusColumn, row => row.Status).SortKey(DownColumn, row => row.DownloadRate)
            .SortKey(UpColumn, row => row.UploadRate);
        Torrents.Placeholder = Syno.TableView.Placeholder.Loading;
        Torrents.SelectionChanged += (_, _) => Model.Select(Torrents.Selection.Items.Cast<Torrent>(), Torrents.Selection.Current as Torrent);
        Bind(_source, TextBox.TextProperty, nameof(AddDraft.Source));
        Bind(_destination, TextBox.TextProperty, nameof(AddDraft.Destination), BindingMode.TwoWay);
        Bind(_paused, CheckBox.IsCheckedProperty, nameof(AddDraft.Paused), BindingMode.TwoWay);
        Bind(_preview, TextBlock.TextProperty, nameof(AddDraft.Preview));
        Bind(_addError, InfoBar.MessageProperty, nameof(AddDraft.Message));
        Bind(_addError, InfoBar.IsOpenProperty, nameof(AddDraft.HasError));
        Bind(_addError, InfoBar.SeverityProperty, nameof(AddDraft.Severity));
        Bind(_destination, TextBox.IsEnabledProperty, nameof(AddDraft.CanEdit));
        Bind(_paused, CheckBox.IsEnabledProperty, nameof(AddDraft.CanEdit));
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        UpdateMinimum(scale);
        AppWindow.Resize(new SizeInt32((int)(1040 * scale), (int)(680 * scale)));
        AppWindow.Closing += OnClosing;
        Closed += (_, _) => Model.Dispose();
        AddShortcut(VirtualKey.O, () => Run(Model.Add), AddButton);
        AddShortcut(VirtualKey.W, () => _ = CloseWindow(false));
        AddShortcut(VirtualKey.Q, () => Run(Model.Exit), ExitButton);
        AddShortcut(VirtualKey.P, () => Run(Model.Pause), PauseButton, Torrents);
        AddShortcut(VirtualKey.S, () => Run(Model.Resume), ResumeButton, Torrents);
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
        if (string.IsNullOrEmpty(args.PropertyName) || args.PropertyName == nameof(MainViewModel.Theme))
            Root.RequestedTheme = Model.Theme switch { "light" => ElementTheme.Light, "dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        if (string.IsNullOrEmpty(args.PropertyName) || args.PropertyName == nameof(MainViewModel.IsLoading))
            Torrents.Placeholder = Model.IsLoading ? Syno.TableView.Placeholder.Loading : Syno.TableView.Placeholder.Empty;
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

    private void AddShortcut(VirtualKey key, Action action, UIElement? target = null, DependencyObject? scope = null)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = VirtualKeyModifiers.Control, ScopeOwner = scope };
        accelerator.Invoked += (_, args) =>
        {
            if (_addDialog is not null || Root.XamlRoot?.Content is null) return;
            action();
            args.Handled = true;
        };
        var owner = target ?? Root;
        if (owner == Root) owner.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        owner.KeyboardAccelerators.Add(accelerator);
    }

    private async Task ShowAdd()
    {
        if (_addDialog is not null) return;
        if (Model.Draft.Source.Length == 0 && !await PickSource()) return;
        _sourceButton = new Button();
        Bind(_sourceButton, Button.IsEnabledProperty, nameof(AddDraft.CanEdit));
        _sourceButton.Click += async (_, _) => await PickSource();
        _destinationButton = new Button();
        Bind(_destinationButton, Button.IsEnabledProperty, nameof(AddDraft.CanEdit));
        _destinationButton.Click += async (_, _) => await PickDestination();
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(_source);
        body.Children.Add(_sourceButton);
        body.Children.Add(_preview);
        body.Children.Add(_destination);
        body.Children.Add(_destinationButton);
        body.Children.Add(_paused);
        body.Children.Add(_addError);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Content = body, DefaultButton = ContentDialogButton.Primary };
        _addDialog = dialog;
        Model.IsAddOpen = true;
        RefreshText();
        Bind(dialog, ContentDialog.IsPrimaryButtonEnabledProperty, nameof(AddDraft.CanSubmit));
        _dialogClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.PrimaryButtonClick += OnSubmit;
        dialog.Closing += (_, args) => { if (Model.IsBusy && !_closing) args.Cancel = true; };
        try { await dialog.ShowAsync(); }
        catch (Exception error) { Model.Report(error); }
        finally
        {
            body.Children.Clear();
            _addDialog = null;
            Model.IsAddOpen = false;
            _dialogClosed.TrySetResult();
        }
        if (!_closing)
        {
            try { await Model.CancelDraft(); }
            catch (Exception error) { Model.Report(error); }
        }
    }

    private async Task<bool> PickSource()
    {
        if (Model.IsBusy || Model.IsPicking) return false;
        Model.IsPicking = true;
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".torrent");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            Model.IsPicking = false;
            return file is not null && await Model.Draft.Prepare(file.Path);
        }
        catch (Exception error) { Model.Report(error); return false; }
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
            if (folder is not null) Model.Draft.Destination = folder.Path;
        }
        catch (Exception error) { Model.Report(error); }
        finally { Model.IsPicking = false; }
    }

    private async void OnSubmit(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        var deferral = args.GetDeferral();
        sender.PrimaryButtonText = Model.Text.Get("add", "pending");
        try { args.Cancel = !await Model.Draft.Submit(); }
        finally
        {
            sender.PrimaryButtonText = Model.Text.Get("add", "submit");
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
        if (_closing) return;
        _closing = true;
        try
        {
            if (!Model.CanClose)
            {
                if (engineExit) await Model.CancelClose();
                return;
            }
            var wasOpen = _addDialog is not null;
            _addDialog?.Hide();
            if (wasOpen && _dialogClosed is not null) await _dialogClosed.Task;
            if (Model.HasDraft)
            {
                var prompt = new ContentDialog { XamlRoot = Root.XamlRoot, DefaultButton = ContentDialogButton.Close };
                _closePrompt = prompt;
                RefreshText();
                var choice = await prompt.ShowAsync();
                _closePrompt = null;
                if (choice != ContentDialogResult.Primary)
                {
                    if (engineExit) await Model.CancelClose();
                    if (wasOpen) _ = ShowAdd();
                    return;
                }
                await Model.CancelDraft();
            }
            await Model.Close(engineExit);
            _allowClose = true;
            Close();
        }
        catch (Exception error) { Model.Report(error); }
        finally { _closing = false; }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);
}
