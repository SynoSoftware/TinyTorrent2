using Microsoft.UI.Xaml.Controls;

namespace Syno.TableView;

/// <summary>
/// The table's icon button: the hierarchy expander and the header buttons. Its default style
/// lives in the table's theme, so the table never depends on a style the host defines.
/// </summary>
public sealed partial class SubtleButton : Button
{
    public SubtleButton() => DefaultStyleKey = typeof(SubtleButton);
}
