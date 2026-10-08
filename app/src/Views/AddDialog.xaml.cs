using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Syno.TinyTorrent.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace Syno.TinyTorrent.Views;

public sealed partial class AddDialog : UserControl, IDisposable
{
    public MainViewModel Model { get; }
    private readonly XamlRoot _root;
    private readonly FileBrowser _browser;
    private bool _maximized;

    // Null until the person chooses; until then the room decides. Files that
    // arrive later do not change that, because the column would move under the person.
    private bool? _choicesShown;
    private readonly bool _hasFiles;
    public event EventHandler? DestinationRequested;
    public event EventHandler? CloseRequested;

    public AddDialog(MainViewModel model, XamlRoot root)
    {
        Model = model;
        _root = root;
        _hasFiles = model.AddDraft.HasFiles;
        InitializeComponent();
        // Resetting recent folders during localisation clears the editable
        // ComboBox's display and focus, so they stay fixed while the dialog is open.
        Destination.ItemsSource = model.AddDraft.Folders;
        _browser = new FileBrowser(model.AddDraft.Files);
        _browser.SetBinding(
            FileBrowser.ModelProperty,
            new Binding
            {
                Source = model.AddDraft,
                Path = new PropertyPath(nameof(AddDraft.Files)),
                Mode = BindingMode.OneWay,
            }
        );
        FileHost.Content = _browser;
        Split.ValueChanged += (_, value) =>
        {
            var share = value / Available;
            ChoicesColumn.Width = new GridLength(1 - share, GridUnitType.Star);
            FilesColumn.Width = new GridLength(share, GridUnitType.Star);
        };
        // ContentDialog may unload its content while the dialog is still open.
        _root.Changed += OnRootChanged;
        Fit();
        RefreshText();
    }

    public void Dispose()
    {
        _root.Changed -= OnRootChanged;
    }

    // Uses the splitter's set width, not its actual width, so the result stays
    // right while the splitter is hidden.
    private double Available =>
        Math.Max(0, Body.ActualWidth - Split.Width - Split.Margin.Left - Split.Margin.Right);
    private double ChoicesMinimum => Math.Max(220, Available * 0.25);

    internal void RefreshText()
    {
        Bindings.Update();
        ToolTipService.SetToolTip(Close, Model.Text.Get("add", "close"));
        AutomationProperties.SetName(Close, Model.Text.Get("add", "close"));
        ShowResize();
        ShowChoices();
        _browser.RefreshText();
    }

    // ContentDialog cannot be resized by dragging, so the dialog chooses its own size
    // from the window: a comfortable default, or nearly the whole window.
    private void Fit()
    {
        var window = _root.Size;
        var room = window.Width - 96;
        var width = _maximized ? room : Math.Min(960, room);
        var height = _maximized ? window.Height - 176 : Math.Clamp(window.Height - 220, 320, 580);
        if (Width != width)
            Width = width;
        if (Height != height)
            Height = height;
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
        if (args.NewSize.Width == args.PreviousSize.Width || double.IsNaN(Width))
            return;
        ShowChoices();
    }

    private void ShowChoices()
    {
        // Narrower than this, file names have no room beside the folder and options.
        var shown = _choicesShown ?? (!_hasFiles || Available - ChoicesMinimum >= 480);
        Choices.Visibility = Split.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        // MaxWidth hides the column without losing its star share, so showing
        // the folder and options again restores the person's split.
        ChoicesColumn.MinWidth = shown ? ChoicesMinimum : 0;
        ChoicesColumn.MaxWidth = shown ? double.PositiveInfinity : 0;
        var share = FilesColumn.Width.Value / (ChoicesColumn.Width.Value + FilesColumn.Width.Value);
        Split.SetBounds(Available * 0.3, Available - ChoicesMinimum, Available * share);
        var text = Model.Text.Get("add", shown ? "hide_choices" : "show_choices");
        ChoicesIcon.Glyph = shown ? Lucide.PanelLeftClose : Lucide.PanelLeftOpen;
        ToolTipService.SetToolTip(ChoicesButton, text);
        AutomationProperties.SetName(ChoicesButton, text);
    }

    private void OnChoices(object sender, RoutedEventArgs args)
    {
        _choicesShown = Choices.Visibility != Visibility.Visible;
        ShowChoices();
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
        Destination.Text = Model.AddDraft.Destination;
        if (Editor(Destination) is not { } editor)
            return;
        editor.TextChanged += (_, _) =>
        {
            if (Model.AddDraft.CanEdit)
                Model.AddDraft.Destination = editor.Text;
        };
    }

    private static TextBox? Editor(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is TextBox { Name: "EditableText" } box)
                return box;
            if (Editor(child) is { } found)
                return found;
        }
        return null;
    }

    public static Visibility Hidden(bool value) =>
        value ? Visibility.Collapsed : Visibility.Visible;

    public static GridLength PaneHeight(bool visible) => new(visible ? 1 : 0, GridUnitType.Star);

    private void OnDestination(object sender, RoutedEventArgs args) =>
        DestinationRequested?.Invoke(this, EventArgs.Empty);

    private void OnClose(object sender, RoutedEventArgs args) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);

    private async void OnPaste(object sender, RoutedEventArgs args)
    {
        if (!Model.AddDraft.CanEdit)
            return;
        try
        {
            var content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.Text))
            {
                var text = await content.GetTextAsync();
                if (IsLoaded && Model.AddDraft.CanEdit)
                    Model.AddDraft.Magnet = text;
            }
        }
        catch (Exception error)
        {
            Model.Report(error);
        }
    }

    private async void OnPreview(object sender, RoutedEventArgs args)
    {
        await Model.AddDraft.PrepareMagnet();
        FocusError();
    }

    internal void FocusError()
    {
        if (Model.AddDraft.HasMagnetError)
            MagnetInput.Focus(FocusState.Programmatic);
        else if (Model.AddDraft.HasDestinationError)
        {
            _choicesShown = true;
            ShowChoices();
            UpdateLayout();
            Destination.StartBringIntoView();
            if (Editor(Destination) is { } editor)
                editor.Focus(FocusState.Programmatic);
            else
                Destination.Focus(FocusState.Programmatic);
        }
    }
}
