using System.Diagnostics;
using Syno.TinyTorrent.Shared;

namespace Syno.TinyTorrent.Shell;

/// <summary>
/// The plugins that the person turned on or off in the installer's plugin
/// tree. The tree opens with what this app has, so Setup writes only the
/// boxes the person changed, one per line beside the executables: "+imdb"
/// turns a plugin on and "-imdb" turns it off, and every other plugin keeps
/// its setting. "+gozler" installs gozler without turning discovery on. The
/// app reads the file once, after Library, subtitles and the city data are
/// ready, and deletes it before it acts, so a choice never runs twice. Each
/// change does what its Settings control does.
/// </summary>
internal static class SetupChoices
{
    // installer/Setup.iss writes this file and these names.
    private const string FileName = "setup-choices.txt";
    private const string Discovery = "discovery";
    private const string Imdb = "imdb";
    private const string Cities = "cities";
    private const string Subtitles = "subtitles";
    private const string Video = "video";
    private const string GozlerAlone = "gozler";

    internal static async Task Apply(MainViewModel owner)
    {
        var path = Path.Combine(AppContext.BaseDirectory, FileName);
        string[] choices;
        try
        {
            choices = File.ReadAllLines(path);
            File.Delete(path);
        }
        catch (FileNotFoundException)
        {
            return;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"The installer's choices were not read: {error.Message}");
            return;
        }
        foreach (var choice in choices)
        {
            if (choice.Length > 1 && choice[0] is '+' or '-')
            {
                await Switch(owner, choice[1..], choice[0] == '+');
            }
        }
    }

    private static async Task Switch(MainViewModel owner, string plugin, bool on)
    {
        switch (plugin)
        {
            case Cities:
                await SwitchData(owner, owner.Geolocation.Download, on);
                break;
            case Imdb:
                await SwitchData(owner, owner.Library.Download, on);
                break;
            case Subtitles when owner.Subtitles is { } subtitles:
                try
                {
                    await subtitles.SetEnabled(on);
                }
                catch (Exception error)
                {
                    owner.Notifications.Report(error, "failed_settings", null, null);
                }
                break;
            case Video when owner.Library.ChosenProvider is { } provider:
                try
                {
                    await (
                        on
                            ? owner.Library.EnableAutomaticDownload(provider.ProviderId)
                            : owner.Library.DisableAutomaticDownload()
                    );
                }
                catch (Exception error)
                {
                    owner.Notifications.Report(error, "failed_settings", null, null);
                }
                break;
            case Discovery:
                await owner.SwitchDiscovery(on);
                break;
            case GozlerAlone when on && Gozler.Locate() == GozlerProgram.NotInstalled:
                owner.RequestGozler(discover: false);
                break;
        }
    }

    // The installer only adds data that is missing, because it is not the
    // place to update; Settings updates it.
    private static async Task SwitchData(MainViewModel owner, DataDownload? download, bool on)
    {
        if (download is null)
        {
            return;
        }
        if (!on)
        {
            await download.Delete();
        }
        else if (download is { Installed: null, CanStart: true })
        {
            _ = owner.DownloadData(download);
        }
    }
}
