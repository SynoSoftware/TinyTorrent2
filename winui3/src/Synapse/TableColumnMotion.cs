using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace Synapse;

/// <summary>
/// Section 19's reposition continuity for a column move. <see cref="TableCellsPanel"/> arranges its
/// cells at absolute x positions, and a custom panel's arrange carries no transition, so a move
/// would otherwise land every cell in its new place in one frame.
/// </summary>
/// <remarks>
/// Each affected cell is put back where it was rendered, as a composition translation, and animated
/// to zero. Nothing about the resolved layout is involved: the cells are already arranged where the
/// new layout says they belong, so an interrupted or overtaken animation ends in the correct place.
/// Section 19 also requires no substitute motion when the system disables animations, which is why
/// this does nothing at all in that case.
/// </remarks>
internal static class TableColumnMotion
{
    /// <summary>Below this a cell has not visibly moved and does not need animating.</summary>
    private const double MinimumMoveDips = 0.5;

    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(180);

    private static readonly UISettings SystemSettings = new();

    /// <summary>Where each visible column's cells are rendered now.</summary>
    internal static Dictionary<TableColumn, double> CaptureOffsets(ResolvedLayout layout)
    {
        Dictionary<TableColumn, double> offsets = new();

        if (!SystemSettings.AnimationsEnabled)
        {
            return offsets;
        }

        foreach (VisibleColumn visible in layout.VisibleColumns)
        {
            offsets[visible.Column.Column] = visible.Offset;
        }

        return offsets;
    }

    /// <summary>
    /// Slide every realized cell from the position <paramref name="before"/> recorded for its
    /// column into the position the new layout gives it.
    /// </summary>
    internal static void SlideFrom(TableView owner, Dictionary<TableColumn, double> before)
    {
        if (before.Count == 0)
        {
            return;
        }

        // The cells must already be arranged where they are going, so that the animation is the
        // only thing that has to move them back.
        owner.UpdateLayout();

        IReadOnlyList<VisibleColumn> visible = owner.Geometry.VisibleColumns;

        foreach (TableCellsPanel panel in owner.RealizedPanels())
        {
            // Every realized panel is already synced to this column set: a column-set change
            // reconciles every attached panel's cells synchronously, and TableCellsPanel's measure
            // and arrange throw if a panel's cell count and the visible count ever disagree. This
            // Min is defensive, not load-bearing — panel.Children.Count already equals
            // visible.Count here.
            int count = Math.Min(panel.Children.Count, visible.Count);

            for (int i = 0; i < count; i++)
            {
                if (!before.TryGetValue(visible[i].Column.Column, out double was))
                {
                    continue;
                }

                double distance = was - visible[i].Offset;
                if (Math.Abs(distance) >= MinimumMoveDips)
                {
                    Slide(panel.Children[i], distance);
                }
            }
        }
    }

    private static void Slide(UIElement cell, double distance)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(cell, true);
        Visual visual = ElementCompositionPreview.GetElementVisual(cell);
        visual.Properties.InsertVector3("Translation", new Vector3((float)distance, 0, 0));

        Compositor compositor = visual.Compositor;
        Vector3KeyFrameAnimation slide = compositor.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(
            1,
            Vector3.Zero,
            compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1)));
        slide.Duration = Duration;

        visual.StartAnimation("Translation", slide);
    }
}
