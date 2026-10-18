namespace HandsFree.Core;

/// <summary>Bounded, session-local statistics for frames processed by the hand pipeline.</summary>
public sealed class HandTrackingMetricAccumulator
{
    private const int MaximumLatencySamples = 120;
    private const int MaximumFrameTimes = 120;
    private readonly object _gate = new();
    private readonly Queue<double> _latenciesMilliseconds = new();
    private readonly Queue<DateTimeOffset> _frameTimes = new();
    private long _processedFrames;
    private long _droppedFrames;

    public void RecordFrame(TimeSpan onnxInferenceTime, DateTimeOffset capturedAt, long newlyDroppedFrames)
    {
        if (onnxInferenceTime < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(onnxInferenceTime));
        if (newlyDroppedFrames < 0) throw new ArgumentOutOfRangeException(nameof(newlyDroppedFrames));

        lock (_gate)
        {
            _processedFrames++;
            _droppedFrames += newlyDroppedFrames;
            _latenciesMilliseconds.Enqueue(onnxInferenceTime.TotalMilliseconds);
            if (_latenciesMilliseconds.Count > MaximumLatencySamples) _latenciesMilliseconds.Dequeue();
            _frameTimes.Enqueue(capturedAt);
            if (_frameTimes.Count > MaximumFrameTimes) _frameTimes.Dequeue();
        }
    }

    public HandTrackingMetricsSnapshot Snapshot()
    {
        lock (_gate)
        {
            double? average = _latenciesMilliseconds.Count == 0 ? null : _latenciesMilliseconds.Average();
            double? last = _latenciesMilliseconds.Count == 0 ? null : _latenciesMilliseconds.Last();
            double? p95 = null;
            if (_latenciesMilliseconds.Count > 0)
            {
                var sorted = _latenciesMilliseconds.Order().ToArray();
                var index = Math.Max(0, (int)Math.Ceiling(sorted.Length * 0.95) - 1);
                p95 = sorted[index];
            }

            var frameTimes = _frameTimes.ToArray();
            var spanSeconds = frameTimes.Length < 2 ? 0 : (frameTimes[^1] - frameTimes[0]).TotalSeconds;
            var fps = spanSeconds <= 0 ? 0 : (frameTimes.Length - 1) / spanSeconds;
            return new HandTrackingMetricsSnapshot(_processedFrames, _droppedFrames, fps, last, average, p95);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _processedFrames = 0;
            _droppedFrames = 0;
            _latenciesMilliseconds.Clear();
            _frameTimes.Clear();
        }
    }
}

public sealed record HandTrackingMetricsSnapshot(
    long ProcessedFrames,
    long DroppedFrames,
    double FramesPerSecond,
    double? LastOnnxLatencyMilliseconds,
    double? AverageOnnxLatencyMilliseconds,
    double? P95OnnxLatencyMilliseconds)
{
    public static HandTrackingMetricsSnapshot Idle { get; } = new(0, 0, 0, null, null, null);
}

public static class ProcessCpuUsage
{
    /// <summary>Returns per-process CPU percent where one fully used logical core is 100%.</summary>
    public static double? CalculatePercent(TimeSpan cpuTimeDelta, TimeSpan elapsedTime)
    {
        if (cpuTimeDelta < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(cpuTimeDelta));
        if (elapsedTime <= TimeSpan.Zero) return null;
        return cpuTimeDelta.TotalSeconds / elapsedTime.TotalSeconds * 100;
    }
}

public sealed record ProcessResourceSnapshot(double? ProcessCpuPercent, long WorkingSetBytes, long ManagedHeapBytes);
