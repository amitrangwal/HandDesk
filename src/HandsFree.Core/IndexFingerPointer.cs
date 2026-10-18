namespace HandsFree.Core;

/// <summary>Normalized movement window in the mirrored preview's displayed coordinate space.</summary>
public sealed record PointerMovementWindow(double Left, double Top, double Width, double Height)
{
    public static PointerMovementWindow Default { get; } = new(0.2, 0.1, 0.6, 0.8);
    public bool IsValid => double.IsFinite(Left) && double.IsFinite(Top) && double.IsFinite(Width) && double.IsFinite(Height)
        && Left >= 0 && Top >= 0 && Width > 0 && Height > 0 && Left + Width <= 1 && Top + Height <= 1;
}

public readonly record struct SimulatedPointerPosition(double X, double Y);

public enum IndexFingerPoseFailure { None, NoHand, LowConfidence, IndexNotExtended, OtherFingerNotCurled, ThumbNotCurled }

public sealed record IndexFingerPoseResult(bool IsRecognized, SimulatedPointerPosition? Fingertip, IndexFingerPoseFailure Failure)
{
    public static IndexFingerPoseResult Rejected(IndexFingerPoseFailure failure) => new(false, null, failure);
}

/// <summary>Conservative geometric detector for an index-point pose using the existing 21 landmarks.</summary>
public static class IndexFingerPoseDetector
{
    private const float MinimumHandConfidence = 0.8f;
    private static readonly (int Pip, int Tip)[] CurledFingers = [(10, 12), (14, 16), (18, 20)];

    public static IndexFingerPoseResult Detect(HandTrackingResult? hand)
    {
        if (hand is not { IsDetected: true, Landmarks.Count: 21 }) return IndexFingerPoseResult.Rejected(IndexFingerPoseFailure.NoHand);
        if (!float.IsFinite(hand.Confidence) || hand.Confidence < MinimumHandConfidence
            || hand.Landmarks.Any(point => !float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z)))
            return IndexFingerPoseResult.Rejected(IndexFingerPoseFailure.LowConfidence);

        var points = hand.Landmarks;
        var wrist = points[0];
        var palmWidth = Distance(points[5], points[17]);
        if (palmWidth < 0.02f) return IndexFingerPoseResult.Rejected(IndexFingerPoseFailure.LowConfidence);

        // A straight index finger places its tip distinctly farther from the wrist than its PIP joint.
        if (Distance(wrist, points[8]) < Distance(wrist, points[6]) + palmWidth * 0.18f
            || Distance(points[5], points[8]) < Distance(points[5], points[6]) * 1.2f)
            return IndexFingerPoseResult.Rejected(IndexFingerPoseFailure.IndexNotExtended);

        foreach (var (pip, tip) in CurledFingers)
            if (Distance(wrist, points[tip]) >= Distance(wrist, points[pip]))
                return IndexFingerPoseResult.Rejected(IndexFingerPoseFailure.OtherFingerNotCurled);

        var palmCenterX = (points[0].X + points[5].X + points[9].X + points[13].X + points[17].X) / 5f;
        var palmCenterY = (points[0].Y + points[5].Y + points[9].Y + points[13].Y + points[17].Y) / 5f;
        if (MathF.Sqrt(MathF.Pow(points[4].X - palmCenterX, 2) + MathF.Pow(points[4].Y - palmCenterY, 2)) > palmWidth * 0.8f)
            return IndexFingerPoseResult.Rejected(IndexFingerPoseFailure.ThumbNotCurled);

        return new IndexFingerPoseResult(true, new SimulatedPointerPosition(points[8].X, points[8].Y), IndexFingerPoseFailure.None);
    }

    private static float Distance(HandLandmark a, HandLandmark b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}

public sealed record IndexFingerPointerState(
    bool PoseRecognized,
    bool FingertipInsideWindow,
    SimulatedPointerPosition? Pointer,
    IndexFingerPoseFailure PoseFailure,
    string Message);

/// <summary>Maps the index tip to a simulated desktop. No platform input is emitted.</summary>
public sealed class IndexFingerPointerMapper
{
    private const double Smoothing = 0.35;
    private SimulatedPointerPosition? _pointer;
    private SimulatedPointerPosition? _entryFingertip;
    private SimulatedPointerPosition? _entryPointer;
    private bool _reanchor = true;

    public IndexFingerPointerState Update(HandTrackingResult? hand, bool trackingSelectedHand, PointerMovementWindow window,
        bool previewMirrored, double desktopWidth, double desktopHeight)
    {
        if (!window.IsValid || !double.IsFinite(desktopWidth) || !double.IsFinite(desktopHeight) || desktopWidth <= 0 || desktopHeight <= 0)
        {
            Reset();
            return new(false, false, null, IndexFingerPoseFailure.LowConfidence, "Movement window or simulated desktop size is invalid.");
        }

        if (!trackingSelectedHand)
        {
            _reanchor = true;
            _entryFingertip = null;
            _entryPointer = null;
            return new(false, false, _pointer, IndexFingerPoseFailure.NoHand, "Tracking lost; simulated pointer paused.");
        }

        var pose = IndexFingerPoseDetector.Detect(hand);
        if (!pose.IsRecognized || pose.Fingertip is not { } fingertip)
        {
            _reanchor = true;
            _entryFingertip = null;
            _entryPointer = null;
            return new(false, false, _pointer, pose.Failure, FailureMessage(pose.Failure));
        }

        var previewPoint = new SimulatedPointerPosition(previewMirrored ? 1 - fingertip.X : fingertip.X, fingertip.Y);
        var mapped = TryMap(previewPoint, window, desktopWidth, desktopHeight);
        if (mapped is null)
        {
            _reanchor = true;
            _entryFingertip = null;
            _entryPointer = null;
            return new(true, false, _pointer, IndexFingerPoseFailure.None, "Index pose recognized; fingertip is outside the movement window. Pointer paused.");
        }

        if (_reanchor || _pointer is null)
        {
            if (_pointer is null)
                _pointer = mapped.Value;
            _entryFingertip = previewPoint;
            _entryPointer = _pointer;
            _reanchor = false;
            return new(true, true, _pointer, IndexFingerPoseFailure.None, "Index pose recognized; fingertip is inside the movement window.");
        }

        var anchorFinger = _entryFingertip!.Value;
        var anchorPointer = _entryPointer!.Value;
        var target = new SimulatedPointerPosition(
            Math.Clamp(anchorPointer.X + (previewPoint.X - anchorFinger.X) / window.Width * desktopWidth, 0, desktopWidth),
            Math.Clamp(anchorPointer.Y + (previewPoint.Y - anchorFinger.Y) / window.Height * desktopHeight, 0, desktopHeight));
        _pointer = new SimulatedPointerPosition(_pointer.Value.X + (target.X - _pointer.Value.X) * Smoothing,
            _pointer.Value.Y + (target.Y - _pointer.Value.Y) * Smoothing);
        return new(true, true, _pointer, IndexFingerPoseFailure.None, "Index pose recognized; fingertip is inside the movement window.");
    }

    public void Reset()
    {
        _pointer = null;
        _entryFingertip = null;
        _entryPointer = null;
        _reanchor = true;
    }

    public static SimulatedPointerPosition? TryMap(SimulatedPointerPosition normalizedPoint, PointerMovementWindow window,
        double desktopWidth, double desktopHeight)
    {
        if (!window.IsValid || !double.IsFinite(normalizedPoint.X) || !double.IsFinite(normalizedPoint.Y)
            || !double.IsFinite(desktopWidth) || !double.IsFinite(desktopHeight) || desktopWidth <= 0 || desktopHeight <= 0)
            return null;
        var xRatio = (normalizedPoint.X - window.Left) / window.Width;
        var yRatio = (normalizedPoint.Y - window.Top) / window.Height;
        const double edgeTolerance = 1e-9;
        if (xRatio < -edgeTolerance || xRatio > 1 + edgeTolerance || yRatio < -edgeTolerance || yRatio > 1 + edgeTolerance) return null;
        xRatio = Math.Clamp(xRatio, 0, 1);
        yRatio = Math.Clamp(yRatio, 0, 1);
        return new SimulatedPointerPosition(xRatio * desktopWidth, yRatio * desktopHeight);
    }

    /// <summary>Maps a pointer measured in preview-control coordinates, excluding letterbox areas.</summary>
    public static SimulatedPointerPosition? TryMapPreviewPoint(double pointX, double pointY, double imageLeft, double imageTop,
        double imageWidth, double imageHeight, PointerMovementWindow window, bool previewMirrored, double desktopWidth, double desktopHeight)
    {
        if (!double.IsFinite(pointX) || !double.IsFinite(pointY) || !double.IsFinite(imageLeft) || !double.IsFinite(imageTop)
            || !double.IsFinite(imageWidth) || !double.IsFinite(imageHeight) || imageWidth <= 0 || imageHeight <= 0
            || pointX < imageLeft || pointX > imageLeft + imageWidth || pointY < imageTop || pointY > imageTop + imageHeight)
            return null;
        var normalizedX = (pointX - imageLeft) / imageWidth;
        if (previewMirrored) normalizedX = 1 - normalizedX;
        return TryMap(new SimulatedPointerPosition(normalizedX, (pointY - imageTop) / imageHeight), window, desktopWidth, desktopHeight);
    }

    private static string FailureMessage(IndexFingerPoseFailure failure) => failure switch
    {
        IndexFingerPoseFailure.NoHand => "No selected hand; simulated pointer paused.",
        IndexFingerPoseFailure.LowConfidence => "Hand pose is uncertain; simulated pointer paused.",
        IndexFingerPoseFailure.IndexNotExtended => "Index finger is not clearly extended.",
        IndexFingerPoseFailure.OtherFingerNotCurled => "Curl the middle, ring, and little fingers.",
        IndexFingerPoseFailure.ThumbNotCurled => "Curl the thumb toward the palm.",
        _ => "Index pose recognized."
    };
}
