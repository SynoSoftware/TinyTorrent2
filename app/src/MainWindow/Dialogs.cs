using Microsoft.UI.Xaml.Controls;

namespace Syno.TinyTorrent;

public sealed partial class MainWindow
{
    private DialogInteraction? _interaction;
    private bool HasDialog => _interaction is not null;

    private sealed class DialogInteraction
    {
        public ContentDialog? Dialog { get; set; }
        public Action? RefreshText { get; set; }
        public Func<Task>? Restore { get; set; }
        public Func<Task<bool>>? ResolveDraft { get; set; }
        public bool IsResolved { get; set; }
        public bool IsDraftDecision { get; init; }
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private async Task<bool> Interact(Func<DialogInteraction, Task<bool>> action, bool isDraftDecision = false)
    {
        if (HasDialog) return false;
        var interaction = new DialogInteraction { IsDraftDecision = isDraftDecision };
        _interaction = interaction;
        var completed = false;
        try { completed = await action(interaction); }
        catch (Exception error) { Model.Report(error); return false; }
        finally
        {
            _interaction = null;
            interaction.Completion.TrySetResult(completed);
        }
        ShowDeferredAdd();
        return completed;
    }

    private void ShowDeferredAdd()
    {
        if (!HasDialog && !Model.IsClosing && !_allowClose && (Model.Draft.Sources.Count > 0 || Model.Draft.EditingMagnet))
            _ = ShowAdd();
    }

    private async Task<ContentDialogResult> ShowDialog(DialogInteraction interaction, ContentDialog dialog, Action refreshText)
    {
        interaction.Dialog = dialog;
        interaction.RefreshText = refreshText;
        try
        {
            RefreshDialogs();
            refreshText();
            return await dialog.ShowAsync();
        }
        finally
        {
            interaction.Dialog = null;
            interaction.RefreshText = null;
        }
    }
}
