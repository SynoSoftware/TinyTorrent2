param(
    [string] $BinaryDirectory = "$PSScriptRoot/../../artifacts/bin/TinyTorrent/debug_win-x64"
)

$ErrorActionPreference = 'Stop'
$binary = (Resolve-Path -LiteralPath $BinaryDirectory).Path
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $binary 'TinyTorrentUI.dll'))
$flags = [Reflection.BindingFlags]'Instance,Public,NonPublic'

Add-Type -TypeDefinition @'
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

public sealed class UpdateReplies : HttpMessageHandler
{
    public int Requests;
    public HttpStatusCode Status = HttpStatusCode.OK;
    public string Tag = "v999.0.0.0";

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellation)
    {
        Interlocked.Increment(ref Requests);
        return Task.FromResult(new HttpResponseMessage(Status)
        {
            Content = new StringContent("{\"tag_name\":\"" + Tag + "\"}")
        });
    }
}
'@

function Require([bool] $Condition, [string] $Failure) {
    if (!$Condition) { throw $Failure }
}

function New-Value([string] $Name, [object[]] $Arguments = @()) {
    $type = $assembly.GetType("Syno.TinyTorrent.$Name", $true)
    [Activator]::CreateInstance($type, $flags, $null, $Arguments, $null)
}

function Field($Target, [string] $Name) {
    ,$Target.GetType().GetField($Name, $flags).GetValue($Target)
}

function Set-Field($Target, [string] $Name, $Value) {
    $Target.GetType().GetField($Name, $flags).SetValue($Target, $Value)
}

function Property($Target, [string] $Name) {
    $Target.GetType().GetProperty($Name, $flags).GetValue($Target)
}

function Observe {
    $updates.GetType().GetMethod('Observe', $flags).Invoke($updates, @($true)) | Out-Null
    $pending = Field $updates '_check'
    if ($pending) { $pending.GetAwaiter().GetResult() | Out-Null }
}

function Check {
    $command = Property $updates 'Check'
    Require ($command.CanExecute($null)) 'A manual check was disabled.'
    $command.Execute($null)
    (Field $updates '_check').GetAwaiter().GetResult() | Out-Null
    Require ($command.CanExecute($null)) 'A finished check prevented an immediate retry.'
}

$directory = Join-Path ([IO.Path]::GetTempPath()) "TinyTorrentUpdateChecks-$([Guid]::NewGuid())"
[IO.Directory]::CreateDirectory($directory) | Out-Null
$strings = New-Value 'Shared.Strings'
$setting = [Runtime.CompilerServices.RuntimeHelpers]::GetUninitializedObject(
    $assembly.GetType('Syno.TinyTorrent.Settings.Setting', $true))
Set-Field $setting '_choice' $false
$client = New-Value 'Shared.Engine.PipeClient' @($strings)
Set-Field $client '_dataDirectory' $directory
$http = New-Value 'Shared.Network.ProviderHttp'
$replies = [UpdateReplies]::new()
Set-Field $http '_route' (New-Value 'Shared.Network.HttpRoute')
Set-Field $http '_lifetime' ([Threading.CancellationTokenSource]::new())
Set-Field $http '_client' ([Net.Http.HttpClient]::new($replies))
$work = New-Value 'Shared.BackgroundWork' @($null)
# Keep results queued for inspection without starting a WinUI dispatcher.
Set-Field $work '_queued' $true
$results = Field $work '_completed'
$updates = New-Value 'Shell.Updates' @($http, $work, $client, $setting)
$owner = [Runtime.CompilerServices.RuntimeHelpers]::GetUninitializedObject(
    $assembly.GetType('Syno.TinyTorrent.Shell.MainViewModel', $true))
Set-Field $owner '<Updates>k__BackingField' $updates
$notifications = New-Value 'Shell.Notifications' @($owner, $strings)

try {
    $cache = Join-Path $directory 'updates.json'
    [IO.File]::WriteAllText($cache, (@{
        CheckedAt = [DateTimeOffset]::UtcNow.ToString('o')
        Version = $assembly.GetName().Version.ToString()
    } | ConvertTo-Json))
    Observe
    Require ($replies.Requests -eq 0) 'Disabling automatic checks still contacted the server.'

    Check
    Require ($replies.Requests -eq 1 -and (Property $updates 'HasUpdate')) 'A manual check with automatic checks off used the fresh cache or hid the newer release.'
    Require ($results.Count -eq 1 -and $results[0].SummaryKey -eq 'update_available') 'A manual check did not report the available update.'
    $notifications.GetType().GetMethod('ShowWorkResult', $flags).Invoke($notifications, @($results[0])) | Out-Null
    $toast = $notifications.Toasts[0]
    Require ([object]::ReferenceEquals($toast.Action, (Property $updates 'Install')) -and $toast.Action.CanExecute($null)) 'The update notification did not offer the authoritative install command.'
    Require ($toast.ActionText.Length -gt 0 -and $replies.Requests -eq 1) 'Discovery started downloading before the person chose Update now.'

    Set-Field $setting '_choice' $true
    Observe
    Require ($replies.Requests -eq 1) 'An automatic check ignored the daily cache.'
    Set-Field $setting '_choice' $false
    $results.Clear()
    $replies.Status = [Net.HttpStatusCode]::ServiceUnavailable
    Check
    Require ($replies.Requests -eq 2 -and $results[0].Outcome.ToString() -eq 'Failed') 'A refused check was not reported as a failure.'

    $results.Clear()
    $replies.Status = [Net.HttpStatusCode]::OK
    $replies.Tag = 'v' + $assembly.GetName().Version
    Check
    Require ($replies.Requests -eq 3 -and $results[0].SummaryKey -eq 'up_to_date' -and !(Property $updates 'HasUpdate')) 'An immediate retry reused the failed attempt or did not report up to date.'

    $results.Clear()
    Set-Field $setting '_choice' $true
    $saved = Field $updates '_releaseCheck'
    $saved.GetType().GetProperty('CheckedAt').SetValue($saved, [DateTimeOffset]::UtcNow.AddDays(-2))
    $replies.Tag = 'v999.0.0.0'
    Observe
    Require ($replies.Requests -eq 4 -and $results[0].SummaryKey -eq 'update_available') 'The automatic check did not report a newer release after its cache expired.'
    'Update checks passed: manual discovery, notification action, daily cache, failure and immediate retry.'
}
finally {
    $http.Dispose()
    $work.Dispose()
    $client.Dispose()
    [IO.File]::Delete($cache)
    [IO.File]::Delete("$cache.tmp")
    [IO.Directory]::Delete($directory)
}
