using System.ComponentModel;
using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.System;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Views;

namespace Syno.TinyTorrent;

public sealed partial class MainWindow
{
    private FileForm? _filesForm;

    private void OnQueueKey(object sender, KeyRoutedEventArgs args)
    {
        // VirtualKey omits the Windows OEM plus and minus codes.
        const VirtualKey plus = (VirtualKey)0xBB;
        const VirtualKey minus = (VirtualKey)0xBD;
        if (args.Key != plus && args.Key != minus || HasDialog || Model.Page != WindowPage.Torrents || HasEditorFocus()) return;
        if (!Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return;
        if (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return;
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var key = args.Key == plus ? VirtualKey.Add : VirtualKey.Subtract;
        var modifiers = VirtualKeyModifiers.Control | (shift ? VirtualKeyModifiers.Shift : VirtualKeyModifiers.None);
        var command = _shortcuts.FirstOrDefault(pair => pair.Value.Key == key && pair.Value.Modifiers == modifiers).Key;
        if (command is null || !command.CanExecute(null)) return;
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
        FileMenu.Items.Add(Menu("add_file", Model.Add, Syno.Lucide.FilePlus));
        FileMenu.Items.Add(Menu("add_magnet", Model.AddMagnet, Syno.Lucide.Link));
        FileMenu.Items.Add(new MenuFlyoutSeparator());
        FileMenu.Items.Add(new MenuFlyoutItem { Text = Model.Text.Get("finding", "settings"),
            Command = Model.ShowPreferences, Icon = Icon(Syno.Lucide.Settings), KeyboardAcceleratorTextOverride = ShortcutText(Model.ShowPreferences) });
        FileMenu.Items.Add(new MenuFlyoutSeparator());
        FileMenu.Items.Add(Menu("exit", Model.Exit, Syno.Lucide.Power));
        TorrentMenu.Items.Clear();
        var selection = new MenuFlyoutItem { IsEnabled = false };
        selection.SetBinding(MenuFlyoutItem.TextProperty, new Binding { Source = Model,
            Path = new PropertyPath(nameof(MainViewModel.SelectionText)), Mode = BindingMode.OneWay });
        TorrentMenu.Items.Add(selection);
        TorrentMenu.Items.Add(new MenuFlyoutSeparator());
        AddSelection(TorrentMenu.Items, true);
        TorrentMenu.Items.Add(new MenuFlyoutSeparator());
        TorrentMenu.Items.Add(Menu("pause_all", Model.PauseAll, Syno.Lucide.Pause));
        TorrentMenu.Items.Add(Menu("resume_all", Model.ResumeAll, Syno.Lucide.Play));
        TorrentMenu.Items.Add(Menu("limits", Model.Limits, Syno.Lucide.Gauge));
        ViewMenu.Items.Clear();
        ViewMenu.Items.Add(Choice(() => Model.FilterLabel, Model.SwitchFilters, () => Model.IsFilterOpen));
        ClearFiltersButton.Text = Model.Text.Get("window", "clear_filters");
        ToolTipService.SetToolTip(ClearFiltersButton, Model.Text.Get("window", "clear_filters_tip"));
        HelpMenu.Items.Clear();
        HelpMenu.Items.Add(new MenuFlyoutItem { Text = Model.Text.Get("about", "title"), Command = Model.ShowAbout, Icon = Icon(Syno.Lucide.Info) });
        foreach (var (button, name, command) in new[] { (AddButton, "add_file", Model.Add), (MagnetButton, "add_magnet", Model.AddMagnet) })
        {
            var label = Model.Text.Get("commands", name);
            var shortcut = ShortcutText(command);
            AutomationProperties.SetName(button, label);
            ToolTipService.SetToolTip(button, shortcut.Length > 0 ? Model.Text.Format("shortcuts", "tip", label, shortcut) : label);
        }
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
        items.Add(Menu("pause", Model.Pause, Syno.Lucide.Pause));
        items.Add(Menu("resume", Model.Resume, Syno.Lucide.Play));
        items.Add(Menu("force", Model.Force, Syno.Lucide.Zap));
        items.Add(new MenuFlyoutSeparator());
        items.Add(Menu("open", Model.Open, Syno.Lucide.File));
        items.Add(Menu("open_folder", Model.OpenFolder, Syno.Lucide.FolderOpen));
        items.Add(Menu("copy_magnet", Model.CopyMagnet, Syno.Lucide.Link));
        items.Add(Menu("copy_hash", Model.CopyHash, Syno.Lucide.Hash));
        if (properties) items.Add(Menu("properties", Model.Properties, Syno.Lucide.Info));
        items.Add(new MenuFlyoutSeparator());
        items.Add(Menu("verify", Model.Verify, Syno.Lucide.RefreshCw));
        items.Add(Menu("move", Model.MoveFiles, Syno.Lucide.FolderInput));
        var queue = new MenuFlyoutSubItem { Text = Model.Text.Get("menus", "queue"), Icon = Icon(Syno.Lucide.ListOrdered) };
        queue.Items.Add(Menu("up", Model.Up, Syno.Lucide.ArrowUp));
        queue.Items.Add(Menu("down", Model.Down, Syno.Lucide.ArrowDown));
        queue.Items.Add(Menu("top", Model.Top, Syno.Lucide.ArrowUpToLine));
        queue.Items.Add(Menu("bottom", Model.Bottom, Syno.Lucide.ArrowDownToLine));
        items.Add(queue);
        items.Add(new MenuFlyoutSeparator());
        items.Add(Choice(() => Model.Text.Get("commands", "sequential"), Model.SwitchSequential, () => Model.Sequential));
        items.Add(Choice(() => Model.Text.Get("commands", "first_last"), Model.SwitchFirstLast, () => Model.FirstLast));
        items.Add(new MenuFlyoutSeparator());
        items.Add(Menu("remove", Model.Remove, Syno.Lucide.ListX));
        items.Add(Menu("delete_files", Model.DeleteFiles, Syno.Lucide.Trash2));
    }

    private MenuFlyoutItem Menu(string name, ICommand command, string glyph) =>
        new() { Text = Model.Text.Get("commands", name), Command = command,
            Icon = Icon(glyph), KeyboardAcceleratorTextOverride = ShortcutText(command) };

    private string ShortcutText(ICommand command)
    {
        if (!_shortcuts.TryGetValue(command, out var accelerator)) return string.Empty;
        var parts = new List<string>();
        if (accelerator.Modifiers.HasFlag(VirtualKeyModifiers.Control)) parts.Add(Model.Text.Get("shortcuts", "control"));
        if (accelerator.Modifiers.HasFlag(VirtualKeyModifiers.Menu)) parts.Add(Model.Text.Get("shortcuts", "alt"));
        if (accelerator.Modifiers.HasFlag(VirtualKeyModifiers.Shift)) parts.Add(Model.Text.Get("shortcuts", "shift"));
        parts.Add(accelerator.Key switch
        {
            VirtualKey.Delete => Model.Text.Get("shortcuts", "delete"),
            VirtualKey.Add => "+",
            VirtualKey.Subtract => "-",
            _ => accelerator.Key.ToString()
        });
        return string.Join("+", parts);
    }

    // A plain item showing its state as its icon, not a ToggleMenuFlyoutItem:
    // a toggle gives every item in its menu an empty check column. The item
    // reads its state when its menu opens and while the menu stays open.
    private MenuFlyoutItem Choice(Func<string> text, ICommand command, Func<bool?> state)
    {
        var icon = new FontIcon { FontFamily = Syno.Lucide.Font };
        var item = new MenuFlyoutItem { Command = command, Icon = icon };
        void Show(object? sender, PropertyChangedEventArgs? args)
        {
            var current = state();
            item.Text = text();
            icon.Glyph = current switch { true => Syno.Lucide.SquareCheck, false => Syno.Lucide.Square, null => Syno.Lucide.SquareMinus };
            AutomationProperties.SetItemStatus(item, Model.Text.Get("menus", current switch { true => "on", false => "off", null => "mixed" }));
        }
        Show(null, null);
        item.Loaded += (_, _) => { Show(null, null); Model.PropertyChanged += Show; };
        item.Unloaded += (_, _) => Model.PropertyChanged -= Show;
        return item;
    }

    private static FontIcon Icon(string glyph) => new() { FontFamily = Syno.Lucide.Font, Glyph = glyph };

    private async Task ConfirmMerge()
    {
        if (HasDialog || Model.IsClosing) return;
        await Interact(async interaction =>
        {
            var dialog = new Dialog
            {
                XamlRoot = Root.XamlRoot,
                DefaultButton = ContentDialogButton.Primary,
                Glyph = Syno.Lucide.Merge,
                PrimaryGlyph = Syno.Lucide.Merge
            };
            var choice = await ShowDialog(interaction, dialog, () =>
            {
                dialog.Title = Model.Text.Get("add", "merge_title");
                dialog.PrimaryButtonText = Model.Text.Get("add", "merge");
                dialog.PrimaryToolTip = Model.Text.Get("add", "merge_tip");
                dialog.CloseButtonText = Model.Text.Get("add", "cancel");
                dialog.Content = Lines(Model.Draft.Sources
                    .Select(source => source.MetadataReady ? Model.Text.Format("units", "detail", source.Name, Model.Text.Bytes(source.Size)) : source.Name)
                    .Append(Model.Text.Get("add", "already_listed")));
            });
            // Either way the person is taken to the torrents already in the list.
            foreach (var source in Model.Draft.Sources)
                source.Merge = choice == ContentDialogResult.Primary && source.MergeAvailable;
            await Model.Draft.Submit();
            return true;
        });
    }

    // A confirmation's facts, one trimmed line each.
    private static UIElement Lines(IEnumerable<string> texts)
    {
        var lines = new StackPanel { Spacing = 4 };
        foreach (var text in texts)
        {
            var line = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTipService.SetToolTip(line, text);
            lines.Children.Add(line);
        }
        return new ScrollViewer { MaxHeight = 240, Content = lines };
    }

    private async Task ConfirmRemove(Torrent[] torrents)
    {
        if (HasDialog || Model.IsClosing) return;
        await Interact(async interaction =>
        {
            var dialog = new Dialog
            {
                XamlRoot = Root.XamlRoot,
                DefaultButton = ContentDialogButton.Close,
                Glyph = Syno.Lucide.ListX,
                PrimaryGlyph = Syno.Lucide.ListX
            };
            var choice = await ShowDialog(interaction, dialog, () =>
            {
                dialog.Title = Model.Text.Get("remove", "title");
                dialog.PrimaryButtonText = Model.Text.Get("commands", "remove");
                dialog.PrimaryToolTip = Model.Text.Get("remove", "tip");
                dialog.CloseButtonText = Model.Text.Get("add", "cancel");
                dialog.Content = Lines(torrents.Select(torrent => Model.Text.Format("units", "detail", torrent.Name, torrent.SizeText)));
            });
            if (choice == ContentDialogResult.Primary) await Model.RemoveTorrents(torrents);
            return true;
        });
    }

    private async Task ShowFiles(Torrent[] torrents, FileAction action, bool initialize = true)
    {
        if (HasDialog || Model.IsClosing) return;
        await Interact(async interaction =>
        {
            if (initialize) Model.Files.Begin(torrents, action);
            if (action == FileAction.Move)
                interaction.Restore = () => !interaction.IsResolved && torrents.All(Model.Contains) ? ShowFiles(torrents, action, false) : Task.CompletedTask;
            interaction.ResolveDraft = async () =>
            {
                if (!Model.Files.HasDraft) return true;
                return interaction.IsResolved = await ResolveDraft("move", Model.Files.Submit, () =>
                    { Model.Files.Cancel(); return Task.CompletedTask; });
            };
            var form = new FileForm(Model);
            form.DestinationRequested += async (_, _) =>
            {
                var folder = await PickFolder();
                if (folder is not null) Model.Files.Destination = folder;
            };
            var body = new ScrollViewer { Content = form, Width = Math.Min(560, Root.ActualWidth - 80),
                MaxHeight = Math.Max(220, Root.ActualHeight - 180), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            void ResizeBody(object sender, SizeChangedEventArgs args)
            {
                var atEnd = body.VerticalOffset >= body.ScrollableHeight - 1;
                body.Width = Math.Min(560, Root.ActualWidth - 80);
                body.MaxHeight = Math.Max(220, Root.ActualHeight - 180);
                body.UpdateLayout();
                if (Model.Files.HasError && atEnd) body.ChangeView(null, body.ScrollableHeight, null, true);
            }
            body.SizeChanged += (_, args) =>
            {
                if (Model.Files.HasError && body.VerticalOffset + args.PreviousSize.Height >= body.ExtentHeight - 1)
                    body.ChangeView(null, body.ScrollableHeight, null, true);
            };
            form.SizeChanged += (_, args) =>
            {
                if (Model.Files.HasError && body.VerticalOffset + body.ViewportHeight >= args.PreviousSize.Height - 1)
                {
                    body.UpdateLayout();
                    body.ChangeView(null, body.ScrollableHeight, null, true);
                }
            };
            Root.SizeChanged += ResizeBody;
            var dialog = new Dialog { XamlRoot = Root.XamlRoot, Content = body,
                DefaultButton = action == FileAction.Delete ? ContentDialogButton.Close : ContentDialogButton.Primary };
            dialog.Resources["ContentDialogMaxWidth"] = 608d;
            dialog.Opened += (_, _) =>
            {
                if (!Model.Files.HasError) return;
                body.UpdateLayout();
                body.ChangeView(null, body.ScrollableHeight, null, true);
            };
            dialog.SetBinding(ContentDialog.IsPrimaryButtonEnabledProperty, new Binding { Source = Model.Files,
                Path = new PropertyPath(nameof(FileOperation.CanSubmit)), Mode = BindingMode.OneWay });
            dialog.PrimaryButtonClick += async (_, args) =>
            {
                args.Cancel = true;
                var deferral = args.GetDeferral();
                try
                {
                    args.Cancel = !await Model.Files.Submit();
                    if (!args.Cancel) interaction.IsResolved = true;
                    if (args.Cancel)
                    {
                        body.UpdateLayout();
                        body.ChangeView(null, body.ScrollableHeight, null, true);
                    }
                }
                finally { deferral.Complete(); }
            };
            dialog.Closing += (_, args) => { if ((Model.Files.IsPending || Model.IsPicking) && !Model.IsClosing) args.Cancel = true; };
            _filesForm = form;
            var scope = initialize ? Model.Files.RefreshScope() : Task.CompletedTask;
            try
            {
                await ShowDialog(interaction, dialog, () =>
                {
                    dialog.Title = Model.Files.Title;
                    dialog.Glyph = dialog.PrimaryGlyph = Model.Files.SubmitGlyph;
                    dialog.PrimaryButtonText = Model.Files.SubmitText;
                    dialog.PrimaryToolTip = Model.Files.SubmitToolTip;
                    dialog.CloseButtonText = Model.Text.Get("add", "cancel");
                    form.RefreshText();
                });
                interaction.IsResolved |= !Model.IsClosing;
            }
            finally
            {
                await scope;
                Root.SizeChanged -= ResizeBody;
                body.Content = null;
                _filesForm = null;
                if (!Model.IsClosing) Model.Files.Cancel();
            }
            return true;
        });
    }

    private async void OnInspectorClose(object sender, RoutedEventArgs args)
    {
        if (Model.IsClosing || Model.Inspector.IsPending) return;
        if (Model.Inspector.HasDraft)
        {
            if (!await ResolveDraft("torrent", Model.Inspector.SaveDraft, () =>
                { Model.Inspector.CancelDraft(); return Task.CompletedTask; })) return;
        }
        if (!Model.CloseInspector()) return;
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
        if (!Model.CanEdit) return;
        if (args.DataView.Contains(StandardDataFormats.StorageItems) || args.DataView.Contains(StandardDataFormats.Text))
            args.AcceptedOperation = DataPackageOperation.Copy;
    }

    private async void OnDrop(object sender, DragEventArgs args)
    {
        args.Handled = true;
        var deferral = args.GetDeferral();
        try
        {
            await AddSources(args.DataView, sender as AddForm);
        }
        catch (Exception error) { Model.Report(error); }
        finally { deferral.Complete(); }
    }

    private async Task AddSources(DataPackageView content, AddForm? form = null)
    {
        bool CanApply() => Model.CanEdit &&
            (form is null || form == _form && form.IsLoaded && Model.Draft.CanEdit);
        if (!CanApply()) return;
        if (content.Contains(StandardDataFormats.StorageItems))
        {
            var files = await content.GetStorageItemsAsync();
            if (!CanApply()) return;
            if (Model.IsAddOpen && files is [StorageFolder folder])
            {
                if (Model.Draft.CanEdit) Model.Draft.Destination = folder.Path;
                return;
            }
            await Model.AddSources(files.OfType<StorageFile>().Where(file => file.FileType.Equals(".torrent", StringComparison.OrdinalIgnoreCase)).Select(file => file.Path));
        }
        else if (content.Contains(StandardDataFormats.Text))
        {
            var text = await content.GetTextAsync();
            if (!CanApply()) return;
            await Model.AddSources(text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        }
    }
}
