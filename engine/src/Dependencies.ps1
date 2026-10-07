# Creates 3rdParty at the repository root: the engine's dependencies as git
# checkouts at the pinned revisions below, the build tools that Visual Studio lacks,
# and the static libraries and headers the engine builds against. The
# arrangement and its reasons are in docs/architecture.md, "Third-party
# dependencies".
#
# Only the repository owner runs this script; a build never starts it. The
# first run creates 3rdParty. After a pin, URL or option below changes, run it
# with -Update: each step records what it was done for in 3rdParty\<step>.pin,
# so only the changed steps and the steps built against them run again.

param([switch] $Update)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$libtorrentUrl = 'https://github.com/SynoSoftware/libtorrent.git'
$libtorrentCommit = 'a84871f1e8d50472232e242db9bf4881367fd58e'
$opensslTag = 'openssl-3.6.4'
$boostTag = 'boost-1.92.0'
$jsonTag = 'v3.12.0'

# Strawberry Perl, because OpenSSL's Configure needs a native Windows Perl for
# Visual C++ targets; the MSYS and Git for Windows Perls do not work.
$perlUrl = 'https://github.com/StrawberryPerl/Perl-Dist-Strawberry/releases/download/SP_54221_64bit/strawberry-perl-5.42.2.1-64bit-portable.zip'
$perlSha256 = '32D83BE90CF04B807CFB9477482BC36302CDEE6F5B04CF57E81ADECBD8F07898'
$nasmUrl = 'https://www.nasm.us/pub/nasm/releasebuilds/3.01/win64/nasm-3.01-win64.zip'
$nasmSha256 = 'E0BA5157007ABC7B1A65118A96657A961DDF55F7E3F632EE035366DFCE039CA4'

# Static libraries for the static C runtime, as the engine links. Neither build
# uses /GL: a library compiled with /GL refuses to link after a compiler update,
# while one compiled without it links with every later compiler.
$opensslOptions = @('VC-WIN64A', 'no-shared', 'no-module', 'enable-static-engine', 'enable-capieng',
    'no-apps', 'no-tests', 'no-docs', 'no-makedepend')
# deprecated-functions=OFF builds the TORRENT_ABI_VERSION=100 interface without
# deprecated functions. WebTorrent would add a WebRTC stack the product does not use.
$libtorrentOptions = @('-DBUILD_SHARED_LIBS=OFF', '-Dstatic_runtime=ON', '-Ddeprecated-functions=OFF',
    '-Dwebtorrent=OFF')

$configurations = @('Release', 'Debug')
$root = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) '3rdParty'
$tools = Join-Path $root 'tools'
$build = Join-Path $root 'build'
$complete = Join-Path $root 'complete'
$perl = Join-Path $tools 'perl\bin\perl.exe'

# Runs a program and stops the script when it fails. Error output is passed on
# as text, because Windows PowerShell otherwise turns it into errors that stop
# the script while the program is still working.
function Invoke-Native {
    $program = $args[0]
    $arguments = @($args | Select-Object -Skip 1)
    $ErrorActionPreference = 'Continue'
    & $program @arguments 2>&1 | ForEach-Object {
        if ($_ -is [Management.Automation.ErrorRecord]) { Write-Host $_.TargetObject } else { Write-Host $_ }
    }
    if ($LASTEXITCODE -ne 0) { throw "$program $arguments failed with exit code $LASTEXITCODE." }
}

# Runs $work unless 3rdParty\$name.pin already holds $pin. The completion record
# is removed first, so an interrupted run never looks like a usable installation.
function Invoke-Step([string] $name, [string] $pin, [scriptblock] $work) {
    $record = Join-Path $root "$name.pin"
    if ((Test-Path $record) -and [IO.File]::ReadAllText($record) -eq $pin) { return }
    Write-Host "3rdParty: $name"
    Remove-Item $complete, $record -ErrorAction SilentlyContinue
    Remove-Item $build -Recurse -Force -ErrorAction SilentlyContinue
    $null = New-Item -ItemType Directory $build
    & $work
    Remove-Item $build -Recurse -Force
    [IO.File]::WriteAllText($record, $pin)
}

function Save-Archive([string] $url, [string] $sha256) {
    $archive = Join-Path $build 'download.zip'
    $extracted = Join-Path $build 'download'
    Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $archive
    $hash = (Get-FileHash -Algorithm SHA256 $archive).Hash
    if ($hash -ne $sha256) { throw "$url has SHA-256 $hash; the pin is $sha256." }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($archive, $extracted)
    return $extracted
}

function Sync-Checkout([string] $name, [string] $url, [string] $revision, [switch] $Submodules) {
    $directory = Join-Path $root $name
    $checkout = $directory
    $atRevision = $false
    if (Test-Path $directory) {
        # A failed submodule update can leave clean children at their previous
        # revisions. Refuse source edits, but allow those checkouts to catch up.
        $changes = @(git -C $directory status --porcelain --ignore-submodules=all)
        if ($LASTEXITCODE -ne 0) { throw "Cannot inspect $directory." }
        $changes += @(git -C $directory diff --cached --name-only --ignore-submodules=none)
        if ($LASTEXITCODE -ne 0) { throw "Cannot inspect staged changes in $directory." }
        if ($Submodules) {
            $changes += @(git -C $directory submodule foreach --quiet --recursive `
                'git status --porcelain --ignore-submodules=all && git diff --cached --name-only --ignore-submodules=none')
            if ($LASTEXITCODE -ne 0) { throw "Cannot inspect submodules in $directory." }
        }
        if ($changes) { throw "$directory has uncommitted changes." }
        Invoke-Native git -C $directory remote set-url origin $url
        $commit = git -C $directory rev-parse --verify --quiet "$revision^{commit}"
        $atRevision = $LASTEXITCODE -eq 0 -and (git -C $directory rev-parse HEAD) -eq $commit
    }
    else {
        # Prepared separately and moved when complete, so an interrupted fetch
        # never stands in for a checkout.
        $checkout = Join-Path $build $name
        Invoke-Native git init --quiet $checkout
        Invoke-Native git -C $checkout remote add origin $url
    }
    if (-not $atRevision) {
        Invoke-Native git -C $checkout fetch --quiet --depth 1 origin $revision
        Invoke-Native git -C $checkout checkout --quiet --detach FETCH_HEAD
    }
    if ($Submodules) {
        Invoke-Native git -C $checkout submodule sync --quiet --recursive
        Invoke-Native git -C $checkout submodule update --quiet --init --recursive --depth 1
    }
    if ($checkout -ne $directory) { Move-Item $checkout $directory }
}

# Writes libtorrent's public compile definitions for one configuration, from
# the definitions its build exported, to the file Engine.vcxproj reads.
function Write-Definitions([string] $configuration) {
    $prefix = Join-Path $root $configuration
    $exported = Get-Content -Raw (Join-Path $prefix 'lib\cmake\LibtorrentRasterbar\LibtorrentRasterbarTargets.cmake')
    if ($exported -notmatch 'INTERFACE_COMPILE_DEFINITIONS "([^"]*)"') { throw 'libtorrent exported no compile definitions.' }
    # The exported file escapes each $ of a generator expression as \$.
    $definitions = foreach ($definition in $Matches[1].Replace('\$', '$') -split ';') {
        if ($definition -match '^\$<\$<CONFIG:(\w+)>:([^$<>]+)>$') {
            if ($Matches[1] -eq $configuration) { $Matches[2] }
        }
        elseif ($definition.Contains('$<')) {
            throw "libtorrent exported the definition $definition, which this script cannot evaluate."
        }
        else { $definition }
    }
    [IO.File]::WriteAllLines((Join-Path $prefix 'libtorrent.definitions'), [string[]] $definitions)
}

# A complete installation is rebuilt only on request, so a mistaken run costs
# nothing.
if ((Test-Path $complete) -and -not $Update) {
    Write-Host '3rdParty is complete; nothing was downloaded or built. After changing a pin, run with -Update.'
    exit 0
}

$null = New-Item -ItemType Directory -Force $root

# The Visual Studio environment scripts call vswhere from the path.
$env:PATH = (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer') + ';' + $env:PATH
$visualStudio = vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
Import-Module (Join-Path $visualStudio 'Common7\Tools\Microsoft.VisualStudio.DevShell.dll')
Enter-VsDevShell -VsInstallPath $visualStudio -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64 -no_logo'
$cmake = Join-Path $visualStudio 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
$ninja = Join-Path $visualStudio 'Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe'
# OpenSSL's Configure finds NASM on the path.
$env:PATH = (Join-Path $tools 'nasm') + ';' + $env:PATH

Invoke-Step 'perl' "$perlUrl $perlSha256" {
    $extracted = Save-Archive $perlUrl $perlSha256
    $directory = Join-Path $tools 'perl'
    Remove-Item $directory -Recurse -Force -ErrorAction SilentlyContinue
    $null = New-Item -ItemType Directory $directory
    Move-Item (Join-Path $extracted 'perl\bin'), (Join-Path $extracted 'perl\lib'), (Join-Path $extracted 'licenses') $directory
}

Invoke-Step 'nasm' "$nasmUrl $nasmSha256" {
    $extracted = Save-Archive $nasmUrl $nasmSha256
    $directory = Join-Path $tools 'nasm'
    Remove-Item $directory -Recurse -Force -ErrorAction SilentlyContinue
    Move-Item (Get-ChildItem $extracted -Directory).FullName $directory
}

Invoke-Step 'json' $jsonTag {
    Sync-Checkout 'json' 'https://github.com/nlohmann/json.git' $jsonTag
}

# Boost is used for its headers only. "b2 headers" links each library's headers
# into the checkout's boost folder, which the builds include.
Invoke-Step 'boost' $boostTag {
    Sync-Checkout 'boost' 'https://github.com/boostorg/boost.git' $boostTag -Submodules
    Push-Location (Join-Path $root 'boost')
    # Boost's bootstrap calls its own scripts from the current folder, which
    # NoDefaultCurrentDirectoryInExePath forbids.
    Invoke-Native cmd /c 'set NoDefaultCurrentDirectoryInExePath=&& .\bootstrap.bat'
    Invoke-Native .\b2.exe headers
    Pop-Location
}

$opensslPin = "$opensslTag $opensslOptions"
Invoke-Step 'openssl' $opensslPin {
    Sync-Checkout 'openssl' 'https://github.com/openssl/openssl.git' $opensslTag
    foreach ($configuration in $configurations) {
        $directory = Join-Path $build "openssl-$configuration"
        $staging = Join-Path $directory 'staging'
        $debug = @(if ($configuration -eq 'Debug') { '--debug' })
        $null = New-Item -ItemType Directory $directory
        Push-Location $directory
        Invoke-Native $perl (Join-Path $root 'openssl\Configure') @opensslOptions @debug
        Invoke-Native nmake /nologo
        # The default prefix stays compiled in and the files are staged with
        # DESTDIR: a prefix in 3rdParty would make OpenSSL search a developer
        # path at runtime on users' machines (CVE-2019-12572).
        Invoke-Native nmake /nologo install_dev "DESTDIR=$staging"
        Pop-Location
        $installed = (Get-Item (Join-Path $staging 'Program*\OpenSSL')).FullName
        $prefix = Join-Path $root $configuration
        $null = New-Item -ItemType Directory -Force $prefix
        Copy-Item (Join-Path $installed 'include'), (Join-Path $installed 'lib') $prefix -Recurse -Force
    }
}

# libtorrent compiles against OpenSSL's and Boost's headers, so their pins are
# part of its record and changing them rebuilds it.
Invoke-Step 'libtorrent' "$libtorrentUrl $libtorrentCommit $libtorrentOptions; $opensslPin; $boostTag" {
    Sync-Checkout 'libtorrent' $libtorrentUrl $libtorrentCommit -Submodules
    foreach ($configuration in $configurations) {
        $prefix = Join-Path $root $configuration
        $directory = Join-Path $build "libtorrent-$configuration"
        # Debug information is embedded in the library, so removing the build
        # tree leaves no reference to a missing PDB.
        Invoke-Native $cmake -S (Join-Path $root 'libtorrent') -B $directory -G Ninja -Wno-dev `
            "-DCMAKE_MAKE_PROGRAM=$ninja" "-DCMAKE_BUILD_TYPE=$configuration" "-DCMAKE_INSTALL_PREFIX=$prefix" `
            '-DCMAKE_POLICY_DEFAULT_CMP0141=NEW' '-DCMAKE_MSVC_DEBUG_INFORMATION_FORMAT=Embedded' `
            "-DOPENSSL_ROOT_DIR=$prefix" '-DOPENSSL_USE_STATIC_LIBS=ON' "-DBoost_INCLUDE_DIR=$(Join-Path $root 'boost')" `
            @libtorrentOptions
        Invoke-Native $cmake --build $directory
        Invoke-Native $cmake --install $directory
        Write-Definitions $configuration
    }
}

[IO.File]::WriteAllText($complete, "Every step in engine\src\Dependencies.ps1 completed.`n")
