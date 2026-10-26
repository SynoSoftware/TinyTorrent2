using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Syno.TableView;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Settings;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Engine;

namespace Syno.TinyTorrent.Search;

public sealed partial class Workspace : UserControl
{
    private ScrollViewer? _scroll;

    internal event EventHandler? ClearRequested;
    internal event EventHandler? RebuildRequested;
    internal event EventHandler<string>? FolderRequested;

    public Browser Model { get; }
    private Setting Indexing { get; }

    internal Workspace(Browser model, Setting indexing)
    {
        Model = model;
        Indexing = indexing;
        InitializeComponent();
        Results.Schema<Row>().Key(row => row.Key)
            .Hierarchy(NameColumn, row => row.Children, row => row.IsExpanded, (row, value) => row.IsExpanded = value)
            .SortKey(NameColumn, row => row.Name, StringComparer.CurrentCultureIgnoreCase)
            .SortKey(SizeColumn, row => row.Bytes)
            .SortKey(SeedsColumn, row => row.Source.Availability.Seeds)
            .SortKey(PeersColumn, row => row.Source.Availability.Peers);
        Results.SelectionChanged += (_, selection) => Model.Select(selection);
        Results.ItemInvoked += (_, _) =>
        {
            Model.Select(Results.Selection);
            Model.InspectorRequestedFromView();
        };
        Results.ItemContextRequested += (_, args) => Context().ShowAt(args.Target,
            new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = args.Position });
        Results.LayoutChanged += (_, change) =>
        {
            if (change is not (LayoutChange.Sort or LayoutChange.Reset))
                return;
            var sort = Results.Sort;
            var order = sort?.Column == SizeColumn ? CatalogueSort.Size
                : sort?.Column == SeedsColumn ? CatalogueSort.Seeds
                : sort?.Column == PeersColumn ? CatalogueSort.Peers
                : CatalogueSort.Name;
            Model.Sort(order, sort?.Direction == SortDirection.Descending);
        };
        Results.Sort = new Sort(NameColumn);
        Results.SizeChanged += (_, _) => AttachScroll();
        Loaded += (_, _) =>
        {
            model.PropertyChanged -= OnModel;
            model.PropertyChanged += OnModel;
            AttachScroll();
            Results.Selection = Model.Selection;
        };
        Unloaded += (_, _) =>
        {
            if (IsLoaded)
                return;
            model.PropertyChanged -= OnModel;
            if (_scroll is not null)
                _scroll.ViewChanged -= OnScroll;
            _scroll = null;
        };
        RefreshText();
    }

    internal void FocusRows(FocusState state) => Results.Focus(state);

    internal void RefreshText()
    {
        Results.Strings = Model.Text.Table;
        foreach (var column in Results.Columns)
            column.DisplayName = Model.Text.Get("catalogue", column.Id!);
    }

    // Table.Selection is deliberately not bindable, so the view pushes the model's selection.
    private void OnModel(object? sender, PropertyChangedEventArgs args) => Results.Selection = Model.Selection;

    // When the binding moves the switch, Setting.Toggle already holds that value and does nothing.
    private async void OnIndexingToggled(object sender, RoutedEventArgs args) => await Indexing.Toggle(EnableIndexing.IsOn);

    private async void OnContinue(object sender, RoutedEventArgs args) => await Model.More();

    private void OnClear(object sender, RoutedEventArgs args) => Model.ClearSearch();

    private void OnFolder(object sender, RoutedEventArgs args)
    {
        if (Model.RecoveryFolder.Length > 0)
            FolderRequested?.Invoke(this, Model.RecoveryFolder);
    }

    private void OnRepair(object sender, RoutedEventArgs args) => RebuildRequested?.Invoke(this, EventArgs.Empty);

    private void OnDelete(object sender, RoutedEventArgs args) => ClearRequested?.Invoke(this, EventArgs.Empty);

    private async void OnScroll(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        if (_scroll is { } scroll && scroll.VerticalOffset + scroll.ViewportHeight >= scroll.ExtentHeight - 100)
            await Model.More();
    }

    private void AttachScroll()
    {
        if (IsLoaded && _scroll is null && FindScroll(Results) is { } rows)
        {
            _scroll = rows;
            rows.ViewChanged += OnScroll;
        }
    }

    private MenuFlyout Context() => MenuItems.Flyout(() =>
    [
        new MenuCommand(Model.Text.Get("catalogue", "download_selected"), Lucide.Download, Model.DownloadSelected),
        new MenuCommand(Model.Text.Get("catalogue", "download_torrent"), Lucide.Package, Model.DownloadWhole),
        new MenuCommand(Model.Text.Get("catalogue", "refresh"), Lucide.RefreshCw, Model.Refresh),
    ], () => Model.Text);

    internal static ScrollViewer? FindScroll(DependencyObject element)
    {
        if (element is ScrollViewer scroll)
            return scroll;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            if (FindScroll(VisualTreeHelper.GetChild(element, index)) is { } child)
                return child;
        return null;
    }
}
