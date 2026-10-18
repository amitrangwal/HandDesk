using System.Diagnostics;
using HandsFree.Core;

namespace HandsFree.Windows;

/// <summary>Samples lightweight process and managed-memory counters on demand.</summary>
public sealed class WindowsProcessResourceSampler : IDisposable
{
    private readonly Process _process = Process.GetCurrentProcess();
    private TimeSpan? _previousCpuTime;
    private long _previousTimestamp;
    private bool _disposed;

    public ProcessResourceSnapshot Sample()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _process.Refresh();
        var now = Stopwatch.GetTimestamp();
        var cpuTime = _process.TotalProcessorTime;
        double? cpuPercent = null;
        if (_previousCpuTime is { } previousCpu)
        {
            var elapsed = Stopwatch.GetElapsedTime(_previousTimestamp, now);
            cpuPercent = ProcessCpuUsage.CalculatePercent(cpuTime - previousCpu, elapsed);
        }

        _previousCpuTime = cpuTime;
        _previousTimestamp = now;
        return new ProcessResourceSnapshot(cpuPercent, _process.WorkingSet64, GC.GetTotalMemory(false));
    }

    public void ResetCpuBaseline()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _process.Refresh();
        _previousCpuTime = _process.TotalProcessorTime;
        _previousTimestamp = Stopwatch.GetTimestamp();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _process.Dispose();
    }
}
