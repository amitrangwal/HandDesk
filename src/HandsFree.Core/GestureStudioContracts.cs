using System.Text.Json;

namespace HandsFree.Core;

/// <summary>One synchronized row in a Gesture Studio landmark JSONL file (schema version 1).</summary>
public sealed record GestureStudioFrameRecord(
    int SchemaVersion,
    Guid SessionId,
    Guid SampleId,
    long VideoFrameIndex,
    DateTimeOffset CapturedAtUtc,
    double ElapsedMilliseconds,
    HandTrackingIdentityState TrackingState,
    bool IsDetected,
    float? Confidence,
    float? Handedness,
    float? HandednessConfidence,
    IReadOnlyList<HandLandmark>? Landmarks,
    string? MissingReason)
{
    public static GestureStudioFrameRecord FromFrame(Guid sessionId, Guid sampleId, long videoFrameIndex,
        DateTimeOffset recordingStartedAt, HandTrackingFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var detected = frame.IdentityState == HandTrackingIdentityState.Tracking
            && frame.Result.IsDetected && frame.Result.Landmarks.Count == 21;
        return new GestureStudioFrameRecord(1, sessionId, sampleId, videoFrameIndex,
            frame.CapturedAt.ToUniversalTime(), Math.Max(0, (frame.CapturedAt - recordingStartedAt).TotalMilliseconds),
            frame.IdentityState, detected,
            detected ? frame.Result.Confidence : null,
            detected ? frame.Result.Handedness : null,
            detected ? frame.Result.HandednessConfidence : null,
            detected ? frame.Result.Landmarks.ToArray() : null,
            detected ? null : string.IsNullOrWhiteSpace(frame.TrackingMessage) ? "No selected hand in this frame." : frame.TrackingMessage);
    }

    public string ToJsonLine() => JsonSerializer.Serialize(this, JsonOptions);

    public static GestureStudioFrameRecord ParseJsonLine(string json) =>
        JsonSerializer.Deserialize<GestureStudioFrameRecord>(json, JsonOptions)
        ?? throw new InvalidDataException("Gesture Studio landmark row is empty or invalid.");

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}
