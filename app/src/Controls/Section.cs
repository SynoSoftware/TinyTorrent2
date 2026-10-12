using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Syno.TinyTorrent.Controls;

public sealed partial class Section : ContentControl
{
    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header),
        typeof(string),
        typeof(Section),
        new PropertyMetadata(string.Empty)
    );
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph),
        typeof(string),
        typeof(Section),
        new PropertyMetadata(string.Empty, OnChanged)
    );
    public static readonly DependencyProperty DetailProperty = DependencyProperty.Register(
        nameof(Detail),
        typeof(object),
        typeof(Section),
        new PropertyMetadata(null, OnChanged)
    );
    public static readonly DependencyProperty HeaderStyleProperty = DependencyProperty.Register(
        nameof(HeaderStyle),
        typeof(Style),
        typeof(Section),
        new PropertyMetadata(null)
    );
    public static readonly DependencyProperty HeaderDetailProperty = DependencyProperty.Register(
        nameof(HeaderDetail),
        typeof(object),
        typeof(Section),
        new PropertyMetadata(null, OnChanged)
    );
    public static readonly DependencyProperty ActionProperty = DependencyProperty.Register(
        nameof(Action),
        typeof(object),
        typeof(Section),
        new PropertyMetadata(null)
    );

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public object? Detail
    {
        get => GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }

    public Style? HeaderStyle
    {
        get => (Style?)GetValue(HeaderStyleProperty);
        set => SetValue(HeaderStyleProperty, value);
    }

    public object? HeaderDetail
    {
        get => GetValue(HeaderDetailProperty);
        set => SetValue(HeaderDetailProperty, value);
    }

    // A button at the header row's right end, at the far right of a
    // full-width section rather than at the edge of one of its columns.
    public object? Action
    {
        get => GetValue(ActionProperty);
        set => SetValue(ActionProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        Refresh();
    }

    private static void OnChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args
    ) => ((Section)sender).Refresh();

    private void Refresh()
    {
        if (GetTemplateChild("Icon") is FontIcon icon)
            icon.Visibility = Glyph.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (GetTemplateChild("DetailHost") is ContentControl detail)
            detail.Visibility = Detail is null ? Visibility.Collapsed : Visibility.Visible;
        if (GetTemplateChild("HeaderDetailHost") is ContentControl headerDetail)
            headerDetail.Visibility = HeaderDetail is null
                ? Visibility.Collapsed
                : Visibility.Visible;
    }
}
