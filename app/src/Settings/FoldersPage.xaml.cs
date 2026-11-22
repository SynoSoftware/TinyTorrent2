using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Syno.TableView;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shell;
using Windows.System;

namespace Syno.TinyTorrent.Settings;

/// <summary>
/// Library folders, a child page of Plugins: a tree of the drives and their
/// folders with a check box each, and a summary of the places Library shows.
/// A click saves at once, as Windows' own settings do, so the page has no
/// Apply and Cancel.
/// </summary>
public sealed partial class FoldersPage : UserControl
{
    private readonly MainViewModel _main;

    // A row realized just before the choices changed joins RealizedItems only
    // at the table's next update of it, so that update refreshes the check
    // boxes once more.
    private bool _checksBehind;

    internal event EventHandler? ParentRequested;

    public FolderTree Model { get; }

    internal FoldersPage(MainViewModel main)
    {
        _main = main;
        Model = new FolderTree(main);
        InitializeComponent();
        // The table takes its column's name when it first loads, which can
        // come before this page's own Loaded.
        RefreshText();
        Tree.Schema<FolderNode>()
            .Key(node => node.Path)
            .Hierarchy(
                NameColumn,
                node => node.Children,
                node => node.IsExpanded,
                (node, expanded) => Model.Expand(node, expanded),
                node => node.State is FolderState.Unloaded or FolderState.Loading
            )
            .SortKey(NameColumn, node => node.Name, StringComparer.CurrentCultureIgnoreCase)
            .SortKey(SizeColumn, node => node.Size, emptyLast: true)
            .SortKey(FilesColumn, node => node.Files, emptyLast: true)
            .SortKey(FoldersColumn, node => node.Folders, emptyLast: true);
        // The table reads only the folder named, so a folder of 25,662
        // subfolders costs the table nothing for the rest of the tree.
        Model.Filled += (_, folders) => Tree.RefreshView(folders);
        Tree.RealizedItemsChanged += (_, items) =>
        {
            if (!_checksBehind)
                return;
            _checksBehind = false;
            foreach (var folder in items.Cast<FolderNode>())
                folder.Refresh();
        };
        Model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(FolderTree.Summary))
                PageHeader.Subtitle = Model.Summary;
        };
        // WinUI can deliver Unloaded while the page is still in the tree, so a
        // late Unloaded is ignored and Loaded can repeat.
        Loaded += async (_, _) =>
        {
            _main.PropertyChanged -= OnMain;
            _main.PropertyChanged += OnMain;
            _main.TextChanged -= OnText;
            _main.TextChanged += OnText;
            RefreshText();
            await Model.Open();
            ShowPlaces();
        };
        Unloaded += (_, _) =>
        {
            if (IsLoaded)
                return;
            _main.PropertyChanged -= OnMain;
            _main.TextChanged -= OnText;
            Model.Close();
        };
    }

    internal void FocusHeading() => Hierarchy.Focus(FocusState.Programmatic);

    private void OnHierarchy(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Index == 0)
            ParentRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnMain(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(MainViewModel.Folders) or "")
        {
            RefreshChecks();
            ShowPlaces();
        }
    }

    // Only the rows the table realizes show a check box; a row realized later
    // reads its check box from the choices then.
    private void RefreshChecks()
    {
        foreach (var folder in Tree.RealizedItems.Cast<FolderNode>())
            folder.Refresh();
        _checksBehind = true;
    }

    // A click sets the check box before the change is saved, so a change
    // that failed sets the boxes back.
    private async Task Toggle(FolderNode folder)
    {
        if (!await Model.Toggle(folder))
            RefreshChecks();
    }

    private void OnText(object? sender, EventArgs args)
    {
        RefreshText();
        RefreshChecks();
        ShowPlaces();
    }

    private async void OnCheck(object sender, RoutedEventArgs args) =>
        await Toggle((FolderNode)((CheckBox)sender).Tag);

    // Space sets the current folder's check box, as a click does, so the
    // keyboard needs no second stop on each row.
    private async void OnTreeKey(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Space || !InRows() || Tree.Selection.Current is not FolderNode folder)
            return;
        args.Handled = true;
        await Toggle(folder);
    }

    // Whether focus is on the rows, not on the column header.
    private bool InRows()
    {
        for (
            var element = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            element is not null && !ReferenceEquals(element, Tree);
            element = VisualTreeHelper.GetParent(element)
        )
        {
            if (element is ListViewItem or ListView)
                return true;
        }
        return false;
    }

    private void OnBodySize(object sender, SizeChangedEventArgs args)
    {
        // Side by side when both fit, as Connection setup's cards are;
        // otherwise the summary goes below the tree and takes at most a
        // third of the height, so the tree keeps room to scroll.
        var stacked = !FieldColumns.Fits(Body.ActualWidth, 880);
        TreeColumn.Width = new(stacked ? 1 : 3, GridUnitType.Star);
        PlacesColumn.Width = stacked ? new(0) : new(2, GridUnitType.Star);
        PlacesRow.Height = stacked ? GridLength.Auto : new(0);
        PlacesCard.MaxHeight = stacked ? Body.ActualHeight / 3 : double.PositiveInfinity;
        Grid.SetColumn(PlacesCard, stacked ? 0 : 1);
        Grid.SetRow(PlacesCard, stacked ? 1 : 0);
    }

    private void RefreshText()
    {
        var trail = new[] { _main.Text.Get(SettingsCategory.Plugins), _main.Text.Get("discovery", "folders") };
        if (Hierarchy.ItemsSource is not string[] current || !current.SequenceEqual(trail))
            Hierarchy.ItemsSource = trail;
        AutomationProperties.SetName(Hierarchy, trail[^1]);
        PageHeader.Title = _main.Text.Get("discovery", "shows");
        AutomationProperties.SetName(Tree, _main.Text.Get("discovery", "tree"));
        NameColumn.DisplayName = _main.Text.Get("discovery", "folder");
        SizeColumn.DisplayName = _main.Text.Get("columns", "size");
        FilesColumn.DisplayName = _main.Text.Get("columns", "files");
        FoldersColumn.DisplayName = _main.Text.Get("columns", "folders");
        PlacesTitle.Text = _main.Text.Get("discovery", "places");
    }

    // One row for each place Library shows: a drive, or a shown folder inside
    // a left-out one, with the folders left out inside it. The rows stay and
    // their text changes; a row is added or removed only when the number of
    // places changes, because new WinUI elements grow the working set.
    private void ShowPlaces()
    {
        PageHeader.Subtitle = Model.Summary;
        var names = Model.Roots.ToDictionary(root => root.Path, root => root.Name, StringComparer.OrdinalIgnoreCase);
        var places = Model.Choices.Places(Model.Drives);
        while (Places.Children.Count > places.Count)
            Places.Children.RemoveAt(Places.Children.Count - 1);
        while (Places.Children.Count < places.Count)
        {
            Places.Children.Add(
                new SettingsRow
                {
                    Icon = new FontIcon
                    {
                        FontFamily = Segoe.Font,
                        FontSize = (double)Application.Current.Resources["TinyTorrentHeaderIconSize"],
                    },
                }
            );
        }
        for (var index = 0; index < places.Count; index++)
        {
            var (folder, leftOut) = places[index];
            var inside = FileSources.Folder(folder).Length;
            var row = (SettingsRow)Places.Children[index];
            ((FontIcon)row.Icon!).Glyph = names.ContainsKey(folder) ? Segoe.HardDrive : Segoe.Folder;
            row.Header = names.GetValueOrDefault(folder, folder);
            row.Description = leftOut.Count == 0
                ? _main.Text.Get("discovery", "nothing_left_out")
                : _main.Text.Format(
                    "discovery",
                    "places_left_out",
                    _main.Text.FormatCount("discovery", "left_out_count", leftOut.Count),
                    string.Join("; ", leftOut.Select(left => left[inside..]))
                );
            // The last row has no line under it; a row that was last takes
            // the card's own line again.
            if (index == places.Count - 1)
                row.BorderThickness = new(0);
            else
                row.ClearValue(Control.BorderThicknessProperty);
        }
    }
}
