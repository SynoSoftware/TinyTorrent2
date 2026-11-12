using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Shared;

namespace Syno.TinyTorrent.Shell;

// The dialogs that every downloaded dataset shares, worded by the dataset's
// text section.
public sealed partial class MainWindow
{
    // Settings opens this dialog when the person chooses a provider whose
    // data is not downloaded, and when the person updates installed data,
    // which states the data's age. The download that start begins continues
    // after the dialog closes, and Settings shows its progress.
    private async Task ConfirmDownload(IDataset dataset, Func<Task> start)
    {
        var installed = dataset.Installed;
        var current = dataset.IsCurrent;
        var dialog = new Dialog
        {
            Glyph = Segoe.Database,
            PrimaryGlyph = Segoe.Download,
            IsPrimaryButtonEnabled = dataset.CanDownload,
        };
        void Follow(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName is nameof(IDataset.CanDownload) or "")
            {
                dialog.IsPrimaryButtonEnabled = dataset.CanDownload;
            }
        }
        dataset.PropertyChanged += Follow;
        dialog.PrimaryButtonClick += (sender, args) =>
        {
            args.Cancel = !dataset.CanDownload;
            if (!args.Cancel)
            {
                _ = start();
            }
        };
        try
        {
            await Confirm(
                dialog,
                () =>
                {
                    var section = dataset.Section;
                    if (installed is null)
                    {
                        dialog.Title = Model.Text.Get(section, "download_title");
                        dialog.Content = DataConfirmation(
                            [
                                Model.Text.Get(section, "download_body"),
                                Model.Text.Get(section, "download_local"),
                            ],
                            [Model.Text.Get(section, "preparation_time")]
                        );
                        dialog.PrimaryButtonText = Model.Text.Get(section, "download");
                        dialog.PrimaryToolTip = Model.Text.Get(section, "download_tip");
                        return;
                    }
                    // Current data warns, as the dataset's IsCurrent explains;
                    // data less than a week old notes that little has changed.
                    var days = DatasetRow.Age(installed);
                    dialog.Title = Model.Text.Get(section, "update_title");
                    var body =
                        current ? Model.Text.Get(section, "update_current")
                        : days < 7 ? Model.Text.FormatCount(section, "update_recent", days)
                        : Model.Text.Format(section, "update_age", days);
                    dialog.Content = DataConfirmation(
                        [body, Model.Text.Get(section, "update_frequency")],
                        [Model.Text.Get(section, "preparation_time")]
                    );
                    dialog.PrimaryButtonText = Model.Text.Get(section, "update");
                    dialog.PrimaryToolTip = Model.Text.Get(section, "update_tip");
                }
            );
        }
        finally
        {
            dataset.PropertyChanged -= Follow;
        }
    }

    private async Task ConfirmDataDeletion(IDataset dataset)
    {
        if (dataset.Installed is not { } data)
            return;
        var dialog = new Dialog
        {
            Glyph = Segoe.Database,
            PrimaryGlyph = Segoe.Trash2,
        };
        await Confirm(
            dialog,
            () =>
            {
                var section = dataset.Section;
                dialog.Title = Model.Text.Get(section, "delete_title");
                dialog.Content = DataConfirmation(
                    [
                        Model.Text.Format(
                            section,
                            "delete_body",
                            Model.Text.Bytes(data.Length)
                        ),
                        Model.Text.Get(section, "delete_kept"),
                    ],
                    [
                        Model.Text.Get(section, "restore_time"),
                        Model.Text.Get(section, "preparation_time"),
                    ]
                );
                dialog.PrimaryButtonText = Model.Text.Get("file_action", "delete");
                dialog.PrimaryToolTip = Model.Text.Get(section, "delete_tip");
            },
            dataset.Delete
        );
    }

    private StackPanel DataConfirmation(IEnumerable<string> facts, IEnumerable<string> caution)
    {
        var icon = new FontIcon
        {
            FontFamily = Segoe.Font,
            Glyph = Segoe.TriangleAlert,
            Style = (Style)Root.Resources["DataCautionIconStyle"],
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(
            icon,
            Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw
        );
        var message = Lines(caution);
        Grid.SetColumn(message, 1);
        return new StackPanel
        {
            Spacing = 12,
            Children =
            {
                Lines(facts),
                new Grid
                {
                    ColumnSpacing = 8,
                    ColumnDefinitions =
                    {
                        new ColumnDefinition { Width = GridLength.Auto },
                        new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    },
                    Children = { icon, message },
                },
            },
        };
    }
}
