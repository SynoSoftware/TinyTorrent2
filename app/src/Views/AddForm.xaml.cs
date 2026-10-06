using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Syno.TinyTorrent.Controls;

namespace Syno.TinyTorrent.Views;

public sealed partial class AddForm : UserControl
{
    public MainViewModel Model { get; }
    private readonly FileBrowser _browser;
    private bool _maximized;
    // Null until the person chooses; until then the room decides.
    private bool? _settingsShown;
    private bool _hasFiles;
    public event EventHandler? DestinationRequested;
    public event EventHandler? CloseRequested;

    public AddForm(MainViewModel model)
    {
        Model = model;
        _hasFiles = model.Draft.HasFiles;
        InitializeComponent();
        _browser = new FileBrowser(model.Draft.Files);
        _browser.SetBinding(FileBrowser.ModelProperty, new Binding { Source = model.Draft, Path = new PropertyPath(nameof(AddDraft.Files)), Mode = BindingMode.OneWay });
        FileHost.Content = _browser;
        Split.ValueChanged += (_, value) =>
        {
            var share = value / Available;
            OptionsColumn.Width = new GridLength(1 - share, GridUnitType.Star);
            FilesColumn.Width = new GridLength(share, GridUnitType.Star);
        };
        Loaded += (_, _) =>
        {
            XamlRoot.Changed += OnRootChanged;
            Model.Draft.PropertyChanged += OnDraftChanged;
            Fit();
        };
        Unloaded += (_, _) =>
        {
            if (XamlRoot is { } root) root.Changed -= OnRootChanged;
            Model.Draft.PropertyChanged -= OnDraftChanged;
        };
        RefreshText();
    }

    // Uses the splitter's set width, not its actual width, so the result stays
    // right while the splitter is hidden.
    private double Available => Math.Max(0, Body.ActualWidth - Split.Width - Split.Margin.Left - Split.Margin.Right);
    private double SettingsMinimum => Math.Max(220, Available * 0.25);

    internal void RefreshText()
    {
        Bindings.Update();
        ToolTipService.SetToolTip(Close, Model.Text.Get("add", "close"));
        AutomationProperties.SetName(Close, Model.Text.Get("add", "close"));
        ShowResize();
        ShowSettings();
        _browser.RefreshText();
    }

    // ContentDialog cannot be resized by dragging, so the form chooses its own size
    // from the window: a comfortable default, or nearly the whole window.
    private void Fit()
    {
        var window = XamlRoot.Size;
        var room = window.Width - 96;
        var width = _maximized ? room : Math.Min(960, room);
        var height = _maximized ? window.Height - 176 : Math.Clamp(window.Height - 220, 320, 580);
        if (Width != width) Width = width;
        if (Height != height) Height = height;
        ShowResize();
    }

    private void ShowResize()
    {
        var text = Model.Text.Get("add", _maximized ? "restore" : "maximize");
        ResizeIcon.Glyph = _maximized ? Lucide.Minimize2 : Lucide.Maximize2;
        ToolTipService.SetToolTip(Resize, text);
        AutomationProperties.SetName(Resize, text);
    }

    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Fit();

    private void OnResize(object sender, RoutedEventArgs args)
    {
        _maximized = !_maximized;
        Fit();
    }

    private void OnBodySize(object sender, SizeChangedEventArgs args)
    {
        // The first layout runs before Fit sets the width.
        if (args.NewSize.Width == args.PreviousSize.Width || double.IsNaN(Width)) return;
        ShowSettings();
    }

    private void OnDraftChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (Model.Draft.HasFiles == _hasFiles) return;
        _hasFiles = Model.Draft.HasFiles;
        ShowSettings();
    }

    private void ShowSettings()
    {
        // Narrower than this, file names have no room beside the settings.
        var shown = _settingsShown ?? (!_hasFiles || Available - SettingsMinimum >= 480);
        Settings.Visibility = Split.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        // MaxWidth hides the column without losing its star share, so showing
        // the settings again restores the person's split.
        OptionsColumn.MinWidth = shown ? SettingsMinimum : 0;
        OptionsColumn.MaxWidth = shown ? double.PositiveInfinity : 0;
        var share = FilesColumn.Width.Value / (OptionsColumn.Width.Value + FilesColumn.Width.Value);
        Split.SetBounds(Available * 0.3, Available - SettingsMinimum, Available * share);
        var text = Model.Text.Get("add", shown ? "hide_settings" : "show_settings");
        SettingsIcon.Glyph = shown ? Lucide.PanelLeftClose : Lucide.PanelLeftOpen;
        ToolTipService.SetToolTip(SettingsButton, text);
        AutomationProperties.SetName(SettingsButton, text);
    }

    private void OnSettings(object sender, RoutedEventArgs args)
    {
        _settingsShown = Settings.Visibility != Visibility.Visible;
        ShowSettings();
    }

    // A ComboBox commits typed text only on Enter or focus loss, and the draft
    // rechecks a changed folder once a second, so a commit made by pressing Add
    // would disable Add before the click ends. Every keystroke therefore
    // reaches the draft.
    private void OnDestinationLoaded(object sender, RoutedEventArgs args)
    {
        Destination.Loaded -= OnDestinationLoaded;
        // An editable ComboBox draws Text set before its template only after it
        // takes focus, so the binding's first value is set again here.
        Destination.Text = string.Empty;
        Destination.Text = Model.Draft.Destination;
        if (Editor(Destination) is not { } editor) return;
        editor.TextChanged += (_, _) =>
        {
            if (Model.Draft.CanEdit) Model.Draft.Destination = editor.Text;
        };
    }

    private static TextBox? Editor(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is TextBox { Name: "EditableText" } box) return box;
            if (Editor(child) is { } found) return found;
        }
        return null;
    }

    public static Visibility Hidden(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public static GridLength PaneHeight(bool visible) => new(visible ? 1 : 0, GridUnitType.Star);
    public static Visibility Shown(string text) => text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    private void OnDestination(object sender, RoutedEventArgs args) => DestinationRequested?.Invoke(this, EventArgs.Empty);
    private void OnClose(object sender, RoutedEventArgs args) => CloseRequested?.Invoke(this, EventArgs.Empty);
    private async void OnPaste(object sender, RoutedEventArgs args)
    {
        if (!Model.Draft.CanEdit) return;
        try
        {
            var content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.Text))
            {
                var text = await content.GetTextAsync();
                if (IsLoaded && Model.Draft.CanEdit) Model.Draft.Magnet = text;
            }
        }
        catch (Exception error) { Model.Report(error); }
    }
    private async void OnPreview(object sender, RoutedEventArgs args)
    {
        await Model.Draft.PrepareMagnet();
        FocusMagnetError();
    }

    internal void FocusMagnetError()
    {
        if (Model.Draft.HasMagnetError) MagnetInput.Focus(FocusState.Programmatic);
    }
}
