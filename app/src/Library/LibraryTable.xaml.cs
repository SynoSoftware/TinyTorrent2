using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Syno.TableView;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Controls;

namespace Syno.TinyTorrent.Library;

public sealed partial class LibraryTable : UserControl
{
    private readonly Browser _model;
    private Table? _table;
    internal Table? Table => _table;
    private LibraryConfiguration _configuration;
    private readonly Dictionary<LibraryConfiguration, double> _positions = [];
    internal Dictionary<LibraryConfiguration, ColumnLayout> Layouts { get; } = [];
    internal event EventHandler<ItemContextRequestedEventArgs>? ItemContextRequested;

    public LibraryTable(Browser model)
    {
        _model = model;
        InitializeComponent();
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

    internal void FocusRows() => _table?.Focus(FocusState.Keyboard);

    private void OnModel(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(Browser.Rows) or nameof(Browser.Configuration) or "")
            Refresh();
    }

    private void Refresh()
    {
        if (_table is null || _configuration != _model.Configuration)
            Create();
        if (_table is not { } table)
            return;
        table.Strings = _model.Text.Table;
        foreach (var column in table.Columns)
            column.DisplayName = _model.Text.Get("library", column.Id == "title" ? "content_title" : column.Id!);
        if (_model.Rows.Count == 0)
            RefreshEmpty(table);
        else
        {
            ClearEmpty();
            table.EmptyContent = null;
        }
        if (ReferenceEquals(table.ItemsSource, _model.Rows))
            table.RefreshView();
        else
            table.ItemsSource = _model.Rows;
        if (_model.Selected is { } selected && _model.Rows.Contains(selected))
            table.Selection = new Selection([selected], selected);
    }

    private void RefreshEmpty(Table table)
    {
        var empty = new StackPanel { Spacing = 12, MaxWidth = 420, Margin = new Thickness(24),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        empty.Children.Add(new TextBlock { Text = _model.EmptyText, TextAlignment = TextAlignment.Center,
            Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
        if (_model.Query.Length > 0)
            empty.Children.Add(new TextBlock { Text = _model.Query, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                Style = (Style)Application.Current.Resources["TinyTorrentTitleDetailTextStyle"] });
        foreach (var configuration in Enum.GetValues<LibraryConfiguration>())
        {
            var count = _model.Counts.GetValueOrDefault(configuration);
            if (configuration == _model.Configuration || count == 0)
                continue;
            var name = _model.Text.Get("library", configuration.ToString().ToLowerInvariant());
            var show = new ActionButton
            {
                Text = _model.Text.Format("filters", "count", name, count),
                Glyph = configuration switch
                {
                    LibraryConfiguration.Videos => Lucide.Film,
                    LibraryConfiguration.Music => Lucide.Music,
                    _ => Lucide.Files,
                },
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            AutomationProperties.SetAutomationId(show, "LibraryMatches-" + configuration);
            ToolTipService.SetToolTip(show, _model.Text.Format("library", "show_matches_tip", name, count.ToString("N0")));
            show.Click += (_, _) => _model.Configuration = configuration;
            empty.Children.Add(show);
        }
        if (_model.IsEmpty)
        {
            var show = new ActionButton { Text = _model.Text.Get("window", "torrents"), Glyph = Lucide.Download, Command = _model.ShowTorrents,
                HorizontalAlignment = HorizontalAlignment.Center };
            AutomationProperties.SetAutomationId(show, "LibraryShowTorrents");
            ToolTipService.SetToolTip(show, _model.Text.Get("library", "show_torrents"));
            empty.Children.Add(show);
        }
        if (_model.Query.Length > 0)
        {
            var clear = new ActionButton { Text = _model.Text.Get("library", "clear_search"), Glyph = Lucide.X, HorizontalAlignment = HorizontalAlignment.Center };
            AutomationProperties.SetAutomationId(clear, "LibraryClearSearch");
            ToolTipService.SetToolTip(clear, _model.Text.Get("library", "clear_search_tip"));
            clear.Click += (_, _) => _model.Query = string.Empty;
            empty.Children.Add(clear);
        }
        if (_model.Filters.Count > 0)
        {
            var clear = new ActionButton { Text = _model.Text.Get("window", "clear_filters"), Glyph = Lucide.X, Command = _model.ClearFilters,
                HorizontalAlignment = HorizontalAlignment.Center };
            AutomationProperties.SetAutomationId(clear, "LibraryClearFilters");
            ToolTipService.SetToolTip(clear, _model.Text.Get("window", "clear_filters_tip"));
            empty.Children.Add(clear);
        }
        ClearEmpty();
        table.EmptyContent = empty;
    }

    private void ClearEmpty()
    {
        if (_table?.EmptyContent is StackPanel content)
            foreach (var action in content.Children.OfType<ActionButton>())
                action.Command = null;
    }

    private void Create()
    {
        SaveLayout();
        ClearEmpty();
        _configuration = _model.Configuration;
        var table = new Table { Strings = _model.Text.Table };
        AutomationProperties.SetAutomationId(table, "LibraryTable");
        var schema = table.Schema<LibraryRow>().Key(row => row.Entry.EntryId);
        var visible = _configuration switch
        {
            LibraryConfiguration.Videos => new[] { "Title", "Type", "Year", "Genres" },
            LibraryConfiguration.Music => new[] { "Title", "Artist", "Album", "Track", "Duration", "Year" },
            _ => new[] { "Name", "Folder", "Size", "Modified" },
        };
        var hidden = _configuration switch
        {
            LibraryConfiguration.Videos => new[] { "Cast", "Size", "Name", "Folder", "Modified" },
            LibraryConfiguration.Music => new[] { "Genres", "Name", "Folder", "Size", "Modified" },
            _ => new[] { "Kind", "Title" },
        };
        foreach (var name in visible.Concat(hidden))
        {
            var column = new Column
            {
                Id = name.ToLowerInvariant(),
                DisplayName = _model.Text.Get("library", name == "Title" ? "content_title" : name.ToLowerInvariant()),
                CellTemplate = (DataTemplate)Resources[name],
                Width = name is "Title" or "Name" or "Folder" ? 300 : name == "Size" ? 110 : 150,
                MinWidth = name == visible[0] ? 160 : 48,
                CanHide = name != visible[0],
                CellAlignment = name == "Size" ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                IsVisible = visible.Contains(name),
            };
            table.Columns.Add(column);
            switch (name)
            {
                case "Size": schema.SortKey(column, row => row.Entry.Size); break;
                case "Year": schema.SortKey(column, row => row.Entry.Year); break;
                case "Track": schema.SortKey(column, row => row.Entry.Track); break;
                case "Duration": schema.SortKey(column, row => row.Entry.Duration); break;
                case "Modified": schema.SortKey(column, row => row.Entry.Modified); break;
                case "Artist": schema.SortKey(column, row => row, ArtistOrder.Instance); break;
                default: schema.SortKey(column, row => name switch
                {
                    "Name" => row.Name, "Folder" => row.Folder, "Title" => row.Title,
                    "Type" => row.Type, "Genres" => row.Genres, "Cast" => row.Cast,
                    "Album" => row.Album, _ => row.Kind,
                }, StringComparer.CurrentCultureIgnoreCase); break;
            }
        }
        table.SelectionChanged += (_, selection) => _model.Select(selection.Current as LibraryRow);
        table.ItemInvoked += (_, _) => _model.Open.Execute(null);
        table.ItemContextRequested += (_, args) => ItemContextRequested?.Invoke(this, args);
        table.Sort = new Sort(table.Columns.First(column => column.Id ==
            (_configuration == LibraryConfiguration.Music ? "artist" : _configuration == LibraryConfiguration.Videos ? "title" : "name")));
        if (Layouts.TryGetValue(_configuration, out var layout))
            table.Layout = layout;
        _table = table;
        Host.Children.Clear();
        Host.Children.Add(table);
        if (_positions.TryGetValue(_configuration, out var position))
            table.ScrollTo(position);
    }

    private sealed class ArtistOrder : IComparer<LibraryRow>
    {
        internal static ArtistOrder Instance { get; } = new();
        public int Compare(LibraryRow? left, LibraryRow? right)
        {
            var artist = StringComparer.CurrentCultureIgnoreCase.Compare(left?.Artist, right?.Artist);
            if (artist != 0)
                return artist;
            var album = StringComparer.CurrentCultureIgnoreCase.Compare(left?.Album, right?.Album);
            return album != 0 ? album : Nullable.Compare(left?.Entry.Track, right?.Entry.Track);
        }
    }
}
