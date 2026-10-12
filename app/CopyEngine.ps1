param(
    [Parameter(Mandatory)][string] $Source,
    [Parameter(Mandatory)][string] $Destination
)

$ErrorActionPreference = 'Stop'

if (Test-Path -LiteralPath $Destination) {
    if ((Get-FileHash -LiteralPath $Source).Hash -eq (Get-FileHash -LiteralPath $Destination).Hash) {
        return
    }

    $session = [Diagnostics.Process]::GetCurrentProcess().SessionId
    $running = @(Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($Destination)) -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $Destination -and $_.SessionId -eq $session })
    foreach ($process in $running) {
        if (-not $process.HasExited) {
            Write-Host 'The engine changed. Stopping TinyTorrent before replacing it.'
            Stop-Process -InputObject $process -Force
            if (-not $process.WaitForExit(5000)) {
                throw 'TinyTorrent did not stop within five seconds.'
            }
        }
    }
}

Copy-Item -LiteralPath $Source -Destination $Destination -Force
