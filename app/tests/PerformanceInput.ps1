param(
    [Parameter(Mandatory)][string] $Directory,
    [Parameter(Mandatory)][int] $ParentPid
)

$ErrorActionPreference = 'Stop'
$sources = 'CaptureInput.cs', 'CaptureSource.cs' | ForEach-Object { Join-Path (Split-Path $PSScriptRoot -Parent) "src/Shell/MainWindow/$_" }
Add-Type -Path $sources
[Syno.TinyTorrent.Shell.PerformanceInput]::Work($Directory, $ParentPid)
