using Microsoft.UI.Xaml;

namespace Syno.TinyTorrent.Controls;

public static class Progress
{
    public static Style SelectStyle(bool muted) =>
        (Style)
            Application.Current.Resources[
                muted ? "TinyTorrentMutedProgressStyle" : "TinyTorrentProgressStyle"
            ];
}
