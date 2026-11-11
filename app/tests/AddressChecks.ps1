param(
    [string] $BinaryDirectory = "$PSScriptRoot/../../artifacts/bin/TinyTorrent/debug_win-x64"
)

$ErrorActionPreference = 'Stop'
$binary = (Resolve-Path -LiteralPath $BinaryDirectory).Path
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $binary 'TinyTorrentUI.dll'))
$type = $assembly.GetType('Syno.TinyTorrent.Torrents.PeerAddress', $true)
$compare = $type.GetMethod('CompareTo', [Type[]]@($type))
$flags = [Reflection.BindingFlags]'Instance,NonPublic'

function New-Address([string] $Text) {
    [Activator]::CreateInstance($type, $flags, $null, [object[]]@($Text), $null)
}

# The Address column's order, each address before the next. Text order would
# put 198.67.9.9 between 198.67.9.89 and 198.67.9.90, and 9.9.9.9 last of the
# IPv4 addresses. "1.2" is a legacy short form, so it stays text.
$order = @(
    '9.9.9.9',
    '198.67.9.9',
    '198.67.9.89',
    '198.67.9.90',
    '2001:db8::9',
    '2001:db8::10',
    '2001:db8::a:0',
    '2001:db8:0:1::',
    'fe80::1',
    '1.2',
    'abc'
)
for ($index = 1; $index -lt $order.Count; $index++) {
    $left = New-Address $order[$index - 1]
    $right = New-Address $order[$index]
    if ($compare.Invoke($left, [object[]]@($right)) -ge 0) {
        throw "$($order[$index - 1]) does not sort before $($order[$index])."
    }
}

'Peer address order checks passed.'
