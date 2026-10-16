using Syno.TinyTorrent.Controls;

namespace Syno.TinyTorrent.Shared;

// The one item at the left of a dialog's buttons. Each kind is a single item,
// so the button row never stacks two, and Dialog draws each kind the same way
// in every dialog.
public abstract record DialogFooter
{
    private DialogFooter() { }

    // Don't show this dialog again. Only a submit saves the preference.
    public sealed record OptOut(DialogPreference Preference) : DialogFooter;

    // One fact about the whole dialog, such as Move's count and total size.
    public sealed record Detail(string Text) : DialogFooter;

    // A check's result beside the button that runs it.
    public sealed record Check(CheckStatus Status) : DialogFooter;
}
