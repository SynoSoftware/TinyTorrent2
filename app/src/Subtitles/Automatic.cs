using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Shared;

namespace Syno.TinyTorrent.Subtitles;

internal sealed class Automatic : UserControl
{
    private readonly Strings _text;
    private readonly Bridge _bridge;
    private readonly bool _home;
    private readonly ToggleSwitch _toggle = new();
    private readonly SettingsRow _row;
    private bool _refreshing;
    internal Control Field => _toggle;

    internal Automatic(Strings text, Action<Exception, string> report, bool home = false)
    {
        _text = text;
        _bridge = new Bridge(DispatcherQueue, Refresh, report);
        _home = home;
        _row = new SettingsRow { Content = _toggle };
        Content = _row;
        AutomationProperties.SetAutomationId(
            _toggle,
            home ? "SubtitleAutomaticHome" : "SubtitleAutomatic"
        );
        _toggle.Toggled += (_, _) =>
        {
            if (!_refreshing)
            {
                var enabled = _toggle.IsOn;
                _ = _bridge.Run(model => model.SetEnabled(enabled), "failed_settings");
            }
        };
        Unloaded += (_, _) => SetVisible(false);
        Refresh();
    }

    internal void Attach(Acquisition? model)
    {
        if (_bridge.Attach(model))
        {
            Refresh();
        }
    }

    internal void SetVisible(bool visible)
    {
        if (_bridge.SetVisible(visible) && visible)
        {
            Refresh();
        }
    }

    internal void Refresh()
    {
        _refreshing = true;
        _row.Header = _text.Get(
            _home ? "settings" : "subtitles",
            _home ? "home_automatic" : "automatic"
        );
        _row.Description = _text.Get(
            _home ? "settings" : "subtitles",
            _home ? "home_automatic_hint" : "automatic_hint"
        );
        AutomationProperties.SetName(_toggle, _row.Header);
        if (_bridge.Model?.Saving.IsCompleted != false)
            _toggle.IsOn = _bridge.Model?.Enabled == true;
        _toggle.IsEnabled = _bridge.Model is not null;
        _row.StateText = SettingsRow.State(
            _toggle.IsOn,
            _text.Get("settings", "on"),
            _text.Get("settings", "off")
        );
        _refreshing = false;
    }
}
