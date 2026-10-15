param(
    [string] $BinaryDirectory = "$PSScriptRoot/../../artifacts/bin/TinyTorrent/debug_win-x64"
)

$ErrorActionPreference = 'Stop'
$binary = (Resolve-Path -LiteralPath $BinaryDirectory).Path
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $binary 'TinyTorrentUI.dll'))
$flags = [Reflection.BindingFlags]'Static,NonPublic'
$map = $assembly.GetType('Syno.TinyTorrent.Controls.PeerMap', $true)
$areaType = $assembly.GetType('Syno.TinyTorrent.Models.Geography+Area', $true)
$pointType = $assembly.GetType('Syno.TinyTorrent.Models.GeoPoint', $true)
$reach = $map.GetMethod('Reach', $flags)
$distribute = $map.GetMethod('Distribute', $flags)

function Require([bool] $Condition, [string] $Failure) {
    if (!$Condition) { throw $Failure }
}

# Regions most populous first, as PeerMap.Regions returns them.
function New-Regions([double[]] $Populations) {
    $regions = [Array]::CreateInstance($areaType, $Populations.Count)
    for ($index = 0; $index -lt $Populations.Count; $index++) {
        $point = [Activator]::CreateInstance($pointType, [object[]]@([double]$index, [double]0))
        $regions[$index] = [Activator]::CreateInstance(
            $areaType, [object[]]@("$index", $Populations[$index], $point))
    }
    , $regions
}

function Get-Reach([double[]] $Populations, [int] $Connections) {
    $reach.Invoke($null, [object[]]@((New-Regions $Populations), $Connections))
}

function Get-Shares([double[]] $Populations, [int] $Connections) {
    $result = $distribute.Invoke($null, [object[]]@((New-Regions $Populations), $Connections))
    , @($result | ForEach-Object { $_.Item2 })
}

# The most probable placement keeps a second connection in a 90% region
# (81% likely) instead of spreading one to each region (18%); the tenth
# connection is the first worth more in the 10% region (10 > 90 / 10).
Require ((Get-Reach @(90, 10) 2) -eq 1) 'Two connections over 90/10 spread to the 10% region.'
Require ((Get-Reach @(90, 10) 9) -eq 1) 'Nine connections over 90/10 reached the 10% region.'
Require ((Get-Reach @(90, 10) 10) -eq 2) 'Ten connections over 90/10 did not reach the 10% region.'
Require ((Get-Reach @(50, 20, 10) 10) -eq 3) 'Ten connections over 50/20/10 did not reach all three regions.'

# The listed places hold 80% of the people, but the regions used take all of
# the country's traffic, so no measured speed is dropped.
$shares = Get-Shares @(50, 20, 10) 10
Require (($shares -join ',') -eq '0.625,0.25,0.125') "Shares over 50/20/10 were $($shares -join ',')."
$shares = Get-Shares @(50, 20, 10) 2
Require (($shares -join ',') -eq '1') "Shares of two connections over 50/20/10 were $($shares -join ',')."

'Estimated distribution checks passed.'
