param(
    [string] $BinaryDirectory = "$PSScriptRoot/../../artifacts/bin/TinyTorrent/debug_win-x64"
)

$ErrorActionPreference = 'Stop'
$binary = (Resolve-Path -LiteralPath $BinaryDirectory).Path
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $binary 'TinyTorrentUI.dll'))
$flags = [Reflection.BindingFlags]'Instance,Public,NonPublic'
$strings = [Activator]::CreateInstance($assembly.GetType('Syno.TinyTorrent.Shared.Strings', $true))
$type = $assembly.GetType('Syno.TinyTorrent.Shell.Notifications', $true)
$kind = $assembly.GetType('Syno.TinyTorrent.Shell.Notice', $true).GetProperty('Kind', $flags)

# The notices below read only the owner's closed state, which an owner whose
# constructor never ran reports as open; the constructor needs a WinUI thread.
$owner = [Runtime.CompilerServices.RuntimeHelpers]::GetUninitializedObject(
    $assembly.GetType('Syno.TinyTorrent.Shell.MainViewModel', $true))

function Require([bool] $Condition, [string] $Failure) {
    if (!$Condition) { throw $Failure }
}

function New-Notifications {
    $type.GetConstructors($flags)[0].Invoke([object[]]@($owner, $strings))
}

function Show-Outcome($Notifications, [string] $Subject) {
    $type.GetMethod('ShowOutcome', $flags).Invoke(
        $Notifications, [object[]]@('subtitles', 'connected', $Subject)) | Out-Null
}

function Step($Notifications, [double] $Seconds) {
    $type.GetMethod('Elapse', $flags).Invoke(
        $Notifications, [object[]]@([TimeSpan]::FromSeconds($Seconds))) | Out-Null
}

function Set-Visible($Notifications, [bool] $Visible) {
    $type.GetMethod('SetVisible', $flags).Invoke($Notifications, [object[]]@($Visible)) | Out-Null
}

function Get-Kinds($Notifications) {
    , @($Notifications.Notices | ForEach-Object { $kind.GetValue($_).ToString() })
}

# A timed notice closes after seven seconds on screen.
$notifications = New-Notifications
Show-Outcome $notifications 'first'
Set-Visible $notifications $true
Step $notifications 6.75
Require ($notifications.Notices.Count -eq 1) 'A timed notice closed before its lifetime.'
Step $notifications 0.25
Require ($notifications.Notices.Count -eq 0) 'A timed notice outlived its lifetime.'

# A fifth timed notice removes the oldest.
foreach ($subject in 'one', 'two', 'three', 'four', 'five') {
    Show-Outcome $notifications $subject
}
$subjects = @($notifications.Notices | ForEach-Object { $_.Subject })
Require (($subjects -join ',') -eq 'two,three,four,five') "Five timed notices left $($subjects -join ',')."

# A command error survives the countdown and the stack hiding, while a timed
# notice the person saw retires when the stack hides.
$notifications = New-Notifications
$type.GetMethod('Report', $flags, $null, [Type[]]@([Exception]), $null).Invoke(
    $notifications, [object[]]@([IO.IOException]::new('Stop failed'))) | Out-Null
Set-Visible $notifications $true
Step $notifications 60
Show-Outcome $notifications 'seen'
Set-Visible $notifications $true
Set-Visible $notifications $false
Set-Visible $notifications $true
Require (((Get-Kinds $notifications) -join ',') -eq 'CommandError') "After a hide the stack held $((Get-Kinds $notifications) -join ',')."

# A failed background result stays, and the same work failing again replaces
# it; a completed one closes by itself.
$notifications = New-Notifications
$description = [Activator]::CreateInstance(
    $assembly.GetType('Syno.TinyTorrent.Shared.WorkDescription+Imdb', $true),
    [object[]]@([Enum]::Parse($assembly.GetType('Syno.TinyTorrent.ImdbPhase', $true), 'Downloading')))
$progress = [Activator]::CreateInstance(
    $assembly.GetType('Syno.TinyTorrent.Shared.WorkProgress+Unknown', $true), $true)
$execution = [Activator]::CreateInstance(
    $assembly.GetType('Syno.TinyTorrent.Shared.WorkExecution+Running', $true), [object[]]@($true))
$observation = [Activator]::CreateInstance(
    $assembly.GetType('Syno.TinyTorrent.Shared.WorkObservation', $true),
    [object[]]@($description, $progress, $execution))
$outcome = $assembly.GetType('Syno.TinyTorrent.WorkOutcome', $true)
$resultType = $assembly.GetType('Syno.TinyTorrent.Shared.WorkResult', $true)
foreach ($state in 'Failed', 'Failed', 'Completed') {
    $result = [Activator]::CreateInstance($resultType, [object[]]@(
        $observation, [Enum]::Parse($outcome, $state), [IO.IOException]::new('Import failed'), $null, [object[]]@()))
    $type.GetMethod('ShowWorkResult', $flags).Invoke($notifications, [object[]]@($result)) | Out-Null
}
Set-Visible $notifications $true
Step $notifications 60
Set-Visible $notifications $false
Set-Visible $notifications $true
Require ($notifications.Notices.Count -eq 1 -and $notifications.Notices[0].IsError) 'A failed work result did not stay, a repeated failure piled up, or a completed one did not close.'

'Notification checks passed.'
