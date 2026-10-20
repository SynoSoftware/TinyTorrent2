using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Torrents;

namespace Syno.TinyTorrent.Shell;

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

        // CloseWindow hid the dialog, rather than the person closing it.
        public bool IsSuspended { get; set; }
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
        Model.Library.CancelInformation();
        var completed = false;
        try
        {
            completed = await action(interaction);
        }
        catch (Exception error)
        {
            Model.Notifications.Report(error);
            return false;
        }
        finally
        {
            _interaction = null;
            interaction.Completion.TrySetResult(completed);
        }
        ShowDeferredUnfinished();
        ShowDeferredAdd();
        return completed;
    }

    // The engine holds the unfinished work until it is answered, so this dialog
    // opens as soon as the window can show it, before the Add draft.
    private void ShowDeferredUnfinished()
    {
        if (Model.Unfinished is not null && !_cloaked && !_allowClose && !Model.IsPicking)
            _ = AskUnfinished();
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
            dialog.OptOutText = Model.Text.Get("dialog", "never_show");
            refreshText();
        }
        dialog.XamlRoot = Root.XamlRoot;
        dialog.Opened += (_, _) => RaiseToasts();
        interaction.Dialog = dialog;
        interaction.RefreshText = ShowText;
        try
        {
            RefreshPopups();
            ShowText();
            return await dialog.ShowAsync();
        }
        finally
        {
            interaction.Dialog = null;
            interaction.RefreshText = null;
        }
    }

    // An editor's primary button submits its draft, and nothing submits while
    // the window closes. The dialog stays open while the draft or a picker is
    // pending, and after a refused submit. A dialog passes `submit` only to add
    // a step to the draft's own. The person closing the dialog drops the
    // draft, also while the window waits to close; a dialog that the window's
    // close hides leaves the draft to its ClosePolicy.
    private async Task ShowEditor(
        DialogInteraction interaction,
        Dialog dialog,
        IDraft draft,
        Action refreshText,
        Func<Task<bool>>? submit = null
    )
    {
        void EnableSubmit(object? sender = null, PropertyChangedEventArgs? args = null) =>
            dialog.IsPrimaryButtonEnabled = draft.CanSubmit && !Model.IsClosing;
        EnableSubmit();
        draft.PropertyChanged += EnableSubmit;
        Model.PropertyChanged += EnableSubmit;
        dialog.PrimaryButtonClick += async (_, args) =>
            await Submit(interaction, args, submit ?? draft.Submit);
        dialog.Closing += (_, args) =>
        {
            if ((draft.IsPending || Model.IsPicking) && !interaction.IsSuspended)
                args.Cancel = true;
        };
        try
        {
            await ShowDialog(interaction, dialog, refreshText);
            if (interaction.IsSuspended)
            {
                // CloseWindow resolves the draft after the dialog has closed.
                // An unchanged draft stays unresolved, and so does a Discard
                // draft, which only the window's closing drops. A failed close
                // then reopens the dialog with the person's input.
                interaction.ResolveDraft = async () =>
                    !draft.HasDraft
                    || draft.ClosePolicy is ClosePolicy.Discard
                    || (interaction.IsResolved = await ResolveDraft(draft));
            }
            else
            {
                interaction.IsResolved = true;
                await draft.Discard();
            }
        }
        finally
        {
            draft.PropertyChanged -= EnableSubmit;
            Model.PropertyChanged -= EnableSubmit;
            draft.Stop();
            dialog.Content = null;
        }
    }

    // Applies the draft's ClosePolicy while the window closes. False keeps the
    // window open.
    private async Task<bool> ResolveDraft(IDraft draft)
    {
        switch (draft.ClosePolicy)
        {
            case ClosePolicy.Save:
                return await draft.Submit();
            case ClosePolicy.Ask question:
                return await Ask(draft, question);
            default:
                await draft.Discard();
                return true;
        }
    }

    private async Task ShowAccount(AccountDialog content)
    {
        await Interact(async interaction =>
        {
            interaction.Restore = () => ShowAccount(content);
            var dialog = new Dialog
            {
                Content = content,
                Footer = content.Status is { } status ? new DialogFooter.Check(status) : null,
                Glyph = Lucide.KeyRound,
                PrimaryGlyph = Lucide.Check,
            };
            dialog.SizeToContent();
            try
            {
                await ShowEditor(
                    interaction,
                    dialog,
                    content,
                    () =>
                    {
                        dialog.Title = content.Title;
                        dialog.PrimaryButtonText = Model.Text.Get("dialog", "save");
                        dialog.PrimaryToolTip = Model.Text.Get("account", "save_tip");
                        content.RefreshText();
                    }
                );
            }
            finally
            {
                dialog.Footer = null;
            }
            return true;
        });
    }

    private async Task ConfirmDatabaseDeletion()
    {
        await Interact(async interaction =>
        {
            var dialog = new Dialog { Glyph = Lucide.Database, PrimaryGlyph = Lucide.Trash2 };
            var choice = await ShowDialog(
                interaction,
                dialog,
                () =>
                {
                    dialog.Title = Model.Text.Get("window", "delete_database_title");
                    dialog.Content = Model.Text.Get("window", "delete_database_body");
                    dialog.PrimaryButtonText = Model.Text.Get("file_action", "delete");
                    dialog.PrimaryToolTip = Model.Text.Get("window", "delete_database_tip");
                }
            );
            if (choice != ContentDialogResult.Primary)
                return false;
            await Model.Features.Delete();
            return true;
        });
    }

    // One question for all the disk work that Exit or a crash left unfinished.
    // Neither answer is Cancel, and the engine keeps the work waiting, so the
    // dialog has no Close button and Escape leaves it open until the engine
    // is unavailable.
    private async Task AskUnfinished()
    {
        if (Model.Unfinished is not { } work)
            return;
        await Interact(async interaction =>
        {
            var preference = new DialogPreference(Model.Settings);
            var dialog = new Dialog
            {
                Glyph = Lucide.ListChecks,
                PrimaryGlyph = Lucide.Play,
                SecondaryGlyph = Lucide.Undo2,
                Footer = new DialogFooter.OptOut(preference),
            };
            async Task<bool> Answer(UnfinishedAnswer answer)
            {
                if (!await Model.AnswerUnfinished(answer))
                    return false;
                if (preference.NeverShow)
                    await Model.RememberUnfinished(answer);
                return true;
            }
            dialog.PrimaryButtonClick += async (_, args) =>
                await Submit(interaction, args, () => Answer(UnfinishedAnswer.Continue));
            dialog.SecondaryButtonClick += async (_, args) =>
                await Submit(interaction, args, () => Answer(UnfinishedAnswer.Abandon));
            dialog.Closing += (_, args) =>
            {
                if (
                    args.Result == ContentDialogResult.None
                    && !interaction.IsSuspended
                    && Model.Unfinished is not null
                )
                    args.Cancel = true;
            };
            await ShowDialog(
                interaction,
                dialog,
                () =>
                {
                    dialog.Title = Model.Text.Get("unfinished", "title");
                    dialog.CloseButtonText = string.Empty;
                    dialog.PrimaryButtonText = Model.Text.Get("unfinished", "continue");
                    dialog.PrimaryToolTip = Model.Text.Get("unfinished", "continue_tip");
                    dialog.SecondaryButtonText = Model.Text.Get("unfinished", "abandon");
                    dialog.SecondaryToolTip = Model.Text.Get("unfinished", "abandon_tip");
                    dialog.Content = Lines(
                        new[]
                        {
                            (Key: "moves", Count: work.Moves),
                            (Key: "checks", Count: work.Checks),
                            (Key: "deletions", Count: work.Deletions),
                        }
                            .Where(fact => fact.Count > 0)
                            .Select(fact =>
                                Model.Text.FormatCount("unfinished", fact.Key, fact.Count)
                            )
                    );
                }
            );
            return true;
        });
    }

    // A confirmation's facts, one trimmed line each.
    private static FrameworkElement Lines(IEnumerable<string> texts)
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
