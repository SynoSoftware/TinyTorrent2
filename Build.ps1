# Builds the engine, the app or both for development checks and prints the output
# folder to launch. docs/agents/compilation.md says when to run it; the installer has
# its own Release build in installer/Build.ps1.
param(
    [switch] $Capture,
    [switch] $Release,
    # Builds only the engine and leaves the app folder alone, because the
    # window there may not speak this engine's protocol.
    [switch] $Engine,
    # Builds only the app, with an engine of the current sources beside it:
    # one already built in any output folder, or one compiled when none is.
    [switch] $App,
    # Further projects to build last, such as the TableView tests.
    [string[]] $Project = @()
)

$ErrorActionPreference = 'Stop'
$repository = $PSScriptRoot
$artifacts = Join-Path $repository 'artifacts'
$records = Join-Path $artifacts 'compilation'
$history = Join-Path $records 'builds.jsonl'
$configuration = if ($Release) { 'Release' } else { 'Debug' }
$flavour = "$($configuration.ToLowerInvariant())_win-x64"
if ($Capture) { $flavour += '_capture' }
$extras = @($Project | ForEach-Object { [IO.Path]::GetRelativePath($repository, (Resolve-Path -LiteralPath $_).Path) } | Sort-Object -Unique)
$properties = ([xml](Get-Content (Join-Path $repository 'Directory.Build.props'))).Project.PropertyGroup
$engineName = $properties.EngineTargetName | Where-Object { $_ }
$windowName = $properties.WindowTargetName | Where-Object { $_ }
# The parts this build compiles. Each excludes what only the other part reads,
# so an edit to one part does not rebuild the other.
$engineInputs = @(':(exclude)app', ':(exclude)lib/TableView', ':(exclude)dotnet-tools.json')
$parts = [ordered]@{}
if (-not $App) { $parts.engine = $engineInputs }
if (-not $Engine) { $parts.app = @(':(exclude)engine') }
if (-not $parts.Count) { $parts.engine = @(); $parts.app = @() }
if ($extras) { $parts.projects = @() }

function Get-Folder([string] $Root) { Join-Path $Root "bin/TinyTorrent/$flavour" }

# What the checks run: the engine itself after an engine-only build, otherwise
# the app folder with its engine.
function Get-Output([string] $Root) {
    if ($parts.Contains('app')) { Get-Folder $Root } else { Get-Product $Root 'engine' }
}

# The file each part builds, whose hash shows whether another tool rebuilt it.
function Get-Product([string] $Root, [string] $Part) {
    switch ($Part) {
        'engine' { Join-Path $Root "bin/Engine/$configuration/$engineName.exe" }
        'app' { Join-Path (Get-Folder $Root) "$windowName.dll" }
    }
}

# Identifies a part's build inputs by the content of every file outside the
# documentation and `$Excluded`, so a documentation edit, git add or commit
# does not cost a build. Git ignores artifacts/ and 3rdParty/, which are not
# inputs, and secrets/keys.json, which the app build reads.
function Get-Source([string[]] $Excluded) {
    $inputs = @('.', ':(exclude)docs', ':(exclude)*.md', ':(exclude)installer') + $Excluded
    $listed = @(git -C $repository ls-files --cached --others --exclude-standard -- @inputs)
    if ($LASTEXITCODE -ne 0 -or $listed.Count -eq 0) { throw 'Git could not describe the checkout.' }
    $files = $listed + 'secrets/keys.json' |
        Where-Object { Test-Path -LiteralPath (Join-Path $repository $_) -PathType Leaf } | Sort-Object -Unique
    $blobs = @($files | git -C $repository hash-object --stdin-paths)
    if ($LASTEXITCODE -ne 0 -or $blobs.Count -ne $files.Count) { throw 'Git could not describe the checkout.' }
    $hash = [Security.Cryptography.IncrementalHash]::CreateHash('SHA256')
    try {
        $text = for ($index = 0; $index -lt $files.Count; ++$index) { "$($blobs[$index]) $($files[$index])" }
        $hash.AppendData([Text.Encoding]::UTF8.GetBytes($text -join "`n"))
        return [Convert]::ToHexString($hash.GetHashAndReset())
    }
    finally { $hash.Dispose() }
}

# Hashes the file each part built, so an output that another tool rebuilt no
# longer counts as this record's build.
function Get-Binaries([string] $Root, [string[]] $Built) {
    $binaries = [ordered]@{}
    foreach ($part in $Built) {
        $file = Get-Product $Root $part
        if ($file -and (Test-Path -LiteralPath $file)) { $binaries[$part] = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash }
    }
    return $binaries
}

# Returns the hash of each output root's engine whose latest build is the
# current engine source and still in place, keyed by root. Both flavours of
# this configuration put the engine in the same place.
function Find-Engines([string] $Source) {
    $latest = @{}
    if (Test-Path -LiteralPath $history) {
        foreach ($line in Get-Content -LiteralPath $history) {
            $build = $line | ConvertFrom-Json
            if ($build.flavour -like "$($configuration.ToLowerInvariant())_*" -and $build.sources.engine) {
                $latest[$build.root] = $build
            }
        }
    }
    $engines = @{}
    foreach ($root in $latest.Keys) {
        $build = $latest[$root]
        if ($build.result -eq 'succeeded' -and $build.sources.engine -eq $Source -and
            (Get-Binaries $root 'engine').engine -eq $build.binaries.engine) {
            $engines[$root] = $build.binaries.engine
        }
    }
    return $engines
}

# Returns the output root where the latest build of each requested part, in
# this flavour, is that part's current source, so a repeated request costs no
# compilation. The app's folder must also hold a current engine, because the
# app copied whichever engine its root had when it was built. Extra projects
# count only with the same extra projects.
function Find-Current([hashtable] $Sources) {
    if (-not (Test-Path -LiteralPath $history)) { return }
    $latest = @{}
    foreach ($line in Get-Content -LiteralPath $history) {
        $build = $line | ConvertFrom-Json
        if ($build.flavour -ne $flavour -or -not $build.sources) { continue }
        foreach ($part in $build.sources.PSObject.Properties.Name) {
            if ($part -ne 'projects' -or "$($build.projects)" -eq "$extras") { $latest["$($build.root)|$part"] = $build }
        }
    }
    $roots = @($latest.Values | ForEach-Object root | Sort-Object -Unique)
    foreach ($root in $roots) {
        $current = $true
        foreach ($part in $Sources.Keys) {
            $build = $latest["$root|$part"]
            if (-not $build -or $build.result -ne 'succeeded' -or $build.sources.$part -ne $Sources[$part] -or
                (Get-Binaries $root $part)[$part] -ne $build.binaries.$part) {
                $current = $false
                break
            }
        }
        if ($current -and $Sources.Contains('app')) {
            $copy = Join-Path (Get-Folder $root) "$engineName.exe"
            $current = (Test-Path -LiteralPath $copy) -and
                $engines.Values -contains (Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash
        }
        if ($current) { return $root }
    }
}

# Returns the first output root that no running program uses, because Windows
# refuses to replace a running executable.
function Find-Free {
    $resolved = (Get-Item -LiteralPath $artifacts).Target
    $running = @(Get-Process | Where-Object Path | ForEach-Object Path)
    for ($lane = 1; ; ++$lane) {
        $roots = @($artifacts)
        if ($resolved) { $roots += [IO.Path]::GetFullPath($resolved) }
        if ($lane -gt 1) { $roots = $roots | ForEach-Object { Join-Path $_ "lanes\$lane" } }
        $busy = $running | Where-Object {
            $path = $_
            $roots | Where-Object { $path.StartsWith("$_\bin\", [StringComparison]::OrdinalIgnoreCase) }
        }
        if ($busy) { continue }
        # A benchmark can load the app DLL into PowerShell outside this folder.
        foreach ($name in "$windowName.dll", "$engineName.exe") {
            $file = Join-Path (Get-Folder $roots[0]) $name
            if (-not (Test-Path -LiteralPath $file)) { continue }
            try {
                $probe = [IO.File]::Open($file, 'Open', 'ReadWrite', 'None')
                $probe.Dispose()
            }
            catch [IO.IOException] { $busy = $true; break }
        }
        if (-not $busy) { return $roots[0] }
    }
}

function Invoke-MSBuild([string] $Project, [string[]] $Arguments, [string] $Log) {
    # Out-Host keeps MSBuild's lines out of the returned result.
    & $msbuild $Project @Arguments /nologo /nr:false /v:m /fl "/flp:logfile=$Log;verbosity=normal" | Out-Host
    return $LASTEXITCODE -eq 0
}

if (-not (Test-Path -LiteralPath (Join-Path $repository '3rdParty/complete'))) {
    throw '3rdParty is incomplete. Stop and report it to the owner; never rebuild it.'
}
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$msbuild = @(& $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\amd64\MSBuild.exe') | Select-Object -First 1
if (-not $msbuild) { throw 'Visual Studio MSBuild could not be found.' }

$null = New-Item -ItemType Directory -Path (Join-Path $records 'logs') -Force
# Child processes inherit this priority, so the compilers yield to the owner's
# foreground work.
(Get-Process -Id $PID).PriorityClass = 'BelowNormal'
$lock = $null
while (-not $lock) {
    try { $lock = [IO.File]::Open((Join-Path $records 'build.lock'), 'OpenOrCreate', 'ReadWrite', 'None') }
    catch [IO.IOException] {
        Write-Host 'Waiting for another build to finish...'
        Start-Sleep -Seconds 10
    }
}
try {
    # Read after the lock, so a build that finished while this one waited counts.
    $sources = @{}
    foreach ($part in $parts.Keys) { $sources[$part] = Get-Source $parts[$part] }
    $engineSource = if ($parts.Contains('engine')) { $sources.engine } else { Get-Source $engineInputs }
    $engines = Find-Engines $engineSource
    $current = Find-Current $sources
    if ($current) {
        Write-Host "Up to date: $(Get-Output $current)"
        Write-Host "Artifacts: $current"
        exit 0
    }
    $root = Find-Free
    # The app build copies its root's engine beside itself, so that engine must
    # be a build of the current engine source. A current build in another root
    # is copied in; only when none exists is the engine compiled.
    if ($parts.Contains('app') -and -not $parts.Contains('engine') -and -not $engines.ContainsKey($root)) {
        $donor = @($engines.Keys)[0]
        if ($donor) {
            $source = Get-Product $donor 'engine'
            $target = Get-Product $root 'engine'
            $null = New-Item -ItemType Directory -Path (Split-Path $target) -Force
            foreach ($extension in '.exe', '.pdb') {
                $file = [IO.Path]::ChangeExtension($source, $extension)
                if (Test-Path -LiteralPath $file) { Copy-Item -LiteralPath $file -Destination ([IO.Path]::ChangeExtension($target, $extension)) -Force }
            }
            # The root's incremental-link state describes its old engine, so
            # its next engine build links in full.
            Remove-Item -LiteralPath ([IO.Path]::ChangeExtension($target, '.ilk')) -ErrorAction SilentlyContinue
            Write-Host "Copied the current engine from $donor"
        }
        else { $parts.Insert(0, 'engine', $engineInputs) }
        $sources.engine = $engineSource
    }
    $common = @("/p:Configuration=$configuration", '/p:Platform=x64')
    if ($root -ne $artifacts) { $common += "/p:ArtifactsPath=$root" }
    $managed = @('/restore') + $common
    if ($Capture) { $managed += '/p:EnableCapture=true' }
    $started = [DateTime]::UtcNow
    $stamp = $started.ToString('yyyyMMdd-HHmmss')
    $steps = @()
    if ($parts.Contains('engine')) { $steps += @{ project = 'engine/src/Engine.vcxproj'; arguments = $common } }
    if ($parts.Contains('app')) { $steps += @{ project = 'app/src/TinyTorrent.csproj'; arguments = $managed } }
    $steps += @($extras | ForEach-Object { @{ project = $_; arguments = $managed } })
    $logs = @()
    $succeeded = $true
    Write-Host "Building $($parts.Keys -join ', ') ($flavour) into $root"
    foreach ($step in $steps) {
        $logs += Join-Path $records "logs/$stamp-$([IO.Path]::GetFileNameWithoutExtension($step.project)).log"
        $succeeded = Invoke-MSBuild (Join-Path $repository $step.project) $step.arguments $logs[-1]
        if (-not $succeeded) { break }
    }
    $build = [ordered]@{
        started = $started.ToString('o'); ended = [DateTime]::UtcNow.ToString('o')
        result = if ($succeeded) { 'succeeded' } else { 'failed' }
        flavour = $flavour; projects = $extras; root = $root; checkout = $repository; sources = $sources
        commit = (git -C $repository rev-parse --short HEAD); changed = @(git -C $repository status --porcelain).Count
        logs = $logs; binaries = Get-Binaries $root $sources.Keys
    }
    Add-Content -LiteralPath $history -Value ($build | ConvertTo-Json -Compress -Depth 3) -Encoding utf8NoBOM
    $stray = & "$env:ProgramFiles\gozler\gozler.exe" -path $repository -ad 'regex:^(bin|obj|bin-fl|TestResults)$' '!*\artifacts\*' '!*\3rdParty\*' '!*\.claude\*' -n all
    if ($LASTEXITCODE -ne 0) { Write-Warning "gozler could not check for generated output outside artifacts/ (exit $LASTEXITCODE)." }
    if ($stray) { Write-Warning "Generated output appeared outside artifacts/; find the project that wrote it:`n$($stray -join "`n")" }
    if (-not $succeeded) {
        Write-Host "Build failed. Logs: $($logs -join ', ')"
        exit 1
    }
    Write-Host "Built: $(Get-Output $root)"
    Write-Host "Artifacts: $root"
}
finally { $lock.Dispose() }
