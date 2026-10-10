using System.ComponentModel;

namespace Syno.TinyTorrent.Views;

// The input that an editor dialog's primary button submits.
internal interface IDraft : INotifyPropertyChanged
{
    // Enables the primary button, so a change raises PropertyChanged.
    bool CanSubmit { get; }

    bool HasDraft { get; }

    // Work the dialog waits for before it closes.
    bool IsPending { get; }

    // False keeps the dialog open.
    Task<bool> Submit();
}
