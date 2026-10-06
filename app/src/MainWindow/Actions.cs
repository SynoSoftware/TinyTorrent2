using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Data;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.System;
using Syno.TinyTorrent.Helpers;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Views;

namespace Syno.TinyTorrent;

public sealed partial class MainWindow
{
    private ContentDialog? _limitsDialog;
    private TaskCompletionSource? _limitsClosed;
    private ContentDialog? _removeDialog;
    private TaskCompletionSource? _removeClosed;
    private ContentDialog? _filesDialog;
    private FileForm? _filesForm;
    private TaskCompletionSource? _filesClosed;
    private bool HasDialog => _addDialog is not null || _limitsDialog is not null || _removeDialog is not null || _filesDialog is not null || _closePrompt is not null;

    private void OnQueueKey(object sender, KeyRoutedEventArgs args)
    {
        // VirtualKey omits the Windows OEM plus and minus codes.
        const VirtualKey plus = (VirtualKey)0xBB;
        const VirtualKey minus = (VirtualKey)0xBD;
        if (args.Key != plus && args.Key != minus || HasDialog || Model.Page != WindowPage.Torrents || HasEditorFocus()) return;
        if (!Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return;
        if (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return;
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var command = args.Key == plus ? shift ? Model.Top : Model.Up : shift ? Model.Bottom : Model.Down;
        if (!command.CanExecute(null)) return;
        command.Execute(null);
        args.Handled = true;
    }

    private void RefreshMenus()
    {
        FileMenu.Title = Model.Text.Get("menus", "file");
        FileMenu.AccessKey = Model.Text.Get("menus", "file_key");
        TorrentMenu.Title = Model.Text.Get("menus", "torrent");
        TorrentMenu.AccessKey = Model.Text.Get("menus", "torrent_key");
        ViewMenu.Title = Model.Text.Get("menus", "view");
        ViewMenu.AccessKey = Model.Text.Get("menus", "view_key");
        HelpMenu.Title = Model.Text.Get("menus", "help");
        HelpMenu.AccessKey = Model.Text.Get("menus", "help_key");
        AutomationProperties.SetName(Menus, Model.Text.Get("commands", "menu"));
        FileMenu.Items.Clear();
        FileMenu.Items.Add(Menu("add_file", Model.Add, Syno.Lucide.FilePlus, "Ctrl+O"));
        FileMenu.Items.Add(Menu("add_magnet", Model.AddMagnet, Syno.Lucide.Link, "Ctrl+Shift+O"));
        FileMenu.Items.Add(new MenuFlyoutSeparator());
        FileMenu.Items.Add(new MenuFlyoutItem { Text = Model.Text.Get("finding", "settings"),
            Command = Model.ShowPreferences, Icon = Icon(Syno.Lucide.Settings), KeyboardAcceleratorTextOverride = "Alt+O" });
        FileMenu.Items.Add(new MenuFlyoutSeparator());
        FileMenu.Items.Add(Menu("exit", Model.Exit, Syno.Lucide.Power, "Ctrl+Q"));
        TorrentMenu.Items.Clear();
        var selection = new MenuFlyoutItem { IsEnabled = false };
        selection.SetBinding(MenuFlyoutItem.TextProperty, new Binding { Source = Model,
            Path = new PropertyPath(nameof(MainViewModel.SelectionText)), Mode = BindingMode.OneWay });
        TorrentMenu.Items.Add(selection);
        TorrentMenu.Items.Add(new MenuFlyoutSeparator());
        AddSelection(TorrentMenu.Items, true);
        TorrentMenu.Items.Add(new MenuFlyoutSeparator());
        TorrentMenu.Items.Add(Menu("pause_all", Model.PauseAll, Syno.Lucide.Pause, "Ctrl+Shift+P"));
        TorrentMenu.Items.Add(Menu("resume_all", Model.ResumeAll, Syno.Lucide.Play, "Ctrl+Shift+S"));
        TorrentMenu.Items.Add(Menu("limits", Model.Limits, Syno.Lucide.Gauge));
        ClearFiltersButton.Content = Model.Text.Get("window", "clear_filters");
        HelpMenu.Items.Clear();
        HelpMenu.Items.Add(new MenuFlyoutItem { Text = Model.Text.Get("about", "title"), Command = Model.ShowAbout, Icon = Icon(Syno.Lucide.Info) });
    }

    private void ShowSelectionMenu(FrameworkElement target, Point? position)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(new MenuFlyoutItem { Text = Torrents.Selection.Items.Count == 1 ?
            ((Torrent)Torrents.Selection.Items[0]).Name : Model.SelectionText, IsEnabled = false });
        menu.Items.Add(new MenuFlyoutSeparator());
        AddSelection(menu.Items, !Model.HasInspector);
        if (position is { } point) menu.ShowAt(target, point); else menu.ShowAt(target);
    }

    private void AddSelection(IList<MenuFlyoutItemBase> items, bool properties)
    {
        items.Add(Menu("pause", Model.Pause, Syno.Lucide.Pause, "Ctrl+P"));
        items.Add(Menu("resume", Model.Resume, Syno.Lucide.Play, "Ctrl+S"));
        items.Add(Menu("force", Model.Force, Syno.Lucide.Zap, "Ctrl+M"));
        items.Add(new MenuFlyoutSeparator());
        items.Add(Menu("open", Model.Open, Syno.Lucide.File));
        items.Add(Menu("open_folder", Model.OpenFolder, Syno.Lucide.FolderOpen));
        items.Add(Menu("copy_magnet", Model.CopyMagnet, Syno.Lucide.Link));
        items.Add(Menu("copy_hash", Model.CopyHash, Syno.Lucide.Hash));
        if (properties) items.Add(Menu("properties", Model.Properties, Syno.Lucide.Info));
        items.Add(new MenuFlyoutSeparator());
        items.Add(Menu("verify", Model.Verify, Syno.Lucide.RefreshCw, "Ctrl+R"));
        items.Add(Menu("move", Model.MoveFiles, Syno.Lucide.FolderInput));
        var queue = new MenuFlyoutSubItem { Text = Model.Text.Get("menus", "queue"), Icon = Icon(Syno.Lucide.ListOrdered) };
        queue.Items.Add(Menu("up", Model.Up, Syno.Lucide.ArrowUp, "Ctrl++"));
        queue.Items.Add(Menu("down", Model.Down, Syno.Lucide.ArrowDown, "Ctrl+-"));
        queue.Items.Add(Menu("top", Model.Top, Syno.Lucide.ArrowUpToLine, "Ctrl+Shift++"));
        queue.Items.Add(Menu("bottom", Model.Bottom, Syno.Lucide.ArrowDownToLine, "Ctrl+Shift+-"));
        items.Add(queue);
        items.Add(new MenuFlyoutSeparator());
        items.Add(Menu("remove", Model.Remove, Syno.Lucide.X, "Delete"));
        items.Add(Menu("delete_files", Model.DeleteFiles, Syno.Lucide.Trash2, "Shift+Delete"));
    }

    private MenuFlyoutItem Menu(string name, ICommand command, string glyph, string shortcut = "") =>
        new() { Text = Model.Text.Get("commands", name), Command = command,
            Icon = Icon(glyph), KeyboardAcceleratorTextOverride = shortcut };

    private static FontIcon Icon(string glyph) => new() { FontFamily = Syno.Lucide.Font, Glyph = glyph };

    private async Task ConfirmRemove(Torrent[] torrents)
    {
        if (HasDialog) return;
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, FlowDirection = Root.FlowDirection,
            Title = Model.Text.Get("remove", "title"),
            Content = Model.Text.Format("remove", "detail", string.Join(Environment.NewLine, torrents.Select(torrent => torrent.Name))),
            Tag = torrents,
            PrimaryButtonText = Model.Text.Get("commands", "remove"), CloseButtonText = Model.Text.Get("add", "cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        _removeDialog = dialog;
        _removeClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) await Model.RemoveTorrents(torrents);
        }
        catch (Exception error) { Model.Report(error); }
        finally
        {
            _removeDialog = null;
            _removeClosed.TrySetResult();
            if (!Model.IsClosing && (Model.Draft.Sources.Count > 0 || Model.Draft.EditingMagnet)) _ = ShowAdd();
        }
    }

    private async Task ShowFiles(Torrent[] torrents, FileAction action, bool initialize = true)
    {
        if (HasDialog || Model.IsClosing) return;
        if (initialize) Model.Files.Begin(torrents, action);
        var form = new FileForm(Model);
        form.DestinationRequested += async (_, _) =>
        {
            var folder = await PickFolder();
            if (folder is not null) Model.Files.Destination = folder;
        };
        var body = new ScrollViewer { Content = form, Width = Math.Min(560, Root.ActualWidth - 80),
            MaxHeight = Math.Max(220, Root.ActualHeight - 180), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Content = body,
            DefaultButton = action == FileAction.Delete ? ContentDialogButton.Close : ContentDialogButton.Primary };
        dialog.Resources["ContentDialogMaxWidth"] = 608d;
        dialog.SetBinding(ContentDialog.IsPrimaryButtonEnabledProperty, new Binding { Source = Model.Files,
            Path = new PropertyPath(nameof(FileOperation.CanSubmit)), Mode = BindingMode.OneWay });
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            var deferral = args.GetDeferral();
            try { args.Cancel = !await Model.Files.Submit(); }
            finally { deferral.Complete(); }
        };
        dialog.Closing += (_, args) => { if ((Model.Files.IsPending || Model.IsPicking) && !Model.IsClosing) args.Cancel = true; };
        _filesDialog = dialog;
        _filesForm = form;
        _filesClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RefreshText();
        try
        {
            if (initialize) _ = Model.Files.RefreshScope();
            await dialog.ShowAsync();
        }
        catch (Exception error) { Model.Report(error); }
        finally
        {
            body.Content = null;
            _filesDialog = null;
            _filesForm = null;
            if (!Model.IsClosing) Model.Files.Cancel();
            _filesClosed.TrySetResult();
            if (!Model.IsClosing && (Model.Draft.Sources.Count > 0 || Model.Draft.EditingMagnet)) _ = ShowAdd();
        }
    }

    private async Task ShowLimits(bool initialize = true)
    {
        if (HasDialog) return;
        if (initialize) Model.Speed.Begin();
        var body = new StackPanel { Spacing = 12 };
        foreach (var choice in Model.Speed.Choices)
        {
            var editor = new NumberBox { DataContext = choice, Header = Model.Text.Get("limits", choice.Name), Minimum = 0,
                Maximum = int.MaxValue / 1024.0, ValidationMode = NumberBoxValidationMode.Disabled,
                Text = choice.Input, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
            editor.Loaded += (_, _) =>
            {
                if (TextEditor.Find(editor) is { } input)
                    input.TextChanged += (_, _) => { if (editor.IsEnabled) choice.Input = input.Text; };
            };
            editor.SetBinding(Control.IsEnabledProperty, new Binding { Source = Model.Speed, Path = new PropertyPath(nameof(SpeedLimits.CanApply)), Mode = BindingMode.OneWay });
            body.Children.Add(editor);
        }
        body.Children.Add(new TextBlock { Text = Model.Text.Get("limits", "units"), TextWrapping = TextWrapping.Wrap });
        var feedback = new InfoBar { IsClosable = false, Severity = InfoBarSeverity.Error };
        feedback.SetBinding(InfoBar.MessageProperty, new Binding { Source = Model.Speed, Path = new PropertyPath(nameof(SpeedLimits.Message)), Mode = BindingMode.OneWay });
        feedback.SetBinding(InfoBar.IsOpenProperty, new Binding { Source = Model.Speed, Path = new PropertyPath(nameof(SpeedLimits.HasError)), Mode = BindingMode.OneWay });
        body.Children.Add(feedback);
        var restart = new Button { Command = Model.Restart };
        AutomationProperties.SetAutomationId(restart, "Restart");
        restart.SetBinding(ContentControl.ContentProperty, new Binding { Source = Model, Path = new PropertyPath(nameof(MainViewModel.RestartText)), Mode = BindingMode.OneWay });
        var connection = new InfoBar { IsClosable = false, Severity = InfoBarSeverity.Error, ActionButton = restart };
        connection.SetBinding(InfoBar.MessageProperty, new Binding { Source = Model, Path = new PropertyPath(nameof(MainViewModel.Message)), Mode = BindingMode.OneWay });
        connection.SetBinding(InfoBar.IsOpenProperty, new Binding { Source = Model, Path = new PropertyPath(nameof(MainViewModel.CanRestart)), Mode = BindingMode.OneWay });
        body.Children.Add(connection);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, FlowDirection = Root.FlowDirection,
            Title = Model.Text.Get("commands", "limits"), Content = body, PrimaryButtonText = Model.Text.Get("limits", "apply"),
            CloseButtonText = Model.Text.Get("add", "cancel"), DefaultButton = ContentDialogButton.Primary };
        dialog.SetBinding(ContentDialog.IsPrimaryButtonEnabledProperty, new Binding { Source = Model.Speed, Path = new PropertyPath(nameof(SpeedLimits.CanApply)), Mode = BindingMode.OneWay });
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            var deferral = args.GetDeferral();
            try { args.Cancel = !await Model.Speed.Apply(); }
            finally { deferral.Complete(); }
        };
        dialog.Closing += (_, args) => { if (Model.Speed.IsPending && !Model.IsClosing) args.Cancel = true; };
        _limitsDialog = dialog;
        _limitsClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try { await dialog.ShowAsync(); }
        catch (Exception error) { Model.Report(error); }
        finally
        {
            _limitsDialog = null;
            _limitsClosed.TrySetResult();
            if (!Model.IsClosing) Model.Speed.Begin();
            if (!Model.IsClosing && (Model.Draft.Sources.Count > 0 || Model.Draft.EditingMagnet)) _ = ShowAdd();
        }
    }

    private async void OnInspectorClose(object sender, RoutedEventArgs args)
    {
        if (Model.Inspector.IsPending) return;
        if (Model.Inspector.HasDraft)
        {
            if (!await ConfirmDiscard()) return;
            Model.Inspector.CancelDraft();
        }
        Model.CloseInspector();
        Torrents.Focus(FocusState.Programmatic);
    }

    private async Task PasteSources()
    {
        try
        {
            var content = Clipboard.GetContent();
            await AddSources(content);
        }
        catch (Exception error) { Model.Report(error); }
    }

    private void OnDragOver(object sender, DragEventArgs args)
    {
        if (args.DataView.Contains(StandardDataFormats.StorageItems) || args.DataView.Contains(StandardDataFormats.Text))
            args.AcceptedOperation = DataPackageOperation.Copy;
    }

    private async void OnDrop(object sender, DragEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            await AddSources(args.DataView);
        }
        catch (Exception error) { Model.Report(error); }
        finally { deferral.Complete(); }
    }

    private async Task AddSources(DataPackageView content)
    {
        if (content.Contains(StandardDataFormats.StorageItems))
        {
            var files = await content.GetStorageItemsAsync();
            await Model.AddSources(files.OfType<StorageFile>().Where(file => file.FileType.Equals(".torrent", StringComparison.OrdinalIgnoreCase)).Select(file => file.Path));
        }
        else if (content.Contains(StandardDataFormats.Text))
            await Model.AddSources((await content.GetTextAsync()).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
    }
}
