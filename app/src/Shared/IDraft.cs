using System.ComponentModel;

namespace Syno.TinyTorrent.Shared;

// The input that an editor dialog's primary button submits.
internal interface IDraft : INotifyPropertyChanged
{
    // Enables the primary button, so a change raises PropertyChanged.
    bool CanSubmit { get; }

    bool HasDraft { get; }

    // Work the dialog waits for before it closes.
    bool IsPending { get; }

    // What closing the window does with the draft. A draft that cannot be
    // saved, for example because the engine is unavailable, reports Discard.
    // Pending work does not count: its Submit refuses, so the window stays open.
    ClosePolicy ClosePolicy { get; }

    // False keeps the dialog open.
    Task<bool> Submit();

    // Drops the draft, so the next opening starts from the saved values.
    Task Discard();

    // Cancels a search or check that is still running when the dialog closes.
    void Stop();
}

// What closing the window does with an editor's unfinished draft.
public abstract record ClosePolicy
{
    private ClosePolicy() { }

    // Submits the draft, because leaving an editor applies its input.
    public sealed record Save : ClosePolicy;

    // Drops the draft, because it cannot be saved or must not be saved
    // without the person's Save.
    public sealed record Discard : ClosePolicy;

    // Asks whether to submit or drop the draft, because submitting starts
    // work the person has not confirmed. Key selects the `changes` texts;
    // Subject and Submit are read each time the prompt's text refreshes.
    public sealed record Ask(string Key, string Glyph, Func<string> Subject, Func<string> Submit)
        : ClosePolicy;
}
