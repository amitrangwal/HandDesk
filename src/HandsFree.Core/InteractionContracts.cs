namespace HandsFree.Core;

public sealed record GestureInput(string Name);

public sealed record VoiceInput(string Text);

public sealed record UserIntent(string Name, string? Argument = null);

public interface IGestureInputProvider
{
    IAsyncEnumerable<GestureInput> ReadGesturesAsync(CancellationToken cancellationToken = default);
}

public interface IVoiceInputProvider
{
    IAsyncEnumerable<VoiceInput> ReadUtterancesAsync(CancellationToken cancellationToken = default);
}

public interface IIntentInterpreter
{
    ValueTask<UserIntent?> InterpretAsync(string input, CancellationToken cancellationToken = default);
}

public interface IActionExecutor
{
    ValueTask ExecuteAsync(UserIntent intent, CancellationToken cancellationToken = default);
}

/// <summary>One normalized landmark from a hand pose, in image coordinates.</summary>
public sealed record HandLandmark(float X, float Y, float Z);

/// <summary>Platform-independent result for a single detected hand.</summary>
public sealed class HandTrackingResult
{
    private HandTrackingResult(IReadOnlyList<HandLandmark> landmarks, float confidence, float handedness)
    {
        Landmarks = landmarks;
        Confidence = confidence;
        Handedness = handedness;
        Bounds = NormalizedHandBounds.From(landmarks);
        IsDetected = true;
    }

    private HandTrackingResult()
    {
        Landmarks = Array.Empty<HandLandmark>();
    }

    public static HandTrackingResult NoHand { get; } = new();

    public bool IsDetected { get; }

    public IReadOnlyList<HandLandmark> Landmarks { get; }

    public float Confidence { get; }

    /// <summary>Probability score for an anatomical right hand: 0 is left and 1 is right.</summary>
    public float Handedness { get; }

    public float HandednessConfidence => Math.Max(Handedness, 1 - Handedness);

    /// <summary>Bounding rectangle derived from the 21 normalized image landmarks.</summary>
    public NormalizedHandBounds? Bounds { get; }

    public static bool TryCreate(IEnumerable<HandLandmark> landmarks, float confidence, float handedness, out HandTrackingResult? result)
    {
        ArgumentNullException.ThrowIfNull(landmarks);
        var points = landmarks.ToArray();
        if (points.Length != 21 || !float.IsFinite(confidence) || confidence is < 0 or > 1
            || !float.IsFinite(handedness) || handedness is < 0 or > 1
            || points.Any(point => point is null || !float.IsFinite(point.X) || point.X is < 0 or > 1
                || !float.IsFinite(point.Y) || point.Y is < 0 or > 1 || !float.IsFinite(point.Z)))
        {
            result = null;
            return false;
        }

        result = new HandTrackingResult(Array.AsReadOnly(points), confidence, handedness);
        return true;
    }
}

/// <summary>Hand selection preference; reset clears identity but retains this preference.</summary>
public enum HandTrackingPreference
{
    RightOnly,
    LeftOnly,
    Either
}

public sealed record NormalizedHandBounds(float X, float Y, float Width, float Height)
{
    public float CenterX => X + Width / 2;
    public float CenterY => Y + Height / 2;

    public float IntersectionOverUnion(NormalizedHandBounds other)
    {
        var intersectionWidth = Math.Max(0, Math.Min(X + Width, other.X + other.Width) - Math.Max(X, other.X));
        var intersectionHeight = Math.Max(0, Math.Min(Y + Height, other.Y + other.Height) - Math.Max(Y, other.Y));
        var intersection = intersectionWidth * intersectionHeight;
        var union = Width * Height + other.Width * other.Height - intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    internal static NormalizedHandBounds From(IReadOnlyList<HandLandmark> landmarks)
    {
        var minX = landmarks.Min(point => point.X);
        var minY = landmarks.Min(point => point.Y);
        var maxX = landmarks.Max(point => point.X);
        var maxY = landmarks.Max(point => point.Y);
        return new NormalizedHandBounds(minX, minY, maxX - minX, maxY - minY);
    }
}

/// <summary>A camera frame and its optional single-hand result. Pixels are tightly packed BGR24.</summary>
public sealed record HandTrackingFrame(
    int Width,
    int Height,
    ReadOnlyMemory<byte> BgrPixels,
    HandTrackingResult Result,
    DateTimeOffset CapturedAt,
    HandTrackingIdentityState IdentityState = HandTrackingIdentityState.Searching,
    string TrackingMessage = "Looking for a hand.");

public enum HandTrackingIdentityState
{
    Searching,
    Tracking,
    TemporarilyLost,
    LostLocked
}

public sealed record HandTrackingSelection(HandTrackingResult Result, HandTrackingIdentityState State, string Message)
{
    public int HandednessRejectedCandidates { get; init; }
    public int SpatiallyRejectedCandidates { get; init; }
    public float? LastRejectedHandednessScore { get; init; }
    public float? LastRejectedHandednessConfidence { get; init; }
}

public enum HandTrackingState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Faulted,
    Disposed
}

public sealed record HandTrackingStatus(HandTrackingState State, string Message);

/// <summary>Platform boundary for explicit camera capture and local hand inference.</summary>
public interface IHandTrackingProvider : IAsyncDisposable
{
    event Action<HandTrackingFrame>? FrameAvailable;
    event Action<string>? StatusChanged;
    event Action<string>? Faulted;
    HandTrackingMetricsSnapshot Metrics { get; }
    HandTrackingDiagnosticsSnapshot Diagnostics { get; }
    void ResetDiagnostics();
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    void ResetHandSelection();
}
