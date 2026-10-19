param(
    [string] $BinaryDirectory = "$PSScriptRoot/../../artifacts/bin/TinyTorrent/debug_win-x64"
)

$ErrorActionPreference = 'Stop'
$binary = (Resolve-Path -LiteralPath $BinaryDirectory).Path
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $binary 'TinyTorrentUI.dll'))
$flags = [Reflection.BindingFlags]'Instance,Public,NonPublic'
$strings = [Activator]::CreateInstance($assembly.GetType('Syno.TinyTorrent.Shared.Strings', $true))
$settingType = $assembly.GetType('Syno.TinyTorrent.Settings.Setting', $true)
$storeType = $assembly.GetType('Syno.TinyTorrent.Settings.SettingStore', $true)

# Compiled, because the setting calls these after a reply, possibly on a
# thread where script blocks cannot run.
function New-Constant([type] $Delegate, [type[]] $Parameters = @()) {
    $expression = [Linq.Expressions.Expression]
    $expression::Lambda(
        $Delegate,
        $expression::Constant($true),
        [Linq.Expressions.ParameterExpression[]]@($Parameters | ForEach-Object { $expression::Parameter($_) })
    ).Compile()
}

# The store records each saved value and leaves its reply to the check.
$saved = [Collections.Generic.List[object]]::new()
$replies = [Collections.Generic.List[Threading.Tasks.TaskCompletionSource]]::new()
$save = [Management.Automation.LanguagePrimitives]::ConvertTo(
    {
        param($setting, $value)
        $saved.Add($value)
        $reply = [Threading.Tasks.TaskCompletionSource]::new()
        $replies.Add($reply)
        $reply.Task
    },
    [Func``3].MakeGenericType($settingType, [object], [Threading.Tasks.Task]))
$store = ($storeType.GetConstructors($flags) | Where-Object { $_.GetParameters().Count -eq 4 }).Invoke(
    [object[]]@(
        (New-Constant ([Func[bool]])),
        (New-Constant ([Func[bool]])),
        $save,
        (New-Constant ([Func[Exception, bool]]) @([Exception]))
    ))
$kind = [Enum]::Parse($assembly.GetType('Syno.TinyTorrent.SettingKind'), 'Integer')
$category = [Enum]::Parse($assembly.GetType('Syno.TinyTorrent.SettingsCategory'), 'Network')
$setting = $settingType.GetConstructors($flags)[0].Invoke(
    [object[]]@($store, $strings, 'port', $kind, $category))

$setting.Input = '5000'
$commit = $setting.Commit()
$setting.Input = '6000'
$replies[0].SetException([InvalidOperationException]::new('The engine refused the port.'))
$commit.GetAwaiter().GetResult()
if ($saved.Count -ne 1 -or $saved[0] -ne 5000) {
    throw 'The setting did not save its committed input exactly once.'
}
if ($setting.IsPending -or $setting.Input -ne '6000' -or !$setting.HasDraft) {
    throw 'A refused earlier save discarded newer input.'
}
if ($null -ne $settingType.GetProperty('Failure', $flags).GetValue($setting)) {
    throw 'A refused earlier save marked newer input as refused.'
}
Write-Output 'PASS: a refused earlier save keeps newer input and does not mark it refused.'
