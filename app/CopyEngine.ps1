param(
    [Parameter(Mandatory)][string] $Source,
    [Parameter(Mandatory)][string] $Destination
)

$ErrorActionPreference = 'Stop'

function Resolve-PhysicalPath([string] $Path) {
    # Running engines can sit under hidden folders such as AppData.
    $entry = Get-Item -LiteralPath $Path -Force
    $parent = Split-Path -Path $entry.FullName -Parent
    if (-not $parent) {
        return $entry.FullName
    }
    $target = $entry.ResolveLinkTarget($true)
    if ($target) {
        return Resolve-PhysicalPath $target.FullName
    }
    return Join-Path (Resolve-PhysicalPath $parent) $entry.Name
}

if (Test-Path -LiteralPath $Destination) {
    if ((Get-FileHash -LiteralPath $Source).Hash -eq (Get-FileHash -LiteralPath $Destination).Hash) {
        return
    }

    $session = [Diagnostics.Process]::GetCurrentProcess().SessionId
    # The artifacts junction gives the running process a different path spelling.
    $physical = Resolve-PhysicalPath $Destination
    $running = @(Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($Destination)) -ErrorAction SilentlyContinue |
        Where-Object { $_.SessionId -eq $session -and $_.Path -and (Resolve-PhysicalPath $_.Path) -eq $physical })
    foreach ($process in $running) {
        if (-not $process.HasExited) {
            Write-Host "The engine changed. Stopping TinyTorrent PID $($process.Id) at '$($process.Path)' before replacing it."
            Stop-Process -InputObject $process -Force
            if (-not $process.WaitForExit(5000)) {
                throw "TinyTorrent PID $($process.Id) did not stop within five seconds. Close it or stop its debugger, then build again."
            }
        }
    }
}

try {
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}
catch {
    throw "Cannot replace '$Destination': $($_.Exception.Message) Close TinyTorrent or stop its debugger, then build again."
}
