using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace Synapse_Sample;

/// <summary>
/// Resolves a theme resource key to the brush it currently names.
/// </summary>
/// <remarks>
/// The row publishes a key rather than a brush, because a brush read once is the wrong colour for
/// the rest of the session as soon as the user switches theme. This resolves the key each time a
/// cell binds, and the page asks the rows to re-announce their keys when the theme changes, which
/// is what makes the two halves add up to a colour that follows the theme.
/// </remarks>
internal sealed partial class ThemeBrush : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language) =>
        value is string key ? Application.Current.Resources[key] as Brush : null;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException("A brush is never turned back into a key.");
}
