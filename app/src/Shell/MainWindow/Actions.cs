using System.ComponentModel;
using System.Windows.Input;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Settings;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Torrents;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.System;

namespace Syno.TinyTorrent.Shell;

public sealed partial class MainWindow
{
    private FileDialog? _fileDialog;
    private MenuFlyout? _selectionMenu;
    private RelayCommand? _switchInspector;
    private bool _menusPending;
    private int _menuVersion;
    // Whether the queued fill also fills the View menu, which a selection
    // change leaves as it is.
    private bool _fillsView;
    private IReadOnlyList<MenuEntry> _torrentCommands = [];

    private IReadOnlyList<MenuEntry> WorkspaceMenu(string group) =>
        [
            new MenuChoice(Model.Text.Get("window", "torrents"), Syno.Segoe.List,
                NavigationPage == WindowPage.Torrents, Model.ShowTorrents) { Group = group },
            new MenuChoice(Model.Text.Get("library", "title"), Syno.Segoe.FolderOpen,
                NavigationPage == WindowPage.Library, Model.ShowLibrary) { Group = group },
        ];

    private IReadOnlyList<MenuEntry> LanguageMenu() =>
        [
            .. Strings.Languages.Select(language => new MenuChoice(
                Model.Text.LanguageChoice(language),
                null,
                Model.Text.Language == language.Name,
                new RelayCommand(
                    () => Model.Settings.SelectLanguage(language.Name),
                    () => Model.Settings.CanSelectLanguage
                )
            )
            {
                FlagRegion = Strings.FlagRegion(language),
                Group = "Language",
            }),
        ];

    private void OnQueueKey(object sender, KeyRoutedEventArgs args)
    {
        if (
            args.Key != (VirtualKey)OemKey.Plus && args.Key != (VirtualKey)OemKey.Minus
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
        var key = args.Key == (VirtualKey)OemKey.Plus ? VirtualKey.Add : VirtualKey.Subtract;
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
        var library = Model.Page == WindowPage.Library;
        FileMenu.Title = Model.Text.Get("menus", "file");
        FileMenu.AccessKey = MenuKey("file");
        TorrentMenu.Title = library
            ? Model.Text.Get("library", "title")
            : Model.Text.Get("menus", "torrent");
        TorrentMenu.AccessKey = MenuKey(library ? "library" : "torrent");
        ViewMenu.Title = Model.Text.Get("menus", "view");
        ViewMenu.AccessKey = MenuKey("view");
        HelpMenu.Title = Model.Text.Get("menus", "help");
        HelpMenu.AccessKey = MenuKey("help");
        AutomationProperties.SetName(Menus, Model.Text.Get("commands", "menu"));
        // File and Help hold only commands, whose items follow the commands'
        // enabled state, so only the language changes them.
        MenuItems.Fill(
            FileMenu.Items,
            [
                Command("add_file", Model.Add, Syno.Segoe.FilePlus),
                Command("add_magnet", Model.AddMagnet, Syno.Segoe.Link),
                new MenuSeparator(),
                Command("settings", Model.ShowSettings, Syno.Segoe.Settings),
                new MenuSeparator(),
                Command("close", Model.CloseWindow, Syno.Segoe.X),
                Command("exit", Model.Exit, Syno.Segoe.Power),
            ],
            Model.Text
        );
        MenuItems.Fill(
            HelpMenu.Items,
            [
                new MenuCommand(
                    Model.UpdateText,
                    Syno.Segoe.CircleFadingArrowUp,
                    Model.Updates.Check
                )
                {
                    AccessKey = MenuKey("update"),
                },
                new MenuCommand(Model.Text.Get("about", "title"), Syno.Segoe.Info, Model.ShowAbout)
                {
                    AccessKey = MenuKey("about"),
                },
            ],
            Model.Text
        );
        _torrentCommands = Selection(properties: true, includeRetry: true).ToArray();
        QueueMenus();
        ClearFiltersButton.Text = Model.Text.Get("window", "clear_filters");
        ToolTipService.SetToolTip(
            ClearFiltersButton,
            Model.Text.Get("window", "clear_filters_tip")
        );
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
                (LibraryOpenButton, "open", Model.Library.Open),
                (LibraryFolderButton, "open_folder", Model.Library.OpenFolder),
                (LibraryPropertiesButton, "properties", Model.Library.Properties),
                (SelectedMapButton, "selected_map", SelectedMap),
                (AllMapButton, "all_map", AllMap),
                (SettingsButton, "settings", Model.ShowSettings),
            }
        )
        {
            var label = Model.Text.Get("commands", name);
            button.Label = label;
            ToolTipService.SetToolTip(button, Model.Text.ShortcutTip(label, ShortcutText(command)));
        }
    }

    // Each page's toolbar holds only the commands that can act on that page,
    // so no button works on another page's hidden selection and none stays
    // disabled for good. Add, Magnet and Settings act on the application and
    // appear on every page. The All connections map opens from Library too.
    private void ShowPageCommands()
    {
        var page = NavigationPage;
        Show(
            page == WindowPage.Torrents,
            ResumeButton,
            PauseButton,
            FolderSeparator,
            FolderButton,
            PropertiesButton,
            VerifySeparator,
            VerifyButton,
            RemoveSeparator,
            RemoveButton,
            SelectedMapButton
        );
        Show(
            page == WindowPage.Library,
            LibraryOpenButton,
            LibraryFolderButton,
            LibraryPropertiesButton
        );

        static void Show(bool visible, params UIElement[] commands)
        {
            foreach (var command in commands)
                command.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // Fills again the menus that show this model property. An item's enabled
    // state follows its command and the window's accelerators run commands,
    // not items, so a menu whose content is unchanged needs no fill.
    private void ObserveMenus(string? property)
    {
        switch (property)
        {
            case null
                or ""
                or nameof(MainViewModel.ShowsFilters)
                or nameof(MainViewModel.HasInspector)
                or nameof(MainViewModel.IsToolbarOpen):
                QueueMenus();
                break;
            case nameof(MainViewModel.Selected):
                QueueTorrentMenu();
                break;
        }
    }

    // Queues a fill of the View menu as well as the Torrent menu.
    private void QueueMenus()
    {
        _fillsView = true;
        QueueTorrentMenu();
    }

    // One update raises several changes in the same turn, so the menus refill once
    // after it.
    private void QueueTorrentMenu()
    {
        _menuVersion++;
        if (_menusPending || _allowClose)
            return;
        _menusPending = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, async () =>
        {
            _menusPending = false;
            if (_allowClose)
                return;
            var version = _menuVersion;
            var rows = (Rows: Model.Selected.ToArray(), CanEdit: Model.CanEdit);
            var commands = _torrentCommands;
            var text = Model.Text;
            var entry =
                Model.Page == WindowPage.Library
                && Model.Library.Selected is { } selected
                && !Model.Library.HasOrigins(selected.Entry.EntryId)
                    ? selected.Entry
                    : null;
            try
            {
                var preparation = Torrents.PrepareSelection(async (_, cancellation) =>
                {
                    var origins = entry is null
                        ? null
                        : await Model.Library.ReadOrigins(entry.EntryId, cancellation);
                    var entries = PrepareTorrentMenu(rows, commands, text, cancellation);
                    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
                    {
                        try
                        {
                            if (!cancellation.IsCancellationRequested && version == _menuVersion && !_allowClose)
                            {
                                if (entry is not null && origins is not null)
                                    Model.Library.ApplyOrigins(entry.EntryId, origins);
                                FillMenus(entries);
                            }
                            completion.TrySetResult();
                        }
                        catch (Exception error)
                        {
                            completion.TrySetException(error);
                        }
                    }))
                        completion.TrySetCanceled();
                    await completion.Task.WaitAsync(cancellation);
                });
                await preparation.ConfigureAwait(
                    ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
                if (!preparation.IsCanceled)
                    await preparation;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception error)
            {
                Model.Library.ReportCommand(error);
            }
        });
    }

    private IReadOnlyList<MenuEntry> PrepareTorrentMenu(
        (Torrent[] Rows, bool CanEdit) selection, IReadOnlyList<MenuEntry> commands,
        Strings text, CancellationToken cancellation)
    {
        var sequential = MainViewModel.Shared(selection.Rows, row => row.Sequential, cancellation);
        var firstLast = MainViewModel.Shared(selection.Rows, row => row.FirstLast, cancellation);
        var limited = MainViewModel.Shared(selection.Rows, row => row.IsLimited, cancellation);
        var retry = selection.CanEdit && selection.Rows.Any(row => row.CanRetry);
        List<MenuEntry> entries = [new MenuHeading(text.Format("window", "selected", selection.Rows.Length), Segoe.ListChecks)];
        foreach (var command in commands)
        {
            cancellation.ThrowIfCancellationRequested();
            if (command is MenuCommand action && action.Command == Model.Retry && !retry)
                continue;
            entries.Add(command switch
            {
                MenuChoice choice => choice with
                {
                    State = choice.Command == Model.SwitchSequential ? sequential : firstLast,
                },
                MenuCommand speed when speed.Command == Model.LimitSpeed =>
                    SpeedLimit(speed, limited, text),
                _ => command,
            });
        }
        return entries;
    }

    // The gauge's needle shows whether the selected torrents have their own limit:
    // low when all of them do, halfway when some do, and high when none does.
    private static MenuCommand SpeedLimit(MenuCommand command, bool? limited, Strings text) =>
        command with
        {
            Glyph = limited switch
            {
                true => Syno.Segoe.Limited,
                false => Syno.Segoe.Gauge,
                null => Syno.Segoe.PartlyLimited,
            },
            Status = text.Get(
                "menus",
                limited switch
                {
                    true => "on",
                    false => "off",
                    null => "mixed",
                }
            ),
        };

    private void FillMenus(IReadOnlyList<MenuEntry> torrent)
    {
        if (_selectionMenu is not null)
            MenuItems.Refresh(_selectionMenu);
        var library = Model.Page == WindowPage.Library;
        MenuItems.Fill(
            TorrentMenu.Items,
            library
                ? [.. LibraryMenu()]
                :
                [
                    .. torrent,
                    new MenuSeparator(),
                    Command("pause_all", Model.PauseAll, Syno.Segoe.Pause),
                    Command("resume_all", Model.ResumeAll, Syno.Segoe.Play),
                    Command("limits", Model.Limits, Syno.Segoe.Gauge),
                ],
            Model.Text
        );
        if (!_fillsView)
            return;
        _fillsView = false;
        List<MenuEntry> view =
        [
            .. WorkspaceMenu("WorkspaceView"),
            new MenuSeparator(),
            new MenuChoice(
                Model.Text.Get("filters", "title"),
                Syno.Segoe.Funnel,
                Model.ShowsFilters,
                Model.SwitchFilters
            )
            {
                AccessKey = MenuKey("filters"),
            },
            new MenuChoice(
                Model.Text.Get("inspector", "title"),
                Syno.Segoe.PanelBottom,
                Model.HasInspector,
                _switchInspector ??= new RelayCommand(SwitchInspector, () => true)
            )
            {
                AccessKey = MenuKey("inspector"),
            },
        ];
        view.Add(
            new MenuChoice(
                Model.Text.Get("menus", "toolbar"),
                Syno.Segoe.PanelTop,
                Model.IsToolbarOpen,
                Model.SwitchToolbar
            )
            {
                AccessKey = MenuKey("toolbar"),
            }
        );
        if (library)
            view.AddRange(LibraryViewMenu());
        view.Add(new MenuSeparator());
        view.Add(Command("selected_map", SelectedMap, Syno.Segoe.GraphStar));
        view.Add(Command("all_map", AllMap, Syno.Segoe.Globe2));
        MenuItems.Fill(ViewMenu.Items, view, Model.Text);
    }

    private void ShowSelectionMenu(FrameworkElement target, Point? position)
    {
        _selectionMenu ??= MenuItems.Flyout(
            () =>
                [
                    new MenuHeading(
                        Torrents.Selection.Items.Count == 1
                            ? ((Torrent)Torrents.Selection.Items[0]).Name
                            : Model.SelectionText,
                        Syno.Segoe.ListChecks
                    ),
                    .. Selection(properties: !Model.HasInspector),
                ],
            () => Model.Text
        );
        if (position is { } point)
            _selectionMenu.ShowAt(target, point);
        else
            _selectionMenu.ShowAt(target);
    }

    private IEnumerable<MenuEntry> Selection(bool properties, bool includeRetry = false)
    {
        yield return Command("pause", Model.Pause, Syno.Segoe.Square);
        yield return Command("resume", Model.Resume, Syno.Segoe.Play);
        yield return Command("force", Model.Force, Syno.Segoe.Zap);
        if (includeRetry || Model.Retry.CanExecute(null))
            yield return Command("retry", Model.Retry, Syno.Segoe.RotateCcw);
        yield return new MenuSeparator();
        yield return Command("open", Model.Open, Syno.Segoe.File);
        yield return Command("open_folder", Model.OpenFolder, Syno.Segoe.FolderOpen);
        yield return Command("copy_magnet", Model.CopyMagnet, Syno.Segoe.Link);
        yield return Command("copy_hash", Model.CopyHash, Syno.Segoe.Hash);
        if (properties)
            yield return Command("properties", Model.Properties, Syno.Segoe.Info);
        yield return new MenuSeparator();
        yield return Command("verify", Model.Verify, Syno.Segoe.BookOpenCheck);
        yield return Command("move", Model.MoveFiles, Syno.Segoe.FolderInput);
        yield return Command("set_folder", Model.SetFolder, Syno.Segoe.FolderInput);
        yield return new MenuSubmenu(
            Model.Text.Get("menus", "queue"),
            Syno.Segoe.ListOrdered,
            [
                Command("up", Model.Up, Syno.Segoe.ArrowUp),
                Command("down", Model.Down, Syno.Segoe.ArrowDown),
                Command("top", Model.Top, Syno.Segoe.ArrowUpToLine),
                Command("bottom", Model.Bottom, Syno.Segoe.ArrowDownToLine),
            ]
        )
        {
            AccessKey = MenuKey("queue"),
        };
        yield return new MenuSeparator();
        yield return Choice("sequential", Model.SwitchSequential, Model.Sequential);
        yield return Choice("first_last", Model.SwitchFirstLast, Model.FirstLast);
        yield return SpeedLimit(
            Command("speed_limit", Model.LimitSpeed, Syno.Segoe.Gauge),
            Model.Limited,
            Model.Text
        );
        yield return new MenuSeparator();
        yield return Command("remove", Model.Remove, Syno.Segoe.Trash2);
    }

    private MenuCommand Command(string name, ICommand command, string glyph) =>
        new(Model.Text.Get("commands", name), glyph, command)
        {
            AccessKey = MenuKey(name),
            Shortcut = ShortcutText(command),
        };

    private MenuChoice Choice(string name, ICommand command, bool? state) =>
        new(Model.Text.Get("commands", name), null, state, command) { AccessKey = MenuKey(name) };

    private string MenuKey(string name) => Model.Text.Get("menus", name + "_key");

    private string ShortcutText(ICommand command) =>
        _shortcuts.TryGetValue(command, out var accelerator)
            ? Model.Text.Shortcut(accelerator.Modifiers, accelerator.Key)
            : string.Empty;

    private async Task ConfirmMerge()
    {
        await Interact(async interaction =>
        {
            var dialog = new Dialog { Glyph = Syno.Segoe.Merge, PrimaryGlyph = Syno.Segoe.Merge };
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

    private async Task ShowMove(Torrent[] torrents, bool initialize = true, bool download = false, string? destination = null)
    {
        await Interact(async interaction =>
        {
            var draft = Model.MoveDraft;
            if (initialize)
                draft.Begin(torrents, download, destination);
            interaction.Restore = () =>
                torrents.All(Model.Contains)
                    ? ShowMove(torrents, initialize: false, download, destination)
                    : Task.CompletedTask;
            var content = new FileDialog(Model, draft);
            content.DestinationRequested += async (_, _) =>
            {
                var folder = await PickFolder(draft.Destination, draft.Folder);
                if (folder is not null)
                    draft.Destination = folder;
            };
            var dialog = new Dialog { Content = content };
            await ShowFileDialog(
                interaction,
                dialog,
                initialize,
                () =>
                {
                    // Only Move's footer gives the count and size: Removal's
                    // footer is its opt-out, so its title and check box carry
                    // them instead.
                    if (draft.Total.Length > 0)
                        dialog.Footer = new DialogFooter.Detail(draft.Total);
                },
                draft.Submit
            );
            return true;
        });
    }

    private async Task ShowRemoval(Torrent[] torrents, DeletionMode deletion)
    {
        var focus = FocusManager.GetFocusedElement(Root.XamlRoot) is Control
        {
            FocusState: FocusState.Keyboard,
        }
            ? FocusState.Keyboard
            : FocusState.Pointer;
        await Interact(async interaction =>
        {
            var draft = Model.RemovalDraft;
            draft.Begin(torrents, deletion);
            var submitted = false;
            async Task<bool> Submit()
            {
                if (!Model.IsClosing && Navigation.IsWorkspace(Model.Page))
                    FocusRows(focus);
                Torrents.Release(torrents);
                submitted = await draft.Submit();
                return submitted;
            }
            if (
                // CanSubmit needs the engine, which has confirmed every setting by then.
                Model.Settings.ConfirmDelete.ConfirmedOn != true
                && draft.CanSubmit
                && await Submit()
            )
            {
                await draft.Discard();
                return true;
            }
            var content = new FileDialog(Model, draft);
            var dialog = new Dialog
            {
                Content = content,
                Footer = new DialogFooter.OptOut(draft.Confirmation),
            };
            // The check boxes change what the button deletes, so its tooltip
            // follows them.
            void FollowChoices(object? sender, PropertyChangedEventArgs args) =>
                ShowLabels(dialog, draft);
            draft.PropertyChanged += FollowChoices;
            try
            {
                await ShowFileDialog(interaction, dialog, initialize: true, null, Submit);
            }
            finally
            {
                draft.PropertyChanged -= FollowChoices;
            }
            return submitted;
        });
    }

    // What Move and Remove share: the dialog's size, its title and button
    // from the draft, and the scope read before it opens.
    private async Task ShowFileDialog(
        DialogInteraction interaction,
        Dialog dialog,
        bool initialize,
        Action? refreshFooter,
        Func<Task<bool>> submit
    )
    {
        var content = (FileDialog)dialog.Content;
        var draft = content.Draft;
        void Resize(object? sender = null, SizeChangedEventArgs? args = null)
        {
            content.Width = Math.Min(560, Root.ActualWidth - 80);
            content.MaxHeight = Math.Clamp(Root.ActualHeight - 180, 220, 440);
        }
        Resize();
        Root.SizeChanged += Resize;
        dialog.SizeToContent();
        _fileDialog = content;
        // The scope holds Move's shared-file choices and Removal's .torrent
        // files, so the dialog opens with every row it shows.
        if (initialize)
            await draft.RefreshScope();
        void RefreshText()
        {
            ShowLabels(dialog, draft);
            refreshFooter?.Invoke();
            content.RefreshText();
        }
        try
        {
            if (draft is RemovalDraft)
            {
                dialog.SetBinding(
                    ContentDialog.IsPrimaryButtonEnabledProperty,
                    new Binding
                    {
                        Source = draft,
                        Path = new PropertyPath(nameof(FileDraft.CanSubmit)),
                        Mode = BindingMode.OneWay,
                    }
                );
                try
                {
                    // Membership changes only after WinUI finishes closing its
                    // modal popup and restores focus to the existing table.
                    if (
                        await ShowDialog(interaction, dialog, RefreshText) == ContentDialogResult.Primary
                        && !Model.IsClosing
                    )
                    {
                        interaction.IsResolved = true;
                        if (!await submit() && draft.Failure is { } error)
                            Model.Notifications.Report(error);
                    }
                }
                finally
                {
                    await draft.Discard();
                    dialog.Content = null;
                }
            }
            else
                await ShowEditor(interaction, dialog, draft, RefreshText, submit);
        }
        finally
        {
            Root.SizeChanged -= Resize;
            _fileDialog = null;
        }
    }

    private static void ShowLabels(Dialog dialog, FileDraft draft)
    {
        dialog.Title = draft.Title;
        dialog.Glyph = draft.Glyph;
        dialog.PrimaryGlyph = draft.SubmitGlyph;
        dialog.PrimaryButtonText = draft.SubmitText;
        dialog.PrimaryToolTip = draft.SubmitToolTip;
    }

    private async Task ShowSpeedLimit(Torrent[] torrents, bool initialize = true)
    {
        await Interact(async interaction =>
        {
            if (initialize)
                Model.SpeedLimit.Begin(torrents);
            interaction.Restore = () =>
                torrents.All(Model.Contains) ? ShowSpeedLimit(torrents, false) : Task.CompletedTask;
            var content = new SpeedLimitDialog(Model);
            var dialog = new Dialog
            {
                Content = content,
                Glyph = Syno.Segoe.Gauge,
                PrimaryGlyph = Syno.Segoe.Check,
            };
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
            var content = new ProxyDialog(Model);
            var dialog = new Dialog
            {
                Content = content,
                Footer = new DialogFooter.Check(content.Status),
                Glyph = Syno.Segoe.Route,
                PrimaryGlyph = Syno.Segoe.Check,
            };
            dialog.SizeToContent();
            try
            {
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
            }
            finally
            {
                content.Detach();
            }
            return true;
        });
    }

    private async void OnInspectorClose(object? sender, EventArgs args) => await HideInspector();

    private async Task SwitchInspector()
    {
        if (Model.HasInspector)
        {
            await HideInspector();
            return;
        }
        if (Model.IsClosing || HasDialog || Model.Inspector.IsPending)
            return;
        Model.OpenInspector(expand: false);
    }

    private async Task HideInspector()
    {
        if (Model.IsClosing || Model.Inspector.IsPending)
            return;
        if (Model.Inspector.HasDraft && !await LeaveInspector())
            return;
        if (Model.CloseInspector())
            FocusRows(FocusState.Programmatic);
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
            Model.Notifications.Report(error);
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
            Model.Notifications.Report(error);
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
            if (Model.AddDraft.IsOpen && files is [StorageFolder folder])
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
                    .Select(file => file.Path),
                picked: false
            );
        }
        else if (data.Contains(StandardDataFormats.Text))
        {
            var text = await data.GetTextAsync();
            if (!CanApply())
                return;
            await Model.AddSources(
                text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries),
                picked: false
            );
        }
    }
}
