using System.ComponentModel;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace Syno.TinyTorrent.Settings;

public sealed partial class ThemePicker : UserControl
{
    private bool _refreshing;
    public Editor Model { get; }
    internal RadioButtons Choices => Theme;

    internal ThemePicker(Editor model)
    {
        Model = model;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Model.Theme.PropertyChanged += OnModel;
            Model.TextChanged += OnText;
            Bindings.Update();
            Refresh();
            SizeTiles();
        };
        Unloaded += (_, _) =>
        {
            Model.Theme.PropertyChanged -= OnModel;
            Model.TextChanged -= OnText;
        };
    }

    private void OnModel(object? sender, PropertyChangedEventArgs args) => Refresh();

    private void OnText(object? sender, EventArgs args) => Bindings.Update();

    private void Refresh()
    {
        var index = Model.Theme.Input switch
        {
            "light" => 1,
            "dark" => 2,
            _ => 0,
        };
        if (Theme.SelectedIndex == index)
            return;
        _refreshing = true;
        Theme.SelectedIndex = index;
        _refreshing = false;
    }

    private async void OnTheme(object sender, SelectionChangedEventArgs args)
    {
        if (!_refreshing && Theme.SelectedItem is RadioButton { Tag: string theme })
            await Model.SelectTheme(theme);
    }

    private void OnTilesSize(object sender, SizeChangedEventArgs args) => SizeTiles();

    private void SizeTiles()
    {
        var width = Math.Max(0, (Theme.ActualWidth - 16) / 3);
        foreach (var tile in new[] { SystemTheme, LightTheme, DarkTheme })
            tile.Width = width;
    }

    private void OnPreviewSize(object sender, SizeChangedEventArgs args)
    {
        var preview = (FrameworkElement)sender;
        preview.Height = preview.ActualWidth * 3 / 7;
        var visual = ElementCompositionPreview.GetElementVisual(preview);
        if (
            visual.Clip
            is not CompositionGeometricClip
            {
                Geometry: CompositionRoundedRectangleGeometry geometry
            }
        )
        {
            geometry = visual.Compositor.CreateRoundedRectangleGeometry();
            geometry.CornerRadius = new Vector2(8);
            visual.Clip = visual.Compositor.CreateGeometricClip(geometry);
        }
        geometry.Size = new Vector2((float)preview.ActualWidth, (float)preview.ActualHeight);
    }
}
