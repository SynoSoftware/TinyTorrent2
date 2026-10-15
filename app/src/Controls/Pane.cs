using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Syno.TinyTorrent.Services;
using Windows.Foundation;

namespace Syno.TinyTorrent.Controls;

/// <summary>
/// Renders pane state and requests visibility or layout changes from its owner.
/// </summary>
/// <remarks>
/// Padding insets the expanded surface, leaving the compact strip at its dock edge.
/// </remarks>
public sealed partial class Pane : ContentControl
{
    private Grid? _heading;
    private ContentPresenter? _identity;
    private ContentPresenter? _navigation;
    private ContentPresenter? _actions;
    private Grid? _expanded;
    private Border? _separator;
    private Button? _collapse;
    private Button? _maximize;
    private Button? _close;
    private Button? _toggle;
    private Grid? _toggleClip;
    private FontIcon? _toggleGlyph;
    private FontIcon? _maximizeGlyph;
    private Strings? _text;
    private string? _section;

    public event EventHandler? CloseRequested;
    public event EventHandler<FocusState>? CollapseRequested;
    public event EventHandler<FocusState>? OpenRequested;
    public event EventHandler? MaximizeRequested;

    public static readonly DependencyProperty DockProperty = DependencyProperty.Register(
        nameof(Dock),
        typeof(Models.Dock),
        typeof(Pane),
        new PropertyMetadata(Models.Dock.None, OnChromeChanged)
    );

    public static readonly DependencyProperty CanCloseProperty = DependencyProperty.Register(
        nameof(CanClose),
        typeof(bool),
        typeof(Pane),
        new PropertyMetadata(false, OnChromeChanged)
    );

    public static readonly DependencyProperty CanMaximizeProperty = DependencyProperty.Register(
        nameof(CanMaximize),
        typeof(bool),
        typeof(Pane),
        new PropertyMetadata(false, OnChromeChanged)
    );

    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
        nameof(IsCompact),
        typeof(bool),
        typeof(Pane),
        new PropertyMetadata(false, OnChromeChanged)
    );

    public static readonly DependencyProperty IsMaximizedProperty = DependencyProperty.Register(
        nameof(IsMaximized),
        typeof(bool),
        typeof(Pane),
        new PropertyMetadata(false, OnChromeChanged)
    );

    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header),
        typeof(object),
        typeof(Pane),
        new PropertyMetadata(null, OnChromeChanged)
    );
    public static readonly DependencyProperty NavigationProperty = DependencyProperty.Register(
        nameof(Navigation),
        typeof(object),
        typeof(Pane),
        new PropertyMetadata(null, OnChromeChanged)
    );
    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(
        nameof(Actions),
        typeof(object),
        typeof(Pane),
        new PropertyMetadata(null, OnChromeChanged)
    );

    /// <summary>
    /// The compact strip's edge. None removes Pin and keeps the expanded presentation.
    /// </summary>
    public Models.Dock Dock
    {
        get => (Models.Dock)GetValue(DockProperty);
        set => SetValue(DockProperty, value);
    }

    public bool CanClose
    {
        get => (bool)GetValue(CanCloseProperty);
        set => SetValue(CanCloseProperty, value);
    }

    public bool CanMaximize
    {
        get => (bool)GetValue(CanMaximizeProperty);
        set => SetValue(CanMaximizeProperty, value);
    }

    public bool IsCompact
    {
        get => (bool)GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }

    public bool IsMaximized
    {
        get => (bool)GetValue(IsMaximizedProperty);
        set => SetValue(IsMaximizedProperty, value);
    }

    public object? Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public object? Navigation
    {
        get => GetValue(NavigationProperty);
        set => SetValue(NavigationProperty, value);
    }

    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

    internal double HeaderHeight => _heading?.ActualHeight ?? 0;

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (_collapse is not null)
            _collapse.Click -= OnCollapse;
        if (_maximize is not null)
            _maximize.Click -= OnMaximize;
        if (_close is not null)
            _close.Click -= OnClose;
        if (_toggle is not null)
            _toggle.Click -= OnOpen;
        if (_actions is not null)
            _actions.SizeChanged -= OnActionsSize;
        _heading = (Grid)GetTemplateChild("Heading");
        _identity = (ContentPresenter)GetTemplateChild("Identity");
        _navigation = (ContentPresenter)GetTemplateChild("Navigation");
        _actions = (ContentPresenter)GetTemplateChild("Actions");
        _expanded = (Grid)GetTemplateChild("Expanded");
        _separator = (Border)GetTemplateChild("ActionSeparator");
        _collapse = (Button)GetTemplateChild("Collapse");
        _maximize = (Button)GetTemplateChild("Maximize");
        _close = (Button)GetTemplateChild("Close");
        _toggle = (Button)GetTemplateChild("Toggle");
        _toggleClip = (Grid)GetTemplateChild("ToggleClip");
        _toggleGlyph = (FontIcon)GetTemplateChild("ToggleGlyph");
        _maximizeGlyph = (FontIcon)GetTemplateChild("MaximizeGlyph");
        ((FontIcon)_collapse.Content).Glyph = Lucide.Pin;
        ((FontIcon)_close.Content).Glyph = Lucide.X;
        _collapse.Click += OnCollapse;
        _maximize.Click += OnMaximize;
        _close.Click += OnClose;
        _toggle.Click += OnOpen;
        _actions.SizeChanged += OnActionsSize;
        var id = AutomationProperties.GetAutomationId(this);
        AutomationProperties.SetAutomationId(_collapse, id + "Minimize");
        AutomationProperties.SetAutomationId(_maximize, id + "Maximize");
        AutomationProperties.SetAutomationId(_close, id + "Close");
        AutomationProperties.SetAutomationId(_toggle, id + "Toggle");
        RefreshChrome();
    }

    internal void RefreshText(Strings text, string section)
    {
        _text = text;
        _section = section;
        RefreshChrome();
    }

    internal void FocusHeader(FocusState state)
    {
        ApplyTemplate();
        var button =
            Dock != Models.Dock.None ? _collapse
            : CanMaximize ? _maximize
            : CanClose ? _close
            : null;
        button?.Focus(state);
    }

    internal void FocusToggle(FocusState state)
    {
        ApplyTemplate();
        _toggle?.Focus(state);
    }

    private static FocusState FocusFrom(Button button) =>
        button.FocusState == FocusState.Keyboard ? FocusState.Keyboard : FocusState.Programmatic;

    private void OnCollapse(object sender, RoutedEventArgs args)
    {
        if (Dock != Models.Dock.None)
            CollapseRequested?.Invoke(this, FocusFrom((Button)sender));
    }

    private void OnOpen(object sender, RoutedEventArgs args)
    {
        if (Dock != Models.Dock.None)
            OpenRequested?.Invoke(this, FocusFrom((Button)sender));
    }

    private void OnMaximize(object sender, RoutedEventArgs args)
    {
        if (CanMaximize)
            MaximizeRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnClose(object sender, RoutedEventArgs args)
    {
        if (CanClose)
            CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private static void OnChromeChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args
    )
    {
        var pane = (Pane)sender;
        pane.RefreshChrome();
        pane.InvalidateMeasure();
    }

    private void OnActionsSize(object sender, SizeChangedEventArgs args) => RefreshSeparator();

    private void RefreshSeparator()
    {
        if (_separator is not null)
            _separator.Visibility =
                Actions is not null
                && _actions is { ActualWidth: > 0 }
                && (Dock != Models.Dock.None || CanMaximize || CanClose)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
    }

    private void RefreshChrome()
    {
        if (
            _expanded is null
            || _collapse is null
            || _maximize is null
            || _close is null
            || _toggle is null
            || _toggleClip is null
            || _toggleGlyph is null
            || _maximizeGlyph is null
        )
            return;
        var compact = IsCompact && Dock != Models.Dock.None;
        _expanded.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        _toggle.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        _collapse.Visibility = Dock == Models.Dock.None ? Visibility.Collapsed : Visibility.Visible;
        _maximize.Visibility = CanMaximize ? Visibility.Visible : Visibility.Collapsed;
        _close.Visibility = CanClose ? Visibility.Visible : Visibility.Collapsed;
        _maximizeGlyph.Glyph = IsMaximized ? Lucide.Minimize2 : Lucide.Maximize2;
        var vertical = Dock is Models.Dock.Left or Models.Dock.Right;
        _toggle.Width = vertical ? 8 : double.NaN;
        _toggle.Height = vertical ? double.NaN : 8;
        _toggle.HorizontalAlignment =
            Dock == Models.Dock.Right ? HorizontalAlignment.Right
            : vertical ? HorizontalAlignment.Left
            : HorizontalAlignment.Stretch;
        _toggle.VerticalAlignment =
            Dock == Models.Dock.Top ? VerticalAlignment.Top
            : vertical ? VerticalAlignment.Stretch
            : VerticalAlignment.Bottom;
        _toggle.Margin = Dock switch
        {
            Models.Dock.Left => new Thickness(0, 0, 2, 0),
            Models.Dock.Right => new Thickness(2, 0, 0, 0),
            Models.Dock.Top => new Thickness(0, 0, 0, 2),
            _ => new Thickness(0, 2, 0, 0),
        };
        _toggleClip.Width = vertical ? 8 : 16;
        _toggleClip.Height = vertical ? 16 : 8;
        _toggleClip.Clip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, _toggleClip.Width, _toggleClip.Height),
        };
        _toggleGlyph.Glyph = vertical ? Lucide.EllipsisVertical : Lucide.Ellipsis;
        if (_text is { } text && _section is { } section)
        {
            Name(_close, "close");
            Name(_collapse, "minimize");
            Name(_toggle, "expand");
            if (CanMaximize)
                Name(_maximize, IsMaximized ? "restore" : "maximize");

            void Name(Button button, string key)
            {
                var label = text.Get(section, key);
                AutomationProperties.SetName(button, label);
                ToolTipService.SetToolTip(button, label);
            }
        }
        RefreshSeparator();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_identity is not null && _navigation is not null && _actions is not null)
        {
            // The tabs and actions sit in Auto columns, so the identity only gets the width they leave.
            _identity.HorizontalAlignment = Navigation is null
                ? HorizontalAlignment.Stretch
                : HorizontalAlignment.Center;
            _identity.Margin = Navigation is null ? new Thickness(0) : new Thickness(12, 0, 0, 0);
            _actions.Margin = Actions is null ? new Thickness(0) : new Thickness(12, 0, 0, 0);
            // An empty presenter can fall back to the templated parent's body content.
            _navigation.Visibility = Navigation is null ? Visibility.Collapsed : Visibility.Visible;
            _actions.Visibility = Actions is null ? Visibility.Collapsed : Visibility.Visible;
        }
        return base.MeasureOverride(availableSize);
    }
}
