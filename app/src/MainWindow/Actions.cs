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

    private void OnOverflow(object sender, RoutedEventArgs args) => ShowSelectionMenu(OverflowButton, null, false);

    private void ShowSelectionMenu(FrameworkElement target, Point? position, bool row)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(new MenuFlyoutItem { Text = row && Torrents.Selection.Items.Count == 1 ?
            ((Torrent)Torrents.Selection.Items[0]).Name : Model.SelectionText, IsEnabled = false });
        menu.Items.Add(new MenuFlyoutSeparator());
        if (row)
        {
            Menu(menu, "pause", Model.Pause);
            Menu(menu, "resume", Model.Resume);
        }
        Menu(menu, "force", Model.Force);
        Menu(menu, "open", Model.Open);
        Menu(menu, "open_folder", Model.OpenFolder);
        Menu(menu, "copy_magnet", Model.CopyMagnet);
        Menu(menu, "copy_hash", Model.CopyHash);
        Menu(menu, "verify", Model.Verify);
        Menu(menu, "move", Model.MoveFiles);
        menu.Items.Add(new MenuFlyoutSeparator());
        Menu(menu, "up", Model.Up);
        Menu(menu, "down", Model.Down);
        Menu(menu, "top", Model.Top);
        Menu(menu, "bottom", Model.Bottom);
        menu.Items.Add(new MenuFlyoutSeparator());
        Menu(menu, "remove", Model.Remove);
        Menu(menu, "delete_files", Model.DeleteFiles);
        if (row && !Model.HasInspector) Menu(menu, "properties", Model.Properties);
        if (position is { } point) menu.ShowAt(target, point); else menu.ShowAt(target);
    }

    private void Menu(MenuFlyout menu, string name, ICommand command) =>
        menu.Items.Add(new MenuFlyoutItem { Text = Model.Text.Get("commands", name), Command = command });

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
            if (!_closing && (Model.Draft.Sources.Count > 0 || Model.Draft.EditingMagnet)) _ = ShowAdd();
        }
    }

    private async Task ShowFiles(Torrent[] torrents, FileAction action, bool initialize = true)
    {
        if (HasDialog || _closing) return;
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
        dialog.Closing += (_, args) => { if ((Model.Files.IsPending || Model.IsPicking) && !_closing) args.Cancel = true; };
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
            if (!_closing) Model.Files.Cancel();
            _filesClosed.TrySetResult();
            if (!_closing && (Model.Draft.Sources.Count > 0 || Model.Draft.EditingMagnet)) _ = ShowAdd();
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
            editor.SetBinding(Control.IsEnabledProperty, new Binding { Source = Model, Path = new PropertyPath(nameof(MainViewModel.CanEdit)), Mode = BindingMode.OneWay });
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
        dialog.SetBinding(ContentDialog.IsPrimaryButtonEnabledProperty, new Binding { Source = Model, Path = new PropertyPath(nameof(MainViewModel.CanEdit)), Mode = BindingMode.OneWay });
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            var deferral = args.GetDeferral();
            try { args.Cancel = !await Model.Speed.Apply(); }
            finally { deferral.Complete(); }
        };
        dialog.Closing += (_, args) => { if (Model.IsBusy && !_closing) args.Cancel = true; };
        _limitsDialog = dialog;
        _limitsClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try { await dialog.ShowAsync(); }
        catch (Exception error) { Model.Report(error); }
        finally
        {
            _limitsDialog = null;
            _limitsClosed.TrySetResult();
            if (!_closing) Model.Speed.Begin();
            if (!_closing && (Model.Draft.Sources.Count > 0 || Model.Draft.EditingMagnet)) _ = ShowAdd();
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
