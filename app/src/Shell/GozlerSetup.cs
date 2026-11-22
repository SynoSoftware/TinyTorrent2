using System.ComponentModel;
using System.Diagnostics;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Network;

namespace Syno.TinyTorrent.Shell;

/// <summary>
/// The draft of gozler's install prompt, and the one download, check and run
/// of gozler's setup. Only when the person selects Install, TinyTorrent asks
/// GitHub for the gozler release that it names, and runs only that release's
/// setup, checked against the SHA-256 that GitHub gives it, because gozler's
/// releases are unsigned. The setup runs with its own wizard, because a
/// silent setup lets nobody see every file and that choice decides which
/// folders Library can see.
/// </summary>
internal sealed class GozlerSetup(MainViewModel owner, ProviderHttp http) : IDraft
{
    // ERROR_CANCELLED: the person declined Windows' permission prompt.
    private const int Declined = 1223;

    private InstallStage _stage = new InstallStage.Ready();
    private CancellationTokenSource? _work;

    public event PropertyChangedEventHandler? PropertyChanged;

    // Whether discovery switches on after the installation; each request sets it.
    internal bool Discovers { get; set; } = true;

    internal InstallStage Stage
    {
        get => _stage;
        private set
        {
            _stage = value;
            PropertyChanged?.Invoke(this, new(string.Empty));
        }
    }

    public bool CanSubmit => Stage is InstallStage.Ready or InstallStage.Failed;

    public bool HasDraft => false;

    // Cancel stops a download; once the setup runs, nothing stops it.
    public bool IsPending => Stage is InstallStage.Running;

    public ClosePolicy ClosePolicy => new ClosePolicy.Discard();

    // The installation continues while the dialog stays open, so Cancel can
    // stop its download. The Installed stage closes the dialog.
    public Task<bool> Submit()
    {
        if (CanSubmit)
            _ = Install();
        return Task.FromResult(false);
    }

    public Task Discard()
    {
        Stage = new InstallStage.Ready();
        return Task.CompletedTask;
    }

    public void Stop() => _work?.Cancel();

    private async Task Install()
    {
        using var work = new CancellationTokenSource();
        _work = work;
        // Created on the UI thread, so the download's progress arrives there.
        var progress = new Progress<WorkProgress>(received =>
        {
            if (!work.IsCancellationRequested && Stage is InstallStage.Downloading)
                Stage = new InstallStage.Downloading(received);
        });
        try
        {
            Stage = new InstallStage.Downloading(new WorkProgress.Unknown());
            if (await Find(work.Token) is not { } published)
            {
                Stage = new InstallStage.Failed(InstallFailure.NotReleased);
                return;
            }
            var folder = Path.Combine(Path.GetTempPath(), "TinyTorrent-gozler");
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (DirectoryNotFoundException) { }
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, Path.GetFileName(published.Url.LocalPath));
            await Release.Download(
                http,
                published.Url,
                path,
                ((IProgress<WorkProgress>)progress).Report,
                work.Token
            );
            // Open without write or delete sharing from the check until the
            // setup ends, so the program that Windows runs as administrator
            // is the one checked.
            using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!await Release.Matches(path, published.Sha256, work.Token))
            {
                held.Dispose();
                File.Delete(path);
                Stage = new InstallStage.Failed(InstallFailure.Damaged);
                return;
            }
            work.Token.ThrowIfCancellationRequested();
            Stage = new InstallStage.Running();
            if (!await Run(path, work.Token))
            {
                Stage = new InstallStage.Failed(InstallFailure.Declined);
                return;
            }
            // A cancelled wizard and a failed installation leave no service,
            // or keep the older gozler of an update, while the setup's exit
            // code says none of these reliably.
            if (Gozler.Locate() == GozlerProgram.NotInstalled)
            {
                Stage = new InstallStage.Failed(InstallFailure.NotInstalled);
                return;
            }
            // Starting the program takes a moment, which the UI thread must not wait for.
            if (!await Task.Run(() => Gozler.KnowsOptions(work.Token)))
            {
                Stage = new InstallStage.Failed(InstallFailure.Older);
                return;
            }
            Stage = new InstallStage.Installed();
            if (Discovers)
                await owner.SwitchDiscovery(true);
        }
        catch (Exception error) when (work.IsCancellationRequested)
        {
            Debug.WriteLine($"gozler's installation stopped with its dialog: {error.Message}");
        }
        catch (Exception error)
        {
            Debug.WriteLine($"gozler was not installed: {error}");
            Stage = new InstallStage.Failed(
                Stage is InstallStage.Running ? InstallFailure.NotInstalled : InstallFailure.Download,
                owner.Text.Error(error)
            );
        }
        finally
        {
            if (ReferenceEquals(_work, work))
                _work = null;
        }
    }

    // Started through the Windows shell, as a double-click starts it, so
    // Windows asks for the administrator permission that gozler's setup
    // requires. False when the person declined it.
    private static async Task<bool> Run(string path, CancellationToken cancellation)
    {
        Process? setup;
        try
        {
            // Windows waits for the permission answer before it returns.
            setup = await Task.Run(
                () => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }),
                cancellation
            );
        }
        catch (Win32Exception error) when (error.NativeErrorCode == Declined)
        {
            return false;
        }
        using (setup)
        {
            if (setup is not null)
                await setup.WaitForExitAsync(cancellation);
        }
        return true;
    }

    // The setup of gozler's SetupRelease, or null while GitHub has no such
    // release or its setup, because gozler's release workflow publishes the
    // release before it uploads the setup.
    private async Task<Setup?> Find(CancellationToken cancellation)
    {
        var version = Gozler.SetupRelease.ToString(3);
        using var release = await Release.Tagged(http, Gozler.Repository, "v" + version, cancellation);
        if (release is null)
        {
            return null;
        }
        // gozler's release build gives its setup this name.
        var name = $"gozler-{version}-x64-Setup.exe";
        var url = Release.Asset(release.RootElement, name);
        var sha256 = Release.Digest(release.RootElement, name);
        if (url is null || sha256 is null)
        {
            return null;
        }
        return new Setup(url, sha256);
    }

    private sealed record Setup(Uri Url, string Sha256);
}

/// <summary>Where gozler's installation stands, which its prompt shows.</summary>
internal abstract record InstallStage
{
    private InstallStage() { }

    internal sealed record Ready : InstallStage;

    internal sealed record Downloading(WorkProgress Progress) : InstallStage;

    internal sealed record Running : InstallStage;

    internal sealed record Failed(InstallFailure Reason, string? Detail = null) : InstallStage;

    internal sealed record Installed : InstallStage;
}
