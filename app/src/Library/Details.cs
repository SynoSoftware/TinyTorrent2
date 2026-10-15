using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Models;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace Syno.TinyTorrent.Library;

internal sealed partial class Details : UserControl, IDisposable
{
    private const double SectionMinimum = 1040;
    private readonly List<Button> _actions = [];
    private readonly Field? _fetchMessage;
    private readonly ScrollViewer _viewport;
    private readonly FieldColumns? _sections;
    private readonly WrapPanel? _cast;
    private readonly UISettings _uiSettings = new();
    private double _columnWidth = double.NaN;
    private double _columnScale = double.NaN;
    private double _columnHeight;
    private readonly List<System.Action> _refresh = [];

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
            AddField(
                left,
                "folder",
                row.Folder,
                Lucide.Folder,
                Action(Lucide.FolderOpen, text.Get("commands", "open_folder"), library.OpenFolder)
            );
            var origins = new StackPanel();
            left.Children.Add(origins);
            IReadOnlyList<Origin>? shownOrigins = null;
            _refresh.Add(() =>
            {
                if (shownOrigins is not null && shownOrigins.SequenceEqual(library.Origins))
                    return;
                foreach (var field in origins.Children.OfType<Field>())
                    if (field.Action is Button button)
                    {
                        button.Command = null;
                        _actions.Remove(button);
                    }
                origins.Children.Clear();
                shownOrigins = library.Origins.ToArray();
                foreach (var origin in shownOrigins)
                {
                    var show = Action(
                        Lucide.LocateFixed,
                        text.Get("library", "show_torrents"),
                        new RelayCommand(
                            () =>
                            {
                                library.ShowTorrent(origin.OriginId);
                                return Task.CompletedTask;
                            },
                            () => library.CanShowTorrent
                        )
                    );
                    origins.Children.Add(
                        new Field
                        {
                            Label = text.Get("menus", "torrent"),
                            Glyph = Lucide.LocateFixed,
                            Content = Value(origin.Name),
                            Action = show,
                        }
                    );
                }
            });
            AddField(right, "size", row.Size, Lucide.HardDrive);
            var fileType = new Field
            {
                Label = text.Get("library", "file_type"),
                Glyph = row.Glyph,
                Content = Value(string.Empty),
            };
            right.Children.Add(fileType);
            var associated = new Grid { ColumnSpacing = 8 };
            associated.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            associated.ColumnDefinitions.Add(new ColumnDefinition());
            var icon = new Image { Width = 20, Height = 20 };
            associated.Children.Add(icon);
            var name = Value(string.Empty);
            Grid.SetColumn(name, 1);
            associated.Children.Add(name);
            var application = new Field
            {
                Label = text.Get("library", "opens_with"),
                Glyph = Lucide.Play,
                Content = associated,
            };
            right.Children.Add(application);
            _refresh.Add(() =>
            {
                SetValue(fileType, library.FileType);
                name.Text =
                    library.Application.Length > 0 ? library.Application
                    : library.FileType.Length > 0 ? text.Get("library", "no_application")
                    : string.Empty;
                ToolTipService.SetToolTip(name, name.Text);
                application.Visibility =
                    name.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                icon.Source = library.ApplicationIcon;
                icon.Visibility = icon.Source is null ? Visibility.Collapsed : Visibility.Visible;
            });
            AddField(right, "created", row.Created, Lucide.Calendar);
            AddField(right, "modified", row.Modified, Lucide.Clock);
        }
        else
        {
            left.Spacing = 20;
            fields = new FieldColumns
            {
                Primary = left,
                Secondary = right,
                Spacing = 32,
                StackSpacing = 20,
                SecondaryWidth = new GridLength(384),
                SeparateSecondary = true,
            };
            var kind = row.Entry.Kind == FileKind.Video ? row.Type : row.Kind;
            IReadOnlyList<Episode>? shownEpisodes = null;
            VideoChoice? shownSeries = null;
            _refresh.Add(() =>
            {
                if (
                    shownEpisodes is not null
                    && shownEpisodes.SequenceEqual(library.Episodes)
                    && shownSeries == library.Series
                )
                    return;
                shownEpisodes = library.Episodes.ToArray();
                shownSeries = library.Series;
                var episodes = library.Episodes;
                var title = string.Join(
                    " · ",
                    episodes.Select(episode => episode.Name).Where(name => name.Length > 0)
                );
                Heading(title.Length > 0 ? title : row.Title, null, kind);
                group.Detail = null;
                if (episodes.Count > 0 && library.Series is { } series)
                {
                    var context = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
                    context.Inlines.Add(
                        FilterLink("series", series.Title, () => library.FilterSeries(series))
                    );
                    foreach (var season in episodes.GroupBy(episode => episode.Season))
                    {
                        context.Inlines.Add(new Run { Text = Store.Separator });
                        var label = text.Format("library", "season_number", season.Key);
                        context.Inlines.Add(
                            FilterLink(
                                "season",
                                label,
                                () => library.FilterSeries(series, season.Key)
                            )
                        );
                        context.Inlines.Add(
                            new Run
                            {
                                Text =
                                    Store.Separator
                                    + text.Format(
                                        "library",
                                        season.Count() > 1 ? "episode_numbers" : "episode_number",
                                        string.Join(", ", season.Select(episode => episode.Number))
                                    ),
                            }
                        );
                    }
                    ToolTipService.SetToolTip(
                        context,
                        series.Title
                            + Store.Separator
                            + string.Join(
                                Store.Separator,
                                episodes.Select(episode =>
                                    $"S{episode.Season:00}E{episode.Number:00}"
                                )
                            )
                    );
                    group.Detail = context;
                }
            });
            Controls.Section? first;
            Controls.Section? second;
            WrapPanel? cast = null;
            if (row.Entry.Kind == FileKind.Audio)
            {
                first = Section("artist", row.Artist, Lucide.Users, true);
                second = Section("album", row.Album, Lucide.Disc, true);
                AddField(right, "track", row.Track, Lucide.ListMusic);
                AddField(right, "duration", row.Duration, Lucide.Clock);
                if (
                    first is null
                    && second is null
                    && right.Children.Count == 0
                    && row.Year.Length == 0
                    && row.Genres.Length == 0
                )
                    AddField(left, "tags", text.Get("library", "no_tags"), Lucide.Music);
            }
            else
            {
                if (!row.Entry.HasMetadata)
                {
                    // The action sits at the heading's far right, not at the
                    // edge of the left column, where it would float mid-pane.
                    group.Action = MetadataActions.Button(library);
                    var metadata = new Field
                    {
                        Label = text.Get("library", "metadata"),
                        Content = Value(string.Empty),
                    };
                    left.Children.Add(metadata);
                    _refresh.Add(() =>
                    {
                        var message = library.AutomaticWait ?? text.Get("library", "no_metadata");
                        SetValue(metadata, message);
                        metadata.Glyph = Lucide.ScanSearch;
                        ToolTipService.SetToolTip(metadata, message);
                    });
                }
                first = null;
                second = null;
                AddRatings();
                if (row.Entry.HasMetadata)
                {
                    first = new Controls.Section
                    {
                        Header = text.Get("library", "synopsis"),
                        Glyph = Lucide.AlignLeft,
                        Content = new TextBlock
                        {
                            Style = (Style)
                                Application.Current.Resources["TinyTorrentBodyTextStyle"],
                            TextWrapping = TextWrapping.Wrap,
                            IsTextSelectionEnabled = true,
                        },
                    };
                    cast = new WrapPanel
                    {
                        Orientation = Orientation.Vertical,
                        ColumnSpacing = 12,
                        RowSpacing = 4,
                    };
                    second = new Controls.Section
                    {
                        Header = text.Get("library", "cast"),
                        Glyph = Lucide.Users,
                        Content = cast,
                    };
                    var synopsis = first!;
                    var castSection = second;
                    var castNames = cast;
                    var previousCast = string.Empty;
                    _refresh.Add(() =>
                    {
                        var description = (TextBlock)synopsis.Content;
                        if (description.Text != library.Synopsis)
                            description.Text = library.Synopsis;
                        synopsis.Visibility =
                            library.Synopsis.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                        castSection.Visibility =
                            library.Cast.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                        if (previousCast == library.Cast)
                            return;
                        previousCast = library.Cast;
                        castNames.Children.Clear();
                        foreach (
                            var name in library.Cast.Split(
                                Store.Separator,
                                StringSplitOptions.RemoveEmptyEntries
                            )
                        )
                            castNames.Children.Add(Listed(Value(name)));
                    });
                    var boxOffice = AddFact("box_office", Lucide.Ticket);
                    var certification = AddFact("certification", Lucide.ShieldCheck);
                    var directors = AddFact("directors_one", Lucide.Clapperboard);
                    _refresh.Add(() =>
                    {
                        SetValue(boxOffice, library.BoxOffice);
                        SetValue(certification, library.Certification);
                        SetValue(directors, library.Directors);
                        directors.Label = text.FormatCount(
                            "library",
                            "directors",
                            library
                                .Directors.Split(
                                    Store.Separator,
                                    StringSplitOptions.RemoveEmptyEntries
                                )
                                .Length
                        );
                        // The facts wait hidden for the detail read, so the
                        // column shows only once one of its fields has a value.
                        var secondary = right.Children.Any(child =>
                            child.Visibility == Visibility.Visible
                        )
                            ? right
                            : null;
                        if (!ReferenceEquals(fields.Secondary, secondary))
                            fields.Secondary = secondary;
                    });
                }
                _fetchMessage = new Field
                {
                    Label = text.Get("library", "metadata"),
                    Glyph = Lucide.ScanSearch,
                    Content = Value(library.FetchMessage),
                };
                left.Children.Add(_fetchMessage);
            }
            AddField(right, "year", row.Year, Lucide.Calendar);
            AddField(right, "genres", row.Genres, Lucide.Tag);
            fields.Secondary = right.Children.Count > 0 ? right : null;
            var sections = new FieldColumns
            {
                Primary = first ?? second,
                Secondary = first is null ? null : second,
                WidthReference = fields,
                MinimumWidth = SectionMinimum,
                Spacing = 32,
                StackSpacing = 20,
            };
            if (row.Entry.Kind == FileKind.Video && cast is not null)
            {
                _sections = sections;
                _cast = cast;
                sections.WidthReference = null;
                _viewport.SizeChanged += OnViewportSizeChanged;
                _refresh.Add(() =>
                {
                    var primary = first?.Visibility == Visibility.Visible ? first : null;
                    var secondary = second?.Visibility == Visibility.Visible ? second : null;
                    if (
                        !ReferenceEquals(sections.Primary, primary ?? secondary)
                        || !ReferenceEquals(sections.Secondary, primary is null ? null : secondary)
                    )
                    {
                        sections.Primary = sections.Secondary = null;
                        sections.Primary = primary ?? secondary;
                        sections.Secondary = primary is null ? null : secondary;
                    }
                });
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
        Refresh(library);

        TextBlock Value(string value)
        {
            var block = new TextBlock
            {
                Text = value,
                Style = (Style)Application.Current.Resources["InspectorValueStyle"],
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
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
                Label = text.Get("library", key),
                Glyph = glyph,
                Action = action,
                Content = block,
            };
            if (key == "genres")
            {
                var genres = new WrapPanel { ColumnSpacing = 12, RowSpacing = 4 };
                foreach (
                    var genre in value.Split(Store.Separator, StringSplitOptions.RemoveEmptyEntries)
                )
                {
                    var link = Listed(LinkedValue(key, genre));
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

        // Each site's score sits on its own scale, so the sites stay separate
        // values in one field rather than being combined.
        void AddRatings()
        {
            var ratings = new WrapPanel { ColumnSpacing = 12, RowSpacing = 4 };
            foreach (var source in RatingSource.All)
            {
                var rating = row.RatingText(source);
                if (rating.Length == 0)
                    continue;
                var block = Listed(
                    Value(
                        text.Format(
                            "library",
                            "site_rating",
                            text.Get("library", source.Id),
                            rating
                        )
                    )
                );
                ToolTipService.SetToolTip(block, row.RatingTip(source));
                ratings.Children.Add(block);
            }
            if (ratings.Children.Count > 0)
                right.Children.Add(
                    new Field
                    {
                        Label = text.Get("library", "ratings"),
                        Glyph = Lucide.Star,
                        Content = ratings,
                        VerticalContentAlignment = VerticalAlignment.Top,
                    }
                );
        }

        // The Inspector reads these facts after it opens, so each field waits
        // collapsed until the refresh gives it a value.
        Field AddFact(string key, string glyph)
        {
            var field = new Field
            {
                Label = text.Get("library", key),
                Glyph = glyph,
                Content = Value(string.Empty),
                Visibility = Visibility.Collapsed,
            };
            right.Children.Add(field);
            return field;
        }

        // Listed values keep the full line box, as a confirmation's fact lines
        // do, so the list's four-pixel gap separates whole lines. The tight
        // bounds of a single value leave stacked lines almost touching.
        TextBlock Listed(TextBlock block)
        {
            block.TextLineBounds = TextLineBounds.Full;
            return block;
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
            return new Controls.Section
            {
                Header = text.Get("library", key),
                Glyph = glyph,
                Content = block,
            };
        }

        Button Action(string glyph, string label, ICommand command)
        {
            var button = new Button
            {
                Command = command,
                Style = (Style)Application.Current.Resources["TinyTorrentSubtleButtonStyle"],
                Content = new FontIcon
                {
                    FontFamily = Lucide.Font,
                    Glyph = glyph,
                    FontSize = (double)Application.Current.Resources["TinyTorrentButtonIconSize"],
                },
            };
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

        void Heading(string title, string? glyph, string? context = null)
        {
            group.Header = title;
            group.Glyph = glyph ?? string.Empty;
            if (glyph is null)
                group.HeaderStyle = (Style)
                    Application.Current.Resources["TinyTorrentSurfaceTitleTextStyle"];
            if (!string.IsNullOrEmpty(context))
            {
                group.HeaderDetail = new TextBlock
                {
                    Text = context,
                    Style = (Style)Application.Current.Resources["TinyTorrentTitleDetailTextStyle"],
                };
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
        _cast.InvalidateMeasure();
        _sections.InvalidateMeasure();
        return true;
    }

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (args.PreviousSize.Height != args.NewSize.Height)
            InvalidateMeasure();
    }

    private static void SetValue(Field field, string value)
    {
        ((TextBlock)field.Content).Text = value;
        ToolTipService.SetToolTip((TextBlock)field.Content, value);
        field.Visibility = value.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void RefreshFetch(Browser library)
    {
        if (_fetchMessage is { } field)
            SetValue(field, library.FetchMessage);
    }

    internal void Refresh(Browser library)
    {
        foreach (var refresh in _refresh)
            refresh();
        _columnWidth = double.NaN;
        RefreshFetch(library);
        RefreshCommands();
        InvalidateMeasure();
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
        _refresh.Clear();
    }
}
