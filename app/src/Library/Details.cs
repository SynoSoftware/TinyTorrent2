using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using System.Windows.Input;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Models;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace Syno.TinyTorrent.Library;

internal sealed partial class Details : UserControl, IDisposable
{
    private const double SectionMinimum = 1040;
    private readonly List<Button> _actions = [];
    private readonly ActionButton? _fetch;
    private readonly Field? _fetchMessage;
    private readonly ScrollViewer _viewport;
    private readonly FieldColumns? _sections;
    private readonly WrapPanel? _cast;
    private readonly UISettings _uiSettings = new();
    private double _columnWidth = double.NaN;
    private double _columnScale = double.NaN;
    private double _columnHeight;

    internal Details(Browser library, bool showsFile, ScrollViewer viewport)
    {
        _viewport = viewport;
        var group = new Controls.Section();
        Content = group;
        if (library.Selected is not { } row)
            return;
        var text = library.Text;
        var left = new StackPanel();
        var right = new StackPanel();
        var fields = new FieldColumns { Primary = left, Secondary = right };
        if (showsFile)
        {
            Heading(text.Get("inspector", "properties"), Lucide.Info);
            AddField(left, "name", row.Name, Lucide.File);
            AddField(left, "folder", row.Folder, Lucide.Folder,
                Action(Lucide.FolderOpen, text.Get("commands", "open_folder"), library.OpenFolder));
            foreach (var origin in library.Origins)
            {
                var show = Action(Lucide.LocateFixed, text.Get("library", "show_torrents"), new RelayCommand(() =>
                {
                    library.ShowTorrent(origin.OriginId);
                    return Task.CompletedTask;
                }, () => library.IsAvailable));
                left.Children.Add(new Field { Label = text.Get("menus", "torrent"), Glyph = Lucide.LocateFixed,
                    Content = Value(origin.Name), Action = show });
            }
            AddField(right, "size", row.Size, Lucide.HardDrive);
            AddField(right, "file_type", library.FileType, row.Glyph);
            var application = AddField(right, "opens_with", library.Application.Length > 0 ? library.Application :
                library.FileType.Length > 0 ? text.Get("library", "no_application") : string.Empty, Lucide.Play);
            if (application is not null && library.ApplicationIcon is { } icon)
            {
                var associated = new Grid { ColumnSpacing = 8 };
                associated.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                associated.ColumnDefinitions.Add(new ColumnDefinition());
                associated.Children.Add(new Image { Source = icon, Width = 20, Height = 20 });
                var name = Value(library.Application);
                Grid.SetColumn(name, 1);
                associated.Children.Add(name);
                application.Content = associated;
            }
            AddField(right, "created", row.Created, Lucide.Calendar);
            AddField(right, "modified", row.Modified, Lucide.Clock);
        }
        else
        {
            left.Spacing = 20;
            fields = new FieldColumns { Primary = left, Secondary = right, Spacing = 32,
                StackSpacing = 20, SecondaryWidth = new GridLength(384), SeparateSecondary = true };
            if (row.Entry.Kind == FileKind.Video && library.IsOnDemand && library.IsEnrichmentEnabled)
            {
                _fetch = new ActionButton { Glyph = Lucide.RefreshCw,
                    Command = library.FetchDetails };
                _actions.Add(_fetch);
            }
            var kind = row.Entry.Kind == FileKind.Video && row.Entry.Type.Length > 0 ? row.Type : row.Kind;
            var episodes = library.Episodes;
            var title = string.Join(" · ", episodes.Select(episode => episode.Name).Where(name => name.Length > 0));
            Heading(title.Length > 0 ? title : row.Title, null, kind, _fetch);
            if (episodes.Count > 0 && library.Series is { } series)
            {
                var context = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
                context.Inlines.Add(FilterLink("series", series.Title, () => library.FilterSeries(series)));
                foreach (var season in episodes.GroupBy(episode => episode.Season))
                {
                    context.Inlines.Add(new Run { Text = Store.Separator });
                    var label = text.Format("library", "season_number", season.Key);
                    context.Inlines.Add(FilterLink("season", label, () => library.FilterSeries(series, season.Key)));
                    context.Inlines.Add(new Run { Text = Store.Separator + text.Format("library",
                        season.Count() > 1 ? "episode_numbers" : "episode_number",
                        string.Join(", ", season.Select(episode => episode.Number))) });
                }
                ToolTipService.SetToolTip(context, series.Title + Store.Separator + string.Join(Store.Separator,
                    episodes.Select(episode => $"S{episode.Season:00}E{episode.Number:00}")));
                group.Detail = context;
            }
            Controls.Section? first;
            Controls.Section? second;
            WrapPanel? cast = null;
            if (row.Entry.Kind == FileKind.Audio)
            {
                first = Section("artist", row.Artist, Lucide.Users, true);
                second = Section("album", row.Album, Lucide.Disc, true);
                AddField(right, "track", row.Track, Lucide.ListMusic);
                AddField(right, "duration", row.Duration, Lucide.Clock);
                if (first is null && second is null && right.Children.Count == 0 && row.Year.Length == 0 && row.Genres.Length == 0)
                    AddField(left, "tags", text.Get("library", "no_tags"), Lucide.Music);
            }
            else
            {
                if (row.Entry.Type.Length == 0 || !library.IsEnrichmentEnabled)
                {
                    var available = library.IsProviderAvailable;
                    var status = text.Get("library", !available ? "source_unavailable" :
                        !library.IsEnrichmentEnabled ? "information_off" : "unidentified");
                    Button? action = !library.IsEnrichmentEnabled && available
                        ? Action(Lucide.Film, text.Get("library", "enable"), library.Enable)
                        : library.IsEnrichmentEnabled ? Action(library.EditMetadataGlyph, library.EditMetadataLabel, library.EditMetadata) : null;
                    var metadata = AddField(left, "metadata", status,
                        available ? Lucide.ScanSearch : Lucide.CloudOff, action);
                    ToolTipService.SetToolTip(metadata!, !available ? text.Get("library", "provider_unavailable") : status);
                }
                first = null;
                second = null;
                if (row.Entry.Type.Length > 0)
                {
                    first = Section("synopsis", library.Synopsis, Lucide.AlignLeft);
                    if (library.Cast.Length > 0)
                    {
                        cast = new WrapPanel { Orientation = Orientation.Vertical, ColumnSpacing = 12, RowSpacing = 4 };
                        foreach (var name in library.Cast.Split(Store.Separator, StringSplitOptions.RemoveEmptyEntries))
                            cast.Children.Add(Value(name));
                        second = new Controls.Section { Header = text.Get("library", "cast"), Glyph = Lucide.Users, Content = cast };
                    }
                    AddField(right, "imdb", row.ImdbTip, Lucide.Star);
                    AddField(right, "tmdb", row.TmdbTip, Lucide.Star);
                }
                if (library.IsOnDemand && library.IsEnrichmentEnabled)
                {
                    _fetchMessage = new Field { Label = text.Get("library", "metadata"),
                        Glyph = Lucide.ScanSearch, Content = Value(library.FetchMessage) };
                    left.Children.Add(_fetchMessage);
                }
            }
            AddField(right, "year", row.Year, Lucide.Calendar);
            AddField(right, "genres", row.Genres, Lucide.Tag);
            fields.Secondary = right.Children.Count > 0 ? right : null;
            var sections = new FieldColumns { Primary = first ?? second, Secondary = first is null ? null : second,
                WidthReference = fields, MinimumWidth = SectionMinimum, Spacing = 32, StackSpacing = 20 };
            if (row.Entry.Kind == FileKind.Video && cast is not null)
            {
                _sections = sections;
                _cast = cast;
                sections.WidthReference = null;
                _viewport.SizeChanged += OnViewportSizeChanged;
            }
            else
                fields.SizeChanged += (_, args) =>
                {
                    if (args.PreviousSize.Width != args.NewSize.Width)
                        sections.InvalidateMeasure();
                };
            left.Children.Add(sections);
        }
        group.Content = fields;
        RefreshFetch(library);

        TextBlock Value(string value)
        {
            var block = new TextBlock { Text = value,
                Style = (Style)Application.Current.Resources["InspectorValueStyle"],
                TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTipService.SetToolTip(block, value);
            return block;
        }

        Field? AddField(Panel parent, string key, string value, string glyph, Button? action = null)
        {
            if (value.Length == 0)
                return null;
            var block = key == "year" ? LinkedValue(key, value) : Value(value);
            if (key is "name" or "folder")
                block.FlowDirection = FlowDirection.LeftToRight;
            var field = new Field
            {
                Label = text.Get("library", key), Glyph = glyph, Action = action,
                Content = block,
            };
            if (key == "genres")
            {
                var genres = new WrapPanel { ColumnSpacing = 12, RowSpacing = 4 };
                foreach (var genre in value.Split(Store.Separator, StringSplitOptions.RemoveEmptyEntries))
                {
                    var link = LinkedValue(key, genre);
                    link.TextWrapping = TextWrapping.Wrap;
                    link.TextTrimming = TextTrimming.None;
                    genres.Children.Add(link);
                }
                field.Content = genres;
                field.VerticalContentAlignment = VerticalAlignment.Top;
            }
            parent.Children.Add(field);
            return field;
        }

        TextBlock LinkedValue(string key, string value)
        {
            var block = Value(value);
            block.Inlines.Clear();
            block.IsTextSelectionEnabled = false;
            block.Inlines.Add(FilterLink(key, value));
            return block;
        }

        Controls.Section? Section(string key, string value, string glyph, bool filter = false)
        {
            if (value.Length == 0)
                return null;
            var block = filter ? LinkedValue(key, value) : Value(value);
            block.Style = (Style)Application.Current.Resources["TinyTorrentBodyTextStyle"];
            block.TextWrapping = TextWrapping.Wrap;
            block.TextTrimming = TextTrimming.None;
            block.IsTextSelectionEnabled = !filter;
            return new Controls.Section { Header = text.Get("library", key), Glyph = glyph, Content = block };
        }

        Button Action(string glyph, string label, ICommand command)
        {
            var button = new Button { Command = command,
                Style = (Style)Application.Current.Resources["TinyTorrentSubtleButtonStyle"],
                Content = new FontIcon { FontFamily = Lucide.Font, Glyph = glyph,
                    Style = (Style)Application.Current.Resources["TinyTorrentStatusIconStyle"] } };
            AutomationProperties.SetName(button, label);
            ToolTipService.SetToolTip(button, label);
            _actions.Add(button);
            return button;
        }

        Hyperlink FilterLink(string section, string value, System.Action? apply = null)
        {
            var link = new Hyperlink();
            link.Inlines.Add(new Run { Text = value });
            link.Click += (_, _) =>
            {
                if (apply is null)
                    library.Filter(section, value);
                else
                    apply();
            };
            var tip = text.Format("library", "filter_tip", text.Get("library", section), value);
            ToolTipService.SetToolTip(link, tip);
            AutomationProperties.SetName(link, tip);
            return link;
        }

        void Heading(string title, string? glyph, string? context = null, Button? action = null)
        {
            group.Header = title;
            group.Glyph = glyph ?? string.Empty;
            group.Action = action;
            if (glyph is null)
                group.HeaderStyle = (Style)Application.Current.Resources["TinyTorrentSurfaceTitleTextStyle"];
            if (!string.IsNullOrEmpty(context))
            {
                group.HeaderDetail = new TextBlock { Text = context,
                    Style = (Style)Application.Current.Resources["TinyTorrentTitleDetailTextStyle"] };
            }
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_sections is null || _cast is null)
            return base.MeasureOverride(availableSize);

        var hasWidth = availableSize.Width >= SectionMinimum;
        var scale = _uiSettings.TextScaleFactor;
        Size size;
        if (hasWidth && (_columnWidth != availableSize.Width || _columnScale != scale))
        {
            SetCastColumns(true);
            size = base.MeasureOverride(availableSize);
            // Cache by width and text scale so a stacked fallback does not
            // remeasure both arrangements on every layout pass.
            _columnWidth = availableSize.Width;
            _columnScale = scale;
            _columnHeight = _sections.DesiredSize.Height;
        }
        else
            size = base.MeasureOverride(availableSize);
        var group = (Controls.Section)Content;
        var fields = (FieldColumns)group.Content;
        var left = (StackPanel)fields.Primary!;
        var overhead = group.DesiredSize.Height - fields.DesiredSize.Height;
        foreach (var child in left.Children)
        {
            if (ReferenceEquals(child, _sections))
                break;
            if (child.Visibility != Visibility.Collapsed)
                overhead += child.DesiredSize.Height + left.Spacing;
        }
        var columns = hasWidth && _columnHeight <= Math.Max(0, _viewport.ActualHeight - overhead);
        if (SetCastColumns(columns))
            size = base.MeasureOverride(availableSize);
        return size;
    }

    private bool SetCastColumns(bool columns)
    {
        if (_sections is null || _cast is null)
            return false;
        var minimum = columns ? 0 : double.PositiveInfinity;
        if (_sections.MinimumWidth == minimum)
            return false;
        _sections.MinimumWidth = minimum;
        _cast.Orientation = columns ? Orientation.Vertical : Orientation.Horizontal;
        _cast.RowSpacing = columns ? 4 : 12;
        _cast.InvalidateMeasure();
        _sections.InvalidateMeasure();
        return true;
    }

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (args.PreviousSize.Height != args.NewSize.Height)
            InvalidateMeasure();
    }

    internal void RefreshFetch(Browser library)
    {
        if (_fetch is not null)
        {
            _fetch.Text = library.FetchLabel;
            ToolTipService.SetToolTip(_fetch, library.FetchTip);
            AutomationProperties.SetName(_fetch, library.FetchTip);
        }
        if (_fetchMessage?.Content is TextBlock message)
        {
            message.Text = library.FetchMessage;
            ToolTipService.SetToolTip(message, message.Text);
            _fetchMessage.Visibility = message.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    internal void RefreshCommands()
    {
        foreach (var action in _actions)
            if (action.Command is RelayCommand command)
                command.Refresh();
    }

    public void Dispose()
    {
        _viewport.SizeChanged -= OnViewportSizeChanged;
        foreach (var action in _actions)
            action.Command = null;
        _actions.Clear();
    }
}
