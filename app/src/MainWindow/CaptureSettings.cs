using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Helpers;
using Syno.TinyTorrent.Models;
using Windows.Graphics;

namespace Syno.TinyTorrent;

public sealed partial class MainWindow
{
    private async Task CaptureSettings(List<object> outcomes, List<string> completed)
    {
        foreach (var (language, theme, width, height) in new[]
        {
            ("en", "dark", 1280, 800),
            ("es", "light", 720, 560),
        })
        {
            Model.SelectLanguage(language);
            await CaptureReady(Model, () => Model.CanClose && Model.Text.Language == language);
            await Model.Settings.SelectTheme(theme);
            await CaptureReady(Model, () => Model.CanClose && Model.Theme == theme);
            var scale = Root.XamlRoot.RasterizationScale;
            var minimum = ((Microsoft.UI.Windowing.OverlappedPresenter)AppWindow.Presenter)
                .PreferredMinimumWidth ?? 0;
            AppWindow.Resize(new SizeInt32(Math.Max((int)(width * scale), minimum), (int)(height * scale)));
            await ShowSettings(new());
            var page = _settingsPage ?? throw new InvalidOperationException("Settings did not open.");
            var advanced = (ToggleSwitch)page.FindName("AdvancedSwitch");
            var prefix = $"settings-{language}-{theme}-{width}";
            foreach (var expanded in new[] { false, true })
            {
                advanced.IsOn = expanded;
                await ShowSettings(new());
                await CaptureReady(Model.Settings, () => !Model.Settings.IsPending);
                var state = expanded ? "advanced" : "standard";
                await CapturePage(prefix + "-index-" + state, page);
                foreach (var category in Enum.GetValues<SettingsCategory>())
                {
                    if (!expanded && category == SettingsCategory.Advanced)
                        continue;
                    await ShowSettings(new(category));
                    await CapturePage(prefix + "-" + category + "-" + state, page);
                }
            }

            await ShowSettings(new(SettingsCategory.Limits));
            await ShowConnection();
            if (SettingsContent.Content is not Views.ConnectionPage)
                throw new InvalidOperationException("Connection setup did not open. " + string.Join("; ",
                    Model.Settings.All.Where(setting => setting.HasDraft || setting.Message.Length > 0)
                        .Select(setting => setting.Name + ": " + setting.Message))
                    + " Schedule: " + Model.Settings.Schedule.ScheduleMessage);
            var connection = Model.Settings.Connection;
            await CapturePage(prefix + "-connection-empty", _connectionPage);
            connection.Download = "200";
            connection.Upload = "40";
            connection.PresetIndex = (int)TransferPreset.Balanced;
            await CapturePage(prefix + "-connection-balanced", _connectionPage);
            if (language == "en")
            {
                if (!await connection.Apply())
                    throw new InvalidOperationException("Connection proposal was not applied: " + connection.Message);
                await CaptureReady(connection, () => !connection.CanApply);
                await CapturePage(prefix + "-connection-applied", _connectionPage);
                if (Model.Settings.ActiveTotal.ConfirmedNumber != 8
                    || Model.Settings.Download.ConfirmedNumber != 21250000
                    || Model.Settings.Upload.ConfirmedNumber != 4250000)
                    throw new InvalidOperationException("Connection proposal did not reach confirmed settings.");
                outcomes.Add(new { journey = "connection settings submission", applied = true });
            }
            await ReturnToSettings();
            await CaptureLayout();
            var drafts = Model.Settings.All.Where(setting => setting.HasDraft).ToArray();
            if (drafts.Length > 0)
                throw new InvalidOperationException("Reading settings created drafts: " + string.Join("; ",
                    drafts.Select(setting => setting.Name + "=" + setting.Input + ": " + setting.Message)));

            await ShowSettings(new());
            advanced.IsOn = false;
            var search = (AutoSuggestBox)page.FindName("SettingsSearch");
            await CaptureLayout();
            var editor = TextEditor.Find(search)
                ?? throw new InvalidOperationException("Settings search has no native editor.");
            editor.Focus(FocusState.Keyboard);
            editor.Text = Model.Settings.RefreshInterval.Label;
            var matches = Model.FindSettings(editor.Text);
            if (matches.Count == 0)
                throw new InvalidOperationException("Settings search did not find an advanced setting.");
            search.ItemsSource = matches;
            search.IsSuggestionListOpen = true;
            await CaptureLayout();
            await CaptureUi(prefix + "-search");
            search.IsSuggestionListOpen = false;
            search.Text = string.Empty;
            completed.Add(prefix);
        }
        if (!await Model.Settings.PrepareLeave())
            throw new InvalidOperationException("Settings refused departure after the capture journey.");
    }
}
