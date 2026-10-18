using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Shared;

namespace Syno.TinyTorrent.Settings;

public sealed partial class Fact : UserControl
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(Fact), new PropertyMetadata(string.Empty)
    );
    public static readonly DependencyProperty ShowsTransfersProperty = DependencyProperty.Register(
        nameof(ShowsTransfers), typeof(bool), typeof(Fact), new PropertyMetadata(false)
    );
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(Fact),
        new PropertyMetadata(string.Empty)
    );
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(string), typeof(Fact),
        new PropertyMetadata(string.Empty)
    );
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(Strings), typeof(Fact), new PropertyMetadata(null)
    );
    public static readonly DependencyProperty DownloadProperty = DependencyProperty.Register(
        nameof(Download), typeof(string), typeof(Fact),
        new PropertyMetadata(string.Empty)
    );
    public static readonly DependencyProperty UploadProperty = DependencyProperty.Register(
        nameof(Upload), typeof(string), typeof(Fact),
        new PropertyMetadata(string.Empty)
    );
    public static readonly DependencyProperty TotalProperty = DependencyProperty.Register(
        nameof(Total), typeof(string), typeof(Fact), new PropertyMetadata(null)
    );

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }
    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }
    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }
    public bool ShowsTransfers
    {
        get => (bool)GetValue(ShowsTransfersProperty);
        set => SetValue(ShowsTransfersProperty, value);
    }
    public Strings? Text
    {
        get => (Strings?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }
    public string Download
    {
        get => (string)GetValue(DownloadProperty);
        set => SetValue(DownloadProperty, value);
    }
    public string Upload
    {
        get => (string)GetValue(UploadProperty);
        set => SetValue(UploadProperty, value);
    }
    public string? Total
    {
        get => (string?)GetValue(TotalProperty);
        set => SetValue(TotalProperty, value);
    }

    public Fact() => InitializeComponent();
}
