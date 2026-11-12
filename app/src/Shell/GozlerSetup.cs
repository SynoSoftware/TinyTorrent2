using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Network;

namespace Syno.TinyTorrent.Shell;

/// <summary>
/// The draft of gozler's install prompt, and the one download, check and run
/// of gozler's setup. TinyTorrent runs only the setup that its build pinned,
/// and runs it with its own wizard, because a silent setup lets nobody see
/// every file and that choice decides which folders Library can see.
/// </summary>
internal sealed class GozlerSetup(MainViewModel owner, ProviderHttp http) : IDraft
{
    // installer/Build.ps1 writes the pinned download beside the executables.
    private const string RecordName = "gozler.json";

    // ERROR_CANCELLED: the person declined Windows' permission prompt.
    private const int Declined = 1223;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private Pinned? _pinned;
    private InstallStage _stage = new InstallStage.Ready();
    private CancellationTokenSource? _work;

    public event PropertyChangedEventHandler? PropertyChanged;

    internal InstallStage Stage
    {
        get => _stage;
        private set
        {
            _stage = value;
            PropertyChanged?.Invoke(this, new(string.Empty));
        }
    }

    /// <summary>The setup's download size, or null when this build pinned none.</summary>
    internal long? Size => _pinned?.Size;

    public bool CanSubmit =>
        _pinned is not null && Stage is InstallStage.Ready or InstallStage.Failed;

    public bool HasDraft => false;

    // Cancel stops a download; once the setup runs, nothing stops it.
    public bool IsPending => Stage is InstallStage.Running;

    public ClosePolicy ClosePolicy => new ClosePolicy.Discard();

    /// <summary>Reads the pinned download as the prompt opens.</summary>
    internal void Begin()
    {
        _pinned = Read();
        Stage = _pinned is null
            ? new InstallStage.Failed(InstallFailure.Unavailable)
            : new InstallStage.Ready();
    }

    // The installation continues while the dialog stays open, so Cancel can
    // stop its download. The Installed stage closes the dialog.
    public Task<bool> Submit()
    {
        if (CanSubmit && _pinned is { } pinned)
            _ = Install(pinned);
        return Task.FromResult(false);
    }

    public Task Discard()
    {
        Stage = new InstallStage.Ready();
        return Task.CompletedTask;
    }

    public void Stop() => _work?.Cancel();

    private async Task Install(Pinned pinned)
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
            var folder = Path.Combine(Path.GetTempPath(), "TinyTorrent-gozler");
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (DirectoryNotFoundException) { }
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, Path.GetFileName(pinned.Url.LocalPath));
            await Release.Download(
                http,
                pinned.Url,
                path,
                ((IProgress<WorkProgress>)progress).Report,
                work.Token
            );
            if (!await Release.Matches(path, pinned.Sha256, work.Token))
            {
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
            // A cancelled wizard and a failed installation both leave no
            // service, while the setup's exit code says neither reliably.
            if (Gozler.Locate() == GozlerProgram.NotInstalled)
            {
                Stage = new InstallStage.Failed(InstallFailure.NotInstalled);
                return;
            }
            Stage = new InstallStage.Installed();
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

    private static Pinned? Read()
    {
        try
        {
            var pinned = JsonSerializer.Deserialize<Pinned>(
                File.ReadAllText(Path.Combine(AppContext.BaseDirectory, RecordName)),
                Options
            );
            return pinned is { Url.IsAbsoluteUri: true, Sha256.Length: 64, Size: > 0 }
                ? pinned
                : null;
        }
        catch (Exception error)
            when (error
                    is FileNotFoundException
                        or DirectoryNotFoundException
                        or IOException
                        or UnauthorizedAccessException
                        or JsonException
            )
        {
            Debug.WriteLine($"No pinned gozler download: {error.Message}");
            return null;
        }
    }

    // The download that installer/Build.ps1 pinned: its URL on gozler's
    // GitHub release, its SHA-256 and its size.
    private sealed record Pinned(Uri Url, string Sha256, long Size);
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
