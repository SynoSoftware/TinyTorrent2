using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Views;

namespace Syno.TinyTorrent;

public sealed partial class MainWindow
{
    private DialogInteraction? _interaction;
    private bool HasDialog => _interaction is not null;

    private sealed class DialogInteraction
    {
        public Dialog? Dialog { get; set; }
        public Action? RefreshText { get; set; }
        public Func<Task>? Restore { get; set; }
        public Func<Task<bool>>? ResolveDraft { get; set; }
        public bool IsResolved { get; set; }
        public bool IsDraftDecision { get; init; }
        public TaskCompletionSource<bool> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private async Task<bool> Interact(
        Func<DialogInteraction, Task<bool>> action,
        bool isDraftDecision = false
    )
    {
        // While the window closes, only the question about an unfinished
        // draft may open.
        if (HasDialog || Model.IsClosing && !isDraftDecision)
            return false;
        var interaction = new DialogInteraction { IsDraftDecision = isDraftDecision };
        _interaction = interaction;
        Model.Library.SetDialog(true);
        var completed = false;
        try
        {
            completed = await action(interaction);
        }
        catch (Exception error)
        {
            Model.Report(error);
            return false;
        }
        finally
        {
            _interaction = null;
            Model.Library.SetDialog(false);
            interaction.Completion.TrySetResult(completed);
        }
        ShowDeferredAdd();
        return completed;
    }

    private void ShowDeferredAdd()
    {
        if (
            !HasDialog
            && !Model.IsClosing
            && !_allowClose
            && (Model.AddDraft.Sources.Count > 0 || Model.AddDraft.EditingMagnet)
        )
            _ = ShowAdd();
    }

    private static async Task Submit(
        DialogInteraction interaction,
        ContentDialogButtonClickEventArgs args,
        Func<Task<bool>> submit
    )
    {
        args.Cancel = true;
        var deferral = args.GetDeferral();
        try
        {
            args.Cancel = !await submit();
            if (!args.Cancel)
                interaction.IsResolved = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async Task<ContentDialogResult> ShowDialog(
        DialogInteraction interaction,
        Dialog dialog,
        Action refreshText
    )
    {
        void ShowText()
        {
            dialog.CloseButtonText = Model.Text.Get("dialog", "cancel");
            refreshText();
        }
        dialog.XamlRoot = Root.XamlRoot;
        interaction.Dialog = dialog;
        interaction.RefreshText = ShowText;
        try
        {
            RefreshDialogs();
            ShowText();
            return await dialog.ShowAsync();
        }
        finally
        {
            interaction.Dialog = null;
            interaction.RefreshText = null;
        }
    }

    // An editor's primary button submits its draft. The dialog stays open while
    // the draft or a picker is pending, and after a refused submit. A dialog
    // passes `submit` only to add a step to the draft's own.
    private async Task ShowEditor(
        DialogInteraction interaction,
        Dialog dialog,
        IDraft draft,
        Action refreshText,
        Func<Task<bool>>? submit = null
    )
    {
        dialog.SetBinding(
            ContentDialog.IsPrimaryButtonEnabledProperty,
            new Binding
            {
                Source = draft,
                Path = new PropertyPath(nameof(IDraft.CanSubmit)),
                Mode = BindingMode.OneWay,
            }
        );
        dialog.PrimaryButtonClick += async (_, args) =>
            await Submit(interaction, args, submit ?? draft.Submit);
        dialog.Closing += (_, args) =>
        {
            if ((draft.IsPending || Model.IsPicking) && !Model.IsClosing)
                args.Cancel = true;
        };
        try
        {
            await ShowDialog(interaction, dialog, refreshText);
            interaction.IsResolved |= !Model.IsClosing;
        }
        finally
        {
            dialog.Content = null;
        }
    }

    private static async Task<bool> SaveOnClose(DialogInteraction interaction, IDraft draft, bool canSave) =>
        !draft.HasDraft || !canSave || (interaction.IsResolved = await draft.Submit());

    // A confirmation's facts, one trimmed line each.
    private static UIElement Lines(IEnumerable<string> texts)
    {
        var lines = new StackPanel { Spacing = 4 };
        foreach (var text in texts)
        {
            var line = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTipService.SetToolTip(line, text);
            lines.Children.Add(line);
        }
        return new ScrollViewer { MaxHeight = 240, Content = lines };
    }
}
