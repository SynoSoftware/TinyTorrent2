# Starts, commands and stops a disposable engine over its pipe. Set $executable
# and $directory before calling Start-Engine.

Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
public static class CheckIdentity {
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint processId);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetTokenInformation(IntPtr token, int information, IntPtr buffer, int size, out int needed);
    public static uint ServerProcessId(IntPtr pipe) {
        if (!GetNamedPipeServerProcessId(pipe, out var processId)) throw new Win32Exception();
        return processId;
    }
    public static string LogonSid() {
        using var identity = WindowsIdentity.GetCurrent();
        var token = identity.AccessToken.DangerousGetHandle();
        GetTokenInformation(token, 28, IntPtr.Zero, 0, out var size);
        var buffer = Marshal.AllocHGlobal(size);
        try {
            if (!GetTokenInformation(token, 28, buffer, size, out size)) throw new Win32Exception();
            return new SecurityIdentifier(Marshal.ReadIntPtr(buffer, IntPtr.Size)).Value;
        } finally { Marshal.FreeHGlobal(buffer); }
    }
}
'@
$logon = [CheckIdentity]::LogonSid()
$script:sequence = 0
$script:process = $null
$script:pipe = $null

function Assert([bool] $condition, [string] $failure) {
    if (-not $condition) { throw $failure }
}

function Read-Bytes([IO.Stream] $stream, [int] $count) {
    $bytes = [byte[]]::new($count)
    $offset = 0
    while ($offset -lt $count) {
        $read = $stream.ReadAsync($bytes, $offset, $count - $offset)
        if (-not $read.Wait(10000)) { $stream.Dispose(); throw 'Pipe reply did not arrive' }
        if ($read.Result -eq 0) { throw 'Pipe disconnected during a frame' }
        $offset += $read.Result
    }
    return ,$bytes
}

function Read-Frame([IO.Stream] $stream) {
    $header = Read-Bytes $stream 4
    $size = [BitConverter]::ToInt32($header, 0)
    Assert ($size -gt 0 -and $size -le 16777216) 'Reply frame violates its limit'
    $bytes = Read-Bytes $stream $size
    return [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
}

function Connect-Pipe {
    $stream = [IO.Pipes.NamedPipeClientStream]::new('.', "TinyTorrent.$logon",
        [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous)
    $stream.Connect(10000)
    if ([CheckIdentity]::ServerProcessId($stream.SafePipeHandle.DangerousGetHandle()) -ne $script:process.Id) {
        $stream.Dispose()
        throw 'TinyTorrent is already running. The check refuses to command an engine it did not start.'
    }
    $hello = Read-Frame $stream
    Assert ($hello.type -eq 'hello' -and $hello.version -eq 8) 'Invalid version handshake'
    return $stream
}

function Send-Command([hashtable] $fields) {
    $fields.request_id = ++$script:sequence
    $bytes = [Text.Encoding]::UTF8.GetBytes(($fields | ConvertTo-Json -Depth 20 -Compress))
    $header = [BitConverter]::GetBytes([int]$bytes.Length)
    $script:pipe.Write($header, 0, 4)
    $script:pipe.Write($bytes, 0, $bytes.Length)
    $reply = Read-Frame $script:pipe
    Assert ($reply.request_id -eq $fields.request_id) 'Reply belongs to another request'
    return $reply
}

function Start-Engine {
    $script:process = Start-Process -FilePath $executable -ArgumentList @('--headless', '--background', '--data', ('"' + $directory + '"')) -WindowStyle Hidden -PassThru
    $script:pipe = Connect-Pipe
    $until = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $reply = Send-Command @{ command = 'snapshot' }
        Assert $reply.ok 'Engine refused its initial snapshot'
        if (-not $reply.data.loading) {
            Assert (-not $reply.data.storage_failed) 'New store could not load'
            return $reply.data
        }
        [Threading.Thread]::Yield() | Out-Null
    } while ([DateTime]::UtcNow -lt $until)
    throw 'Engine startup did not become ready'
}

function Stop-Engine {
    if ($script:pipe) {
        try { $null = Send-Command @{ command = 'exit' } }
        finally { $script:pipe.Dispose(); $script:pipe = $null }
    }
    if ($script:process) {
        Assert ($script:process.WaitForExit(15000)) 'Coordinated Exit did not stop the engine'
        Assert ($script:process.ExitCode -eq 0) 'Final storage commit failed'
        $script:process.Dispose()
        $script:process = $null
    }
}

function Payload-Hash([string] $path) {
    $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { return [Convert]::ToHexString($hasher.ComputeHash($stream)) }
    finally { $hasher.Dispose(); $stream.Dispose() }
}
