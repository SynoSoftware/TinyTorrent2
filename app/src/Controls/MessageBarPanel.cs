using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Syno.TinyTorrent.Controls;

// Lays out a message bar's title, message, actions and close button, in that
// order, as WinUI's InfoBar and the Fluent 2 message bar do: on one line while
// they fit, otherwise the title, the message and the actions each on their own
// line, with the actions at the bottom right. The spacing is WinUI's InfoBar
// spacing.
public sealed partial class MessageBarPanel : Panel
{
    private const double TextTop = 14;
    private const double MessageGap = 12;
    private const double MessageTop = 4;
    private const double ActionGap = 16;
    private const double ActionTop = 8;
    private const double ActionRowTop = 12;
    private const double Bottom = 16;

    private bool _stacked;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children is not [var title, var message, var action, var close])
        {
            return default;
        }
        var unlimited = new Size(double.PositiveInfinity, double.PositiveInfinity);
        foreach (var child in Children)
        {
            child.Measure(unlimited);
        }
        var hasAction = action.DesiredSize.Width > 0;
        var line =
            title.DesiredSize.Width
            + MessageGap
            + message.DesiredSize.Width
            + (hasAction ? ActionGap + action.DesiredSize.Width : 0)
            + ActionGap
            + close.DesiredSize.Width;
        _stacked = line > availableSize.Width;
        if (!_stacked)
        {
            var height = Math.Max(
                TextTop + Math.Max(title.DesiredSize.Height, message.DesiredSize.Height),
                Math.Max(
                    hasAction ? ActionTop + action.DesiredSize.Height : 0,
                    close.DesiredSize.Height
                )
            );
            return new Size(line, height);
        }
        // The close button stays at the top right, beside the title and the
        // message; the action row below them has the full width.
        var text = new Size(
            Math.Max(0, availableSize.Width - close.DesiredSize.Width),
            double.PositiveInfinity
        );
        title.Measure(text);
        message.Measure(text);
        var stacked = Math.Max(
            close.DesiredSize.Height,
            TextTop + title.DesiredSize.Height + MessageTop + message.DesiredSize.Height
        );
        if (hasAction)
        {
            stacked += ActionRowTop + action.DesiredSize.Height;
        }
        return new Size(availableSize.Width, stacked + Bottom);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children is not [var title, var message, var action, var close])
        {
            return finalSize;
        }
        close.Arrange(
            new Rect(
                finalSize.Width - close.DesiredSize.Width,
                0,
                close.DesiredSize.Width,
                close.DesiredSize.Height
            )
        );
        var actionLeft = finalSize.Width - ActionGap - action.DesiredSize.Width;
        if (!_stacked)
        {
            title.Arrange(new Rect(new Point(0, TextTop), title.DesiredSize));
            message.Arrange(
                new Rect(
                    new Point(title.DesiredSize.Width + MessageGap, TextTop),
                    message.DesiredSize
                )
            );
            action.Arrange(
                new Rect(
                    new Point(actionLeft - close.DesiredSize.Width, ActionTop),
                    action.DesiredSize
                )
            );
            return finalSize;
        }
        title.Arrange(new Rect(new Point(0, TextTop), title.DesiredSize));
        message.Arrange(
            new Rect(
                new Point(0, TextTop + title.DesiredSize.Height + MessageTop),
                message.DesiredSize
            )
        );
        action.Arrange(
            new Rect(
                new Point(actionLeft, finalSize.Height - Bottom - action.DesiredSize.Height),
                action.DesiredSize
            )
        );
        return finalSize;
    }
}
