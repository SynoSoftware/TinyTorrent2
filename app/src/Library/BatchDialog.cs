using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;
using Syno.TinyTorrent.Views;

namespace Syno.TinyTorrent.Library;

internal sealed class BatchDialog : UserControl, IDraft
{
    private readonly Browser _library;
    private readonly ProviderOption _source;
    private readonly IReadOnlyList<VideoFetch> _entries;
    private readonly int _videos;
    private readonly ComboBox _scope = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap };

    /// <param name="videos">The number of selected videos, including those <paramref name="entries"/> leaves out.</param>
    internal BatchDialog(Browser library, ProviderOption source, IReadOnlyList<VideoFetch> entries, int videos)
    {
        _library = library;
        _source = source;
        _entries = entries;
        _videos = videos;
        Content = new StackPanel { Spacing = 12, MinWidth = 380, Children = { _scope, _hint } };
        AutomationProperties.SetAutomationId(_scope, "MetadataScope");
        _scope.SelectionChanged += (_, _) =>
        {
            ShowHint();
            PropertyChanged?.Invoke(this, new(nameof(CanSubmit)));
        };
        RefreshText();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public bool IsPending => false;
    public bool HasDraft => false;
    public bool CanSubmit => _entries.Any(entry => entry.Includes(Scope));
    private FetchScope Scope => (FetchScope)Math.Max(0, _scope.SelectedIndex);

    internal void RefreshText()
    {
        var selected = Math.Max(0, _scope.SelectedIndex);
        var keys = new[] { "new", "missing_data", "all", "reidentify" };
        _scope.ItemsSource = keys.Select((key, index) => _library.Text.Format("filters", "count",
            _library.Text.Get("request_protection", key), _entries.Count(entry => entry.Includes((FetchScope)index)))).ToArray();
        _scope.SelectedIndex = selected;
        _scope.Header = _source.Name;
        AutomationProperties.SetName(_scope, _source.Name);
        ShowHint();
    }

    // Says why selected videos are missing from the chosen count, so a count
    // smaller than the selection is not mistaken for what the provider has.
    private void ShowHint()
    {
        var text = _library.Text;
        var lines = new List<string> { text.Get("request_protection", "scope_hint") };
        var elsewhere = _entries.Count(entry => entry.IsIdentifiedElsewhere);
        if (Scope != FetchScope.Reidentify && elsewhere > 0)
            lines.Add(text.FormatCount("request_protection", "elsewhere", elsewhere));
        if (_videos > _entries.Count)
            lines.Add(text.FormatCount("request_protection", "outside", _videos - _entries.Count, _source.Name));
        lines.AddRange(_entries.Select(entry => entry.Wait).OfType<RequestBlock>().Distinct().Select(text.Protection));
        _hint.Text = string.Join("\n", lines);
    }

    public Task<bool> Submit()
    {
        _library.Fetch(_entries, _source, Scope);
        return Task.FromResult(true);
    }
}
