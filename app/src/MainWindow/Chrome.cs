using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.Graphics;

namespace Syno.TinyTorrent;

public sealed partial class MainWindow
{
    private void UpdateChrome()
    {
        if (Root.XamlRoot is null) return;
        var scale = Root.XamlRoot.RasterizationScale;
        UpdateMinimum(scale);
        var left = AppWindow.TitleBar.LeftInset / scale;
        var right = AppWindow.TitleBar.RightInset / scale;
        LeftInset.Width = new GridLength(left);
        RightInset.Width = new GridLength(right > 0 ? right : 138);
        if (Caption.ActualHeight <= 0 || CaptionActions.ActualWidth <= 0) return;
        CaptionText.MaxWidth = Math.Max(0, Caption.ActualWidth - left - RightInset.Width.Value -
            CaptionActions.ActualWidth - CaptionLogo.ActualWidth - CaptionLogo.Margin.Left -
            CaptionLogo.Margin.Right - CaptionText.Margin.Left - CaptionText.Margin.Right);
        var bounds = CaptionActions.TransformToVisual(Caption)
            .TransformBounds(new Rect(0, 0, CaptionActions.ActualWidth, CaptionActions.ActualHeight));
        var width = Math.Max(0, bounds.Left - left);
        AppWindow.TitleBar.SetDragRectangles([new RectInt32((int)Math.Round(left * scale), 0,
            (int)Math.Round(width * scale), (int)Math.Round(Caption.ActualHeight * scale))]);
    }

    private void UpdateMinimum(double scale)
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter) return;
        var width = (int)Math.Ceiling(720 * scale);
        var height = (int)Math.Ceiling(560 * scale);
        if (presenter.PreferredMinimumWidth != width) presenter.PreferredMinimumWidth = width;
        if (presenter.PreferredMinimumHeight != height) presenter.PreferredMinimumHeight = height;
    }

    private void UpdateColors()
    {
        var titleBar = AppWindow.TitleBar;
        titleBar.PreferredTheme = Root.ActualTheme == ElementTheme.Dark ? TitleBarTheme.Dark : TitleBarTheme.Light;
        titleBar.BackgroundColor = Colors.Transparent;
        titleBar.InactiveBackgroundColor = Colors.Transparent;
        titleBar.ForegroundColor = null;
        titleBar.InactiveForegroundColor = null;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = null;
        titleBar.ButtonInactiveForegroundColor = null;
        titleBar.ButtonHoverBackgroundColor = null;
        titleBar.ButtonHoverForegroundColor = null;
        titleBar.ButtonPressedBackgroundColor = null;
        titleBar.ButtonPressedForegroundColor = null;
        RefreshTheme();
    }

    private void RefreshTheme()
    {
        var dark = Root.ActualTheme == ElementTheme.Dark;
        Model.IsDark = dark;
        _themeIcon.Glyph = dark ? Syno.Lucide.Sun : Syno.Lucide.Moon;
        NameButton(ThemeButton, Model.Text.Get("chrome", dark ? "light" : "dark"));
    }

    private static void NameButton(Button button, string text)
    {
        AutomationProperties.SetName(button, text);
        ToolTipService.SetToolTip(button, text);
    }

    private void RefreshText()
    {
        Title = Model.Text.Get("window", "title");
        CaptionText.Text = Title;
        NameButton(AddButton, Model.Text.Get("window", "add"));
        NameButton(PauseButton, Model.Text.Get("window", "pause"));
        NameButton(ResumeButton, Model.Text.Get("window", "resume"));
        NameButton(ExitButton, Model.Text.Get("window", "exit"));
        NameButton(LanguageButton, Model.Text.Get("chrome", Model.Text.Language == "es" ? "english" : "spanish"));
        LanguageButton.Content = new TextBlock { Text = Model.Text.Language.ToUpperInvariant(), FontSize = 12 };
        RefreshTheme();
        AutomationProperties.SetName(Torrents, Model.Text.Get("window", "torrents"));
        NameColumn.DisplayName = Model.Text.Get("columns", "name");
        SizeColumn.DisplayName = Model.Text.Get("columns", "size");
        ProgressColumn.DisplayName = Model.Text.Get("columns", "progress");
        StatusColumn.DisplayName = Model.Text.Get("columns", "status");
        DownColumn.DisplayName = Model.Text.Get("columns", "down");
        UpColumn.DisplayName = Model.Text.Get("columns", "up");
        EmptyTitle.Text = Model.Text.Get("window", "empty_title");
        EmptyInstruction.Text = Model.Text.Get("window", "empty");
        EmptyAdd.Content = Model.Text.Get("window", "add");
        Torrents.Strings = Model.Text.Table;
        Root.FlowDirection = Model.Text.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        RefreshDialogs();
        _source.Header = Model.Text.Get("add", "source");
        _destination.Header = Model.Text.Get("add", "destination");
        _paused.Content = Model.Text.Get("add", "paused");
        if (_sourceButton is not null)
        {
            _sourceButton.Content = Model.Text.Get("add", "browse");
            AutomationProperties.SetName(_sourceButton, Model.Text.Get("add", "source"));
        }
        if (_destinationButton is not null)
        {
            _destinationButton.Content = Model.Text.Get("add", "browse");
            AutomationProperties.SetName(_destinationButton, Model.Text.Get("add", "destination"));
        }
        if (_addDialog is not null)
        {
            _addDialog.Title = Model.Text.Get("add", "title");
            _addDialog.PrimaryButtonText = Model.Text.Get("add", Model.IsBusy ? "pending" : "submit");
            _addDialog.CloseButtonText = Model.Text.Get("add", "cancel");
        }
        if (_closePrompt is not null)
        {
            _closePrompt.Title = Model.Text.Get("add", "discard");
            _closePrompt.Content = Model.Text.Get("add", "discard_detail");
            _closePrompt.PrimaryButtonText = Model.Text.Get("add", "discard_action");
            _closePrompt.CloseButtonText = Model.Text.Get("add", "keep");
        }
        Torrents.RefreshView();
    }

    private void RefreshDialogs()
    {
        ContentDialog?[] dialogs = [_addDialog, _closePrompt];
        foreach (var dialog in dialogs)
        {
            if (dialog is null) continue;
            dialog.RequestedTheme = Root.ActualTheme;
            dialog.FlowDirection = Root.FlowDirection;
        }
    }

}
