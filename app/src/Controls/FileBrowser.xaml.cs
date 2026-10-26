using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Syno.TableView;
using Syno.TinyTorrent.Shared;
using Windows.Foundation;
using Windows.System;

namespace Syno.TinyTorrent.Controls;

public sealed partial class FileBrowser : UserControl
{
    private ScrollViewer? _scroller;
    private bool _paging;
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(
        nameof(Model),
        typeof(FileSelection),
        typeof(FileBrowser),
        new PropertyMetadata(null, OnModelChanged)
    );
    public FileSelection Model
    {
        get => (FileSelection)GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    internal Thickness ToolbarPadding
    {
        get => Toolbar.Padding;
        set => Toolbar.Padding = value;
    }

    public FileBrowser(FileSelection model)
    {
        Model = model;
        InitializeComponent();
        if (!Model.ShowsProgress)
            Files.Columns.Remove(ProgressColumn);
        var schema = Files
            .Schema<FileNode>()
            .Key(node => node.IsFolder ? "folder:" + node.Path : "file:" + node.Index)
            .Hierarchy(
                NameColumn,
                node => node.Children,
                node => node.IsExpanded,
                (node, expanded) => node.IsExpanded = expanded
            );
        if (Model.ShowsProgress)
            schema.SortKey(NameColumn, node => node.Name)
                .SortKey(SizeColumn, node => node.TotalSize)
                .SortKey(PriorityColumn, node => node.Priority)
                .SortKey(ProgressColumn, node => node.Progress);
        Files.SelectionChanged += (_, _) => RefreshActions();
        Files.PointerWheelChanged += (_, args) =>
        {
            if (Model.IsPaged && Model.Roots.Count == 0)
            {
                var delta = args.GetCurrentPoint(Files).Properties.MouseWheelDelta;
                if (delta != 0)
                    _ = ReadWindow(delta < 0);
            }
        };
        Files.ItemContextRequested += (_, args) =>
            ShowPriority(
                args.Target,
                args.SelectedItems.OfType<FileNode>().ToArray(),
                args.Position
            );
        // WinUI can deliver Unloaded while this control is still in the tree, as
        // ContentDialog does, so a late Unloaded is ignored and Loaded can repeat.
        Loaded += (_, _) =>
        {
            Model.Changed -= OnChanged;
            Model.Changed += OnChanged;
            _scroller = Find<ScrollViewer>(Files);
            if (_scroller is not null)
            {
                _scroller.ViewChanged -= OnScroll;
                _scroller.ViewChanged += OnScroll;
            }
            RefreshActions();
            _ = ReadWindow();
        };
        Unloaded += (_, _) =>
        {
            if (!IsLoaded)
            {
                Model.Changed -= OnChanged;
                if (_scroller is not null)
                    _scroller.ViewChanged -= OnScroll;
                _scroller = null;
            }
        };
        RefreshText();
    }

    private static void OnModelChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args
    )
    {
        var browser = (FileBrowser)sender;
        if (browser.Files is null)
            return;
        if (browser.IsLoaded)
        {
            if (args.OldValue is FileSelection previous)
                previous.Changed -= browser.OnChanged;
            browser.Model.Changed += browser.OnChanged;
        }
        browser.Files.Selection = Selection.Empty;
        browser.RefreshText();
        browser.RefreshActions();
        _ = browser.ReadWindow();
    }

    internal void RefreshText()
    {
        Bindings.Update();
        Files.Strings = Model.Text.Table;
        NameColumn.DisplayName = Model.Text.Get("files", "name");
        SizeColumn.DisplayName = Model.Text.Get("columns", "size");
        ProgressColumn.DisplayName = Model.Text.Get("columns", "progress");
        PriorityColumn.DisplayName = Model.Text.Get("files", "priority");
        string matching = Model.AllLabel;
        ToolTipService.SetToolTip(All, matching);
        AutomationProperties.SetName(All, matching);
        Model.Refresh();
    }

    internal bool Reveal(int index)
    {
        if (!Files.IsLoaded || Files.ActualHeight <= 0 || Files.ActualWidth <= 0)
            return false;
        if (Model.Reveal(index) is not { } file)
            return true;
        Files.RefreshView();
        Files.Selection = new Selection([file], file);
        Files.ScrollIntoView(file);
        Files.Focus(FocusState.Keyboard);
        return true;
    }

    private void OnChanged(object? sender, EventArgs args)
    {
        RefreshActions();
        ToolTipService.SetToolTip(All, Model.AllLabel);
        AutomationProperties.SetName(All, Model.AllLabel);
        if (
            Files.Sort is { } sort
            && (sort.Column == ProgressColumn || sort.Column == PriorityColumn)
        )
            Files.RefreshView();
        if (Model.IsPaged && Model.Roots.Count == 0)
            _ = ReadWindow();
    }

    private void OnScroll(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        if (!args.IsIntermediate)
            _ = ReadWindow();
    }

    private async Task ReadWindow(bool? direction = null)
    {
        if (_paging || !IsLoaded || _scroller is not { } scroller || !Model.IsPaged)
            return;
        var height = Find<ListViewItem>(Files)?.ActualHeight ?? 32;
        if (height <= 0)
            return;
        var forward = scroller.VerticalOffset + scroller.ViewportHeight >= scroller.ExtentHeight - 2 * height;
        var backward = scroller.VerticalOffset <= 2 * height;
        if (direction is null && !(forward && Model.HasMore) && !(backward && Model.HasPrevious))
            return;
        var advance = direction ?? (forward && Model.HasMore);
        if (advance ? !Model.HasMore : !Model.HasPrevious)
            return;
        var model = Model;
        var query = model.Search;
        var rows = model.VisibleNodes();
        var position = (int)(scroller.VerticalOffset / height);
        var anchor = rows.ElementAtOrDefault(position);
        var remainder = scroller.VerticalOffset % height;
        _paging = true;
        try
        {
            if (!await model.ReadWindow(advance) || !IsLoaded
                || Model != model || model.Search != query)
                return;
            if (anchor is null)
                return;
            var updated = model.VisibleNodes();
            var index = Enumerable.Range(0, updated.Count).FirstOrDefault(index =>
                anchor.IsFolder ? updated[index].IsFolder && updated[index].Path == anchor.Path
                    : updated[index].Index == anchor.Index, -1);
            if (index >= 0)
                Files.ScrollTo(index * height + remainder);
        }
        finally
        {
            _paging = false;
        }
    }

    private static T? Find<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
                return match;
            if (Find<T>(child) is { } descendant)
                return descendant;
        }
        return null;
    }

    private void RefreshActions()
    {
        Files.IsEnabled = Model.IsEnabled;
        Priority.IsEnabled = Model.IsEnabled && Files.Selection.Items.Count > 0;
    }

    private void OnAll(object sender, RoutedEventArgs args)
    {
        Model.SelectMatching(Model.AllMatching != true);
        All.IsChecked = Model.AllMatching;
    }

    private void OnExpand(object sender, RoutedEventArgs args)
    {
        Model.Expand(true);
        Files.RefreshView();
    }

    private void OnCollapse(object sender, RoutedEventArgs args)
    {
        Model.Expand(false);
        Files.RefreshView();
    }

    private void OnPriority(object sender, RoutedEventArgs args) =>
        ShowPriority(Priority, Files.Selection.Items.OfType<FileNode>().ToArray());

    private void ShowPriority(FrameworkElement target, FileNode[] nodes, Point? position = null)
    {
        if (!Model.IsEnabled || nodes.Length == 0)
            return;
        var menu = MenuItems.Flyout(
            () =>
                [
                    Choice("skip", 0, Lucide.CircleSlash),
                    Choice("low", 1, Lucide.ArrowDown),
                    Choice("normal", 4, Lucide.Minus),
                    Choice("high", 7, Lucide.ArrowUp),
                ],
            () => Model.Text
        );
        FlyoutShowOptions options = new();
        if (position is { } point)
            options.Position = point;
        menu.ShowAt(target, options);

        MenuChoice Choice(string key, int priority, string glyph) =>
            new(
                Model.Text.Get("files", key),
                glyph,
                nodes.All(node => node.Priority == priority),
                new RelayCommand(() => Model.Change(nodes, priority), () => true)
            )
            {
                AccessKey = Model.Text.Get("files", key + "_key"),
                Group = "FilePriority",
            };
    }

    private void OnFilesKey(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is VirtualKey.PageDown or VirtualKey.PageUp
            && Model.IsPaged && Model.Roots.Count == 0)
        {
            _ = ReadWindow(args.Key == VirtualKey.PageDown);
            args.Handled = true;
            return;
        }
        if (args.Key != VirtualKey.F2)
            return;
        FrameworkElement? target = null;
        for (
            var element = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            element is not null;
            element = VisualTreeHelper.GetParent(element)
        )
        {
            if (element is ListViewItem row)
            {
                target = row;
                break;
            }
            if (ReferenceEquals(element, Files) || element is ListView)
            {
                target = Files;
                break;
            }
            if (element is ButtonBase or ComboBox or TextBox or PasswordBox or Slider)
                return;
        }
        if (target is null || Files.Selection.Current is not FileNode current)
            return;
        FileNode[] nodes = Files.Selection.Items.Contains(current)
            ? Files.Selection.Items.OfType<FileNode>().ToArray()
            : [current];
        ShowPriority(target, nodes);
        args.Handled = true;
    }
}
