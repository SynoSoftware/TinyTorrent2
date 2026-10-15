using System.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Subtitles;
using Syno.TinyTorrent.Views;

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
                (error, key) => Model.Report(error, key, null, null),
                entries
            )
            {
                MinWidth = 480,
            };
            content.Attach(subtitles);
            content.SetVisible(true);
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
                content.SetVisible(false);
                content.Attach(null);
            }
            return true;
        });
    }

    // Opens the account of supplierId, or of the saved supplier. Saving makes
    // it the saved supplier, so a supplier and its account take effect together.
    private async Task ShowSupplier(SubtitleSupplier? supplierId = null)
    {
        if (
            Model.Subtitles is not { } subtitles
            || subtitles.GetSupplier(supplierId ?? subtitles.SupplierId) is not { } supplier
        )
            return;
        var name = supplier.SupplierId.ToString();
        var saved = subtitles.Edit(supplier.SupplierId);
        Draft Read(AccountInput input) =>
            saved with
            {
                Username = input.Username,
                Secret = input.Secret,
                KeepSecret = input.KeepSecret,
            };
        await ShowAccount(
            new AccountDialog(
                Model.Text,
                name,
                supplier.Access,
                supplier.HasBuiltInKey,
                new(saved.Username, string.Empty, saved.KeepSecret),
                async input =>
                {
                    await subtitles.Save(Read(input));
                    try
                    {
                        await Model.RetrySubtitles();
                    }
                    catch (Exception error)
                    {
                        Model.Report(error);
                    }
                },
                supplier.Available
                    ? async (input, cancellation) =>
                    {
                        try
                        {
                            await subtitles.Check(Read(input), cancellation);
                            if (!cancellation.IsCancellationRequested)
                                Model.ShowOutcome("subtitles", "connected", name);
                        }
                        catch (OperationCanceledException)
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"Subtitle account check cancelled ({supplier.SupplierId})."
                            );
                        }
                        catch (Exception error) when (!cancellation.IsCancellationRequested)
                        {
                            Model.Report(error, "failed_subtitle_check", () => name, null);
                        }
                    }
                    : null
            )
        );
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
                PrimaryGlyph = Lucide.Download,
                SecondaryGlyph = Lucide.Search,
            };
            void RefreshActions(object? sender, PropertyChangedEventArgs args)
            {
                dialog.PrimaryButtonText = content.HasResults
                    ? Model.Text.Get("subtitles", "download_choice")
                    : string.Empty;
                dialog.PrimaryToolTip = Model.Text.Get("subtitles", "choose_tip");
                dialog.SecondaryButtonText = content.CanSearch
                    ? Model.Text.Get("subtitles", "search_again")
                    : string.Empty;
                dialog.SecondaryToolTip = content.SearchHint;
                dialog.CloseButtonText = Model.Text.Get(
                    content.IsSearching ? "dialog" : "commands",
                    content.IsSearching ? "cancel" : "close"
                );
                dialog.DefaultButton =
                    content.HasResults ? ContentDialogButton.Primary
                    : content.CanSearch ? ContentDialogButton.Secondary
                    : ContentDialogButton.Close;
            }
            content.PropertyChanged += RefreshActions;
            dialog.SecondaryButtonClick += async (_, args) =>
            {
                args.Cancel = true;
                await content.Find(true);
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
                        content.RefreshText();
                    }
                );
            }
            finally
            {
                content.PropertyChanged -= RefreshActions;
                content.Cancel();
            }
            return true;
        });
    }
}
