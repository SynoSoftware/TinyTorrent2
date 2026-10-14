param(
    [Parameter(Mandatory)][string] $Cache,
    [Parameter(Mandatory)][string] $Destination
)

$ErrorActionPreference = 'Stop'

# DB-IP publishes its free country data once a month under a fixed name, so
# this month's file is fetched once and every later build reuses it. The
# previous month stands in during the first hours of a month, before DB-IP
# publishes the new file.
$null = New-Item -ItemType Directory -Path $Cache -Force
$source = $null
foreach ($month in 0, -1) {
    $name = 'dbip-country-lite-' + [DateTime]::UtcNow.AddMonths($month).ToString('yyyy-MM')
    $file = Join-Path $Cache "$name.mmdb"
    if (Test-Path -LiteralPath $file) {
        $source = $file
        break
    }
    $address = "https://download.db-ip.com/free/$name.mmdb.gz"
    $archive = "$file.gz"
    try {
        Invoke-WebRequest -Uri $address -OutFile $archive -TimeoutSec 60
    }
    catch {
        Write-Host "Country data is not available at $address : $($_.Exception.Message)"
        continue
    }
    try {
        $packed = [IO.File]::OpenRead($archive)
        $unpacked = [IO.File]::Create("$file.part")
        try {
            $gzip = [IO.Compression.GZipStream]::new($packed, [IO.Compression.CompressionMode]::Decompress)
            $gzip.CopyTo($unpacked)
            $gzip.Dispose()
        }
        finally {
            $unpacked.Dispose()
            $packed.Dispose()
        }
        Move-Item -LiteralPath "$file.part" -Destination $file -Force
    }
    finally {
        Remove-Item -LiteralPath $archive, "$file.part" -Force -ErrorAction SilentlyContinue
    }
    $source = $file
    break
}
if (-not $source) {
    $newest = Get-ChildItem -LiteralPath $Cache -Filter 'dbip-country-lite-*.mmdb' |
        Sort-Object -Property Name -Descending | Select-Object -First 1
    if (-not $newest) {
        throw "Country data is not available and DB-IP cannot be reached. Download https://download.db-ip.com/free/dbip-country-lite-YYYY-MM.mmdb.gz, unpack it into '$Cache', then build again."
    }
    Write-Warning "DB-IP cannot be reached, so the build uses the older country data $($newest.Name)."
    $source = $newest.FullName
}

if ((Test-Path -LiteralPath $Destination) -and
    (Get-FileHash -LiteralPath $source).Hash -eq (Get-FileHash -LiteralPath $Destination).Hash) {
    return
}
$null = New-Item -ItemType Directory -Path (Split-Path -Path $Destination -Parent) -Force
Copy-Item -LiteralPath $source -Destination $Destination -Force
