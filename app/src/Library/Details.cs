using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using System.Windows.Input;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Models;

namespace Syno.TinyTorrent.Library;

internal sealed partial class Details : UserControl, IDisposable
{
    private readonly List<Button> _actions = [];

    internal Details(Browser library, bool showsFile)
    {
        var content = new StackPanel { Spacing = 16, Margin = new Thickness(0, 4, 0, 0) };
        Content = content;
        if (library.Selected is not { } row)
            return;
        var text = library.Text;
        var group = new StackPanel { Spacing = 6 };
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
                var show = Action(Lucide.Download, text.Get("library", "show_torrents"), new RelayCommand(() =>
                {
                    library.ShowTorrent(origin.OriginId);
                    return Task.CompletedTask;
                }, () => library.IsAvailable));
                left.Children.Add(new Field { Label = text.Get("menus", "torrent"), Glyph = Lucide.Download,
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
            var statistics = new Strip { ColumnSpacing = 32, RowSpacing = 12 };
            if (row.Entry.Kind == FileKind.Audio)
            {
                AddField(statistics, "track", row.Track, Lucide.ListMusic);
                AddField(statistics, "duration", row.Duration, Lucide.Clock);
                AddField(statistics, "year", row.Year, Lucide.Calendar);
                Statistics();
                Heading(row.Title, Lucide.Music);
                AddField(left, "artist", row.Artist, Lucide.Users);
                AddField(left, "album", row.Album, Lucide.Disc);
                AddField(right, "genres", row.Genres, Lucide.Tag);
                if (left.Children.Count == 0 && right.Children.Count == 0 && statistics.Children.Count == 0)
                    AddField(left, "tags", text.Get("library", "no_tags"), Lucide.Music);
            }
            else
            {
                if (row.Entry.Type.Length > 0)
                {
                    AddField(statistics, "type", row.Type, row.Glyph);
                    AddField(statistics, "year", row.Year, Lucide.Calendar);
                    Statistics();
                }
                Heading(row.Title, row.Glyph);
                if (row.Entry.Type.Length == 0 || !library.IsEnrichmentEnabled)
                {
                    var available = library.Provider?.IsAvailable == true;
                    var status = text.Get("library", !available ? "source_unavailable" :
                        !library.IsEnrichmentEnabled ? "information_off" : "unidentified");
                    Button? action = !library.IsEnrichmentEnabled && library.CanEnable
                        ? Action(Lucide.Film, text.Get("library", "enable"), library.Enable)
                        : library.IsEnrichmentEnabled ? Action(library.IdentifyGlyph, library.IdentifyLabel, library.Identify) : null;
                    var identification = AddField(left, "identification", status,
                        available ? Lucide.ScanSearch : Lucide.CloudOff, action);
                    ToolTipService.SetToolTip(identification!, !available ? text.Get("library", "provider_unavailable") : status);
                }
                if (row.Entry.Type.Length > 0)
                {
                    AddField(left, "genres", row.Genres, Lucide.Tag);
                    AddField(left, "cast", library.Cast, Lucide.Users);
                    AddField(left, "synopsis", library.Synopsis, Lucide.AlignLeft);
                }
                if (library.IsOnDemand && library.IsEnrichmentEnabled)
                {
                    var fetch = new ActionButton { Text = library.FetchLabel, Glyph = Lucide.RefreshCw,
                        Command = library.FetchDetails, HorizontalAlignment = HorizontalAlignment.Left };
                    _actions.Add(fetch);
                    ToolTipService.SetToolTip(fetch, library.FetchTip);
                    left.Children.Add(fetch);
                    if (library.FetchMessage.Length > 0)
                        AddField(left, "identification", library.FetchMessage, Lucide.ScanSearch);
                }
            }

            void Statistics()
            {
                if (statistics.Children.Count == 0)
                    return;
                content.Children.Add(statistics);
                content.Children.Add(new Border { Style = (Style)Application.Current.Resources["InspectorDividerStyle"] });
            }
        }
        group.Children.Add(fields);
        content.Children.Add(group);

        TextBlock Value(string value, bool statistic = false)
        {
            var block = new TextBlock { Text = value,
                Style = (Style)Application.Current.Resources[statistic ? "InspectorStatisticValueStyle" : "InspectorValueStyle"],
                TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTipService.SetToolTip(block, value);
            return block;
        }

        Field? AddField(Panel parent, string key, string value, string glyph, Button? action = null)
        {
            if (value.Length == 0)
                return null;
            var block = Value(value, parent is Strip);
            if (key is "name" or "folder")
                block.FlowDirection = FlowDirection.LeftToRight;
            if (key == "synopsis")
            {
                block.TextWrapping = TextWrapping.Wrap;
                block.TextTrimming = TextTrimming.None;
            }
            var field = new Field
            {
                Label = text.Get("library", key), Glyph = glyph, Action = action,
                Content = block,
            };
            if (parent is Strip)
                field.Style = (Style)Application.Current.Resources["InspectorStatisticStyle"];
            parent.Children.Add(field);
            return field;
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

        void Heading(string title, string glyph)
        {
            var label = new TextBlock { Text = title,
                Style = (Style)Application.Current.Resources["TinyTorrentGroupTitleTextStyle"] };
            AutomationProperties.SetHeadingLevel(label, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
            ToolTipService.SetToolTip(label, title);
            var heading = new Grid { ColumnSpacing = 8 };
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            heading.ColumnDefinitions.Add(new ColumnDefinition());
            heading.Children.Add(new FontIcon { FontFamily = Lucide.Font, Glyph = glyph,
                Style = (Style)Application.Current.Resources["TinyTorrentGroupIconStyle"] });
            Grid.SetColumn(label, 1);
            heading.Children.Add(label);
            group.Children.Add(heading);
        }
    }

    public void Dispose()
    {
        foreach (var action in _actions)
            action.Command = null;
        _actions.Clear();
    }
}
