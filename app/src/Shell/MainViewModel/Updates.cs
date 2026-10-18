using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Input;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Network;

namespace Syno.TinyTorrent.Shell;

public sealed partial class MainViewModel
{
    private const string LatestRelease =
        "https://api.github.com/repos/SynoSoftware/TinyTorrent2/releases/latest";
    private ReleaseCheck? _releaseCheck;
    private string? _updatesPath;
    private Task? _updateCheck;
    private Task? _installation;
    private CancellationTokenSource? _updateRequest;

    public bool HasUpdate =>
        Settings.Updates.IsOn && _releaseCheck?.Version is { } version && version > App.Version;
    public string UpdateText => Text.Get("about", "update");
    public ICommand InstallUpdate { get; }
    private bool CanInstallUpdate => HasUpdate && _installation is not { IsCompleted: false };
    private bool CanCheckUpdates =>
        !_closed
        && !_featuresClosed
        && _connected
        && !_loading
        && !_storageFailed
        && _providerRoute is not null
        && Settings.Updates.IsOn;

    private void ObserveUpdates()
    {
        if (!CanCheckUpdates)
        {
            _updateRequest?.Cancel();
            return;
        }
        if (_applyingSettings)
            return;
        if (_updateCheck is { IsCompleted: false })
            return;
        _updateCheck = CheckUpdates();
    }

    private async Task CheckUpdates()
    {
        if (_client.DataDirectory is not { } directory)
            return;
        if (_updatesPath is null)
        {
            _updatesPath = Path.Combine(directory, "updates.json");
            try
            {
                var input = await File.ReadAllTextAsync(_updatesPath);
                _releaseCheck = JsonSerializer.Deserialize<ReleaseCheck>(input);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (Exception error)
                when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                Debug.WriteLine($"Update check cache could not be read: {error.Message}");
            }
            Changed(nameof(HasUpdate));
            ((RelayCommand)InstallUpdate).Refresh();
        }
        // A check saved in the future, after the clock was wrong, counts as old.
        if (
            !CanCheckUpdates
            || _releaseCheck is { } previous
                && previous.CheckedAt <= DateTimeOffset.UtcNow
                && DateTimeOffset.UtcNow - previous.CheckedAt < TimeSpan.FromDays(1)
        )
            return;
        _releaseCheck = new(DateTimeOffset.UtcNow, _releaseCheck?.Version);
        await SaveCheck();
        if (!CanCheckUpdates)
            return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        _updateRequest = cancellation;
        var observation = new WorkObservation(
            new WorkDescription.Update(UpdatePhase.Checking),
            new WorkProgress.Unknown(),
            new WorkExecution.Running()
        );
        BackgroundWork.Operation? operation = null;
        var signalling = Task.CompletedTask;
        var cancelled = false;
        operation = Work.Begin(
            observation,
            () =>
            {
                if (ReferenceEquals(_updateRequest, cancellation))
                {
                    cancelled = true;
                    operation?.Report(
                        observation with
                        {
                            Execution = new WorkExecution.Cancelling(),
                        }
                    );
                    signalling = cancellation.CancelAsync();
                }
                return signalling;
            }
        );
        try
        {
            using var release = await FetchRelease(cancellation.Token);
            if (release is null)
                return;
            if (!CanCheckUpdates)
            {
                operation.Outcome = WorkOutcome.Cancelled;
                return;
            }
            if (!System.Version.TryParse(Tag(release.RootElement), out var version))
                return;
            version = new Version(
                version.Major,
                version.Minor,
                Math.Max(0, version.Build),
                Math.Max(0, version.Revision)
            );
            _releaseCheck = new(_releaseCheck.CheckedAt, version);
            Changed(nameof(HasUpdate));
            ((RelayCommand)InstallUpdate).Refresh();
            await SaveCheck();
            operation.Summarize(HasUpdate ? "update_available" : "up_to_date", version);
            // The check runs by itself, so only a new version is news.
            operation.ReportsSuccess = HasUpdate;
            operation.Outcome = WorkOutcome.Completed;
        }
        catch (OperationCanceledException)
        {
            operation.Outcome = cancelled ? WorkOutcome.Cancelled : WorkOutcome.Failed;
            Debug.WriteLine("Update check cancelled.");
        }
        catch (Exception error)
            when (error
                    is HttpRequestException
                        or JsonException
                        or IOException
                        or InvalidOperationException
            )
        {
            operation.Error = error;
            Debug.WriteLine($"Update check did not complete: {error.Message}");
        }
        finally
        {
            _updateRequest = null;
            try
            {
                await signalling;
            }
            finally
            {
                operation.Dispose();
            }
        }
    }

    private async Task StartInstall()
    {
        _installation = Install();
        ((RelayCommand)InstallUpdate).Refresh();
        try
        {
            await _installation;
        }
        finally
        {
            ((RelayCommand)InstallUpdate).Refresh();
        }
    }

    // Downloads the latest release's installer, checks it and runs it. Setup
    // exits TinyTorrent through its coordinated Exit, replaces the program and
    // opens the window again, so this returns only when Setup fails.
    private async Task Install()
    {
        using var cancellation = new CancellationTokenSource();
        var observation = new WorkObservation(
            new WorkDescription.Update(UpdatePhase.Downloading),
            new WorkProgress.Unknown(),
            new WorkExecution.Running()
        );
        BackgroundWork.Operation? operation = null;
        var signalling = Task.CompletedTask;
        operation = Work.Begin(
            observation,
            () =>
            {
                operation?.Report(
                    observation with
                    {
                        Execution = new WorkExecution.Cancelling(),
                    }
                );
                signalling = cancellation.CancelAsync();
                return signalling;
            }
        );
        try
        {
            using var release =
                await FetchRelease(cancellation.Token)
                ?? throw new UpdateException(UpdateFailure.Missing);
            // installer/Build.ps1 gives the installer this name.
            var name = $"TinyTorrent-{Tag(release.RootElement)}-x64-Setup.exe";
            var installer =
                Asset(release.RootElement, name)
                ?? throw new UpdateException(UpdateFailure.Missing);
            var folder = Path.Combine(Path.GetTempPath(), "TinyTorrent-update");
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (DirectoryNotFoundException) { }
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, name);
            using (
                var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    await Locate(installer, cancellation.Token)
                )
            )
                await _providerHttp.Download(
                    request,
                    path,
                    progress =>
                        operation.Report(observation = observation with { Progress = progress }),
                    cancellation.Token
                );
            await CheckInstaller(release.RootElement, name, path, cancellation.Token);
            operation.Report(
                observation = new WorkObservation(
                    new WorkDescription.Update(UpdatePhase.Installing),
                    new WorkProgress.Unknown(),
                    new WorkExecution.Running(Cancellable: false)
                )
            );
            // OPEN tells Setup to open the window again, not only the engine.
            using var setup =
                Process.Start(
                    new ProcessStartInfo(path, "/SILENT /SUPPRESSMSGBOXES /NORESTART /OPEN=1")
                    {
                        UseShellExecute = false,
                    }
                ) ?? throw new UpdateException(UpdateFailure.Setup);
            await setup.WaitForExitAsync(CancellationToken.None);
            if (setup.ExitCode != 0)
            {
                Debug.WriteLine($"Update setup exited with code {setup.ExitCode}.");
                throw new UpdateException(UpdateFailure.Setup);
            }
            operation.ReportsSuccess = false;
            operation.Outcome = WorkOutcome.Completed;
        }
        catch (OperationCanceledException)
        {
            operation.Outcome = cancellation.IsCancellationRequested
                ? WorkOutcome.Cancelled
                : WorkOutcome.Failed;
        }
        catch (Exception error)
            when (error
                    is UpdateException
                        or HttpRequestException
                        or JsonException
                        or IOException
                        or UnauthorizedAccessException
                        or InvalidDataException
                        or InvalidOperationException
                        or Win32Exception
            )
        {
            operation.Error = error;
            Debug.WriteLine($"Update did not install: {error.Message}");
        }
        finally
        {
            try
            {
                await signalling;
            }
            finally
            {
                operation.Dispose();
            }
        }
    }

    // A TinyTorrent with a trusted signature runs only an installer with a
    // trusted signature from the same publisher, so an unsigned file never
    // replaces a signed installation. Any other TinyTorrent compares the
    // installer with the checksum that the release publishes beside it.
    private async Task CheckInstaller(
        JsonElement release,
        string name,
        string path,
        CancellationToken cancellation
    )
    {
        if (
            Environment.ProcessPath is { } program
            && Authenticode.TrustedPublisher(program) is { } publisher
        )
        {
            if (Authenticode.TrustedPublisher(path) != publisher)
                throw new UpdateException(UpdateFailure.Signature);
            return;
        }
        var checksum =
            Asset(release, name + ".sha256") ?? throw new UpdateException(UpdateFailure.Missing);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            await Locate(checksum, cancellation)
        );
        var reply = await _providerHttp.Send(request, ProviderHttp.JsonLimit, cancellation);
        if (reply.Status != HttpStatusCode.OK)
            throw new HttpRequestException("The checksum was refused.", null, reply.Status);
        var expected = Encoding.ASCII.GetString(reply.Bytes).Split(' ', 2)[0].Trim();
        await using var file = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellation));
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            throw new UpdateException(UpdateFailure.Checksum);
    }

    // The latest release as GitHub describes it, or null when GitHub answers
    // without one.
    private async Task<JsonDocument?> FetchRelease(CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestRelease);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        var reply = await _providerHttp.Send(request, ProviderHttp.JsonLimit, cancellation);
        return reply.Status == HttpStatusCode.OK ? JsonDocument.Parse(reply.Bytes) : null;
    }

    // The release's version as its tag spells it, without the leading v.
    private static string? Tag(JsonElement release) =>
        release.TryGetProperty("tag_name", out var tag) && tag.ValueKind == JsonValueKind.String
            ? tag.GetString()?.TrimStart('v', 'V')
            : null;

    private static Uri? Asset(JsonElement release, string name)
    {
        if (
            !release.TryGetProperty("assets", out var assets)
            || assets.ValueKind != JsonValueKind.Array
        )
            return null;
        foreach (var asset in assets.EnumerateArray())
            if (
                asset.TryGetProperty("name", out var assetName)
                && assetName.ValueKind == JsonValueKind.String
                && assetName.GetString() == name
                && asset.TryGetProperty("browser_download_url", out var url)
                && url.ValueKind == JsonValueKind.String
                && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var location)
            )
                return location;
        return null;
    }

    // GitHub answers a release download with a redirect to the file's
    // storage location, which serves the file.
    private async Task<Uri> Locate(Uri asset, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, asset);
        var reply = await _providerHttp.Send(request, ProviderHttp.JsonLimit, cancellation);
        if (
            reply.Status
                is HttpStatusCode.Found
                    or HttpStatusCode.MovedPermanently
                    or HttpStatusCode.TemporaryRedirect
                    or HttpStatusCode.PermanentRedirect
            && Uri.TryCreate(reply.Header("Location"), UriKind.Absolute, out var location)
        )
            return location;
        throw new HttpRequestException("The release download did not redirect.", null, reply.Status);
    }

    private async Task SaveCheck()
    {
        if (_updatesPath is null)
            return;
        try
        {
            var temporary = _updatesPath + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(_releaseCheck));
            File.Move(temporary, _updatesPath, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Update check cache could not be saved: {error.Message}");
        }
    }

    private sealed record ReleaseCheck(DateTimeOffset CheckedAt, Version? Version);
}

internal sealed class UpdateException(UpdateFailure reason) : Exception(reason.ToString())
{
    internal UpdateFailure Reason { get; } = reason;
}
