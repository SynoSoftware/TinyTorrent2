param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version,
    [ValidatePattern('^[A-Fa-f0-9]{40}$')]
    [string] $Certificate = $env:TINYTORRENT_CERTIFICATE
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repository = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $repository 'artifacts'
$release = Join-Path $artifacts 'release'
$cache = Join-Path $artifacts 'installer/tools'
$session = Join-Path $artifacts ('installer/' + [guid]::NewGuid().ToString('N'))
$publish = Join-Path $session 'app'
$lock = $null

function Reserve-Version([string] $File, [string] $Base) {
    $reader = [IO.StreamReader]::new($File, [Text.UTF8Encoding]::new($false))
    try {
        $source = $reader.ReadToEnd()
        $encoding = $reader.CurrentEncoding
    }
    finally { $reader.Dispose() }
    $pattern = [regex] '<Version\b[^>]*>(\d+\.\d+\.\d+(?:\.\d+)?)</Version>'
    $matches = $pattern.Matches($source)
    if ($matches.Count -ne 1) { throw 'Directory.Build.props must define one numeric product Version.' }
    $number = $matches[0].Groups[1]
    $current = [version] $number.Value
    if (-not $Base) { $Base = $current.ToString(3) }
    $revision = 0
    if ($Base -eq $current.ToString(3)) { $revision = [Math]::Max(0, $current.Revision) }
    if ($revision -ge 65534) { throw 'The build number is exhausted. Choose the next product version with -Version.' }
    $next = "$Base.$($revision + 1)"
    $source = $source.Remove($number.Index, $number.Length).Insert($number.Index, $next)
    [IO.File]::WriteAllText($File, $source, $encoding)
    return $next
}

function Invoke-Tool([string] $File, [string[]] $Arguments, [string] $Log) {
    $ErrorActionPreference = 'Continue'
    $null = New-Item -ItemType File -Path $Log -Force -ErrorAction Stop
    & $File @Arguments 2>&1 | ForEach-Object { "$_" } | Tee-Object -FilePath $Log -ErrorAction Stop | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "$(Split-Path $File -Leaf) failed. See $Log"
    }
}

function Get-Download([string] $Url, [string] $Name, [string] $Publisher) {
    $file = Join-Path $cache $Name
    $receipt = "$file.json"
    if ((Test-Path -LiteralPath $file) -and (Test-Path -LiteralPath $receipt)) {
        $saved = Get-Content -LiteralPath $receipt -Raw | ConvertFrom-Json
        if ($saved.Request -eq $Url -and (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -eq $saved.Hash) {
            $saved | Add-Member -NotePropertyName File -NotePropertyValue $file -Force
            return $saved
        }
    }
    Write-Host "Downloading $Name..."
    Add-Type -AssemblyName System.Net.Http
    $http = [Net.Http.HttpClient]::new()
    $http.Timeout = [TimeSpan]::FromSeconds(180)
    $response = $null
    $output = $null
    try {
        $response = $http.GetAsync($Url).GetAwaiter().GetResult()
        $null = $response.EnsureSuccessStatusCode()
        $output = [IO.File]::Create("$file.download.exe")
        $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult().CopyTo($output)
        $resolved = $response.RequestMessage.RequestUri.AbsoluteUri
    }
    finally {
        if ($output) { $output.Dispose() }
        if ($response) { $response.Dispose() }
        $http.Dispose()
    }
    $signature = Get-AuthenticodeSignature -LiteralPath "$file.download.exe"
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch [regex]::Escape($Publisher)) {
        throw "The signature of $Name could not be verified as $Publisher. Nothing was executed."
    }
    Move-Item -LiteralPath "$file.download.exe" -Destination $file -Force
    $saved = [pscustomobject]@{
        Request = $Url
        Url = $resolved
        Hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    }
    $saved | ConvertTo-Json | Set-Content -LiteralPath $receipt -Encoding UTF8
    $saved | Add-Member -NotePropertyName File -NotePropertyValue $file
    return $saved
}

function Get-Lane {
    $processes = @(Get-CimInstance Win32_Process | Where-Object {
        $_.Name -in 'MSBuild.exe', 'dotnet.exe' -and $_.CommandLine -notmatch '/nodemode:'
    })
    $running = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -and $_.Path.StartsWith($artifacts + '\', [StringComparison]::OrdinalIgnoreCase)
    })
    for ($number = 1; ; ++$number) {
        $path = $artifacts
        if ($number -gt 1) { $path = Join-Path $artifacts "lanes/$number" }
        $busy = @($running | Where-Object {
            if ($number -eq 1) { return $_.Path -notlike "$artifacts\lanes\*" }
            $_.Path.StartsWith($path + '\', [StringComparison]::OrdinalIgnoreCase)
        }).Count -gt 0
        foreach ($process in $processes) {
            if ($process.CommandLine -match '(?i)/(?:p|property):ArtifactsPath=(?:"([^"]+)"|([^\s]+))') {
                $output = $Matches[1]
                if (-not $output) { $output = $Matches[2] }
                if ($output.TrimEnd('\', '/') -eq $path) { $busy = $true }
            }
            elseif ($number -eq 1) { $busy = $true }
        }
        if (-not $busy) { return $path }
    }
}

function Check-Output {
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($repository)
    while ($pending.Count -gt 0) {
        foreach ($folder in Get-ChildItem -LiteralPath $pending.Pop() -Directory -Force) {
            if ($folder.Name -in 'artifacts', '3rdParty', '.git') { continue }
            if ($folder.Name -in 'bin', 'obj', 'bin-fl', 'TestResults') {
                throw "Generated output appeared outside artifacts: $($folder.FullName)"
            }
            $pending.Push($folder.FullName)
        }
    }
}

try {
    $null = New-Item -ItemType Directory -Path $release, $cache, $publish -Force
    try {
        $lock = [IO.File]::Open((Join-Path $artifacts 'installer/build.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    }
    catch { throw 'Another installer is being generated. Wait for it to finish, then try again.' }
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Write-Host 'Generating TinyTorrent installer' -ForegroundColor Cyan

    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) {
        throw 'Visual Studio Build Tools is missing. Install the C++ desktop and .NET desktop workloads used by this repository.'
    }
    $msbuild = @(& $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\amd64\MSBuild.exe') | Select-Object -First 1
    if (-not $msbuild) { throw 'Visual Studio MSBuild could not be found. Install the repository build prerequisites.' }
    if (-not (Test-Path -LiteralPath (Join-Path $repository '3rdParty/complete'))) {
        throw '3rdParty is incomplete. The repository owner must supply the compiled dependencies before generating an installer.'
    }
    $inno = Get-Download 'https://github.com/jrsoftware/issrc/releases/download/is-7_1_0/innosetup-7.1.0-x64.exe' 'innosetup-7.1.0-x64.exe' 'Pyrsys B.V.'
    $compilerDir = Join-Path $cache 'inno-7.1.0'
    $target = (Get-Item -LiteralPath $artifacts).Target
    if ($target) { $compilerDir = Join-Path $target[0] 'installer/tools/inno-7.1.0' }
    $compiler = Join-Path $compilerDir 'ISCC.exe'
    if (-not (Test-Path -LiteralPath $compiler)) {
        Write-Host 'Preparing portable Inno Setup...'
        $toolLog = Join-Path $session 'inno-prepare.log'
        $tool = Start-Process -FilePath $inno.File -ArgumentList "/CURRENTUSER /PORTABLE=1 /LANG=English /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOICONS /TASKS=`"`" /DIR=`"$compilerDir`" /LOG=`"$toolLog`"" -WindowStyle Hidden -Wait -PassThru
        if ($tool.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $compiler)) {
            throw "Portable Inno Setup could not be prepared (exit $($tool.ExitCode)). See $toolLog"
        }
    }

    $lane = Get-Lane
    Write-Host "Build output: $lane"
    $releaseVersion = Reserve-Version (Join-Path $repository 'Directory.Build.props') $Version
    Write-Host "Version: $releaseVersion"
    $properties = @('/p:Configuration=Release', '/p:Platform=x64', "/p:ArtifactsPath=$lane", "/p:Version=$releaseVersion")
    $buildArgs = @('/nologo', '/nr:false', '/v:minimal') + $properties
    Write-Host 'Building the native engine...'
    try {
        Invoke-Tool $msbuild (@((Join-Path $repository 'engine/src/Engine.vcxproj')) + $buildArgs +
            @('/fl', "/flp:logfile=$session/engine.log;verbosity=normal")) (Join-Path $session 'engine-console.log')
    }
    finally { Check-Output }
    Write-Host 'Publishing the WinUI application...'
    try {
        Invoke-Tool $msbuild (@((Join-Path $repository 'app/src/TinyTorrent.csproj')) + $buildArgs +
            @('/restore', '/t:Publish', '/p:RuntimeIdentifier=win-x64', '/p:SelfContained=false',
              '/p:WindowsAppSDKSelfContained=false', "/p:PublishDir=$publish/", '/fl',
              "/flp:logfile=$session/app.log;verbosity=normal")) (Join-Path $session 'app-console.log')
    }
    finally { Check-Output }

    $metadata = & $msbuild (Join-Path $repository 'app/src/TinyTorrent.csproj') @properties /nologo /nr:false '-getProperty:Version,EngineTargetName,WindowTargetName,ProjectAssetsFile,TargetPlatformMinVersion'
    if ($LASTEXITCODE -ne 0) { throw 'Release metadata could not be read from the application project.' }
    $metadata = ($metadata -join "`n" | ConvertFrom-Json).Properties
    $releaseVersion = $metadata.Version
    $engine = $metadata.EngineTargetName
    $window = $metadata.WindowTargetName
    $expected = [version] $releaseVersion
    foreach ($name in $engine, $window) {
        $file = Join-Path $publish "$name.exe"
        if (-not (Test-Path -LiteralPath $file)) { throw "The release is missing $name.exe." }
        $actual = [Diagnostics.FileVersionInfo]::GetVersionInfo($file)
        if ($actual.FileMajorPart -ne $expected.Major -or $actual.FileMinorPart -ne $expected.Minor -or
            $actual.FileBuildPart -ne $expected.Build -or $actual.FilePrivatePart -ne [Math]::Max(0, $expected.Revision)) {
            throw "$name.exe does not match release $releaseVersion. The installer was not generated."
        }
    }

    $assets = Get-Content -LiteralPath $metadata.ProjectAssetsFile -Raw | ConvertFrom-Json
    $packages = $assets.packageFolders.PSObject.Properties.Name | Select-Object -First 1
    $runtime = $assets.libraries.PSObject.Properties | Where-Object { $_.Name -like 'Microsoft.WindowsAppSDK.Runtime/*' } | Select-Object -First 1
    $runtimeDir = Join-Path $packages $runtime.Value.path
    $sdk = Get-Content -LiteralPath (Join-Path $runtimeDir 'WindowsAppSDK-VersionInfo.json') -Raw | ConvertFrom-Json
    if ($sdk.Release.Channel -ne 'stable') { throw 'The installer requires a stable Windows App SDK runtime.' }
    $runtimeVersion = ($runtime.Name -split '/')[1]
    $runtimeUrl = "https://aka.ms/windowsappsdk/$($sdk.Release.Major).$($sdk.Release.Minor)/$runtimeVersion/windowsappruntimeinstall-x64.exe"
    $windows = Get-Download $runtimeUrl "windows-runtime-$runtimeVersion-x64.exe" 'Microsoft Corporation'

    $config = Get-Content -LiteralPath (Join-Path $publish "$window.runtimeconfig.json") -Raw | ConvertFrom-Json
    $framework = $config.runtimeOptions.framework
    if ($framework.name -ne 'Microsoft.NETCore.App') { throw 'The release command needs updating for this .NET framework dependency.' }
    $netVersion = [version] $framework.version
    $channel = "$($netVersion.Major).$($netVersion.Minor)"
    $netReleases = Invoke-RestMethod "https://builds.dotnet.microsoft.com/dotnet/release-metadata/$channel/releases.json" -TimeoutSec 60
    $netRelease = $netReleases.releases | Where-Object { $_.runtime.version -eq $netReleases.'latest-runtime' } | Select-Object -First 1
    $netFile = $netRelease.runtime.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name -like '*.exe' } | Select-Object -First 1
    if (-not $netFile -or [version] $netRelease.runtime.version -lt $netVersion) {
        throw "A supported .NET $channel runtime installer could not be found."
    }
    $dotnet = Get-Download $netFile.url "dotnet-runtime-$($netRelease.runtime.version)-x64.exe" 'Microsoft Corporation'
    if ((Get-FileHash -LiteralPath $dotnet.File -Algorithm SHA512).Hash -ne $netFile.hash) {
        throw 'The .NET runtime download does not match Microsoft release metadata.'
    }
    $visualCpp = Get-Download 'https://aka.ms/vs/17/release/vc_redist.x64.exe' 'vc_redist.x64.exe' 'Microsoft Corporation'
    $vcVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($visualCpp.File)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $windowsPackages = @(Get-ChildItem -LiteralPath (Join-Path $runtimeDir 'tools/MSIX/win10-x64') -Filter '*.msix' | ForEach-Object {
        $archive = [IO.Compression.ZipFile]::OpenRead($_.FullName)
        $reader = $null
        try {
            $reader = [IO.StreamReader]::new($archive.GetEntry('AppxManifest.xml').Open())
            $identity = ([xml] $reader.ReadToEnd()).Package.Identity
        }
        finally {
            if ($reader) { $reader.Dispose() }
            $archive.Dispose()
        }
        $family = ([version] $identity.Version).Major.ToString() + '.*'
        [pscustomobject]@{
            Name = $identity.Name.Replace($identity.Version, $family)
            Version = $identity.Version
            PublisherId = $sdk.Runtime.Identity.PublisherId
        }
    })
    @{
        DotNet = $framework.version
        Windows = $windowsPackages
        VisualCpp = "$($vcVersion.FileMajorPart).$($vcVersion.FileMinorPart).$($vcVersion.FileBuildPart).$($vcVersion.FilePrivatePart)"
        Engine = "$engine.exe"
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $session 'requirements.json') -Encoding UTF8

    $licenses = Join-Path $publish 'licenses'
    $notices = @{
        TinyTorrent = 'LICENSE'; Branding = 'resources/LICENSE'; Lucide = 'lib/Lucide/LICENSE'
        Libtorrent = '3rdParty/libtorrent/COPYING'; OpenSSL = '3rdParty/openssl/LICENSE.txt'
        Boost = '3rdParty/boost/LICENSE_1_0.txt'; Json = '3rdParty/json/LICENSE.MIT'
    }
    foreach ($notice in $notices.GetEnumerator()) {
        $directory = Join-Path $licenses $notice.Key
        $null = New-Item -ItemType Directory -Path $directory -Force
        Copy-Item -LiteralPath (Join-Path $repository $notice.Value) -Destination $directory
    }
    foreach ($package in $assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' }) {
        $directory = Join-Path $packages $package.Value.path
        $files = @(Get-ChildItem -LiteralPath $directory -File | Where-Object { $_.Name -match '^(LICENSE|NOTICE|COPYING)' })
        if ($files.Count -gt 0) {
            $destination = Join-Path $licenses ($package.Name -split '/')[0]
            $null = New-Item -ItemType Directory -Path $destination -Force
            $files | Copy-Item -Destination $destination
        }
    }

    $installerName = "TinyTorrent-$releaseVersion-x64-Setup"
    $minimum = ([version] $metadata.TargetPlatformMinVersion).ToString(3)
    $compilerArgs = @('/Qp', "/DAppVersion=$releaseVersion", "/DMinWindows=$minimum",
        "/DEngineName=$engine", "/DSourceDir=$publish", "/DRuntimeDir=$session", "/DOutputDir=$session",
        "/DDotNetUrl=$($dotnet.Url)", "/DDotNetHash=$($dotnet.Hash)",
        "/DWindowsUrl=$($windows.Url)", "/DWindowsHash=$($windows.Hash)",
        "/DVisualCppUrl=$($visualCpp.Url)", "/DVisualCppHash=$($visualCpp.Hash)")
    if ($Certificate) {
        $sdkBin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
        $signTool = Get-ChildItem -LiteralPath $sdkBin -Directory | Sort-Object Name -Descending |
            ForEach-Object { Join-Path $_.FullName 'x64/signtool.exe' } |
            Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
        if (-not $signTool) { throw 'Windows SDK SignTool is missing. Install the Windows SDK to sign this release.' }
        $signing = @('sign', '/sha1', $Certificate, '/fd', 'SHA256', '/tr', 'http://timestamp.digicert.com', '/td', 'SHA256')
        foreach ($name in "$engine.exe", "$window.exe", "$window.dll", 'TableView.dll', 'Lucide.dll') {
            Invoke-Tool $signTool ($signing + (Join-Path $publish $name)) (Join-Path $session "$name-sign.log")
        }
        $signCommand = '$q' + $signTool + '$q sign /sha1 ' + $Certificate + ' /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $f'
        $compilerArgs += @('/DSigned', "/STinyTorrent=$signCommand")
    }
    else {
        $installerName += '-unsigned'
        Write-Host 'No signing certificate configured. Generating an unsigned installer for testing.' -ForegroundColor Yellow
    }
    Write-Host 'Compiling the installer...'
    Invoke-Tool $compiler ($compilerArgs + "/DInstallerName=$installerName" + (Join-Path $PSScriptRoot 'Setup.iss')) (Join-Path $session 'installer.log')
    $installer = Join-Path $session "$installerName.exe"
    if ($Certificate -and (Get-AuthenticodeSignature -LiteralPath $installer).Status -ne 'Valid') {
        throw 'The generated installer signature is invalid. Nothing was placed in the release folder.'
    }
    $destination = Join-Path $release "$installerName.exe"
    Move-Item -LiteralPath $installer -Destination $destination -Force
    $hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $installerName.exe" | Set-Content -LiteralPath "$destination.sha256" -Encoding ASCII
    Write-Host "`nInstaller ready: $destination" -ForegroundColor Green
    Write-Host "Build logs: $session"
}
catch {
    Write-Host "`n$($_.Exception.Message)" -ForegroundColor Red
    Write-Host "Build logs: $session"
    exit 1
}
finally {
    if ($lock) { $lock.Dispose() }
}
