using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Syno.TableView;

namespace Syno.TinyTorrent.Library;

public sealed partial class LibraryTable : UserControl
{
    private readonly Browser _model;
    private readonly EmptyState _empty;
    private Table? _table;
    internal Table? Table => _table;
    private LibraryConfiguration _configuration;
    private readonly Dictionary<LibraryConfiguration, double> _positions = [];

    // The row each loaded title or name cell shows. Every row the table
    // realizes has one, because each configuration's table cannot hide that
    // column. The table realizes the visible rows and a quarter page around
    // them. The cell of a hidden column unloads, but a container the list
    // keeps after a scroll holds its old row until the list reuses it.
    private readonly Dictionary<FrameworkElement, LibraryRow> _cells = [];
    private bool _sharing;
    internal Dictionary<LibraryConfiguration, ColumnLayout> Layouts { get; } = [];
    internal event EventHandler<ItemContextRequestedEventArgs>? ItemContextRequested;

    public static Style RatingStyle(Ratings ratings, RatingSource source)
    {
        var key = source.Sample(ratings) switch
        {
            RatingSample.Small => "TinyTorrentRatingSubduedStyle",
            RatingSample.Large => "TinyTorrentRatingPrimaryStyle",
            _ => "TinyTorrentRatingSecondaryStyle",
        };
        return (Style)Application.Current.Resources[key];
    }

    public LibraryTable(Browser model)
    {
        _model = model;
        InitializeComponent();
        _empty = new EmptyState(model);
        Loaded += (_, _) =>
        {
            model.PropertyChanged += OnModel;
            Refresh();
        };
        Unloaded += (_, _) => model.PropertyChanged -= OnModel;
        Refresh();
    }

    internal void SaveLayout()
    {
        if (_table is not null)
        {
            Layouts[_configuration] = _table.Layout;
            _positions[_configuration] = _table.VerticalOffset;
        }
    }

    internal void RestoreLayouts(IReadOnlyDictionary<LibraryConfiguration, ColumnLayout> layouts)
    {
        foreach (var pair in layouts)
            Layouts[pair.Key] = pair.Value;
        if (_table is not null && Layouts.TryGetValue(_configuration, out var layout))
            _table.Layout = layout;
    }

    internal void FocusRows(FocusState state) => _table?.Focus(state);

    private void OnModel(object? sender, PropertyChangedEventArgs args)
    {
        if (
            args.PropertyName is nameof(Browser.Rows) or nameof(Browser.Configuration) or ""
            || args.PropertyName == nameof(Browser.EmptyText) && _model.Rows.Count == 0
        )
            Refresh();
        else if (
            args.PropertyName == nameof(Browser.Selection)
            && _table is { } table
            && ReferenceEquals(table.ItemsSource, _model.Rows)
        )
            table.Selection = _model.Selection;
    }

    private void Refresh()
    {
        var selection = _model.Selection;
        var created = _table is null || _configuration != _model.RowsConfiguration;
        if (created)
            Create();
        if (_table is not { } table)
            return;
        table.Strings = _model.Text.Table;
        table.IsEnabled = _model.RowsConfiguration == _model.Configuration;
        foreach (var column in table.Columns)
            column.DisplayName = _model.Text.Get(
                "library",
                column.Id == "title" ? "content_title" : column.Id!
            );
        if (table.IsEnabled && _model.Rows.Count == 0 && _model.EmptyText.Length > 0)
        {
            _empty.Refresh();
            table.EmptyContent = _empty;
        }
        else
            table.EmptyContent = null;
        if (ReferenceEquals(table.ItemsSource, _model.Rows))
            table.RefreshView();
        else
            table.ItemsSource = _model.Rows;
        table.Selection = selection;
        if (created)
        {
            Host.Children.Clear();
            Host.Children.Add(table);
            if (_positions.TryGetValue(_configuration, out var position))
                table.ScrollTo(position);
        }
    }

    private void OnCellRow(FrameworkElement cell, DataContextChangedEventArgs args)
    {
        if (cell.IsLoaded)
            Track(cell);
    }

    private void OnCellLoaded(object sender, RoutedEventArgs args) => Track((FrameworkElement)sender);

    private void OnCellUnloaded(object sender, RoutedEventArgs args)
    {
        var cell = (FrameworkElement)sender;
        // A late Unloaded can arrive for a cell already loaded again.
        if (cell.IsLoaded)
            return;
        _cells.Remove(cell);
        ShareCells();
    }

    private void Track(FrameworkElement cell)
    {
        if (cell.DataContext is LibraryRow row)
            _cells[cell] = row;
        else
            _cells.Remove(cell);
        ShareCells();
    }

    private void ShareCells()
    {
        if (_sharing)
            return;
        _sharing = true;
        // One update for all the cells that a scroll or a layout pass changes.
        DispatcherQueue.TryEnqueue(
            DispatcherQueuePriority.Low,
            () =>
            {
                _sharing = false;
                _model.SetDisplayed(_cells.Values.Select(shown => shown.Entry.EntryId).ToHashSet());
            }
        );
    }

    private void Create()
    {
        SaveLayout();
        if (_table is not null)
            _table.EmptyContent = null;
        _configuration = _model.RowsConfiguration;
        var template = _configuration switch
        {
            LibraryConfiguration.Videos => "LibraryVideosTable",
            LibraryConfiguration.Music => "LibraryMusicTable",
            LibraryConfiguration.Files => "LibraryFilesTable",
            _ => throw new InvalidOperationException("Unknown Library configuration."),
        };
        var table = (Table)((DataTemplate)Resources[template]).LoadContent();
        var schema = table.Schema<LibraryRow>().Key(row => row.Entry.EntryId);
        foreach (var column in table.Columns)
        {
            if (RatingSource.All.FirstOrDefault(source => source.Id == column.Id) is { } rating)
            {
                schema.SortKey(column, row => rating.Read(row.Entry.Ratings)?.Value);
                continue;
            }
            switch (column.Id)
            {
                case "size":
                    schema.SortKey(column, row => row.Entry.Size);
                    break;
                case "year":
                    schema.SortKey(column, row => row.Entry.Year);
                    break;
                case "track":
                    schema.SortKey(column, row => row.Entry.Track);
                    break;
                case "duration":
                    schema.SortKey(column, row => row.Entry.Duration);
                    break;
                case "bitrate":
                    schema.SortKey(column, row => row.Entry.Bitrate);
                    break;
                case "resolution":
                    schema.SortKey(
                        column,
                        row =>
                            row.Entry.Width is { } width && row.Entry.Height is { } height
                                ? (long?)width * height
                                : null
                    );
                    break;
                case "modified":
                    schema.SortKey(column, row => row.Entry.Modified);
                    break;
                case "artist":
                    schema.SortKey(column, row => row, ArtistOrder.Instance);
                    break;
                case "name":
                    schema.SortKey(
                        column,
                        row => row.Name,
                        StringComparer.CurrentCultureIgnoreCase
                    );
                    break;
                case "folder":
                    schema.SortKey(
                        column,
                        row => row.Folder,
                        StringComparer.CurrentCultureIgnoreCase
                    );
                    break;
                case "title":
                    schema.SortKey(
                        column,
                        row => row.Title,
                        StringComparer.CurrentCultureIgnoreCase
                    );
                    break;
                case "type":
                    schema.SortKey(
                        column,
                        row => row.Type,
                        StringComparer.CurrentCultureIgnoreCase
                    );
                    break;
                case "genres":
                    schema.SortKey(
                        column,
                        row => row.Genres,
                        StringComparer.CurrentCultureIgnoreCase
                    );
                    break;
                case "cast":
                    schema.SortKey(
                        column,
                        row => row.Cast,
                        StringComparer.CurrentCultureIgnoreCase
                    );
                    break;
                case "album":
                    schema.SortKey(
                        column,
                        row => row.Album,
                        StringComparer.CurrentCultureIgnoreCase
                    );
                    break;
                case "subtitles":
                    schema.SortKey(
                        column,
                        row => row.Subtitles,
                        StringComparer.CurrentCultureIgnoreCase
                    );
                    break;
                case "kind":
                    schema.SortKey(
                        column,
                        row => row.Kind,
                        StringComparer.CurrentCultureIgnoreCase
                    );
                    break;
                default:
                    throw new InvalidOperationException($"Unknown Library column: {column.Id}");
            }
        }
        table.SelectionChanged += (sender, selection) =>
        {
            if (ReferenceEquals(sender, _table))
                _model.Select(selection);
        };
        table.ItemInvoked += (_, _) => _model.Properties.Execute(null);
        table.ItemContextRequested += (_, args) => ItemContextRequested?.Invoke(this, args);
        table.Sort = new Sort(
            table.Columns.First(column =>
                column.Id
                == (
                    _configuration == LibraryConfiguration.Music ? "artist"
                    : _configuration == LibraryConfiguration.Videos ? "title"
                    : "name"
                )
            )
        );
        if (Layouts.TryGetValue(_configuration, out var layout))
            table.Layout = layout;
        _table = table;
    }

    private sealed class ArtistOrder : IComparer<LibraryRow>
    {
        internal static ArtistOrder Instance { get; } = new();

        public int Compare(LibraryRow? left, LibraryRow? right)
        {
            var artist = string.Compare(
                left?.Artist,
                right?.Artist,
                StringComparison.CurrentCultureIgnoreCase
            );
            if (artist != 0)
                return artist;
            var album = string.Compare(
                left?.Album,
                right?.Album,
                StringComparison.CurrentCultureIgnoreCase
            );
            return album != 0 ? album : Nullable.Compare(left?.Entry.Track, right?.Entry.Track);
        }
    }
}
