using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Input;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent;

public sealed partial class MainViewModel
{
    private const string ReleasePage =
        "https://github.com/SynoSoftware/TinyTorrent2/releases/latest";
    private ReleaseCheck? _releaseCheck;
    private string? _updatesPath;
    private Task? _updateCheck;
    private CancellationTokenSource? _updateRequest;

    public bool HasUpdate =>
        Settings.Updates.IsOn && _releaseCheck?.Version is { } version && version > App.Version;
    public string UpdateText => Text.Get("about", "update");
    public ICommand OpenUpdate { get; }
    private bool CanCheckUpdates =>
        !_closed && !_featuresClosed && _connected && !_loading && !_storageFailed
        && _providerRoute is not null && Settings.Updates.IsOn;

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
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://api.github.com/repos/SynoSoftware/TinyTorrent2/releases/latest");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
            var reply = await _providerHttp.Send(request, ProviderHttp.JsonLimit, cancellation.Token);
            if (reply.Status != HttpStatusCode.OK)
                return;
            using var stream = new MemoryStream(reply.Bytes, writable: false);
            using var release = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellation.Token
            );
            if (!CanCheckUpdates)
                return;
            if (
                !release.RootElement.TryGetProperty("tag_name", out var tag)
                || tag.ValueKind != JsonValueKind.String
            )
                return;
            if (!Version.TryParse(tag.GetString()?.TrimStart('v', 'V'), out var version))
                return;
            version = new Version(
                version.Major,
                version.Minor,
                Math.Max(0, version.Build),
                Math.Max(0, version.Revision)
            );
            _releaseCheck = new(_releaseCheck.CheckedAt, version);
            Changed(nameof(HasUpdate));
            ((RelayCommand)OpenUpdate).Refresh();
            await SaveCheck();
        }
        catch (Exception error)
            when (error is HttpRequestException or OperationCanceledException or JsonException or IOException or InvalidOperationException)
        {
            Debug.WriteLine($"Update check did not complete: {error.Message}");
        }
        finally
        {
            _updateRequest = null;
        }
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
