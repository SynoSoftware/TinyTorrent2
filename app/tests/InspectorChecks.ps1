param(
    [string] $BinaryDirectory = "$PSScriptRoot/../../artifacts/bin/TinyTorrent/debug_win-x64"
)

$ErrorActionPreference = 'Stop'
$binary = (Resolve-Path -LiteralPath $BinaryDirectory).Path
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $binary 'TinyTorrentUI.dll'))

Add-Type -TypeDefinition @'
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

public static class InspectorChecks
{
    public static void Run(Assembly assembly)
    {
        var type = assembly.GetType("Syno.TinyTorrent.Shell.Inspector", true);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        // Cancellation needs no window or engine; the constructor creates their bindings.
        var inspector = RuntimeHelpers.GetUninitializedObject(type);
        var field = type.GetField("_reading", flags);
        var reading = new CancellationTokenSource();
        var token = reading.Token;
        field.SetValue(inspector, reading);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var caller = Environment.CurrentManagedThreadId;
        var callback = 0;
        var registration = token.Register(() =>
        {
            callback = Environment.CurrentManagedThreadId;
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
        });
        try
        {
            type.GetMethod("CancelReading", flags).Invoke(inspector, null);
            if (!token.IsCancellationRequested)
                throw new InvalidOperationException("The obsolete read was not cancelled immediately.");
            var replacement = (CancellationTokenSource)field.GetValue(inspector);
            if (ReferenceEquals(reading, replacement) || replacement.IsCancellationRequested)
                throw new InvalidOperationException("The next inspector read has no fresh lifetime.");
            if (!entered.Wait(TimeSpan.FromSeconds(5)))
                throw new InvalidOperationException("The obsolete read's cancellation callback did not run.");
            if (callback == caller)
                throw new InvalidOperationException("Inspector cancellation ran its callback inside selection.");
        }
        finally
        {
            release.Set();
            registration.Dispose();
            ((CancellationTokenSource)field.GetValue(inspector)).Dispose();
        }
    }
}
'@

[InspectorChecks]::Run($assembly)
'Inspector cancellation checks passed.'
