using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace Syno.TinyTorrent.Helpers;

internal sealed class Motion
{
    private readonly UISettings _settings = new();
    private readonly Storyboard _storyboard = new();

    internal void Play(FrameworkElement content, double offset = 0)
    {
        Stop();
        if (!content.IsLoaded || content.Visibility != Visibility.Visible || !_settings.AnimationsEnabled)
            return;

        var fade = new FadeInThemeAnimation();
        Storyboard.SetTarget(fade, content);
        _storyboard.Children.Add(fade);
        if (offset != 0)
        {
            var move = new RepositionThemeAnimation { FromVerticalOffset = offset };
            Storyboard.SetTarget(move, content);
            _storyboard.Children.Add(move);
        }
        _storyboard.Begin();
    }

    internal void Stop()
    {
        _storyboard.Stop();
        _storyboard.Children.Clear();
    }
}
