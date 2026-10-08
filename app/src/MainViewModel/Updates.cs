using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Input;

namespace Syno.TinyTorrent;

public sealed partial class MainViewModel
{
    private const string ReleasePage =
        "https://github.com/SynoSoftware/TinyTorrent2/releases/latest";
    private static readonly Version RunningVersion =
        typeof(MainViewModel).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);
    private ReleaseCheck? _releaseCheck;
    private string? _updatesPath;
    private Task? _updateCheck;
    private CancellationTokenSource? _updateRequest;

    public bool HasUpdate =>
        Settings.Updates.IsOn && _releaseCheck?.Version is { } version && version > RunningVersion;
    public string UpdateText => Text.Get("about", "update");
    public ICommand OpenUpdate { get; }

    private void ObserveUpdates()
    {
        if (_closed || !Settings.Updates.IsOn)
        {
            _updateRequest?.Cancel();
            return;
        }
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
            _closed
            || !Settings.Updates.IsOn
            || _releaseCheck is { } previous
                && previous.CheckedAt <= DateTimeOffset.UtcNow
                && DateTimeOffset.UtcNow - previous.CheckedAt < TimeSpan.FromDays(1)
        )
            return;
        _releaseCheck = new(DateTimeOffset.UtcNow, _releaseCheck?.Version);
        await SaveCheck();
        if (_closed || !Settings.Updates.IsOn)
            return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        _updateRequest = cancellation;
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("TinyTorrent/" + RunningVersion);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
            using var stream = await http.GetStreamAsync(
                "https://api.github.com/repos/SynoSoftware/TinyTorrent2/releases/latest",
                cancellation.Token
            );
            using var release = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellation.Token
            );
            if (_closed || !Settings.Updates.IsOn)
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
            when (error is HttpRequestException or OperationCanceledException or JsonException)
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
