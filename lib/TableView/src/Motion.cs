using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace Syno.TableView;

/// <summary>
/// Section 19's reposition continuity for a column move and a row reorder. <see cref="CellsPanel"/>
/// arranges its cells at absolute x positions, and a custom panel's arrange carries no transition,
/// so a move would otherwise land every cell in its new place in one frame. The list has no move
/// for a row, only a removal and an insertion, which its transitions show as a fade.
/// </summary>
/// <remarks>
/// Each affected cell or row is put back where it was rendered, as a composition translation, and
/// animated to zero. Nothing about the effective layout is involved: the cells are already
/// arranged where the new layout says they belong, so an interrupted or overtaken animation ends
/// in the correct place.
/// Section 19 also requires no substitute motion when the system disables animations, which is why
/// this does nothing at all in that case.
/// </remarks>
internal static class Motion
{
    /// <summary>Below this a cell has not visibly moved and does not need animating.</summary>
    internal const double MinimumMoveDips = 0.5;

    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(180);

    private static readonly UISettings SystemSettings = new();

    /// <summary>
    /// The easing curve's two control points, shared by the animation and <see
    /// cref="Remaining"/>.
    /// </summary>
    private static readonly Vector2 EaseFrom = new(0.1f, 0.9f), EaseTo = new(0.2f, 1);

    internal static bool IsEnabled => SystemSettings.AnimationsEnabled;

    /// <summary>Where each visible column's cells are rendered now.</summary>
    internal static Dictionary<Column, double> CaptureOffsets(EffectiveLayout layout)
    {
        Dictionary<Column, double> offsets = new();

        if (!IsEnabled)
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
    internal static void SlideFrom(Table owner, Dictionary<Column, double> before)
    {
        if (before.Count == 0)
        {
            return;
        }

        // The cells must already be arranged where they are going, so that the animation is the
        // only thing that has to move them back.
        owner.UpdateLayout();

        IReadOnlyList<VisibleColumn> visible = owner.EffectiveLayout.VisibleColumns;
        Compositor compositor = ElementCompositionPreview.GetElementVisual(owner).Compositor;
        Dictionary<double, Vector3KeyFrameAnimation> animations = new();

        foreach (CellsPanel panel in owner.RealizedPanels())
        {
            // Every realized panel is already synced to this column set: a column-set change
            // reconciles every attached panel's cells synchronously, and CellsPanel's measure
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
                    if (!animations.TryGetValue(distance, out Vector3KeyFrameAnimation? slide))
                    {
                        slide = CreateSlide(compositor);
                        animations.Add(distance, slide);
                    }
                    Slide(panel.Children[i], new Vector3((float)distance, 0, 0), slide);
                }
            }
        }
    }

    /// <summary>An animation that takes a translation from wherever it stands to zero.</summary>
    internal static Vector3KeyFrameAnimation CreateSlide(Compositor compositor)
    {
        Vector3KeyFrameAnimation slide = compositor.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(1, Vector3.Zero, compositor.CreateCubicBezierEasingFunction(EaseFrom, EaseTo));
        slide.Duration = Duration;
        return slide;
    }

    /// <summary>
    /// Show <paramref name="element"/> displaced by <paramref name="distance"/> and run
    /// <paramref name="slide"/> from there.
    /// </summary>
    internal static void Slide(UIElement element, Vector3 distance, Vector3KeyFrameAnimation slide)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        Visual visual = ElementCompositionPreview.GetElementVisual(element);
        visual.Properties.InsertVector3("Translation", distance);

        visual.StartAnimation("Translation", slide);
    }

    /// <summary>Put <paramref name="element"/> back in its layout place at once.</summary>
    internal static void Stop(UIElement element)
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation("Translation");
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
    }

    /// <summary>
    /// The part of a slide over <paramref name="distance"/> that is still to run
    /// <paramref name="elapsed"/> after it started: where the element is shown now, relative to
    /// its layout place. The compositor reports no animated value to the UI thread, so a slide
    /// interrupted by another is continued from this.
    /// </summary>
    internal static double Remaining(double distance, TimeSpan elapsed)
    {
        double progress = elapsed / Duration;
        if (progress >= 1)
        {
            return 0;
        }
        // The curve is x and y as cubic Béziers in one parameter; x is monotonic, so bisect it.
        double low = 0, high = 1;
        for (int step = 0; step < 24; step++)
        {
            double middle = (low + high) / 2;
            if (Bezier(EaseFrom.X, EaseTo.X, middle) < progress)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }
        return distance * (1 - Bezier(EaseFrom.Y, EaseTo.Y, (low + high) / 2));

        static double Bezier(double first, double second, double t) =>
            3 * (1 - t) * (1 - t) * t * first + 3 * (1 - t) * t * t * second + t * t * t;
    }
}
