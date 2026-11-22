# Exits 0 when the Windows App Runtime packages that the release needs are
# installed, 1 when one is missing, and 2 when Windows cannot tell. Setup checks
# .NET and the running engine itself; only this check needs Get-AppxPackage.
# Setup starts it as soon as it opens, without waiting, so it also writes the
# exit code to $Result, which Setup reads when the person selects Install.
param([string] $Result)

function Finish([int] $Code) {
    if ($Result) {
        [IO.File]::WriteAllText("$Result.part", "$Code")
        Move-Item -LiteralPath "$Result.part" -Destination $Result -Force
    }
    exit $Code
}

$ErrorActionPreference = 'Stop'
try {
    $requirements = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'requirements.json') -Raw | ConvertFrom-Json
    $packages = @(Get-AppxPackage)
    $ready = $true
    foreach ($required in $requirements.Windows) {
        $found = @($packages | Where-Object {
            $_.Name -like $required.Name -and $_.PublisherId -eq $required.PublisherId -and
            $_.Architecture -eq 'X64' -and [version] $_.Version -ge [version] $required.Version -and
            $_.Status -eq 'Ok'
        })
        if ($found.Count -eq 0) { $ready = $false }
    }
}
catch {
    Write-Error $_ -ErrorAction Continue
    Finish 2
}
if ($ready) { Finish 0 }
Finish 1
