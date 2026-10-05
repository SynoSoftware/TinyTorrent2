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
        var torrents = Model.Page == WindowPage.Torrents;
        var full = Caption.ActualWidth >= 1120 && torrents;
        foreach (var button in new[] { MagnetButton, PauseButton, ResumeButton })
            button.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in new[] { AddButton, OverflowButton })
            button.Visibility = torrents ? Visibility.Visible : Visibility.Collapsed;
        PageCaption.Visibility = torrents ? Visibility.Collapsed : Visibility.Visible;
        HomeButton.Visibility = Caption.ActualWidth >= 880 ? Visibility.Visible : Visibility.Collapsed;
        FilterCaption.Visibility = Caption.ActualWidth >= 1000 ? Visibility.Visible : Visibility.Collapsed;
        var exclusions = new FrameworkElement[] { HomeButton, FilterButton, Search, CaptionActions }
            .Where(control => control.Visibility == Visibility.Visible && control.ActualWidth > 0)
            .Select(control => control.TransformToVisual(Caption).TransformBounds(new Rect(0, 0, control.ActualWidth, control.ActualHeight)))
            .OrderBy(bounds => bounds.Left);
        var origin = Caption.TransformToVisual(null).TransformPoint(default).X;
        var rectangles = new List<RectInt32>();
        var start = left;
        foreach (var bounds in exclusions)
        {
            if (bounds.Left > start) rectangles.Add(new RectInt32((int)Math.Ceiling((origin + start) * scale), 0,
                (int)Math.Floor((bounds.Left - start) * scale), (int)Math.Round(Caption.ActualHeight * scale)));
            start = Math.Max(start, bounds.Right);
        }
        var end = Caption.ActualWidth - RightInset.Width.Value;
        if (end > start) rectangles.Add(new RectInt32((int)Math.Ceiling((origin + start) * scale), 0,
            (int)Math.Floor((end - start) * scale), (int)Math.Round(Caption.ActualHeight * scale)));
        AppWindow.TitleBar.SetDragRectangles(rectangles.ToArray());
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
        TorrentsPage.Content = Model.Text.Get("window", "torrents");
        SettingsPage.Content = Model.Text.Get("finding", "settings");
        AboutPage.Content = Model.Text.Get("about", "title");
        ExitItem.Content = Model.Text.Get("window", "exit");
        foreach (var page in new[] { TorrentsPage, SettingsPage, AboutPage, ExitItem })
            AutomationProperties.SetName(page, (string)page.Content);
        AutomationProperties.SetName(Navigation, Model.Text.Get("commands", "menu"));
        UpdateNavigation();
        NameButton(MagnetButton, Model.Text.Get("commands", "add_magnet"));
        NameButton(OverflowButton, Model.Text.Get("commands", "selection"));
        NameButton(PauseButton, Model.Text.Get("window", "pause"));
        NameButton(ResumeButton, Model.Text.Get("window", "resume"));
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
        QueueColumn.DisplayName = Model.Text.Get("columns", "queue");
        EtaColumn.DisplayName = Model.Text.Get("columns", "eta");
        RatioColumn.DisplayName = Model.Text.Get("columns", "ratio");
        PeersColumn.DisplayName = Model.Text.Get("columns", "peers");
        AddedColumn.DisplayName = Model.Text.Get("columns", "added");
        Search.PlaceholderText = Model.Text.Get("finding", "placeholder");
        AutomationProperties.SetName(Search, Model.Text.Get("finding", "search"));
        Search.ItemsSource = Model.FindSuggestions(Search.Text);
        FiltersTitle.Text = Model.Text.Get("filters", "title");
        AutomationProperties.SetName(Filters, Model.Text.Get("filters", "title"));
        AutomationProperties.SetName(FilterButton, Model.FilterLabel);
        ToolTipService.SetToolTip(FilterButton, Model.FilterLabel);
        NameButton(HomeButton, Model.Text.Get("window", "torrents"));
        NameButton(FiltersClose, Model.Text.Get("filters", "close"));
        AutomationProperties.SetName(Split, Model.Text.Get("inspector", "resize"));
        AlternativeText.Text = Model.Text.Get("window", "alternative");
        InspectorClose.Content = Model.Text.Get("inspector", "close");
        Torrents.Strings = Model.Text.Table;
        Root.FlowDirection = Model.Text.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        RefreshDialogs();
        _form?.RefreshText();
        _filesForm?.RefreshText();
        if (_filesDialog is { } files)
        {
            files.Title = Model.Files.Title;
            files.PrimaryButtonText = Model.Files.Title;
            files.CloseButtonText = Model.Text.Get("add", "cancel");
        }
        if (_limitsDialog?.Content is StackPanel limits)
        {
            _limitsDialog.Title = Model.Text.Get("commands", "limits");
            _limitsDialog.PrimaryButtonText = Model.Text.Get("limits", "apply");
            _limitsDialog.CloseButtonText = Model.Text.Get("add", "cancel");
            foreach (var editor in limits.Children.OfType<NumberBox>())
                editor.Header = Model.Text.Get("limits", ((LimitChoice)editor.DataContext).Name);
            foreach (var label in limits.Children.OfType<TextBlock>()) label.Text = Model.Text.Get("limits", "units");
        }
        if (_removeDialog is { } removal)
        {
            removal.Title = Model.Text.Get("remove", "title");
            removal.PrimaryButtonText = Model.Text.Get("commands", "remove");
            removal.CloseButtonText = Model.Text.Get("add", "cancel");
            if (removal.Tag is Torrent[] torrents)
                removal.Content = Model.Text.Format("remove", "detail", string.Join(Environment.NewLine, torrents.Select(torrent => torrent.Name)));
        }
        if (_addDialog is not null)
        {
            _addDialog.Title = Model.Text.Get("add", "title");
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
        ContentDialog?[] dialogs = [_addDialog, _closePrompt, _limitsDialog, _removeDialog, _filesDialog];
        foreach (var dialog in dialogs)
        {
            if (dialog is null) continue;
            dialog.RequestedTheme = Root.ActualTheme;
            dialog.FlowDirection = Root.FlowDirection;
        }
    }

}
