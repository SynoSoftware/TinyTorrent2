#nullable enable

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Syno.TinyTorrent.Shell;

public sealed class PerformanceInput : IDisposable
{
    public sealed record Mouse(int X, int Y, uint Data, uint Flags);
    public sealed record Step(double At, Mouse[] Inputs, int? Click = null);
    public sealed record Plan(long Window, int Milliseconds, Mouse[] Before, Step[] Steps);
    public sealed record Result(long Started, long Ended, double WorstLatenessMs, long[] Clicks, long[] ClickTimes, long[] Delivered, string? Failure);
    private sealed record Probe(long Sent, long Completed, bool Answered);

    private readonly nint _window;
    private readonly int _left = GetSystemMetrics(76), _top = GetSystemMetrics(77);
    private readonly int _width = GetSystemMetrics(78), _height = GetSystemMetrics(79);
    public int RefreshHz { get; }

    public PerformanceInput(nint window)
    {
        _window = window;
        var dc = GetDC(window);
        try { RefreshHz = GetDeviceCaps(dc, 116); } finally { ReleaseDC(window, dc); }
    }

    public void Check()
    {
        if ((GetAsyncKeyState(27) & 0x8000) != 0) throw new OperationCanceledException("Escape stopped input.");
        if (GetForegroundWindow() != _window) throw new InvalidOperationException("The benchmark lost foreground focus.");
    }

    public Vector2 Screen(Vector2 point)
    {
        var origin = new NativePoint();
        if (!ClientToScreen(_window, ref origin)) throw new System.ComponentModel.Win32Exception();
        return point + new Vector2(origin.X, origin.Y);
    }

    private Mouse At(Vector2 point) => new(
        (int)Math.Round((point.X - _left) * 65535 / (_width - 1)),
        (int)Math.Round((point.Y - _top) * 65535 / (_height - 1)), 0, 0xC001);

    public void Position(Vector2 point) { Check(); Send([At(point)]); }

    public void Keys(params ushort[] keys)
    {
        Check();
        try { foreach (var key in keys) Send(new NativeInput { Type = 1, Key = key }); }
        finally { foreach (var key in keys.Reverse()) Send(new NativeInput { Type = 1, Key = key, KeyFlags = 2 }); }
    }

    public Plan Idle(int milliseconds) => new((long)_window, milliseconds, [], []);

    public Plan Drag(int milliseconds, Vector2 from, Vector2 to)
    {
        var count = milliseconds * 120 / 1000;
        var steps = Enumerable.Range(0, count + 1).Select(index => {
            var position = At(Vector2.Lerp(from, to, (float)index / count));
            return new Step(index * milliseconds / (double)count,
                index == 0 ? [position, new Mouse(0, 0, 0, 2)] : [position]);
        }).ToArray();
        return new((long)_window, milliseconds, [At(from)], steps);
    }

    public Plan Zoom(int milliseconds, int delta)
    {
        var count = milliseconds * 120 / 1000;
        var previous = 0;
        var steps = Enumerable.Range(0, count + 1).Select(index => {
            var next = (int)Math.Round(delta * index / (double)count);
            var wheel = next - previous;
            previous = next;
            return new Step(index * milliseconds / (double)count,
                wheel == 0 ? [] : [new Mouse(0, 0, unchecked((uint)wheel), 0x800)]);
        }).ToArray();
        return new((long)_window, milliseconds, [], steps);
    }

    public Plan Clicks(Vector2[] positions, int interval) => new((long)_window, positions.Length * interval,
        [At(positions[0])], positions.Select((position, index) => new Step(index * interval,
            [At(position), new Mouse(0, 0, 0, 2), new Mouse(0, 0, 0, 4)], index)).ToArray());

    public async Task<Result> Run(Plan plan, string directory)
    {
        var request = Path.Combine(directory, "input-command.json");
        var resultPath = Path.Combine(directory, "input-result.json");
        File.Delete(resultPath);
        await File.WriteAllTextAsync(request + ".tmp", JsonSerializer.Serialize(plan));
        File.Move(request + ".tmp", request, true);
        var waiting = Stopwatch.StartNew();
        while (!File.Exists(resultPath))
        {
            if (waiting.ElapsedMilliseconds > plan.Milliseconds + 10000)
                throw new TimeoutException("The external input driver did not complete its plan.");
            await Task.Delay(1);
        }
        Result result;
        await using (var stream = new FileStream(resultPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            result = (await JsonSerializer.DeserializeAsync<Result>(stream))!;
        File.Delete(resultPath);
        if (result.Failure is { } failure) throw new InvalidOperationException(failure);
        return result;
    }

    public static void Work(string directory, int parentPid)
    {
        using var parent = Process.GetProcessById(parentPid);
        using var stopping = new CancellationTokenSource();
        var source = Task.Run(() => PerformanceSource.Serve(directory, stopping.Token));
        var probes = new ConcurrentQueue<Probe>();
        Task? probing = null;
        var request = Path.Combine(directory, "input-command.json");
        var resultPath = Path.Combine(directory, "input-result.json");
        File.WriteAllText(Path.Combine(directory, "input-ready"), "");
        try
        {
            while (!parent.HasExited && !File.Exists(Path.Combine(directory, "input-stop")))
            {
                if (source.IsFaulted) source.GetAwaiter().GetResult();
                if (!File.Exists(request)) { Thread.Sleep(1); continue; }
                Result result;
                try
                {
                    Plan plan;
                    using (var stream = new FileStream(request, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        plan = JsonSerializer.Deserialize<Plan>(stream)!;
                    File.Delete(request);
                    probing ??= Task.Run(() => {
                        while (!stopping.IsCancellationRequested && IsWindow((nint)plan.Window))
                        {
                            var sent = Stopwatch.GetTimestamp();
                            var answered = SendMessageTimeout((nint)plan.Window, 0, 0, 0, 2, 1000, out _) != 0;
                            probes.Enqueue(new(sent, Stopwatch.GetTimestamp(), answered));
                            stopping.Token.WaitHandle.WaitOne(16);
                        }
                    });
                    using var input = new PerformanceInput((nint)plan.Window);
                    result = input.Perform(plan);
                }
                catch (Exception error) { result = new(0, 0, 0, [], [], [], error.ToString()); }
                File.WriteAllText(resultPath + ".tmp", JsonSerializer.Serialize(result));
                File.Move(resultPath + ".tmp", resultPath, true);
            }
        }
        finally
        {
            stopping.Cancel();
            try
            {
                try { source.GetAwaiter().GetResult(); } catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
            }
            finally
            {
                probing?.GetAwaiter().GetResult();
                File.WriteAllText(Path.Combine(directory, "ui-probes.json"), JsonSerializer.Serialize(probes.ToArray()));
            }
        }
    }

    private Result Perform(Plan plan)
    {
        Check();
        Send(plan.Before);
        if (plan.Before.Length > 0) Thread.Sleep(50);
        var clicks = new long[plan.Steps.Count(step => step.Click is not null)];
        var clickTimes = new long[clicks.Length];
        var delivered = new long[plan.Steps.Length];
        var priority = Thread.CurrentThread.Priority;
        if (timeBeginPeriod(1) != 0) throw new InvalidOperationException("The input timer could not request 1 ms resolution.");
        try
        {
            Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
            var start = Stopwatch.GetTimestamp();
            var worst = 0d;
            for (var index = 0; index < plan.Steps.Length; index++)
            {
                var step = plan.Steps[index];
                Wait(start, step.At);
                Check();
                var now = Stopwatch.GetTimestamp();
                delivered[index] = now;
                if (step.Click is { } click)
                {
                    clicks[click] = now;
                    clickTimes[click] = Environment.TickCount64 * 1000;
                }
                worst = Math.Max(worst, (now - start) * 1000d / Stopwatch.Frequency - step.At);
                Send(step.Inputs);
            }
            Wait(start, plan.Milliseconds);
            Check();
            return new(start, Stopwatch.GetTimestamp(), worst, clicks, clickTimes, delivered, null);
        }
        finally { Thread.CurrentThread.Priority = priority; timeEndPeriod(1); }
    }

    private void Wait(long start, double milliseconds)
    {
        while ((Stopwatch.GetTimestamp() - start) * 1000d / Stopwatch.Frequency < milliseconds)
        {
            Check();
            Thread.Sleep(1);
        }
    }

    private static void Send(Mouse[] inputs)
    {
        if (inputs.Length == 0) return;
        var native = inputs.Select(input => new NativeInput {
            X = input.X, Y = input.Y, Data = input.Data, Flags = input.Flags }).ToArray();
        if (SendInput((uint)native.Length, native, Marshal.SizeOf<NativeInput>()) != native.Length)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Windows refused benchmark input.");
    }

    private static void Send(NativeInput input)
    {
        if (SendInput(1, [input], Marshal.SizeOf<NativeInput>()) != 1)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Windows refused benchmark input.");
    }

    public void Dispose() => Send(new NativeInput { Flags = 4 });

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { internal int X, Y; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct NativeInput
    {
        [FieldOffset(0)] internal uint Type;
        [FieldOffset(8)] internal int X;
        [FieldOffset(12)] internal int Y;
        [FieldOffset(16)] internal uint Data;
        [FieldOffset(20)] internal uint Flags;
        [FieldOffset(8)] internal ushort Key;
        [FieldOffset(12)] internal uint KeyFlags;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SendMessageTimeout(nint window, uint message,
        nuint wParam, nint lParam, uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(nint dc, int index);
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint period);
}
