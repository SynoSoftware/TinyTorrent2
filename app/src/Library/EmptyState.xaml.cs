using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Controls;

namespace Syno.TinyTorrent.Library;

public sealed partial class EmptyState : UserControl
{
    private readonly Browser _model;

    public EmptyState(Browser model)
    {
        _model = model;
        InitializeComponent();
        Videos.Click += (_, _) => model.Configuration = LibraryConfiguration.Videos;
        Music.Click += (_, _) => model.Configuration = LibraryConfiguration.Music;
        Files.Click += (_, _) => model.Configuration = LibraryConfiguration.Files;
        ClearSearch.Click += (_, _) => model.Query = string.Empty;
        Loaded += (_, _) =>
        {
            Torrents.Command = model.ShowTorrents;
            ClearFilters.Command = model.ClearFilters;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            Torrents.Command = null;
            ClearFilters.Command = null;
        };
    }

    internal void Refresh()
    {
        Message.Text = _model.EmptyText;
        Query.Text = _model.Query;
        Query.Visibility = _model.Query.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshMatches(Videos, LibraryConfiguration.Videos);
        RefreshMatches(Music, LibraryConfiguration.Music);
        RefreshMatches(Files, LibraryConfiguration.Files);
        Torrents.Visibility = _model.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        Torrents.Text = _model.Text.Get("window", "torrents");
        ToolTipService.SetToolTip(Torrents, _model.Text.Get("library", "show_torrents"));
        ClearSearch.Visibility = Query.Visibility;
        ClearSearch.Text = _model.Text.Get("library", "clear_search");
        ToolTipService.SetToolTip(ClearSearch, _model.Text.Get("library", "clear_search_tip"));
        ClearFilters.Visibility =
            _model.Filters.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearFilters.Text = _model.Text.Get("window", "clear_filters");
        ToolTipService.SetToolTip(ClearFilters, _model.Text.Get("window", "clear_filters_tip"));
        if (IsLoaded)
            ActionButton.Align(
                Actions
                    .Children.OfType<ActionButton>()
                    .Where(button => button.Visibility == Visibility.Visible)
            );
    }

    private void RefreshMatches(ActionButton button, LibraryConfiguration configuration)
    {
        var count = _model.Counts.GetValueOrDefault(configuration);
        button.Visibility =
            configuration != _model.Configuration && count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        var name = _model.Text.Get("library", configuration.ToString().ToLowerInvariant());
        button.Text = _model.Text.Format("filters", "count", name, count);
        ToolTipService.SetToolTip(
            button,
            _model.Text.Format("library", "show_matches_tip", name, count.ToString("N0"))
        );
    }
}
