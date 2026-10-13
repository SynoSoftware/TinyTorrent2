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
    private readonly ComboBox _scope = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap };

    internal BatchDialog(Browser library, ProviderOption source, IReadOnlyList<VideoFetch> entries)
    {
        _library = library;
        _source = source;
        _entries = entries;
        Content = new StackPanel { Spacing = 12, MinWidth = 380, Children = { _scope, _hint } };
        AutomationProperties.SetAutomationId(_scope, "MetadataScope");
        _scope.SelectionChanged += (_, _) => PropertyChanged?.Invoke(this, new(nameof(CanSubmit)));
        RefreshText();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public bool IsPending => false;
    public bool HasDraft => false;
    public bool CanSubmit => _entries.Any(entry => entry.Includes((FetchScope)Math.Max(0, _scope.SelectedIndex)));

    internal void RefreshText()
    {
        var selected = Math.Max(0, _scope.SelectedIndex);
        var keys = new[] { "new", "missing_data", "all" };
        _scope.ItemsSource = keys.Select((key, index) => _library.Text.Format("filters", "count",
            _library.Text.Get("request_protection", key), _entries.Count(entry => entry.Includes((FetchScope)index)))).ToArray();
        _scope.SelectedIndex = selected;
        _scope.Header = _source.Name;
        AutomationProperties.SetName(_scope, _source.Name);
        _hint.Text = _library.Text.Get("request_protection", "scope_hint");
        var waits = _entries.Select(entry => entry.Wait).OfType<RequestBlock>().Distinct();
        foreach (var block in waits)
            _hint.Text += "\n" + _library.Text.Protection(block);
    }

    public Task<bool> Submit()
    {
        _library.Fetch(_entries, _source, (FetchScope)Math.Max(0, _scope.SelectedIndex));
        return Task.FromResult(true);
    }
}
