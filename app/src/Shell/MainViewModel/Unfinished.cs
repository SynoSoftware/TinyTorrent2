using System.Text.Json;

namespace Syno.TinyTorrent.Shell;

public sealed partial class MainViewModel
{
    // The torrents whose move, check or file deletion Exit or a crash left
    // unfinished.
    internal readonly record struct UnfinishedTasks(int Moves, int Checks, int Deletions);

    // The work the engine holds until the person answers, or null. It stays
    // null after the answer is saved, even when a snapshot sent before the
    // answer still reports the work, until a snapshot reports none.
    internal UnfinishedTasks? Unfinished { get; private set; }

    private bool _unfinishedAnswered;

    // Raised each time the engine starts waiting for an answer.
    internal event EventHandler? UnfinishedRequested;

    private void ObserveUnfinished(JsonElement snapshot)
    {
        UnfinishedTasks? reported =
            snapshot.TryGetProperty("unfinished_tasks", out var tasks)
            && tasks.ValueKind == JsonValueKind.Object
                ? new(
                    tasks.GetProperty("moves").GetInt32(),
                    tasks.GetProperty("checks").GetInt32(),
                    tasks.GetProperty("deletions").GetInt32()
                )
                : null;
        if (reported is null)
            _unfinishedAnswered = false;
        if (_unfinishedAnswered)
            return;
        var requested = Unfinished is null && reported is not null;
        Unfinished = reported;
        if (requested)
            UnfinishedRequested?.Invoke(this, EventArgs.Empty);
    }

    // Answers for all the waiting work together. A failed answer is reported
    // and leaves the work waiting.
    internal async Task<bool> AnswerUnfinished(UnfinishedAnswer answer)
    {
        try
        {
            await _client.Send(
                "unfinished_tasks",
                new { action = Word(answer) },
                Requester.Person
            );
        }
        catch (Exception error)
        {
            Notifications.Report(error);
            return false;
        }
        Unfinished = null;
        _unfinishedAnswered = true;
        return true;
    }

    // Saves the answer as the setting, so the engine stops asking.
    internal Task RememberUnfinished(UnfinishedAnswer answer) =>
        Settings.UnfinishedTasks.Submit(Word(answer));

    private static string Word(UnfinishedAnswer answer) => answer.ToString().ToLowerInvariant();
}
