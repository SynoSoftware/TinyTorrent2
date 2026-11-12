using Microsoft.UI.Dispatching;
using Syno.TinyTorrent.Shared;

namespace Syno.TinyTorrent.Subtitles;

/// <summary>
/// A view's live connection to the acquisition it shows. While the view is
/// shown, a burst of the model's changes refreshes it once, on the UI
/// thread. An action the person starts through it reports its failure in
/// the window unless the model closed or was replaced meanwhile.
/// </summary>
internal sealed class Bridge
{
    private readonly Action _refresh;
    private readonly Action<Exception, string> _report;
    private readonly TurnCallback _changed;

    internal Bridge(DispatcherQueue dispatcher, Action refresh, Action<Exception, string> report)
    {
        _refresh = refresh;
        _report = report;
        _changed = new TurnCallback(
            dispatcher,
            DispatcherQueuePriority.Normal,
            () =>
            {
                if (IsVisible)
                {
                    _refresh();
                }
            }
        );
    }

    internal Acquisition? Model { get; private set; }
    internal bool IsVisible { get; private set; }

    /// <summary>Connects the view to the model, or to none. Returns whether the model changed.</summary>
    internal bool Attach(Acquisition? model)
    {
        if (ReferenceEquals(Model, model))
        {
            return false;
        }
        if (IsVisible && Model is { } previous)
        {
            previous.Changed -= OnChanged;
        }
        Model = model;
        if (IsVisible && model is not null)
        {
            model.Changed += OnChanged;
        }
        return true;
    }

    /// <summary>Returns whether the visibility changed.</summary>
    internal bool SetVisible(bool visible)
    {
        if (IsVisible == visible)
        {
            return false;
        }
        IsVisible = visible;
        if (Model is { } model)
        {
            if (visible)
            {
                model.Changed += OnChanged;
            }
            else
            {
                model.Changed -= OnChanged;
            }
        }
        return true;
    }

    /// <summary>
    /// Runs an action the person started on the attached model, and refreshes
    /// the view once it has started and again when it ends. The action is
    /// called before this method returns, so settings writes queue in click
    /// order. <paramref name="failure"/> is the notification title key that
    /// names the action. Returns whether the action completed; a cancelled
    /// action is no failure.
    /// </summary>
    internal async Task<bool> Run(Func<Acquisition, Task> action, string failure)
    {
        if (Model is not { } model)
        {
            return false;
        }
        try
        {
            var running = action(model);
            _refresh();
            await running;
            return true;
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine($"Subtitle action cancelled ({failure}).");
            return false;
        }
        catch (Exception error)
        {
            if (ReferenceEquals(model, Model) && !model.IsDisposed)
            {
                _report(error, failure);
            }
            return false;
        }
        finally
        {
            _refresh();
        }
    }

    // Acquisition raises Changed on any thread, worker threads included.
    private void OnChanged(object? sender, EventArgs args) => _changed.Request();
}
