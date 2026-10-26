using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Syno.TinyTorrent.Shared.Engine;
using Windows.System;

namespace Syno.TinyTorrent.Search;

internal sealed class FiltersView : StackPanel
{
    private readonly Browser _model;
    private readonly RadioButtons _scope = new();
    private readonly RadioButton _files = new();
    private readonly RadioButton _torrents = new();
    private readonly TextBox _extension = new();
    private readonly TextBox _torrent = new();
    private readonly TextBox _minimum = new();
    private readonly TextBox _maximum = new();
    private bool _refreshing;
    private bool _composing;

    internal FiltersView(Browser model)
    {
        _model = model;
        Spacing = 16;
        _scope.Items.Add(_files);
        _scope.Items.Add(_torrents);
        Children.Add(_scope);
        Children.Add(_extension);
        Children.Add(_minimum);
        Children.Add(_maximum);
        Children.Add(_torrent);
        _scope.SelectionChanged += (_, _) =>
        {
            if (!_refreshing && _scope.SelectedIndex >= 0)
                model.Target = _scope.SelectedIndex == 1 ? CatalogueTarget.Torrents : CatalogueTarget.Files;
        };
        _extension.TextChanged += (_, _) =>
        {
            if (!_refreshing)
                model.Extension = _extension.Text;
        };
        ConfigureSize(_minimum, () => model.Minimum, value => model.Minimum = value);
        _torrent.TextChanged += (_, _) =>
        {
            if (!_refreshing)
                model.Torrent = _torrent.Text;
        };
        ConfigureSize(_maximum, () => model.Maximum, value => model.Maximum = value);
        Loaded += (_, _) =>
        {
            model.PropertyChanged -= OnModel;
            model.PropertyChanged += OnModel;
            RefreshText();
        };
        Unloaded += (_, _) => { if (!IsLoaded) model.PropertyChanged -= OnModel; };
        AutomationProperties.SetAutomationId(_scope, "CatalogueScope");
        AutomationProperties.SetAutomationId(_extension, "CatalogueExtensions");
        AutomationProperties.SetAutomationId(_minimum, "CatalogueMinimumSize");
        AutomationProperties.SetAutomationId(_maximum, "CatalogueMaximumSize");
        AutomationProperties.SetAutomationId(_torrent, "CatalogueParentTorrent");
        RefreshText();
    }

    internal void RefreshText()
    {
        _refreshing = true;
        try
        {
            _scope.Header = _model.Text.Get("catalogue", "search");
            RefreshCounts();
            _scope.SelectedIndex = _model.Target == CatalogueTarget.Torrents ? 1 : 0;
            _extension.Header = _model.Text.Get("catalogue", "extension");
            _extension.Text = _model.Extension;
            _minimum.Header = _model.Text.Get("catalogue", "minimum");
            _maximum.Header = _model.Text.Get("catalogue", "maximum");
            _torrent.Header = _model.Text.Get("catalogue", "parent");
            _torrent.Text = _model.Torrent;
            _minimum.Text = Size(_model.Minimum);
            _maximum.Text = Size(_model.Maximum);
        }
        finally { _refreshing = false; }
    }

    private void OnModel(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == string.Empty)
            RefreshCounts();
        else if (args.PropertyName is nameof(Browser.Extension) or nameof(Browser.Minimum) or nameof(Browser.Maximum) or nameof(Browser.Target) or nameof(Browser.Torrent))
            RefreshText();
    }

    private void RefreshCounts()
    {
        _files.Content = _model.Text.Get("catalogue", "files") + " · " + _model.State.Files.ToString("N0");
        _torrents.Content = _model.Text.Get("catalogue", "torrents") + " · " + _model.State.Torrents.ToString("N0");
    }

    private void ConfigureSize(TextBox box, Func<long?> current, Action<long?> apply)
    {
        box.InputScope = new InputScope { Names = { new InputScopeName(InputScopeNameValue.Number) } };
        box.TextCompositionStarted += (_, _) => _composing = true;
        box.TextCompositionEnded += (_, _) => _composing = false;
        box.TextChanged += (_, _) =>
        {
            if (_refreshing)
                return;
            box.Description = null;
            if (string.IsNullOrWhiteSpace(box.Text) && !_composing)
                apply(null);
        };
        box.LostFocus += (_, _) => CommitSize(box, apply);
        box.KeyDown += (_, args) =>
        {
            if (_composing)
                return;
            if (args.Key == VirtualKey.Enter)
            {
                args.Handled = true;
                CommitSize(box, apply);
            }
            else if (args.Key == VirtualKey.Escape)
            {
                args.Handled = true;
                box.Text = Size(current());
                box.Description = null;
            }
        };
    }

    private void CommitSize(TextBox box, Action<long?> apply)
    {
        if (_refreshing || _composing)
            return;
        if (string.IsNullOrWhiteSpace(box.Text))
        {
            apply(null);
            box.Description = null;
        }
        else if (decimal.TryParse(box.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var value)
            && value >= 0 && value <= long.MaxValue / 1048576m)
        {
            apply((long)(value * 1048576m));
            box.Description = null;
        }
        else
            box.Description = _model.Text.Get("catalogue", "invalid_size");
    }

    private static string Size(long? bytes) => bytes is { } value
        ? (value / 1048576m).ToString(CultureInfo.CurrentCulture) : string.Empty;
}
