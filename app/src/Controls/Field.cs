using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Syno.TinyTorrent.Controls;

// A value led by its icon and label. InspectorForm.xaml supplies the templates.
public sealed partial class Field : ContentControl
{
    static Field() => AppDomain.CurrentDomain.FirstChanceException += (_, args) => System.IO.File.AppendAllText(
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TinyTorrent", "first-chance.log"),
        $"{DateTime.Now:HH:mm:ss.fff} {args.Exception.GetType()}: {args.Exception.Message}\n{Environment.StackTrace}\n\n");

    public static readonly DependencyProperty GlyphProperty = Text(nameof(Glyph));
    public static readonly DependencyProperty LabelProperty = Text(nameof(Label));
    public static readonly DependencyProperty ActionProperty =
        DependencyProperty.Register(nameof(Action), typeof(object), typeof(Field), new PropertyMetadata(null));

    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    // A button that follows the value and acts on it.
    public object? Action { get => GetValue(ActionProperty); set => SetValue(ActionProperty, value); }

    private static DependencyProperty Text(string name) =>
        DependencyProperty.Register(name, typeof(string), typeof(Field), new PropertyMetadata(string.Empty));
}
