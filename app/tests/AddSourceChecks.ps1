param(
    [string] $BinaryDirectory = "$PSScriptRoot/../../artifacts/bin/TinyTorrent/debug_win-x64"
)

$ErrorActionPreference = 'Stop'
$binary = (Resolve-Path -LiteralPath $BinaryDirectory).Path
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $binary 'TinyTorrentUI.dll'))
$flags = [Reflection.BindingFlags]'Instance,Public,NonPublic'
$strings = [Activator]::CreateInstance($assembly.GetType('Syno.TinyTorrent.Shared.Strings', $true))
$type = $assembly.GetType('Syno.TinyTorrent.Torrents.AddSource', $true)

function Apply-Preview([string] $TorrentId, $Target) {
    $preview = @{
        preview_id = 'preview'
        torrent_id = $TorrentId
        name = 'content'
        size = 12
        metadata_ready = $true
        merge_available = $false
        error = $null
        hashes = @('hash')
        shared_with = @()
    } | ConvertTo-Json -Depth 4
    $document = [Text.Json.JsonDocument]::Parse($preview)
    try {
        $type.GetMethod('Apply', $flags).Invoke($Target, [object[]]@($document.RootElement, 'C:\Downloads', $null))
    }
    finally {
        $document.Dispose()
    }
}

# A timeout is a lost reply, so the add may have happened.
$lostReply = [TimeoutException]::new('The add reply timed out.')
function New-Source { $type.GetConstructors($flags)[0].Invoke([object[]]@('source.torrent', $strings, $null)) }
function Get-Problem($Target) { $type.GetProperty('Problem', $flags).GetValue($Target) }
function Fail([string] $Method, $Target, [Exception] $Failure) {
    $null = $type.GetMethod($Method, $flags).Invoke($Target, [object[]]@($Failure))
}

$added = New-Source
Fail 'FailAdd' $added $lostReply
Apply-Preview 'existing' $added
if ($null -ne (Get-Problem $added) -or
    $type.GetProperty('Content', $flags).GetValue($added).GetType().Name -ne 'Present') {
    throw 'A listed torrent holding the content did not settle an unconfirmed add.'
}

$waiting = New-Source
Fail 'FailAdd' $waiting $lostReply
Fail 'FailPreview' $waiting ([Exception]::new('The preview failed.'))
if ((Get-Problem $waiting).GetType().Name -ne 'Unconfirmed') {
    throw 'A failed preview replaced an unconfirmed add.'
}

Write-Output 'PASS: a listed torrent settles an unconfirmed add, and a failed preview keeps it unconfirmed.'

# Inspect the queued settings command without starting a pipe or touching the user's engine.
$settingsType = $assembly.GetType('Syno.TinyTorrent.Settings.Editor', $true)
$settingType = $assembly.GetType('Syno.TinyTorrent.Settings.Setting', $true)
$preferenceType = $assembly.GetType('Syno.TinyTorrent.Shared.DialogPreference', $true)
$clientType = $assembly.GetType('Syno.TinyTorrent.Shared.Engine.PipeClient', $true)
$settings = [Runtime.CompilerServices.RuntimeHelpers]::GetUninitializedObject($settingsType)
$client = $clientType.GetConstructors($flags)[0].Invoke([object[]]@($strings))
$clientType.GetField('_connected', $flags).SetValue($client, $true)
$settingsType.GetField('_client', $flags).SetValue($settings, $client)
$kind = [Enum]::Parse($assembly.GetType('Syno.TinyTorrent.SettingKind'), 'Boolean')
$category = [Enum]::Parse($assembly.GetType('Syno.TinyTorrent.SettingsCategory'), 'General')
$document = [Text.Json.JsonDocument]::Parse('true')
try {
    # The preference saves through the Editor, so the settings need no store.
    $dialogs = @{}
    foreach ($name in 'torrent_dialog', 'magnet_dialog', 'confirm_delete') {
        $dialogs[$name] = $settingType.GetConstructors($flags)[0].Invoke(
            [object[]]@($null, $strings, $name, $kind, $category))
    }
    foreach ($name in 'torrent_dialog', 'magnet_dialog') {
        $null = $settingType.GetMethod('Confirm', $flags).Invoke($dialogs[$name], [object[]]@($document.RootElement))
    }
}
finally { $document.Dispose() }
# Add passes the setting of each source, so one setting can repeat.
$order = 'torrent_dialog', 'magnet_dialog', 'torrent_dialog', 'confirm_delete'
$choices = [Array]::CreateInstance($settingType, $order.Count)
for ($index = 0; $index -lt $order.Count; $index++) {
    $choices.SetValue($dialogs[$order[$index]], $index)
}
$queue = $clientType.GetField('_commands', $flags).GetValue($client)
$save = $preferenceType.GetMethod('Save', $flags)
try {
    $preference = [Activator]::CreateInstance($preferenceType, [object[]]@($settings))
    $save.Invoke($preference, [object[]]@(,$choices)).GetAwaiter().GetResult()
    if ($queue.Count -ne 0) { throw 'An unchecked opt-out changed a preference.' }
    $preference.NeverShow = $true
    $pending = $save.Invoke($preference, [object[]]@(,$choices))
    if ($queue.Count -ne 1) { throw 'The opt-out did not queue one settings command.' }
    $command = $queue.Dequeue()
    $arguments = $command.GetType().GetProperty('Arguments', $flags).GetValue($command)
    $changes = $arguments.GetType().GetProperty('changes').GetValue($arguments)
    if (
        $changes.Count -ne 2 -or
        !$changes.ContainsKey('torrent_dialog') -or $changes['torrent_dialog'] -or
        !$changes.ContainsKey('magnet_dialog') -or $changes['magnet_dialog']
    ) {
        throw 'The opt-out changed the wrong dialog preferences.'
    }
    $completion = $command.GetType().GetProperty('Completion', $flags).GetValue($command)
    $null = $completion.TrySetCanceled()
    try { $pending.GetAwaiter().GetResult() } catch [OperationCanceledException] { }
}
finally { $client.Dispose() }
Write-Output 'PASS: an opt-out turns off the given dialog settings that are on, in one save; an unchecked opt-out changes nothing.'
