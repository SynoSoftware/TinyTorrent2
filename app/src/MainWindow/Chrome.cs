using Microsoft.UI;
using Microsoft.UI.Input;
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
        var left = Math.Max(0, AppWindow.TitleBar.LeftInset) / scale;
        var right = Math.Max(0, AppWindow.TitleBar.RightInset) / scale;
        LeftInset.Width = new GridLength(left);
        RightInset.Width = new GridLength(right);
        UpdateMinimum(scale);
        if (Caption.ActualHeight <= 0) return;
        var input = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        input.SetRegionRects(NonClientRegionKind.Passthrough,
            new[] { Menus, (FrameworkElement)Search, ThemeButton }.Select(GetRegion).ToArray());
        input.SetRegionRects(NonClientRegionKind.Icon, [GetRegion(AppIcon)]);
    }

    private RectInt32 GetRegion(FrameworkElement control)
    {
        var scale = Root.XamlRoot.RasterizationScale;
        var bounds = control.TransformToVisual(Root).TransformBounds(new Rect(0, 0, control.ActualWidth, control.ActualHeight));
        var left = (int)Math.Floor(bounds.Left * scale);
        var top = (int)Math.Floor(bounds.Top * scale);
        return new RectInt32(left, top, (int)Math.Ceiling(bounds.Right * scale) - left,
            (int)Math.Ceiling(bounds.Bottom * scale) - top);
    }

    private void UpdateMinimum(double scale)
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter) return;
        var content = 48 + Menus.ActualWidth + SearchArea.Margin.Left + Search.MinWidth +
            SearchArea.Margin.Right + ThemeButton.Width + ThemeButton.Margin.Right;
        var frame = AppWindow.Size.Width - AppWindow.ClientSize.Width;
        var width = (int)Math.Ceiling(Math.Max(720, content + LeftInset.Width.Value + RightInset.Width.Value) * scale) + frame;
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
        Model.IsDark = Root.ActualTheme == ElementTheme.Dark;
        ThemeIcon.Glyph = Model.IsDark ? Syno.Lucide.Sun : Syno.Lucide.Moon;
        NameButton(ThemeButton, Model.Text.Get("chrome", Model.IsDark ? "light" : "dark"));
    }

    private static void NameButton(Button button, string text)
    {
        AutomationProperties.SetName(button, text);
        ToolTipService.SetToolTip(button, text);
    }

    private void RefreshText()
    {
        Title = Model.Text.Get("window", "title");
        RefreshTheme();
        BackText.Text = Model.Text.Get("menus", "back");
        RefreshMenus();
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
