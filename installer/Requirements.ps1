param(
    [Parameter(Mandatory)]
    [ValidateSet('DotNet', 'Windows', 'VisualCpp', 'Running')]
    [string] $Kind
)

$ErrorActionPreference = 'Stop'
try {
    $requirements = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'requirements.json') -Raw | ConvertFrom-Json
    $ready = $false
    switch ($Kind) {
        'DotNet' {
            $root = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', 'Registry32')
            $key = $root.OpenSubKey('SOFTWARE\dotnet\Setup\InstalledVersions\x64')
            $location = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'dotnet'
            try {
                if ($key) { $location = $key.GetValue('InstallLocation', $location) }
            }
            finally {
                if ($key) { $key.Dispose() }
                $root.Dispose()
            }
            $directory = Join-Path $location 'shared/Microsoft.NETCore.App'
            if (Test-Path -LiteralPath $directory) {
                $minimum = [version] $requirements.DotNet
                foreach ($folder in Get-ChildItem -LiteralPath $directory -Directory) {
                    $version = $null
                    if ([version]::TryParse($folder.Name, [ref] $version) -and
                        $version.Major -eq $minimum.Major -and $version.Minor -eq $minimum.Minor -and
                        $version -ge $minimum) {
                        $ready = $true
                    }
                }
            }
        }
        'Windows' {
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
        'VisualCpp' {
            $key = 'Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64'
            if (Test-Path $key) {
                $runtime = Get-ItemProperty $key
                $ready = $runtime.Installed -eq 1 -and
                    [version] $runtime.Version.TrimStart('v') -ge [version] $requirements.VisualCpp
            }
        }
        'Running' {
            $session = (Get-Process -Id $PID).SessionId
            $name = [IO.Path]::GetFileNameWithoutExtension($requirements.Engine)
            $ready = @((Get-Process -Name $name -ErrorAction SilentlyContinue) |
                Where-Object { $_.SessionId -eq $session }).Count -gt 0
        }
    }
    if ($ready) { exit 0 }
    exit 1
}
catch {
    Write-Error $_ -ErrorAction Continue
    exit 2
}
