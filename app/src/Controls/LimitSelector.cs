using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Models;

namespace Syno.TinyTorrent.Controls;

public sealed class LimitSelector : ComboBox
{
    private readonly MainViewModel _main;
    private bool _refreshing;

    internal LimitSelector(MainViewModel main)
    {
        _main = main;
        Width = 260;
        HorizontalAlignment = HorizontalAlignment.Right;
        foreach (var mode in new[] { LimitMode.None, LimitMode.Speed, LimitMode.Alternative, LimitMode.Schedule })
            Items.Add(new ComboBoxItem { Tag = mode });
        SelectionChanged += OnSelection;
        Loaded += (_, _) =>
        {
            _main.PropertyChanged += OnChanged;
            Refresh();
        };
        Unloaded += (_, _) => _main.PropertyChanged -= OnChanged;
        Refresh();
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs args) => Refresh();

    private void Refresh()
    {
        _refreshing = true;
        IsEnabled = _main.CanEdit;
        AutomationProperties.SetName(this, _main.Text.Get("transfer_limits", "control"));
        foreach (var item in Items.Cast<ComboBoxItem>())
        {
            var mode = (LimitMode)item.Tag;
            var label = mode switch
            {
                LimitMode.Speed => _main.Text.Get("settings", "speed"),
                LimitMode.Alternative => _main.Text.Get("settings", "alternative"),
                LimitMode.None => _main.Text.Get("transfer_limits", "none"),
                _ => _main.Text.Get("transfer_limits", "follow"),
            };
            if (!Equals(item.Content, label))
            {
                if (ReferenceEquals(SelectedItem, item))
                    SelectedItem = null;
                item.Content = label;
            }
            item.IsEnabled = mode switch
            {
                LimitMode.Speed => _main.CanUseSpeed,
                LimitMode.Alternative => _main.CanUseAlternative,
                LimitMode.None => _main.CanUseNone,
                _ => _main.CanFollowSchedule,
            };
            if ((int)mode == _main.LimitsIndex)
                SelectedItem = item;
        }
        if (_main.LimitsIndex < 0)
            SelectedItem = null;
        _refreshing = false;
    }

    private async void OnSelection(object sender, SelectionChangedEventArgs args)
    {
        if (!_refreshing && SelectedItem is ComboBoxItem { Tag: LimitMode mode })
            await _main.ChooseLimits(mode);
    }
}
