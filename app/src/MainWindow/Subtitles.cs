using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Subtitles;

namespace Syno.TinyTorrent;

public sealed partial class MainWindow
{
    private async Task ShowSupplier(SupplierDialog? content = null)
    {
        if (Model.Subtitles is not { } subtitles)
            return;
        content ??= new SupplierDialog(Model.Text, subtitles);
        await Interact(async interaction =>
        {
            interaction.Restore = () => ShowSupplier(content);
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
                await ShowEditor(interaction, dialog, content, () =>
                {
                    dialog.Title = Model.Text.Get("subtitles", "supplier");
                    dialog.PrimaryButtonText = Model.Text.Get("dialog", "save");
                    dialog.PrimaryToolTip = Model.Text.Get("subtitles", "save_hint");
                    content.RefreshText();
                }, async () =>
                {
                    if (!await content.Submit())
                        return false;
                    try { await Model.RetrySubtitles(); }
                    catch (Exception error) { Model.Report(error); }
                    return true;
                });
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
}
