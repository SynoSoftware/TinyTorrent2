using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Shared;

namespace Syno.TinyTorrent.Controls;

// A statistic value with a download amount and an upload amount, each in its
// direction's colour, and an optional neutral total. The colours are theme
// resources in XAML, so they follow a live theme change.
public sealed partial class TransferPair : UserControl
{
    public static readonly DependencyProperty DownloadProperty = DependencyProperty.Register(
        nameof(Download), typeof(string), typeof(TransferPair), new PropertyMetadata(string.Empty)
    );
    public static readonly DependencyProperty UploadProperty = DependencyProperty.Register(
        nameof(Upload), typeof(string), typeof(TransferPair), new PropertyMetadata(string.Empty)
    );
    public static readonly DependencyProperty TotalProperty = DependencyProperty.Register(
        nameof(Total), typeof(string), typeof(TransferPair), new PropertyMetadata(null)
    );
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(TransferPair), new PropertyMetadata(string.Empty)
    );
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(Strings), typeof(TransferPair), new PropertyMetadata(null)
    );

    public static readonly DependencyProperty ValueStyleProperty = DependencyProperty.Register(
        nameof(ValueStyle), typeof(Style), typeof(TransferPair), new PropertyMetadata(null)
    );

    public TransferPair()
    {
        ValueStyle = (Style)Application.Current.Resources["TinyTorrentStatisticValueStyle"];
        InitializeComponent();
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

    // What Narrator reads and the tooltip shows for the whole value.
    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }
    public Strings? Text
    {
        get => (Strings?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    // The statistic value style unless a Settings header fact sets its own.
    public Style ValueStyle
    {
        get => (Style)GetValue(ValueStyleProperty);
        set => SetValue(ValueStyleProperty, value);
    }

    public static string Amount(Strings? text, string direction, string amount) =>
        text?.Format("units", direction, amount) ?? string.Empty;

    public static string Separated(string? total) =>
        string.IsNullOrEmpty(total) ? string.Empty : " · " + total;
}
