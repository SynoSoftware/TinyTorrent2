param(
    [string] $BinaryDirectory = "$PSScriptRoot/../../artifacts/bin/TinyTorrent/debug_win-x64"
)

$ErrorActionPreference = 'Stop'
$binary = (Resolve-Path -LiteralPath $BinaryDirectory).Path
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $binary 'TinyTorrentUI.dll'))
$flags = [Reflection.BindingFlags]'Instance,Public,NonPublic'
$navigationType = $assembly.GetType('Syno.TinyTorrent.Navigation', $true)
$pageType = $assembly.GetType('Syno.TinyTorrent.Models.WindowPage', $true)
$guardType = [Func``2].MakeGenericType($pageType, [Threading.Tasks.Task[bool]])
$gate = @{ Result = [Threading.Tasks.Task]::FromResult($true) }
$guard = [Management.Automation.LanguagePrimitives]::ConvertTo(
    { param($page) $gate.Result }.GetNewClosure(), $guardType)
$navigation = $navigationType.GetConstructors($flags)[0].Invoke([object[]]@($guard))

function Require([bool] $Condition, [string] $Failure) {
    if (!$Condition) { throw $Failure }
}

function Read-State([string] $Name) {
    $navigationType.GetProperty($Name, $flags).GetValue($navigation)
}

function Start-Navigation([string] $Method, [string] $Page) {
    $arguments = if ($Page) { [object[]]@([Enum]::Parse($pageType, $Page)) } else { [object[]]@() }
    $parameters = [Type[]]@()
    if ($Page) { $parameters = [Type[]]@($pageType) }
    $navigationType.GetMethod($Method, $flags, $null, $parameters, $null).Invoke($navigation, $arguments)
}

function Invoke-Navigation([string] $Method, [string] $Page) {
    $pending = Start-Navigation $Method $Page
    Require ($pending.GetAwaiter().GetResult()) "$Method $Page was refused."
}

function Require-Route([string] $Page, [bool] $HasBack) {
    Require ((Read-State Current).ToString() -eq $Page) "Expected $Page, found $(Read-State Current)."
    Require ((Read-State HasBack) -eq $HasBack) "Back history was wrong on $Page."
    Require ((Read-State CanGoBack) -eq $HasBack) "Back availability was wrong on $Page."
}

Invoke-Navigation Navigate Library
Invoke-Navigation Open Torrents
Invoke-Navigation Navigate Settings
Invoke-Navigation Navigate About
Invoke-Navigation GoBack
Require-Route Settings $true
Invoke-Navigation GoBack
Require-Route Torrents $true
Invoke-Navigation GoBack
Require-Route Library $false
Require (!(Start-Navigation GoBack).GetAwaiter().GetResult()) 'Back succeeded without a destination.'

Invoke-Navigation Open Torrents
$changes = [Collections.Generic.List[string]]::new()
$changed = [ComponentModel.PropertyChangedEventHandler]{ param($sender, $eventArgs) $changes.Add($eventArgs.PropertyName) }.GetNewClosure()
$navigation.add_PropertyChanged($changed)
Invoke-Navigation Navigate Torrents
Require-Route Torrents $false
Require ($changes.Contains('Current')) 'Clearing same-page history did not notify the window.'
$navigation.remove_PropertyChanged($changed)
Invoke-Navigation Navigate Settings
Invoke-Navigation Navigate Settings
Invoke-Navigation Navigate Connection
Invoke-Navigation Navigate About
Invoke-Navigation GoBack
Require-Route Connection $true
Invoke-Navigation GoBack
Require-Route Settings $true
Invoke-Navigation GoBack
Require-Route Torrents $false

Invoke-Navigation Navigate Library
Invoke-Navigation Open Torrents
Invoke-Navigation Navigate Settings
$gate.Result = [Threading.Tasks.Task]::FromResult($false)
Require (!(Start-Navigation GoBack).GetAwaiter().GetResult()) 'A refused departure consumed Back.'
Require (!(Start-Navigation Navigate Library).GetAwaiter().GetResult()) 'A refused workspace switch succeeded.'
Require-Route Settings $true
$gate.Result = [Threading.Tasks.Task]::FromResult($true)
Invoke-Navigation GoBack
Require-Route Torrents $true
Invoke-Navigation GoBack
Require-Route Library $false

Invoke-Navigation Navigate Settings
$completion = [Threading.Tasks.TaskCompletionSource[bool]]::new()
$gate.Result = $completion.Task
$pending = Start-Navigation GoBack
Require (!(Read-State CanGoBack)) 'Back stayed enabled during departure.'
Require (!(Start-Navigation Navigate Torrents).GetAwaiter().GetResult()) 'A competing navigation changed pending history.'
Require (!(Start-Navigation GoBack).GetAwaiter().GetResult()) 'Repeated Back started a second departure.'
$completion.SetResult($false)
Require (!$pending.GetAwaiter().GetResult()) 'A pending refusal consumed history.'
Require-Route Settings $true
$gate.Result = [Threading.Tasks.Task]::FromResult($true)
Invoke-Navigation GoBack
Require-Route Library $false

Invoke-Navigation Navigate Torrents
Invoke-Navigation Navigate Settings
$categoryType = $assembly.GetType('Syno.TinyTorrent.Models.SettingsCategory', $true)
$select = $navigationType.GetMethod('SelectCategory', $flags)
for ($visit = 0; $visit -lt 4; $visit++) {
    foreach ($category in 'General', 'Transfers', 'Limits', 'Network', $null) {
        $value = if ($category) { [Enum]::Parse($categoryType, $category) } else { $null }
        $selected = $select.Invoke($navigation, [object[]]@($value))
        Require ($selected.GetAwaiter().GetResult()) 'A sidebar category could not be selected.'
    }
}
Invoke-Navigation GoBack
Require-Route Torrents $false

$visits = @{ Restored = 0 }
$restore = [Action]{ $visits.Restored++ }.GetNewClosure()
$visit = $navigationType.GetMethod('Visit', $flags)
$keep = $navigationType.GetMethod('KeepWorkspace', $flags)
Invoke-Navigation Navigate Library
Require ($visit.Invoke($navigation, [object[]]@([Enum]::Parse($pageType, 'Torrents'), $restore)).GetAwaiter().GetResult()) 'The layout visit was refused.'
Invoke-Navigation Navigate Settings
Invoke-Navigation GoBack
Require-Route Torrents $true
Require ($visits.Restored -eq 0) 'Returning from Settings restored the layout before leaving its visit.'
$gate.Result = [Threading.Tasks.Task]::FromResult($false)
Require (!(Start-Navigation GoBack).GetAwaiter().GetResult()) 'A refused layout departure consumed its return.'
Require ($visits.Restored -eq 0) 'A refused departure restored the layout.'
$gate.Result = [Threading.Tasks.Task]::FromResult($true)
Invoke-Navigation GoBack
Require-Route Library $false
Require ($visits.Restored -eq 1) 'Leaving the layout visit did not restore exactly once.'
Require ($visit.Invoke($navigation, [object[]]@([Enum]::Parse($pageType, 'Torrents'), $restore)).GetAwaiter().GetResult()) 'The second layout visit was refused.'
$keep.Invoke($navigation, @())
Require-Route Torrents $false
Require ($visits.Restored -eq 1) 'Keeping an explicit layout restored the old one.'
Require (!(Start-Navigation GoBack).GetAwaiter().GetResult()) 'Back undid an explicit layout choice.'

Write-Output 'PASS: contextual return, secondary pages, Connection, same-page reset, refusal, overlapping navigation, sidebar history and temporary layout visits.'
