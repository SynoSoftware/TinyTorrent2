using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Shared;

namespace Syno.TinyTorrent.Subtitles;

internal sealed class Automatic : UserControl
{
    private readonly Strings _text;
    private readonly Action<Exception, string> _report;
    private readonly bool _home;
    private readonly ToggleSwitch _toggle = new();
    private readonly SettingsRow _row;
    private Acquisition? _model;
    private bool _refreshing;
    internal Task Saving { get; private set; } = Task.CompletedTask;
    internal Control Field => _toggle;

    internal Automatic(Strings text, Action<Exception, string> report, bool home = false)
    {
        _text = text;
        _report = report;
        _home = home;
        _row = new SettingsRow { Content = _toggle };
        Content = _row;
        AutomationProperties.SetAutomationId(
            _toggle,
            home ? "SubtitleAutomaticHome" : "SubtitleAutomatic"
        );
        _toggle.Toggled += (_, _) =>
        {
            if (!_refreshing && _model is { } model)
                _ = Save(model, _toggle.IsOn);
        };
        Unloaded += (_, _) => Attach(null);
        Refresh();
    }

    internal void Attach(Acquisition? model)
    {
        if (_model == model)
            return;
        if (_model is not null)
            _model.Changed -= OnChanged;
        _model = model;
        if (_model is not null)
            _model.Changed += OnChanged;
        Refresh();
    }

    private void OnChanged(object? sender, EventArgs args) => DispatcherQueue.TryEnqueue(Refresh);

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
        if (Saving.IsCompleted)
            _toggle.IsOn = _model?.Enabled == true;
        _toggle.IsEnabled = _model is not null;
        _row.StateText = SettingsRow.State(
            _toggle.IsOn,
            _text.Get("settings", "on"),
            _text.Get("settings", "off")
        );
        _refreshing = false;
    }

    private async Task Save(Acquisition model, bool enabled)
    {
        var previous = Saving;
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        Saving = completion.Task;
        await previous;
        try
        {
            if (model.IsDisposed)
                return;
            await model.SetEnabled(enabled);
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("Automatic subtitle setting save cancelled.");
        }
        catch (Exception error)
        {
            if (!model.IsDisposed && (_model is null || _model == model))
                _report(error, "failed_settings");
        }
        finally
        {
            completion.SetResult();
            Refresh();
        }
    }
}
