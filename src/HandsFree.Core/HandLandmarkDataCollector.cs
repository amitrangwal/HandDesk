using System.Text.Json;
using System.Threading.Channels;

namespace HandsFree.Core;

public enum HandLandmarkCollectionState { Stopped, Recording, Faulted, Disposed }

public sealed record HandLandmarkCollectionStatus(
    HandLandmarkCollectionState State,
    string Message,
    long SamplesWritten,
    long SamplesDropped,
    Guid? SessionId,
    string? FilePath);

/// <summary>Versioned JSONL sample. Landmark coordinates use normalized model image space.</summary>
public sealed record HandLandmarkSample(
    int SchemaVersion,
    Guid SessionId,
    long SampleId,
    DateTimeOffset CapturedAtUtc,
    string Label,
    float Confidence,
    float Handedness,
    float HandednessConfidence,
    IReadOnlyList<HandLandmark> Landmarks);

/// <summary>Explicit local collection; a bounded channel keeps disk writes off the tracking/UI path.</summary>
public sealed class HandLandmarkDataCollector : IAsyncDisposable
{
    private const int ChannelCapacity = 128;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string _directory;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _statusGate = new();
    private HandLandmarkCollectionState _state = HandLandmarkCollectionState.Stopped;
    private string _message = "Collection is stopped.";
    private Guid? _sessionId;
    private string? _filePath;
    private bool _trackingLost;
    private long _written;
    private long _dropped;
    private long _nextSampleId;
    private Channel<HandLandmarkSample>? _channel;
    private Task? _writerTask;
    private StreamWriter? _writer;
    private bool _disposed;

    public HandLandmarkDataCollector(string? directory = null)
    {
        _directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HandsFree", "Samples");
    }

    public event Action<HandLandmarkCollectionStatus>? StatusChanged;

    public HandLandmarkCollectionStatus Status
    {
        get
        {
            lock (_statusGate)
                return new(_state, _message, Interlocked.Read(ref _written), Interlocked.Read(ref _dropped), _sessionId, _filePath);
        }
    }

    public async Task<bool> StartAsync(string label, CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeLabel(label, out var normalized))
        {
            if (Status.State != HandLandmarkCollectionState.Recording)
                SetStatus(HandLandmarkCollectionState.Stopped, "Enter a gesture label before starting collection.", null, null);
            return false;
        }

        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed) return false;
            if (_state == HandLandmarkCollectionState.Recording) return false;
            try
            {
                if (_writerTask is not null)
                {
                    try { await _writerTask.ConfigureAwait(false); }
                    catch { /* The previous session's failure is reflected in status; a new explicit session may retry. */ }
                    await CloseWriterAsync().ConfigureAwait(false);
                    _writerTask = null;
                    _channel = null;
                }
                Directory.CreateDirectory(_directory);
                var sessionId = Guid.NewGuid();
                var path = Path.Combine(_directory, $"hand-landmarks-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{sessionId:N}.jsonl");
                var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                _writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
                _channel = Channel.CreateBounded<HandLandmarkSample>(new BoundedChannelOptions(ChannelCapacity)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false
                });
                Interlocked.Exchange(ref _written, 0);
                Interlocked.Exchange(ref _dropped, 0);
                Interlocked.Exchange(ref _nextSampleId, 0);
                _trackingLost = false;
                _sessionId = sessionId;
                _filePath = path;
                _writerTask = WriteSamplesAsync(_channel.Reader, _writer);
                SetStatus(HandLandmarkCollectionState.Recording, $"Recording label '{normalized}'.", sessionId, path);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                await CloseWriterAsync().ConfigureAwait(false);
                SetStatus(HandLandmarkCollectionState.Faulted, $"Could not create local sample file: {exception.Message}", null, null);
                return false;
            }
        }
        finally { _lifecycle.Release(); }
    }

    /// <summary>Queues one fresh selected-hand frame. Returns false for loss, invalid data, stopped, or full queue.</summary>
    public bool TryRecord(HandTrackingFrame frame, string label)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!TryNormalizeLabel(label, out var normalized))
        {
            SetMessage("Recording paused: enter a label; unlabeled samples are not written.");
            return false;
        }
        if (frame.IdentityState != HandTrackingIdentityState.Tracking || !IsValidResult(frame.Result))
        {
            NotifyTrackingLost(frame.TrackingMessage);
            return false;
        }

        Channel<HandLandmarkSample>? channel;
        Guid sessionId;
        lock (_statusGate)
        {
            if (_state != HandLandmarkCollectionState.Recording || _sessionId is not { } activeId) return false;
            _trackingLost = false;
            _message = $"Recording label '{normalized}'.";
            channel = _channel;
            sessionId = activeId;
        }

        var result = frame.Result;
        var sample = new HandLandmarkSample(1, sessionId, Interlocked.Increment(ref _nextSampleId), frame.CapturedAt.ToUniversalTime(), normalized,
            result.Confidence, result.Handedness, result.HandednessConfidence, result.Landmarks.ToArray());
        if (channel?.Writer.TryWrite(sample) == true) return true;
        Interlocked.Increment(ref _dropped);
        SetMessage("Recording is active, but the writer queue is full; this sample was skipped.");
        return false;
    }

    public void NotifyTrackingLost(string? message = null)
    {
        lock (_statusGate)
        {
            if (_state != HandLandmarkCollectionState.Recording || _trackingLost) return;
            _trackingLost = true;
            _message = string.IsNullOrWhiteSpace(message) ? "Recording paused: waiting for the selected hand." : $"Recording paused: {message}";
        }
        RaiseStatusChanged();
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state != HandLandmarkCollectionState.Recording && _writerTask is null) return;
            _channel?.Writer.TryComplete();
            if (_writerTask is not null)
            {
                try { await _writerTask.ConfigureAwait(false); }
                catch (Exception exception)
                {
                    SetStatus(HandLandmarkCollectionState.Faulted, $"Sample writing failed: {exception.Message}", _sessionId, _filePath);
                    await CloseWriterAsync().ConfigureAwait(false);
                    return;
                }
            }
            if (Status.State == HandLandmarkCollectionState.Faulted)
            {
                await CloseWriterAsync().ConfigureAwait(false);
                _channel = null;
                _writerTask = null;
                return;
            }
            await CloseWriterAsync().ConfigureAwait(false);
            SetStatus(HandLandmarkCollectionState.Stopped, $"Collection stopped. {Interlocked.Read(ref _written)} samples saved.", _sessionId, _filePath);
            _channel = null;
            _writerTask = null;
        }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _channel?.Writer.TryComplete();
            if (_writerTask is not null) await _writerTask.ConfigureAwait(false);
            await CloseWriterAsync().ConfigureAwait(false);
            _disposed = true;
            SetStatus(HandLandmarkCollectionState.Disposed, "Collection resources released.", _sessionId, _filePath);
        }
        finally { _lifecycle.Release(); }
    }

    private async Task WriteSamplesAsync(ChannelReader<HandLandmarkSample> reader, StreamWriter writer)
    {
        try
        {
            await foreach (var sample in reader.ReadAllAsync().ConfigureAwait(false))
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(sample, JsonOptions)).ConfigureAwait(false);
                Interlocked.Increment(ref _written);
            }
            await writer.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            while (reader.TryRead(out _)) Interlocked.Increment(ref _dropped);
            lock (_statusGate)
            {
                if (_state == HandLandmarkCollectionState.Recording)
                {
                    _state = HandLandmarkCollectionState.Faulted;
                    _message = $"Sample writing failed: {exception.Message}";
                }
            }
            try { await writer.DisposeAsync().ConfigureAwait(false); }
            catch { /* The original write failure is the actionable status. */ }
            RaiseStatusChanged();
        }
    }

    private async Task CloseWriterAsync()
    {
        if (_writer is null) return;
        await _writer.DisposeAsync().ConfigureAwait(false);
        _writer = null;
    }

    private static bool TryNormalizeLabel(string? label, out string normalized)
    {
        normalized = label?.Trim() ?? string.Empty;
        return normalized.Length is > 0 and <= 80 && !normalized.Any(char.IsControl);
    }

    private static bool IsValidResult(HandTrackingResult? result) => result is { IsDetected: true, Landmarks.Count: 21 }
        && float.IsFinite(result.Confidence) && result.Confidence is >= 0 and <= 1
        && float.IsFinite(result.Handedness) && result.Handedness is >= 0 and <= 1
        && result.Landmarks.All(point => float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z));

    private void SetStatus(HandLandmarkCollectionState state, string message, Guid? sessionId, string? filePath)
    {
        lock (_statusGate)
        {
            _state = state;
            _message = message;
            _sessionId = sessionId;
            _filePath = filePath;
        }
        RaiseStatusChanged();
    }

    private void SetMessage(string message)
    {
        lock (_statusGate)
        {
            if (_state != HandLandmarkCollectionState.Recording || _message == message) return;
            _message = message;
        }
        RaiseStatusChanged();
    }

    private void RaiseStatusChanged() => StatusChanged?.Invoke(Status);
}
