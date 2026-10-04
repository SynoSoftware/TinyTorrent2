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
    private bool HasDialog => _addDialog is not null || _limitsDialog is not null || _removeDialog is not null || _closePrompt is not null;
    private bool _logoHovered;

    private void OnLogoEntered(object sender, PointerRoutedEventArgs args) { _logoHovered = true; RefreshLogo(); }
    private void OnLogoExited(object sender, PointerRoutedEventArgs args) { _logoHovered = false; RefreshLogo(); }
    private void OnLogoFocus(object sender, RoutedEventArgs args) => RefreshLogo();
    private void RefreshLogo()
    {
        var menu = _logoHovered || AppMenu.FocusState != FocusState.Unfocused;
        CaptionLogo.Visibility = menu ? Visibility.Collapsed : Visibility.Visible;
        MenuGlyph.Visibility = menu ? Visibility.Visible : Visibility.Collapsed;
    }
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

    private void OnMenu(object sender, RoutedEventArgs args)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(new MenuFlyoutItem { Text = Model.Text.Get("window", "torrents"), Command = Model.ShowTorrents });
        menu.Items.Add(new MenuFlyoutItem { Text = Model.Text.Get("finding", "settings"), Command = Model.ShowPreferences });
        menu.Items.Add(new MenuFlyoutItem { Text = Model.Text.Get("about", "title"), Command = Model.ShowAbout });
        menu.Items.Add(new MenuFlyoutSeparator());
        Menu(menu, "exit", Model.Exit);
        menu.ShowAt(AppMenu);
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
        menu.Items.Add(new MenuFlyoutSeparator());
        Menu(menu, "up", Model.Up);
        Menu(menu, "down", Model.Down);
        Menu(menu, "top", Model.Top);
        Menu(menu, "bottom", Model.Bottom);
        menu.Items.Add(new MenuFlyoutSeparator());
        Menu(menu, "remove", Model.Remove);
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

    private async Task ShowLimits(bool initialize = true)
    {
        if (HasDialog) return;
        if (initialize) Model.Speed.Begin();
        var body = new StackPanel { Spacing = 12 };
        foreach (var choice in Model.Speed.Choices)
        {
            var editor = new NumberBox { DataContext = choice, Header = Model.Text.Get("limits", choice.Name), Minimum = 0,
                Maximum = int.MaxValue / 1024.0, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
            editor.SetBinding(NumberBox.ValueProperty, new Binding { Source = choice, Path = new PropertyPath(nameof(LimitChoice.Value)), Mode = BindingMode.TwoWay });
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
