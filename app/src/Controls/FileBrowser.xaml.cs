using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Syno.TableView;
using Windows.Foundation;
using Windows.System;

namespace Syno.TinyTorrent.Controls;

public sealed partial class FileBrowser : UserControl
{
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(nameof(Model), typeof(FileSelection), typeof(FileBrowser), new PropertyMetadata(null, OnModelChanged));
    public FileSelection Model { get => (FileSelection)GetValue(ModelProperty); set => SetValue(ModelProperty, value); }

    public FileBrowser(FileSelection model)
    {
        Model = model;
        InitializeComponent();
        if (!Model.ShowsProgress) Files.Columns.Remove(ProgressColumn);
        var schema = Files.Schema<FileNode>()
            .Key(node => node.Path)
            .Hierarchy(NameColumn, node => node.Children, node => node.IsExpanded,
                (node, expanded) => node.IsExpanded = expanded)
            .SortKey(NameColumn, node => node.Name)
            .SortKey(SizeColumn, node => node.TotalSize)
            .SortKey(PriorityColumn, node => node.Priority);
        if (Model.ShowsProgress) schema.SortKey(ProgressColumn, node => node.Progress);
        Files.SelectionChanged += (_, _) => RefreshActions();
        Files.ItemContextRequested += (_, args) =>
            ShowPriority(args.Target, args.SelectedItems.OfType<FileNode>().ToArray(), args.Position);
        Loaded += (_, _) => { Model.Changed += OnChanged; RefreshActions(); };
        Unloaded += (_, _) => Model.Changed -= OnChanged;
        RefreshText();
    }

    private static void OnModelChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var browser = (FileBrowser)sender;
        if (browser.Files is null) return;
        if (browser.IsLoaded)
        {
            if (args.OldValue is FileSelection previous) previous.Changed -= browser.OnChanged;
            browser.Model.Changed += browser.OnChanged;
        }
        browser.Files.Selection = Selection.Empty;
        browser.RefreshText();
        browser.RefreshActions();
    }

    internal void RefreshText()
    {
        Bindings.Update();
        Files.Strings = Model.Text.Table;
        NameColumn.DisplayName = Model.Text.Get("files", "name");
        SizeColumn.DisplayName = Model.Text.Get("columns", "size");
        ProgressColumn.DisplayName = Model.Text.Get("columns", "progress");
        PriorityColumn.DisplayName = Model.Text.Get("files", "priority");
        string matching = Model.Text.Get("files", "all_matching");
        ToolTipService.SetToolTip(All, matching);
        AutomationProperties.SetName(All, matching);
        Model.Refresh();
    }

    private void OnChanged(object? sender, EventArgs args)
    {
        RefreshActions();
        if (Files.Sort is not null) Files.RefreshView();
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
        if (!Model.IsEnabled || nodes.Length == 0) return;
        MenuFlyout menu = new();
        foreach (var (key, priority) in new[] { ("skip", 0), ("low", 1), ("normal", 4), ("high", 7) })
        {
            MenuFlyoutItem item = new() { Text = Model.Text.Get("files", key) };
            item.Click += (_, _) => Model.Change(nodes, priority);
            menu.Items.Add(item);
        }
        FlyoutShowOptions options = new();
        if (position is { } point) options.Position = point;
        menu.ShowAt(target, options);
    }

    private void OnFilesKey(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.F2) return;
        FrameworkElement? target = null;
        for (var element = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
             element is not null;
             element = VisualTreeHelper.GetParent(element))
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
            if (element is ButtonBase or ComboBox or TextBox or PasswordBox or Slider) return;
        }
        if (target is null || Files.Selection.Current is not FileNode current) return;
        FileNode[] nodes = Files.Selection.Items.Contains(current)
            ? Files.Selection.Items.OfType<FileNode>().ToArray() : [current];
        ShowPriority(target, nodes);
        args.Handled = true;
    }
}
