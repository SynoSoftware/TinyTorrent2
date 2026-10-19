using System.ComponentModel;

namespace Syno.TinyTorrent.Shell;

public sealed partial class MainViewModel
{
    // A running close. Exits is whether the engine waits for this close to
    // exit; Exit can arrive during a close or a reset that is already running.
    private readonly record struct Close(bool Resets, bool Exits);

    // Null while the window is open.
    private Close? _close;

    public bool IsClosing => _close is not null;

    // Stops new operations while the window closes. Accepted work still
    // finishes, and CanClose reports when it has. Returns false when a close
    // is already running; an Exit then marks it as exiting and asks the
    // engine to wait for it.
    internal async Task<bool> BeginClose(CloseRequest request)
    {
        var exits = request == CloseRequest.Exit;
        if (_close is { } running)
        {
            if (exits)
            {
                _close = running with { Exits = true };
                await DeferClose();
            }
            return false;
        }
        _close = new(request == CloseRequest.Reset, exits);
        Refresh();
        return true;
    }

    // Waits for accepted work, then leaves the connection setup and Settings.
    // Returns false when the person keeps the window open.
    internal async Task<bool> PrepareClose()
    {
        if (!CanClose)
        {
            if (IsPicking)
                await DeferExit();
            var idle = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            void OnIdle(object? sender, PropertyChangedEventArgs args)
            {
                if (CanClose)
                    idle.TrySetResult();
            }
            PropertyChanged += OnIdle;
            try
            {
                if (CanClose)
                    idle.TrySetResult();
                await idle.Task;
            }
            finally
            {
                PropertyChanged -= OnIdle;
            }
        }
        if (!await Settings.Connection.Depart())
            return false;
        await Settings.PrepareLeave();
        return true;
    }

    // Runs after every draft is resolved. Returns false when the engine did
    // not receive the close reply, so the window stays open.
    internal async Task<bool> FinishClose()
    {
        var resets = _close is { Resets: true };
        // After the drafts are saved, so none is saved over the defaults.
        if (resets)
            await Settings.ResetAll();
        Features.Disconnect();
        // Requests queued before this one reach the engine first, so a command
        // that sends one request needs no wait before the window closes. Exit
        // can arrive during a reset; the engine then waits for this reply
        // rather than a new window.
        if (
            (!resets || Exiting)
            && !await Reply(
                Exiting ? "close_reply" : "window_closed",
                new { state = "closing" }
            )
        )
            return false;
        // The update check and the features share the connection to provider
        // websites, so it closes after both.
        var updates = Updates.Close();
        await Features.Close();
        await updates;
        _providerHttp.Dispose();
        if (resets)
            await Features.ResetSettings();
        // Instead of `window_closed`, which shows the background toast: the
        // engine asks this closing window to activate, is told it cannot, and
        // opens a new window once this one has exited. A refusal, such as from
        // an engine that is exiting, still lets this window close, because its
        // features already have.
        if (resets && !Exiting)
            await Reply("open");
        return true;
    }

    // Ends a close that did not finish, so the window works again.
    internal async Task CancelClose()
    {
        if (Exiting)
            await Reply("close_reply", new { state = "cancelled" });
        _close = null;
        _ = Features.Refresh();
        Refresh();
        if (_activationsPending)
            _ = ReceiveActivations();
        await ReplyActivation(true);
    }

    private bool Exiting => _close is { Exits: true };

    // Asks an exiting engine to wait while the person decides.
    internal Task DeferClose() => Reply("close_reply", new { state = "waiting" });

    // Asks the engine to wait while the person decides, when it exits through
    // this close. Otherwise no engine waits for the close.
    internal Task DeferExit() => Exiting ? DeferClose() : Task.CompletedTask;

    internal Task ReplyExit(ExitAnswer answer) =>
        Reply(
            "exit_reply",
            new
            {
                answer = answer switch
                {
                    ExitAnswer.Confirmed => "confirmed",
                    ExitAnswer.Cancelled => "cancelled",
                    _ => "unavailable",
                },
            }
        );

    internal Task ReplyActivation(bool available) =>
        Reply("activate_reply", new { available });

    // Sends one reply of the close, Exit and activation handshakes. A failure
    // is reported, never thrown, so the window can always finish or undo its
    // close. Returns false only when the send failed; without a connection
    // there is no engine to answer, so it returns true.
    private async Task<bool> Reply(string command, object? arguments = null)
    {
        if (!_connected)
            return true;
        try
        {
            await _client.Send(command, arguments, Requester.Window);
            return true;
        }
        catch (Exception error)
        {
            Notifications.Report(error);
            return false;
        }
    }
}
