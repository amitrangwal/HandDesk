namespace HandsFree.Core;

/// <summary>
/// Keeps one preferred hand identity across detector frames. Temporary loss retains the identity
/// during the recovery window; expiry releases it so a fresh preferred hand can be selected.
/// </summary>
public sealed class SingleHandTrackingSelector
{
    public static readonly TimeSpan DefaultRecoveryWindow = TimeSpan.FromSeconds(1);
    public const float DefaultHandednessConfidenceThreshold = 0.8f;

    private const float MaximumCenterDistance = 0.22f;
    private const float MinimumOverlapForMatch = 0.05f;
    private readonly object _gate = new();
    private readonly TimeSpan _recoveryWindow;
    private readonly float _handednessConfidenceThreshold;
    private NormalizedHandBounds? _lastBounds;
    private DateTimeOffset _lastSeen;
    private float _lastHandedness;
    private bool _hasLock;

    public SingleHandTrackingSelector(
        TimeSpan? recoveryWindow = null,
        HandTrackingPreference preference = HandTrackingPreference.RightOnly,
        float handednessConfidenceThreshold = DefaultHandednessConfidenceThreshold)
    {
        _recoveryWindow = recoveryWindow ?? DefaultRecoveryWindow;
        if (_recoveryWindow <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(recoveryWindow), "The recovery window must be positive.");
        if (!Enum.IsDefined(preference)) throw new ArgumentOutOfRangeException(nameof(preference));
        if (!float.IsFinite(handednessConfidenceThreshold) || handednessConfidenceThreshold is < 0.5f or > 1f)
            throw new ArgumentOutOfRangeException(nameof(handednessConfidenceThreshold), "The handedness confidence threshold must be between 0.5 and 1.");
        Preference = preference;
        _handednessConfidenceThreshold = handednessConfidenceThreshold;
    }

    public HandTrackingPreference Preference { get; }

    public HandTrackingSelection Update(IReadOnlyList<HandTrackingResult> detections, DateTimeOffset capturedAt)
    {
        ArgumentNullException.ThrowIfNull(detections);
        lock (_gate)
        {
            var allValidDetections = detections.Where(item => item is { IsDetected: true, Bounds: not null }).ToArray();
            var validDetections = allValidDetections.Where(IsPreferredHand).ToArray();
            var identityExpired = false;
            if (_hasLock)
            {
                var elapsed = capturedAt >= _lastSeen ? capturedAt - _lastSeen : TimeSpan.Zero;
                if (elapsed >= _recoveryWindow)
                {
                    ClearIdentityLock();
                    identityExpired = true;
                }
                else
                {
                    var match = FindMatch(validDetections);
                    if (match is not null)
                    {
                        Lock(match, capturedAt);
                        return Tracking(match);
                    }

                    return new HandTrackingSelection(HandTrackingResult.NoHand, HandTrackingIdentityState.TemporarilyLost,
                        NoMatchMessage(allValidDetections));
                }
            }

            var initial = SelectBest(validDetections);
            if (initial is not null)
            {
                Lock(initial, capturedAt);
                return Tracking(initial);
            }

            var message = NoCandidateMessage(allValidDetections);
            if (identityExpired)
                message = $"Hand identity expired after {_recoveryWindow.TotalMilliseconds:0} ms without a matching hand. Searching fresh. {message}";
            return new HandTrackingSelection(HandTrackingResult.NoHand, HandTrackingIdentityState.Searching, message);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            ClearIdentityLock();
        }
    }

    private static HandTrackingResult? SelectBest(IReadOnlyList<HandTrackingResult> detections) => detections
        .Select((result, index) => (Result: result, Index: index))
        .OrderByDescending(item => item.Result.Confidence)
        .ThenBy(item => item.Index)
        .Select(item => item.Result)
        .FirstOrDefault();

    private void ClearIdentityLock()
    {
        _lastBounds = null;
        _lastSeen = default;
        _lastHandedness = 0;
        _hasLock = false;
    }

    private HandTrackingResult? FindMatch(IReadOnlyList<HandTrackingResult> detections)
    {
        if (_lastBounds is null) return null;
        HandTrackingResult? best = null;
        var bestScore = float.NegativeInfinity;
        foreach (var candidate in detections)
        {
            var bounds = candidate.Bounds!;
            var distanceX = bounds.CenterX - _lastBounds.CenterX;
            var distanceY = bounds.CenterY - _lastBounds.CenterY;
            var centerDistance = MathF.Sqrt(distanceX * distanceX + distanceY * distanceY);
            var overlap = bounds.IntersectionOverUnion(_lastBounds);
            if (centerDistance > MaximumCenterDistance && overlap < MinimumOverlapForMatch) continue;

            var positionScore = Math.Max(0, 1 - centerDistance / MaximumCenterDistance);
            var handednessSimilarity = 1 - Math.Min(1, Math.Abs(candidate.Handedness - _lastHandedness));
            var score = overlap * 0.60f + positionScore * 0.35f + handednessSimilarity * 0.05f;
            if (score > bestScore || (score == bestScore && candidate.Confidence > (best?.Confidence ?? float.NegativeInfinity)))
            {
                best = candidate;
                bestScore = score;
            }
        }

        return best;
    }

    private bool IsPreferredHand(HandTrackingResult result)
    {
        if (result.HandednessConfidence < _handednessConfidenceThreshold) return false;
        var isRight = result.Handedness >= 0.5f;
        return Preference switch
        {
            HandTrackingPreference.RightOnly => isRight,
            HandTrackingPreference.LeftOnly => !isRight,
            HandTrackingPreference.Either => true,
            _ => false
        };
    }

    private string NoCandidateMessage(IReadOnlyList<HandTrackingResult> detections)
    {
        if (detections.Count == 0) return "No hand selected. Waiting for a visible hand.";
        if (detections.Any(result => result.HandednessConfidence < _handednessConfidenceThreshold))
            return $"Hand detected, but handedness is uncertain (below {_handednessConfidenceThreshold:P0}); waiting for a confident {PreferredHandLabel()} detection.";
        var ignoredHand = Preference switch
        {
            HandTrackingPreference.RightOnly => "Left hand",
            HandTrackingPreference.LeftOnly => "Right hand",
            _ => "Other hand"
        };
        return $"{ignoredHand} detected; waiting for a confident {PreferredHandLabel()} detection.";
    }

    private string NoMatchMessage(IReadOnlyList<HandTrackingResult> detections)
    {
        var detail = detections.Count > 0 && detections.All(result => result.HandednessConfidence < _handednessConfidenceThreshold)
            ? " Detected handedness is uncertain."
            : detections.Any(result => !IsPreferredHand(result))
                ? " Other-hand detections are ignored."
                : string.Empty;
        return $"No matching {PreferredHandLabel()} detected.{detail} Recovering the selected {PreferredHandLabel()} for up to {_recoveryWindow.TotalMilliseconds:0} ms.";
    }

    private string PreferredHandLabel() => Preference switch
    {
        HandTrackingPreference.RightOnly => "right hand",
        HandTrackingPreference.LeftOnly => "left hand",
        HandTrackingPreference.Either => "hand",
        _ => "hand"
    };

    private void Lock(HandTrackingResult result, DateTimeOffset capturedAt)
    {
        _lastBounds = result.Bounds;
        _lastSeen = capturedAt;
        _lastHandedness = result.Handedness;
        _hasLock = true;
    }

    private static HandTrackingSelection Tracking(HandTrackingResult result) =>
        new(result, HandTrackingIdentityState.Tracking, "Tracking the selected hand.");

}
