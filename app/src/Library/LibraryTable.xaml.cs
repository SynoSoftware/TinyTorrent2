using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
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
    internal Dictionary<LibraryConfiguration, ColumnLayout> Layouts { get; } = [];
    internal event EventHandler<ItemContextRequestedEventArgs>? ItemContextRequested;

    public static Style RatingStyle(Ratings ratings, RatingSource source)
    {
        var key = source.Sample(ratings) switch
        {
            RatingSample.Small => "TinyTorrentRatingSubduedStyle",
            RatingSample.Large => "TinyTorrentRatingPrimaryStyle",
            _ => "TinyTorrentSecondaryTextStyle",
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

    internal void FocusRows(FocusState state) => _table?.FocusRows(state);

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
        {
            // The person's search applies at once; a background refresh waits for the settle.
            if (_model.RowsSearched && table.ItemsSource is IReadOnlyList<LibraryRow> shown)
                table.Release([.. shown.Except(_model.Rows), .. _model.Rows.Except(shown)]);
            table.ItemsSource = _model.Rows;
            // A row keeps its object across searches, so the table reads its
            // new sort key only when named.
            if (_model.UpdatedRows.Count > 0)
                table.RefreshView(_model.UpdatedRows);
        }
        table.Selection = selection;
        if (created)
        {
            Host.Children.Clear();
            Host.Children.Add(table);
            if (_positions.TryGetValue(_configuration, out var position))
                table.ScrollTo(position);
        }
    }

    private void Create()
    {
        SaveLayout();
        if (_table is not null)
            _table.EmptyContent = null;
        _configuration = _model.RowsConfiguration;
        var table = new Table();
        AutomationProperties.SetAutomationId(table, "LibraryTable");
        var columns = Columns(_configuration);
        for (var index = 0; index < columns.Length; index++)
        {
            var (id, shown) = columns[index];
            table.Columns.Add(Define(id, shown, canHide: index > 0));
        }
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
        table.RealizedItemsChanged += (sender, rows) =>
        {
            if (ReferenceEquals(sender, _table))
                _model.SetDisplayed(
                    rows.Cast<LibraryRow>().Select(row => row.Entry.EntryId).ToHashSet()
                );
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

    // Each configuration's columns in their default order. The first one
    // cannot be hidden.
    private static (string Id, bool Shown)[] Columns(LibraryConfiguration configuration) =>
        configuration switch
        {
            LibraryConfiguration.Videos =>
            [
                ("title", true),
                ("type", true),
                ("year", true),
                ("duration", true),
                ("resolution", false),
                ("bitrate", false),
                ("subtitles", true),
                ("imdb", true),
                ("tmdb", true),
                ("rottentomatoes", true),
                ("popcornmeter", true),
                ("metacritic", false),
                ("genres", true),
                ("cast", false),
                ("size", false),
                ("name", false),
                ("folder", false),
                ("modified", false),
            ],
            LibraryConfiguration.Music =>
            [
                ("title", true),
                ("artist", true),
                ("album", true),
                ("track", true),
                ("duration", true),
                ("year", true),
                ("bitrate", false),
                ("genres", false),
                ("name", false),
                ("folder", false),
                ("size", false),
                ("modified", false),
            ],
            LibraryConfiguration.Files =>
            [
                ("name", true),
                ("folder", true),
                ("size", true),
                ("modified", true),
                ("kind", false),
                ("title", false),
                ("duration", false),
                ("resolution", false),
                ("bitrate", false),
            ],
            _ => throw new InvalidOperationException("Unknown Library configuration."),
        };

    private Column Define(string id, bool shown, bool canHide)
    {
        var column = id switch
        {
            "title" => new Column { CellTemplate = Cell("Title"), Width = 300 },
            "name" => new Column { CellTemplate = Cell("Name"), Width = 300 },
            "folder" => new Column { CellTemplate = Cell("Folder"), Width = 300 },
            "type" => new Column { CellTemplate = Cell("Type"), CanShrink = false },
            "year" => new Column { CellTemplate = Cell("Year"), CanShrink = false },
            "resolution" => new Column { CellTemplate = Cell("Resolution"), CanShrink = false },
            "subtitles" => new Column { CellTemplate = Cell("Subtitles"), CanShrink = false },
            "modified" => new Column { CellTemplate = Cell("Modified"), CanShrink = false },
            "kind" => new Column { CellTemplate = Cell("Kind"), CanShrink = false },
            "track" => new Column { CellTemplate = Cell("Track"), CanShrink = false },
            "duration" => new Column
            {
                CellTemplate = Cell("Duration"),
                CanShrink = false,
                CellAlignment = HorizontalAlignment.Right,
            },
            "bitrate" => new Column
            {
                CellTemplate = Cell("Bitrate"),
                CanShrink = false,
                CellAlignment = HorizontalAlignment.Right,
            },
            "size" => new Column
            {
                CellTemplate = Cell("Size"),
                CanShrink = false,
                Width = 110,
                CellAlignment = HorizontalAlignment.Right,
            },
            "imdb" => new Column
            {
                CellTemplate = Cell("ImdbRating"),
                CanShrink = false,
                Width = 110,
                CellAlignment = HorizontalAlignment.Right,
            },
            "tmdb" => new Column
            {
                CellTemplate = Cell("TmdbRating"),
                CanShrink = false,
                Width = 110,
                CellAlignment = HorizontalAlignment.Right,
            },
            "metacritic" => new Column
            {
                CellTemplate = Cell("MetacriticRating"),
                CanShrink = false,
                Width = 110,
                CellAlignment = HorizontalAlignment.Right,
            },
            "rottentomatoes" => new Column
            {
                CellTemplate = Cell("RottenTomatoesRating"),
                CanShrink = false,
                Width = 140,
                CellAlignment = HorizontalAlignment.Right,
            },
            "popcornmeter" => new Column
            {
                CellTemplate = Cell("PopcornmeterRating"),
                CanShrink = false,
                Width = 120,
                CellAlignment = HorizontalAlignment.Right,
            },
            "genres" => new Column { CellTemplate = Cell("Genres") },
            "cast" => new Column { CellTemplate = Cell("Cast") },
            "artist" => new Column { CellTemplate = Cell("Artist") },
            "album" => new Column { CellTemplate = Cell("Album") },
            _ => throw new InvalidOperationException($"Unknown Library column: {id}"),
        };
        column.Id = id;
        column.IsVisible = shown;
        column.CanHide = canHide;
        return column;

        DataTemplate Cell(string key) => (DataTemplate)Resources[key];
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
