using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Syno.TinyTorrent.Controls;

namespace Syno.TinyTorrent.Shared;

/// <summary>
/// The Settings row of one dataset's <see cref="DataDownload"/>: its count,
/// size and age, the running download with its Cancel, and Delete beside
/// Download or Update. Update and Delete ask first, so the row raises them
/// for the window's dialogs; Download of missing data starts at once.
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
        Glyph = Segoe.Trash2,
        Visibility = Visibility.Collapsed,
    };

    // Download, or Update once the data is installed.
    private readonly ActionButton _start = new() { Glyph = Segoe.Download };
    private DataDownload? _download;
    private Strings? _text;
    private FileInfo? _installed;
    private Task<long>? _count;

    public UIElement? Icon
    {
        get => _row.Icon;
        set => _row.Icon = value;
    }

    internal event EventHandler<DataDownload>? DownloadRequested;
    internal event EventHandler<DataDownload>? UpdateRequested;
    internal event EventHandler<DataDownload>? DeleteRequested;

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
            Children = { _work, _delete, _start },
        };
        Content = _row;
        _start.Click += (_, _) =>
        {
            if (_download is { } download)
                (_installed is null ? DownloadRequested : UpdateRequested)?.Invoke(this, download);
        };
        _delete.Click += (_, _) =>
        {
            if (_download is { } download)
                DeleteRequested?.Invoke(this, download);
        };
        _work.FocusInvalidated += (_, _) => _row.Focus(FocusState.Programmatic);
    }

    /// <summary>Shows <paramref name="download"/> and follows its changes; null hides the row.</summary>
    internal void Show(DataDownload? download, Strings text)
    {
        if (!ReferenceEquals(_download, download))
        {
            if (_download is not null)
                _download.PropertyChanged -= OnDownload;
            _download = download;
            if (download is not null)
                download.PropertyChanged += OnDownload;
        }
        _text = text;
        Refresh();
    }

    internal void RefreshText() => Refresh();

    private void OnDownload(object? sender, PropertyChangedEventArgs args)
    {
        if (
            args.PropertyName
            is nameof(DataDownload.Installed)
                or nameof(DataDownload.Work)
                or nameof(DataDownload.CanStart)
                or ""
        )
            Refresh();
    }

    private void Refresh()
    {
        if (_download is not { } download || _text is not { } text)
        {
            Visibility = Visibility.Collapsed;
            return;
        }
        Visibility = Visibility.Visible;
        var section = download.Section;
        _installed = download.Installed;
        var count = _installed is null ? null : download.Count;
        if (!ReferenceEquals(count, _count))
        {
            _count = count;
            if (count is { IsCompleted: false })
                ShowCount(count);
        }
        var progress = download.Work;
        var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as Control;
        var focus = focused?.FocusState ?? FocusState.Unfocused;
        var workFocused = _work.HasFocus;
        _row.Header = text.Get(section, "data");
        // The installed data's details are a value, so they stay on one line.
        _row.Summary = _installed is { } installed ? Describe(section, installed, text) : string.Empty;
        _row.Description = _installed is null ? text.Get(section, "data_hint") : string.Empty;
        _start.Text = text.Get(section, _installed is null ? "download" : "update");
        ToolTipService.SetToolTip(
            _start,
            text.Get(section, _installed is null ? "download_tip" : "update_tip")
        );
        AutomationProperties.SetAutomationId(_start, Name + "Download");
        _start.IsEnabled = download.CanStart;
        _start.Visibility = progress is null ? Visibility.Visible : Visibility.Collapsed;
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
                || ReferenceEquals(focused, _start)
                || ReferenceEquals(focused, _delete)
            )
            && (
                focused.Visibility == Visibility.Collapsed
                || !focused.IsEnabled
                || workFocused && !_work.CanFocus
            )
        )
        {
            if (progress is null && _start.IsEnabled)
                _start.Focus(focus);
            else if (!_work.FocusCancel(focus))
                _row.Focus(FocusState.Programmatic);
        }
    }

    // The installed data's age in whole days, as the row and the update
    // dialog state it.
    internal static int Age(FileInfo installed) =>
        (int)(DateTime.UtcNow - installed.LastWriteTimeUtc).TotalDays;

    private string Describe(string section, FileInfo installed, Strings text)
    {
        var size = text.Bytes(installed.Length);
        var days = Age(installed);
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
