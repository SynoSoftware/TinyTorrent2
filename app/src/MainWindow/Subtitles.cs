using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Subtitles;

namespace Syno.TinyTorrent;

public sealed partial class MainWindow
{
    private async Task DownloadSubtitles(IReadOnlySet<long> entries)
    {
        if (Model.Subtitles is not { } subtitles)
            return;
        await Interact(async interaction =>
        {
            interaction.Restore = () => DownloadSubtitles(entries);
            var content = new Batch(
                Model.Text,
                (error, key) => Model.Report(error, key, string.Empty, null),
                entries
            )
            {
                MinWidth = 480,
            };
            content.Attach(subtitles);
            var dialog = new Dialog { Content = content, Glyph = Lucide.Captions };
            content.PropertyChanged += (_, _) =>
            {
                dialog.IsPrimaryButtonEnabled = content.CanAct;
                dialog.PrimaryGlyph = content.ActionGlyph;
                dialog.PrimaryButtonText = content.ActionText;
                dialog.PrimaryToolTip = content.ActionHint;
            };
            dialog.PrimaryButtonClick += async (_, args) =>
            {
                args.Cancel = true;
                await content.Act();
            };
            try
            {
                await ShowDialog(
                    interaction,
                    dialog,
                    () =>
                    {
                        dialog.Title = Model.Text.Get("subtitles", "download_title");
                        dialog.CloseButtonText = Model.Text.Get("commands", "close");
                        content.Refresh();
                    }
                );
                interaction.IsResolved |= !Model.IsClosing;
            }
            finally
            {
                content.Attach(null);
            }
            return true;
        });
    }

    // Opens the account of supplierId, or of the saved supplier.
    private async Task ShowSupplier(
        SubtitleSupplier? supplierId = null,
        SupplierDialog? content = null
    )
    {
        if (Model.Subtitles is not { } subtitles)
            return;
        content ??= new SupplierDialog(
            Model.Text,
            subtitles,
            supplierId ?? subtitles.SupplierId,
            (supplier, failure) =>
            {
                if (failure is null)
                    Model.ShowOutcome(
                        Model.Text.Get("subtitles", "connected"),
                        supplier.ToString()
                    );
                else
                    Model.Report(failure, "failed_subtitle_check", supplier.ToString(), null);
            }
        );
        await Interact(async interaction =>
        {
            interaction.Restore = () => ShowSupplier(content.SupplierId, content);
            interaction.ResolveDraft = () =>
            {
                content.Clear();
                interaction.IsResolved = true;
                return Task.FromResult(true);
            };
            var dialog = new Dialog
            {
                Content = content,
                Footer = content.Status,
                FooterAction = content.CheckAction,
                Glyph = Lucide.Captions,
                PrimaryGlyph = Lucide.Check,
            };
            try
            {
                await ShowEditor(
                    interaction,
                    dialog,
                    content,
                    () =>
                    {
                        dialog.Title = content.SupplierId.ToString();
                        dialog.PrimaryButtonText = Model.Text.Get("dialog", "save");
                        dialog.PrimaryToolTip = Model.Text.Get("subtitles", "save_hint");
                        content.RefreshText();
                    },
                    async () =>
                    {
                        if (!await content.Submit())
                            return false;
                        try
                        {
                            await Model.RetrySubtitles();
                        }
                        catch (Exception error)
                        {
                            Model.Report(error);
                        }
                        return true;
                    }
                );
            }
            finally
            {
                content.CancelCheck();
                dialog.Footer = null;
                if (interaction.IsResolved)
                    content.Clear();
            }
            return true;
        });
    }

    private async Task ChooseSubtitle(long entryId, ChoiceDialog? content = null)
    {
        if (Model.Subtitles is not { } subtitles)
            return;
        content ??= new ChoiceDialog(Model.Text, subtitles, entryId);
        await Interact(async interaction =>
        {
            interaction.Restore = () => ChooseSubtitle(entryId, content);
            interaction.ResolveDraft = () => SaveOnClose(interaction, content, canSave: true);
            var dialog = new Dialog
            {
                Content = content,
                Glyph = Lucide.Captions,
                PrimaryGlyph = Lucide.Check,
            };
            dialog.Opened += async (_, _) =>
            {
                if (!content.HasDraft)
                    await content.Find();
            };
            try
            {
                await ShowEditor(
                    interaction,
                    dialog,
                    content,
                    () =>
                    {
                        dialog.Title = Model.Text.Get("subtitles", "choose");
                        dialog.PrimaryButtonText = Model.Text.Get("dialog", "save");
                        dialog.PrimaryToolTip = Model.Text.Get("subtitles", "choose_tip");
                        content.RefreshText();
                    }
                );
            }
            finally
            {
                content.Cancel();
            }
            return true;
        });
    }
}
