param(
    [Parameter(Mandatory)]
    [string] $BinaryDirectory
)

$ErrorActionPreference = 'Stop'
$binary = (Resolve-Path -LiteralPath $BinaryDirectory).Path
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $binary 'TinyTorrentUI.dll'))
$flags = [Reflection.BindingFlags]'Static,NonPublic'
$graph = $assembly.GetType('Syno.TinyTorrent.Torrents.SpeedGraph', $true)
$sampleType = $assembly.GetType('Syno.TinyTorrent.Torrents.SpeedSample', $true)
$averageType = $graph.GetNestedType('Average', [Reflection.BindingFlags]::NonPublic)
$extentType = $graph.GetNestedType('Extent', [Reflection.BindingFlags]::NonPublic)
$bucket = $graph.GetMethod('Bucket', $flags)
$points = $graph.GetMethod('Points', $flags)
$rate = $averageType.GetProperty('Upload').GetMethod.CreateDelegate(
    [Func``2].MakeGenericType($averageType, [double]))

function Require([bool] $Condition, [string] $Failure) {
    if (-not $Condition) { throw $Failure }
}

foreach ($length in [long[]]@(21600, 86400)) {
    $extent = [Activator]::CreateInstance($extentType,
        [object[]]@([double]$length, [double]100, [long]0, $length, [double]1024))
    foreach ($gap in [long[]]@(1, 4)) {
        $samples = [Array]::CreateInstance($sampleType, 2)
        $samples[0] = [Activator]::CreateInstance($sampleType,
            [object[]]@([long]7199, [double]0, [double]0, [long]0, [double]7200))
        $samples[1] = [Activator]::CreateInstance($sampleType,
            [object[]]@($length, [double]0, [double]0, [long](7199 + $gap), [double]($length - 7198 - $gap)))
        $runs = $bucket.Invoke($null, [object[]]@($samples, [long]0, $length, [long]($length / 60)))
        $expected = if ($gap -eq 1) { 1 } else { 2 }
        Require ($runs.Count -eq $expected) 'Known coverage and missing time were not separated correctly.'
        $first = $points.Invoke($null, [object[]]@($runs[0], $extent, $rate))
        $last = $points.Invoke($null, [object[]]@($runs[$runs.Count - 1], $extent, $rate))
        Require ($first[0].X -eq 0 -and $last[$last.Count - 1].X -eq $length) 'The line stopped at bucket centres instead of observed boundaries.'
        if ($gap -gt 2) {
            Require ($first[$first.Count - 1].X -eq 7199 -and $last[0].X -eq 7203) 'A four-second gap was widened or filled.'
        }
    }
}

'Speed coverage checks passed for 6-hour and 24-hour views.'
