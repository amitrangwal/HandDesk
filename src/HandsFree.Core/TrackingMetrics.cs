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

/// <summary>Decision counts produced for one inference frame. PalmBeforeNms counts score-qualified anchors.</summary>
public sealed record HandTrackingPipelineDiagnostics(
    int PalmCandidatesBeforeNms,
    int PalmCandidatesAfterNms,
    int PalmCandidatesAfterLimit,
    int InvalidCropsSkipped,
    int HandPoseCandidatesProcessed,
    int RejectedByPresenceThreshold)
{
    public static HandTrackingPipelineDiagnostics Empty { get; } = new(0, 0, 0, 0, 0, 0);
}

/// <summary>Bounded aggregate diagnostics; no frame history or image data is retained.</summary>
public sealed record HandTrackingDiagnosticsSnapshot(
    long Frames,
    long PalmCandidatesBeforeNms,
    long PalmCandidatesAfterNms,
    long PalmCandidatesAfterLimit,
    long InvalidCropsSkipped,
    long HandPoseCandidatesProcessed,
    long RejectedByPresenceThreshold,
    long RejectedByHandedness,
    long RejectedBySpatialAssociation,
    long FramesWithSelectedHand,
    long FramesWithNoCandidate,
    long FramesWithTemporarilyLostHand,
    HandTrackingIdentityState CurrentState,
    string CurrentMessage,
    float? SelectedHandConfidence,
    float? SelectedHandedness,
    float? SelectedHandednessConfidence,
    float? LastRejectedHandednessScore,
    float? LastRejectedHandednessConfidence)
{
    public static HandTrackingDiagnosticsSnapshot Empty { get; } = new(
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        HandTrackingIdentityState.Searching, "Waiting for tracking data.", null, null, null, null, null);
}

/// <summary>Thread-safe totals for pipeline and selection decisions.</summary>
public sealed class HandTrackingDiagnosticsAccumulator
{
    private readonly object _gate = new();
    private long _frames, _palmBeforeNms, _palmAfterNms, _palmAfterLimit, _invalidCrops;
    private long _poseProcessed, _presenceRejected, _handednessRejected, _spatialRejected;
    private long _selectedFrames, _noCandidateFrames, _temporarilyLostFrames;
    private HandTrackingIdentityState _state = HandTrackingIdentityState.Searching;
    private string _message = "Waiting for tracking data.";
    private float? _selectedConfidence, _selectedHandedness, _selectedHandednessConfidence;
    private float? _rejectedHandednessScore, _rejectedHandednessConfidence;

    public void RecordFrame(HandTrackingPipelineDiagnostics pipeline, HandTrackingSelection selection, bool hadPoseCandidate)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(selection);
        lock (_gate)
        {
            _frames++;
            _palmBeforeNms += pipeline.PalmCandidatesBeforeNms;
            _palmAfterNms += pipeline.PalmCandidatesAfterNms;
            _palmAfterLimit += pipeline.PalmCandidatesAfterLimit;
            _invalidCrops += pipeline.InvalidCropsSkipped;
            _poseProcessed += pipeline.HandPoseCandidatesProcessed;
            _presenceRejected += pipeline.RejectedByPresenceThreshold;
            _handednessRejected += selection.HandednessRejectedCandidates;
            _spatialRejected += selection.SpatiallyRejectedCandidates;
            if (selection.State == HandTrackingIdentityState.Tracking && selection.Result.IsDetected) _selectedFrames++;
            if (!hadPoseCandidate) _noCandidateFrames++;
            if (selection.State == HandTrackingIdentityState.TemporarilyLost) _temporarilyLostFrames++;
            _state = selection.State;
            _message = selection.Message;
            _selectedConfidence = selection.Result.IsDetected ? selection.Result.Confidence : null;
            _selectedHandedness = selection.Result.IsDetected ? selection.Result.Handedness : null;
            _selectedHandednessConfidence = selection.Result.IsDetected ? selection.Result.HandednessConfidence : null;
            _rejectedHandednessScore = selection.LastRejectedHandednessScore;
            _rejectedHandednessConfidence = selection.LastRejectedHandednessConfidence;
        }
    }

    public HandTrackingDiagnosticsSnapshot Snapshot()
    {
        lock (_gate)
            return new(_frames, _palmBeforeNms, _palmAfterNms, _palmAfterLimit, _invalidCrops,
                _poseProcessed, _presenceRejected, _handednessRejected, _spatialRejected,
                _selectedFrames, _noCandidateFrames, _temporarilyLostFrames, _state, _message,
                _selectedConfidence, _selectedHandedness, _selectedHandednessConfidence,
                _rejectedHandednessScore, _rejectedHandednessConfidence);
    }

    public void Reset()
    {
        lock (_gate)
        {
            _frames = _palmBeforeNms = _palmAfterNms = _palmAfterLimit = _invalidCrops = 0;
            _poseProcessed = _presenceRejected = _handednessRejected = _spatialRejected = 0;
            _selectedFrames = _noCandidateFrames = _temporarilyLostFrames = 0;
            _state = HandTrackingIdentityState.Searching;
            _message = "Waiting for tracking data.";
            _selectedConfidence = _selectedHandedness = _selectedHandednessConfidence = null;
            _rejectedHandednessScore = _rejectedHandednessConfidence = null;
        }
    }
}
