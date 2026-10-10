using System.ComponentModel;
using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Views;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.System;

namespace Syno.TinyTorrent;

public sealed partial class MainWindow
{
    private FileDialog? _fileDialog;

    private void OnQueueKey(object sender, KeyRoutedEventArgs args)
    {
        // VirtualKey omits the Windows OEM plus and minus codes.
        const VirtualKey plus = (VirtualKey)0xBB;
        const VirtualKey minus = (VirtualKey)0xBD;
        if (
            args.Key != plus && args.Key != minus
            || HasDialog
            || Model.Page != WindowPage.Torrents
            || HasEditorFocus()
        )
            return;
        if (
            !Microsoft
                .UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)
        )
            return;
        if (
            Microsoft
                .UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)
        )
            return;
        var shift = Microsoft
            .UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var key = args.Key == plus ? VirtualKey.Add : VirtualKey.Subtract;
        var modifiers =
            VirtualKeyModifiers.Control
            | (shift ? VirtualKeyModifiers.Shift : VirtualKeyModifiers.None);
        var command = _shortcuts
            .FirstOrDefault(pair => pair.Value.Key == key && pair.Value.Modifiers == modifiers)
            .Key;
        if (command is null || !command.CanExecute(null))
            return;
        command.Execute(null);
        args.Handled = true;
    }

    private void RefreshMenus()
    {
        FileMenu.Title = Model.Text.Get("menus", "file");
        FileMenu.AccessKey = MenuKey("file");
        TorrentMenu.Title = Model.Text.Get("menus", "torrent");
        TorrentMenu.AccessKey = MenuKey("torrent");
        ViewMenu.Title = Model.Text.Get("menus", "view");
        ViewMenu.AccessKey = MenuKey("view");
        HelpMenu.Title = Model.Text.Get("menus", "help");
        HelpMenu.AccessKey = MenuKey("help");
        AutomationProperties.SetName(Menus, Model.Text.Get("commands", "menu"));
        foreach (var menu in new[] { FileMenu, TorrentMenu, ViewMenu, HelpMenu })
            ClearMenu(menu.Items);
        FileMenu.Items.Add(Menu("add_file", Model.Add, Syno.Lucide.FilePlus));
        FileMenu.Items.Add(Menu("add_magnet", Model.AddMagnet, Syno.Lucide.Link));
        FileMenu.Items.Add(new MenuFlyoutSeparator());
        FileMenu.Items.Add(Menu("settings", Model.ShowSettings, Syno.Lucide.Settings));
        FileMenu.Items.Add(new MenuFlyoutSeparator());
        FileMenu.Items.Add(Menu("close", Model.CloseWindow, Syno.Lucide.X));
        FileMenu.Items.Add(Menu("exit", Model.Exit, Syno.Lucide.Power));
        if (Model.Page == WindowPage.Library)
        {
            RefreshLibraryMenu();
            RefreshLibraryViewMenu();
        }
        else
        {
            var selection = new MenuFlyoutItem { IsEnabled = false };
            selection.SetBinding(
                MenuFlyoutItem.TextProperty,
                new Binding
                {
                    Source = Model,
                    Path = new PropertyPath(nameof(MainViewModel.SelectionText)),
                    Mode = BindingMode.OneWay,
                }
            );
            TorrentMenu.Items.Add(selection);
            TorrentMenu.Items.Add(new MenuFlyoutSeparator());
            AddSelection(TorrentMenu.Items, true);
            TorrentMenu.Items.Add(new MenuFlyoutSeparator());
            TorrentMenu.Items.Add(Menu("pause_all", Model.PauseAll, Syno.Lucide.Pause));
            TorrentMenu.Items.Add(Menu("resume_all", Model.ResumeAll, Syno.Lucide.Play));
            TorrentMenu.Items.Add(Menu("limits", Model.Limits, Syno.Lucide.Gauge));
            ViewMenu.Items.Add(
                Toggle(
                    () => Model.FilterLabel,
                    "filters",
                    Model.SwitchFilters,
                    () => Model.IsFilterOpen,
                    Syno.Lucide.Funnel
                )
            );
            ViewMenu.Items.Add(
                Toggle(
                    () => Model.Text.Get("menus", "toolbar"),
                    "toolbar",
                    Model.SwitchToolbar,
                    () => Model.IsToolbarOpen,
                    Syno.Lucide.PanelTop
                )
            );
        }
        ClearFiltersButton.Text = Model.Text.Get("window", "clear_filters");
        ToolTipService.SetToolTip(
            ClearFiltersButton,
            Model.Text.Get("window", "clear_filters_tip")
        );
        var subtitles = new MenuFlyoutItem
        {
            Text = Model.Text.Get("subtitles", "setup"),
            Icon = Icon(Syno.Lucide.Captions),
        };
        subtitles.Click += async (_, _) =>
        {
            Model.SubtitleHelpSeen = false;
            await ShowSettings(new());
        };
        HelpMenu.Items.Add(subtitles);
        HelpMenu.Items.Add(new MenuFlyoutSeparator());
        HelpMenu.Items.Add(
            new MenuFlyoutItem
            {
                Text = Model.UpdateText,
                Command = Model.OpenUpdate,
                Icon = Icon(Syno.Lucide.CircleFadingArrowUp),
                AccessKey = MenuKey("update"),
            }
        );
        HelpMenu.Items.Add(new MenuFlyoutItem
        {
            Text = Model.Text.Get("about", "title"),
            Command = Model.ShowAbout,
            Icon = Icon(Syno.Lucide.Info),
            AccessKey = MenuKey("about"),
        });
        AutomationProperties.SetName(Toolbar, Model.Text.Get("menus", "toolbar"));
        foreach (
            var (button, name, command) in new[]
            {
                (AddButton, "add_file", Model.Add),
                (MagnetButton, "add_magnet", Model.AddMagnet),
                (ResumeButton, "resume", Model.Resume),
                (PauseButton, "pause", Model.Pause),
                (FolderButton, "open_folder", Model.OpenFolder),
                (PropertiesButton, "properties", Model.Properties),
                (VerifyButton, "verify", Model.Verify),
                (RemoveButton, "remove", Model.Remove),
                (DeleteButton, "delete_files", Model.DeleteFiles),
            }
        )
        {
            var label = Model.Text.Get("commands", name);
            var shortcut = ShortcutText(command);
            button.Label = label;
            ToolTipService.SetToolTip(
                button,
                shortcut.Length > 0 ? Model.Text.Format("shortcuts", "tip", label, shortcut) : label
            );
        }
    }

    private static void ClearMenu(IList<MenuFlyoutItemBase> items)
    {
        // MenuBarItem mirrors individual removals into its flyout, but ignores
        // Clear's reset, leaving old entries behind after a language change.
        while (items.Count > 0)
        {
            var item = items[items.Count - 1];
            if (item is MenuFlyoutItem command)
                command.Command = null;
            else if (item is MenuFlyoutSubItem submenu)
                ClearMenu(submenu.Items);
            items.RemoveAt(items.Count - 1);
        }
    }

    private void ShowSelectionMenu(FrameworkElement target, Point? position)
    {
        var menu = new MenuFlyout();
        menu.Closed += (_, _) => ClearMenu(menu.Items);
        menu.Items.Add(
            new MenuFlyoutItem
            {
                Text =
                    Torrents.Selection.Items.Count == 1
                        ? ((Torrent)Torrents.Selection.Items[0]).Name
                        : Model.SelectionText,
                IsEnabled = false,
            }
        );
        menu.Items.Add(new MenuFlyoutSeparator());
        AddSelection(menu.Items, !Model.HasInspector);
        if (position is { } point)
            menu.ShowAt(target, point);
        else
            menu.ShowAt(target);
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
        if (properties)
            items.Add(Menu("properties", Model.Properties, Syno.Lucide.Info));
        items.Add(new MenuFlyoutSeparator());
        items.Add(Menu("verify", Model.Verify, Syno.Lucide.RefreshCw));
        items.Add(Menu("move", Model.MoveFiles, Syno.Lucide.FolderInput));
        var queue = new MenuFlyoutSubItem
        {
            Text = Model.Text.Get("menus", "queue"),
            Icon = Icon(Syno.Lucide.ListOrdered),
            AccessKey = MenuKey("queue"),
        };
        queue.Items.Add(Menu("up", Model.Up, Syno.Lucide.ArrowUp));
        queue.Items.Add(Menu("down", Model.Down, Syno.Lucide.ArrowDown));
        queue.Items.Add(Menu("top", Model.Top, Syno.Lucide.ArrowUpToLine));
        queue.Items.Add(Menu("bottom", Model.Bottom, Syno.Lucide.ArrowDownToLine));
        items.Add(queue);
        items.Add(new MenuFlyoutSeparator());
        items.Add(
            Choice(
                () => Model.Text.Get("commands", "sequential"),
                "sequential",
                Model.SwitchSequential,
                () => Model.Sequential
            )
        );
        items.Add(
            Choice(
                () => Model.Text.Get("commands", "first_last"),
                "first_last",
                Model.SwitchFirstLast,
                () => Model.FirstLast
            )
        );
        items.Add(
            Choice(
                () => Model.Text.Get("commands", "speed_limit"),
                "speed_limit",
                Model.LimitSpeed,
                () => Model.Limited
            )
        );
        items.Add(new MenuFlyoutSeparator());
        items.Add(Menu("remove", Model.Remove, Syno.Lucide.ListX));
        items.Add(Menu("delete_files", Model.DeleteFiles, Syno.Lucide.Trash2));
    }

    private MenuFlyoutItem Menu(string name, ICommand command, string glyph) =>
        new()
        {
            Text = Model.Text.Get("commands", name),
            Command = command,
            Icon = Icon(glyph),
            AccessKey = MenuKey(name),
            KeyboardAcceleratorTextOverride = ShortcutText(command),
        };

    private string MenuKey(string name) => Model.Text.Get("menus", name + "_key");

    private string ShortcutText(ICommand command)
    {
        if (!_shortcuts.TryGetValue(command, out var accelerator))
            return string.Empty;
        var parts = new List<string>();
        if (accelerator.Modifiers.HasFlag(VirtualKeyModifiers.Control))
            parts.Add(Model.Text.Get("shortcuts", "control"));
        if (accelerator.Modifiers.HasFlag(VirtualKeyModifiers.Menu))
            parts.Add(Model.Text.Get("shortcuts", "alt"));
        if (accelerator.Modifiers.HasFlag(VirtualKeyModifiers.Shift))
            parts.Add(Model.Text.Get("shortcuts", "shift"));
        var name = accelerator.Key switch
        {
            VirtualKey.Number1 => "number_1",
            VirtualKey.Number2 => "number_2",
            Comma => "comma",
            _ => accelerator.Key.ToString().ToLowerInvariant(),
        };
        parts.Add(Model.Text.Get("shortcuts", name));
        return string.Join("+", parts);
    }

    // A plain item showing its state as its icon, not a ToggleMenuFlyoutItem:
    // a toggle gives every item in its menu an empty check column.
    private MenuFlyoutItem Choice(
        Func<string> text,
        string name,
        ICommand command,
        Func<bool?> state
    )
    {
        var icon = new FontIcon { FontFamily = Syno.Lucide.Font };
        var item = new MenuFlyoutItem
        {
            Command = command,
            Icon = icon,
            AccessKey = MenuKey(name),
        };
        Follow(
            item,
            () =>
            {
                var current = state();
                item.Text = text();
                icon.Glyph = current switch
                {
                    true => Syno.Lucide.SquareCheck,
                    false => Syno.Lucide.Square,
                    null => Syno.Lucide.SquareMinus,
                };
                AutomationProperties.SetItemStatus(
                    item,
                    Model.Text.Get(
                        "menus",
                        current switch
                        {
                            true => "on",
                            false => "off",
                            null => "mixed",
                        }
                    )
                );
            }
        );
        return item;
    }

    // For a menu whose items are all toggles, so no check column stands empty.
    private ToggleMenuFlyoutItem Toggle(
        Func<string> text,
        string name,
        ICommand command,
        Func<bool> state,
        string glyph
    )
    {
        var item = new ToggleMenuFlyoutItem
        {
            Command = command,
            Icon = Icon(glyph),
            AccessKey = MenuKey(name),
        };
        Follow(
            item,
            () =>
            {
                item.Text = text();
                item.IsChecked = state();
            }
        );
        return item;
    }

    // Shows an item's state when its menu opens and while the menu stays open.
    private void Follow(MenuFlyoutItem item, Action show)
    {
        void Show(object? sender, PropertyChangedEventArgs args) => show();
        show();
        item.Loaded += (_, _) =>
        {
            show();
            Model.PropertyChanged += Show;
        };
        item.Unloaded += (_, _) => Model.PropertyChanged -= Show;
    }

    private static FontIcon Icon(string glyph) =>
        new() { FontFamily = Syno.Lucide.Font, Glyph = glyph };

    private async Task ConfirmMerge()
    {
        await Interact(async interaction =>
        {
            var dialog = new Dialog
            {
                Glyph = Syno.Lucide.Merge,
                PrimaryGlyph = Syno.Lucide.Merge,
            };
            var choice = await ShowDialog(
                interaction,
                dialog,
                () =>
                {
                    dialog.Title = Model.Text.Get("add", "merge_title");
                    dialog.PrimaryButtonText = Model.Text.Get("add", "merge");
                    dialog.PrimaryToolTip = Model.Text.Get("add", "merge_tip");
                    dialog.Content = Lines(
                        Model
                            .AddDraft.Sources.Select(source =>
                                source.MetadataReady
                                    ? Model.Text.Format(
                                        "units",
                                        "detail",
                                        source.Name,
                                        Model.Text.Bytes(source.Size)
                                    )
                                    : source.Name
                            )
                            .Append(Model.Text.Get("add", "already_listed"))
                    );
                }
            );
            // Either way the person is taken to the torrents already in the list.
            foreach (var source in Model.AddDraft.Sources)
                source.Merge = choice == ContentDialogResult.Primary && source.MergeAvailable;
            await Model.AddDraft.Submit();
            return true;
        });
    }

    private async Task ConfirmRemove(Torrent[] torrents)
    {
        await Interact(async interaction =>
        {
            var dialog = new Dialog
            {
                Glyph = Syno.Lucide.ListX,
                PrimaryGlyph = Syno.Lucide.ListX,
            };
            var choice = await ShowDialog(
                interaction,
                dialog,
                () =>
                {
                    dialog.Title = Model.Text.Get("remove", "title");
                    dialog.PrimaryButtonText = Model.Text.Get("commands", "remove");
                    dialog.PrimaryToolTip = Model.Text.Get("remove", "tip");
                    dialog.Content = Lines(
                        torrents.Select(torrent =>
                            Model.Text.Format("units", "detail", torrent.Name, torrent.SizeText)
                        )
                    );
                }
            );
            if (choice != ContentDialogResult.Primary)
                return true;
            Torrents.Release(torrents);
            await Model.RemoveTorrents(torrents);
            return true;
        });
    }

    private async Task ShowFiles(Torrent[] torrents, FileAction action, bool initialize = true)
    {
        await Interact(async interaction =>
        {
            if (initialize)
                Model.FileDraft.Begin(torrents, action);
            if (action == FileAction.Move)
                interaction.Restore = () =>
                    torrents.All(Model.Contains)
                        ? ShowFiles(torrents, action, false)
                        : Task.CompletedTask;
            interaction.ResolveDraft = async () =>
                !Model.FileDraft.HasDraft || (interaction.IsResolved = await ResolveDraft("move"));
            var content = new FileDialog(Model);
            content.DestinationRequested += async (_, _) =>
            {
                var folder = await PickFolder();
                if (folder is not null)
                    Model.FileDraft.Destination = folder;
            };
            void Resize(object? sender = null, SizeChangedEventArgs? args = null)
            {
                content.Width = Math.Min(560, Root.ActualWidth - 80);
                var height = Math.Clamp(Root.ActualHeight - 180, 220, 440);
                if (action == FileAction.Delete)
                    content.MaxHeight = height;
                else
                    content.Height = height;
            }
            Resize();
            Root.SizeChanged += Resize;
            var dialog = new Dialog { Content = content };
            dialog.SizeToContent();
            var total = new TextBlock
            {
                Style = (Style)Application.Current.Resources["TinyTorrentTitleDetailTextStyle"],
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            total.SetBinding(
                TextBlock.TextProperty,
                new Binding
                {
                    Source = Model.FileDraft,
                    Path = new PropertyPath(nameof(FileDraft.Total)),
                    Mode = BindingMode.OneWay,
                }
            );
            if (action == FileAction.Delete)
                dialog.Footer = total;
            _fileDialog = content;
            // Files that other torrents share add a block above the destination,
            // so the dialog opens with the scope already read.
            if (initialize)
                await Model.FileDraft.RefreshScope();
            try
            {
                await ShowEditor(
                    interaction,
                    dialog,
                    Model.FileDraft,
                    () =>
                    {
                        dialog.Title = Model.FileDraft.Title;
                        dialog.Glyph = dialog.PrimaryGlyph = Model.FileDraft.SubmitGlyph;
                        dialog.PrimaryButtonText = Model.FileDraft.SubmitText;
                        dialog.PrimaryToolTip = Model.FileDraft.SubmitToolTip;
                        content.RefreshText();
                    },
                    submit: () =>
                    {
                        if (action == FileAction.Delete)
                            Torrents.Release(torrents);
                        return Model.FileDraft.Submit();
                    }
                );
            }
            finally
            {
                Root.SizeChanged -= Resize;
                _fileDialog = null;
                if (!Model.IsClosing || action == FileAction.Delete)
                    Model.FileDraft.Cancel();
            }
            return true;
        });
    }

    private async Task ShowSpeedLimit(Torrent[] torrents, bool initialize = true)
    {
        await Interact(async interaction =>
        {
            if (initialize)
                Model.SpeedLimit.Begin(torrents);
            interaction.Restore = () =>
                torrents.All(Model.Contains)
                    ? ShowSpeedLimit(torrents, false)
                    : Task.CompletedTask;
            interaction.ResolveDraft = () => SaveOnClose(interaction, Model.SpeedLimit, Model.CanSave);
            var content = new SpeedLimitDialog(Model);
            var dialog = new Dialog
            {
                Content = content,
                Glyph = Syno.Lucide.Gauge,
                PrimaryGlyph = Syno.Lucide.Check,
            };
            try
            {
                await ShowEditor(
                    interaction,
                    dialog,
                    Model.SpeedLimit,
                    () =>
                    {
                        dialog.Title = Model.Text.Get("speed_limit", "title");
                        dialog.PrimaryButtonText = Model.Text.Get("dialog", "save");
                        dialog.PrimaryToolTip = Model.Text.Get("speed_limit", "save_tip");
                        content.RefreshText();
                    }
                );
            }
            finally
            {
                if (!Model.IsClosing)
                    Model.SpeedLimit.Cancel();
            }
            return true;
        });
    }

    private async Task ShowProxy(bool initialize = true)
    {
        var proxy = Model.Settings.Proxy;
        await Interact(async interaction =>
        {
            if (initialize)
                proxy.Begin();
            interaction.Restore = () => ShowProxy(false);
            interaction.ResolveDraft = () => SaveOnClose(interaction, proxy, Model.CanSave);
            var content = new ProxyDialog(Model);
            var dialog = new Dialog
            {
                Content = content,
                Footer = content.Status,
                FooterAction = content.CheckAction,
                Glyph = Syno.Lucide.Route,
                PrimaryGlyph = Syno.Lucide.Check,
            };
            await ShowEditor(
                interaction,
                dialog,
                proxy,
                () =>
                {
                    dialog.Title = Model.Text.Get("settings", "proxy_title");
                    dialog.PrimaryButtonText = Model.Text.Get("dialog", "save");
                    dialog.PrimaryToolTip = Model.Text.Get("settings", "proxy_save_tip");
                    dialog.CloseToolTip = Model.Text.Get("settings", "proxy_cancel_tip");
                    content.RefreshText();
                }
            );
            return true;
        });
    }

    private async void OnInspectorClose(object sender, RoutedEventArgs args)
    {
        if (Model.Page == WindowPage.Library)
        {
            Model.Library.Close();
            _libraryTable.FocusRows();
            return;
        }
        if (Model.IsClosing || Model.Inspector.IsPending)
            return;
        if (Model.Inspector.HasDraft && !await LeaveInspector())
            return;
        if (!Model.CloseInspector())
            return;
        Torrents.Focus(FocusState.Programmatic);
    }

    private async Task PasteSources()
    {
        try
        {
            var data = Clipboard.GetContent();
            await AddSources(data);
        }
        catch (Exception error)
        {
            Model.Report(error);
        }
    }

    private void OnDragOver(object sender, DragEventArgs args)
    {
        if (!Model.CanEdit)
            return;
        if (
            args.DataView.Contains(StandardDataFormats.StorageItems)
            || args.DataView.Contains(StandardDataFormats.Text)
        )
            args.AcceptedOperation = DataPackageOperation.Copy;
    }

    private async void OnDrop(object sender, DragEventArgs args)
    {
        args.Handled = true;
        var deferral = args.GetDeferral();
        try
        {
            await AddSources(args.DataView, sender as AddDialog);
        }
        catch (Exception error)
        {
            Model.Report(error);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async Task AddSources(DataPackageView data, AddDialog? content = null)
    {
        bool CanApply() =>
            Model.CanEdit
            && (
                content is null
                || content == _addDialog && content.IsLoaded && Model.AddDraft.CanEdit
            );
        if (!CanApply())
            return;
        if (data.Contains(StandardDataFormats.StorageItems))
        {
            var files = await data.GetStorageItemsAsync();
            if (!CanApply())
                return;
            if (Model.IsAddOpen && files is [StorageFolder folder])
            {
                if (Model.AddDraft.CanEdit)
                    Model.AddDraft.Destination = folder.Path;
                return;
            }
            await Model.AddSources(
                files
                    .OfType<StorageFile>()
                    .Where(file =>
                        file.FileType.Equals(".torrent", StringComparison.OrdinalIgnoreCase)
                    )
                    .Select(file => file.Path)
            );
        }
        else if (data.Contains(StandardDataFormats.Text))
        {
            var text = await data.GetTextAsync();
            if (!CanApply())
                return;
            await Model.AddSources(text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        }
    }
}
