using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Syno.TinyTorrent.Controls;

namespace Syno.TinyTorrent.Shared;

/// <summary>
/// The Settings row of one <see cref="IDataset"/>: its count, size and age,
/// the running download with its Cancel, and Delete beside Download or
/// Update. Update and Delete ask first, so the row raises them for the
/// window's dialogs; Download of missing data starts at once.
/// </summary>
public sealed partial class DatasetRow : UserControl
{
    private readonly SettingsRow _row = new();
    private readonly WorkStatus _work = new()
    {
        MaxWidth = 380,
        IsExpanded = true,
        Visibility = Visibility.Collapsed,
    };
    private readonly ActionButton _delete = new()
    {
        Glyph = Lucide.Trash2,
        Visibility = Visibility.Collapsed,
    };
    private readonly ActionButton _download = new() { Glyph = Lucide.Download };
    private IDataset? _dataset;
    private Strings? _text;
    private FileInfo? _installed;
    private Task<long>? _count;

    internal event EventHandler<IDataset>? DownloadRequested;
    internal event EventHandler<IDataset>? UpdateRequested;
    internal event EventHandler<IDataset>? DeleteRequested;

    public DatasetRow()
    {
        // Tab reaches the buttons, not the row, as with every settings row.
        IsTabStop = false;
        Visibility = Visibility.Collapsed;
        // Download, not Delete, takes the place that Cancel leaves when a
        // download ends, so a late click on Cancel cannot delete the data.
        _row.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { _work, _delete, _download },
        };
        Content = _row;
        _download.Click += (_, _) =>
        {
            if (_dataset is { } dataset)
                (_installed is null ? DownloadRequested : UpdateRequested)?.Invoke(this, dataset);
        };
        _delete.Click += (_, _) =>
        {
            if (_dataset is { } dataset)
                DeleteRequested?.Invoke(this, dataset);
        };
        _work.FocusInvalidated += (_, _) => _row.Focus(FocusState.Programmatic);
    }

    /// <summary>Shows <paramref name="dataset"/> and follows its changes; null hides the row.</summary>
    internal void Show(IDataset? dataset, Strings text)
    {
        if (!ReferenceEquals(_dataset, dataset))
        {
            if (_dataset is not null)
                _dataset.PropertyChanged -= OnDataset;
            _dataset = dataset;
            if (dataset is not null)
                dataset.PropertyChanged += OnDataset;
        }
        _text = text;
        Refresh();
    }

    internal void RefreshText() => Refresh();

    private void OnDataset(object? sender, PropertyChangedEventArgs args)
    {
        if (
            args.PropertyName
            is nameof(IDataset.Installed)
                or nameof(IDataset.DataWork)
                or nameof(IDataset.CanDownload)
                or ""
        )
            Refresh();
    }

    private void Refresh()
    {
        if (_dataset is not { } dataset || _text is not { } text)
        {
            Visibility = Visibility.Collapsed;
            return;
        }
        Visibility = Visibility.Visible;
        var section = dataset.Section;
        _installed = dataset.Installed;
        var count = _installed is null ? null : dataset.Count;
        if (!ReferenceEquals(count, _count))
        {
            _count = count;
            if (count is { IsCompleted: false })
                ShowCount(count);
        }
        var progress = dataset.DataWork;
        var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as Control;
        var focus = focused?.FocusState ?? FocusState.Unfocused;
        var workFocused = _work.HasFocus;
        _row.Header = text.Get(section, "data");
        _row.Description = _installed is { } installed
            ? Describe(section, installed, text)
            : text.Get(section, "data_hint");
        _download.Text = text.Get(section, _installed is null ? "download" : "update");
        ToolTipService.SetToolTip(
            _download,
            text.Get(section, _installed is null ? "download_tip" : "update_tip")
        );
        AutomationProperties.SetAutomationId(_download, Name + "Download");
        _download.IsEnabled = dataset.CanDownload;
        _download.Visibility = progress is null ? Visibility.Visible : Visibility.Collapsed;
        _delete.Text = text.Get("file_action", "delete");
        ToolTipService.SetToolTip(_delete, text.Get(section, "delete_tip"));
        AutomationProperties.SetName(_delete, text.Get(section, "delete"));
        AutomationProperties.SetAutomationId(_delete, Name + "Delete");
        _delete.Visibility =
            progress is null && _installed is not null ? Visibility.Visible : Visibility.Collapsed;
        _work.Show(progress, text);
        if (
            focused is not null
            && (
                workFocused
                || ReferenceEquals(focused, _download)
                || ReferenceEquals(focused, _delete)
            )
            && (
                focused.Visibility == Visibility.Collapsed
                || !focused.IsEnabled
                || workFocused && !_work.CanFocus
            )
        )
        {
            if (progress is null && _download.IsEnabled)
                _download.Focus(focus);
            else if (!_work.FocusCancel(focus))
                _row.Focus(FocusState.Programmatic);
        }
    }

    // The age is in whole days, as the update dialog states it.
    private string Describe(string section, FileInfo installed, Strings text)
    {
        var size = text.Bytes(installed.Length);
        var days = (int)(DateTime.UtcNow - installed.LastWriteTimeUtc).TotalDays;
        var age = days == 0 ? text.Get(section, "age_new") : text.FormatCount(section, "age", days);
        return _count is { IsCompletedSuccessfully: true } count
            ? text.Format(
                section,
                "data_counted",
                text.FormatCount(section, "count", count.Result),
                size,
                age
            )
            : text.Format(section, "data_summary", size, age);
    }

    // Counting reads the whole data, so the row adds the count when it
    // arrives. Data that cannot be read keeps its size and age.
    private async void ShowCount(Task<long> count)
    {
        try
        {
            await count;
        }
        catch (Exception)
        {
            return;
        }
        if (ReferenceEquals(count, _count))
            Refresh();
    }
}
