using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Network;

namespace Syno.TinyTorrent.Library;

public sealed partial class BatchDialog : UserControl, IDraft
{
    private readonly Browser _library;
    private readonly ProviderOption _source;
    private readonly IReadOnlyList<VideoFetch> _entries;
    private readonly int _videos;

    /// <param name="videos">The number of selected videos, including those <paramref name="entries"/> leaves out.</param>
    internal BatchDialog(
        Browser library,
        ProviderOption source,
        IReadOnlyList<VideoFetch> entries,
        int videos
    )
    {
        _library = library;
        _source = source;
        _entries = entries;
        _videos = videos;
        InitializeComponent();
        foreach (var (box, _, _, answer) in Groups)
        {
            box.IsEnabled = _entries.Any(entry => entry.Answer == answer);
            box.IsChecked = answer == ProviderAnswer.Unasked && box.IsEnabled;
            box.Checked += (_, _) => PropertyChanged?.Invoke(this, new(nameof(CanSubmit)));
            box.Unchecked += (_, _) => PropertyChanged?.Invoke(this, new(nameof(CanSubmit)));
        }
        RefreshText();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public bool IsPending => false;
    public bool HasDraft => false;
    public bool CanSubmit => Chosen.Count > 0;
    public ClosePolicy ClosePolicy => new ClosePolicy.Discard();

    private (CheckBox Box, TextBlock Label, TextBlock Count, ProviderAnswer Answer)[] Groups =>
        [
            (Unasked, UnaskedLabel, UnaskedCount, ProviderAnswer.Unasked),
            (NotFound, NotFoundLabel, NotFoundCount, ProviderAnswer.NotFound),
            (Found, FoundLabel, FoundCount, ProviderAnswer.Found),
        ];

    private HashSet<ProviderAnswer> Chosen =>
        Groups
            .Where(item => item.Box.IsEnabled && item.Box.IsChecked == true)
            .Select(item => item.Answer)
            .ToHashSet();

    internal void RefreshText()
    {
        var text = _library.Text;
        ProviderLabel.Text = text.Get("library", "provider");
        ProviderName.Text = _source.Name;
        SelectedLabel.Text = text.Get("request_protection", "selected");
        SelectedCount.Text = text.FormatCount("request_protection", "videos", _videos);
        foreach (var (box, label, count, answer) in Groups)
        {
            label.Text = text.Get(
                "request_protection",
                answer switch
                {
                    ProviderAnswer.Unasked => "unasked",
                    ProviderAnswer.NotFound => "not_found",
                    _ => "found",
                }
            );
            ToolTipService.SetToolTip(label, label.Text);
            var number = _entries.Count(entry => entry.Answer == answer);
            count.Text = number.ToString("N0", CultureInfo.CurrentCulture);
            // The count sits in its own column, so the check box's name
            // carries it for screen readers.
            AutomationProperties.SetName(box, text.Format("filters", "count", label.Text, number));
        }
        // A count smaller than the selection says why, so it is not read as
        // what the provider has.
        Outside.Visibility = _videos > _entries.Count ? Visibility.Visible : Visibility.Collapsed;
        OutsideText.Text = text.FormatCount(
            "request_protection",
            "outside",
            _videos - _entries.Count,
            _source.Name
        );
        ToolTipService.SetToolTip(OutsideText, OutsideText.Text);
        Waits.ItemsSource = _entries
            .Select(entry => entry.Wait)
            .OfType<RequestBlock>()
            .Distinct()
            .Select(text.Protection)
            .ToList();
    }

    public Task<bool> Submit()
    {
        _library.Fetch(_entries, _source, Chosen);
        return Task.FromResult(true);
    }

    public Task Discard() => Task.CompletedTask;

    public void Stop() { }
}
