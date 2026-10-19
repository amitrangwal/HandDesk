using System.Text.Json;
using System.Threading.Channels;
using HandsFree.Core;
using OpenCvSharp;

namespace HandsFree.Windows;

public enum GestureStudioRecordingState { Idle, Recording, Faulted, Disposed }

public sealed record GestureStudioRecordingStatus(
    GestureStudioRecordingState State,
    string Message,
    Guid? SessionId,
    Guid? SampleId,
    long FramesWritten,
    long FramesDropped,
    DateTimeOffset? StartedAtUtc,
    string? SampleDirectory);

public sealed record GestureStudioSampleManifest(
    int SchemaVersion,
    Guid SessionId,
    Guid SampleId,
    DateTimeOffset CreatedAtUtc,
    string Label,
    string Notes,
    string VideoFile,
    string LandmarksFile,
    double VideoFramesPerSecond,
    string Disposition = "Preserved",
    string? ReviewLabel = null,
    string? ReviewNotes = null,
    string? TrimmedVideoFile = null,
    string? TrimmedLandmarksFile = null);

/// <summary>Local video/landmark writer. Inference callbacks only perform a bounded TryWrite.</summary>
public sealed class GestureStudioStore : IAsyncDisposable
{
    private const int QueueCapacity = 8;
    private const double OutputFramesPerSecond = 30;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private readonly string _rootDirectory;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private Channel<HandTrackingFrame>? _frames;
    private Task? _writerTask;
    private VideoWriter? _videoWriter;
    private StreamWriter? _landmarkWriter;
    private GestureStudioRecordingStatus _status = new(GestureStudioRecordingState.Idle, "Recording is stopped.", null, null, 0, 0, null, null);
    private GestureStudioSampleManifest? _manifest;
    private string? _manifestPath;
    private int _width;
    private int _height;
    private long _written;
    private long _dropped;
    private long _frameIndex;
    private bool _disposed;

    public GestureStudioStore(string? rootDirectory = null)
    {
        _rootDirectory = rootDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HandsFree", "Samples");
    }

    public event Action<GestureStudioRecordingStatus>? StatusChanged;
    public string RootDirectory => _rootDirectory;
    public GestureStudioRecordingStatus Status { get { lock (_gate) return _status with { FramesWritten = Interlocked.Read(ref _written), FramesDropped = Interlocked.Read(ref _dropped) }; } }

    public async Task<bool> StartSampleAsync(Guid sessionId, string label, string? notes, int width, int height, CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("A generated session ID is required.", nameof(sessionId));
        if (string.IsNullOrWhiteSpace(label)) { SetStatus(new(GestureStudioRecordingState.Idle, "Enter a gesture label before recording.", sessionId, null, 0, 0, null, null)); return false; }
        if (width <= 0 || height <= 0) { SetStatus(new(GestureStudioRecordingState.Idle, "Wait for a valid camera frame before recording.", sessionId, null, 0, 0, null, null)); return false; }
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed) return false;
            await StopSampleCoreAsync(cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(_rootDirectory);
            var sampleId = Guid.NewGuid();
            var sampleDirectory = Path.Combine(_rootDirectory, sessionId.ToString("N"), sampleId.ToString("N"));
            Directory.CreateDirectory(sampleDirectory);
            var videoPath = Path.Combine(sampleDirectory, "original.mp4");
            var landmarkPath = Path.Combine(sampleDirectory, "landmarks.jsonl");
            var opened = await Task.Run(() =>
            {
                var writer = new VideoWriter(videoPath, VideoWriter.FourCC('m', 'p', '4', 'v'), OutputFramesPerSecond, new Size(width, height));
                try
                {
                    if (!writer.IsOpened()) throw new IOException("OpenCV could not open an MP4 encoder. Check the bundled OpenCV video I/O runtime.");
                    var landmarks = new StreamWriter(new FileStream(landmarkPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 32 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan), new System.Text.UTF8Encoding(false));
                    return (Writer: writer, Landmarks: landmarks);
                }
                catch { writer.Dispose(); throw; }
            }, cancellationToken).ConfigureAwait(false);
            _videoWriter = opened.Writer;
            _landmarkWriter = opened.Landmarks;
            _width = width;
            _height = height;
            _frameIndex = 0;
            Interlocked.Exchange(ref _written, 0);
            Interlocked.Exchange(ref _dropped, 0);
            _manifestPath = Path.Combine(sampleDirectory, "manifest.json");
            _manifest = new GestureStudioSampleManifest(1, sessionId, sampleId, DateTimeOffset.UtcNow, label.Trim(), notes?.Trim() ?? string.Empty,
                "original.mp4", "landmarks.jsonl", OutputFramesPerSecond);
            await SaveManifestAsync(cancellationToken).ConfigureAwait(false);
            _frames = Channel.CreateBounded<HandTrackingFrame>(new BoundedChannelOptions(QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
            var started = new GestureStudioRecordingStatus(GestureStudioRecordingState.Recording, "Recording camera frames and synchronized landmark rows.", sessionId, sampleId, 0, 0, null, sampleDirectory);
            SetStatus(started);
            _writerTask = Task.Run(() => WriteFramesAsync(_frames.Reader, sessionId, sampleId));
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            try { _videoWriter?.Dispose(); } catch { }
            _videoWriter = null;
            if (_landmarkWriter is not null) { try { await _landmarkWriter.DisposeAsync().ConfigureAwait(false); } catch { } }
            _landmarkWriter = null;
            SetStatus(new(GestureStudioRecordingState.Faulted, $"Could not start local recording: {exception.Message}", sessionId, null, 0, 0, null, null));
            return false;
        }
        finally { _lifecycle.Release(); }
    }

    /// <summary>Never blocks inference. Returns false when recording is stopped, frame invalid, or bounded queue is full.</summary>
    public bool TryWriteFrame(HandTrackingFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Width != _width || frame.Height != _height || frame.BgrPixels.Length != (long)_width * _height * 3) return false;
        var channel = _frames;
        if (Status.State != GestureStudioRecordingState.Recording || channel is null) return false;
        if (channel.Writer.TryWrite(frame)) return true;
        Interlocked.Increment(ref _dropped);
        return false;
    }

    public async Task StopSampleAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await StopSampleCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    private async Task StopSampleCoreAsync(CancellationToken cancellationToken)
    {
        var channel = Interlocked.Exchange(ref _frames, null);
        if (channel is null && _writerTask is null) return;
        channel?.Writer.TryComplete();
        var worker = Interlocked.Exchange(ref _writerTask, null);
        if (worker is not null)
        {
            try { await worker.ConfigureAwait(false); }
            catch (Exception exception)
            {
                SetStatus(Status with { State = GestureStudioRecordingState.Faulted, Message = $"Recording finalization failed: {exception.Message}" });
            }
        }
        var videoWriter = Interlocked.Exchange(ref _videoWriter, null);
        try
        {
            if (videoWriter is not null) await Task.Run(videoWriter.Dispose).ConfigureAwait(false);
            if (_landmarkWriter is not null)
            {
                await _landmarkWriter.FlushAsync().ConfigureAwait(false);
                await _landmarkWriter.DisposeAsync().ConfigureAwait(false);
                _landmarkWriter = null;
            }
        }
        catch (Exception exception)
        {
            SetStatus(Status with { State = GestureStudioRecordingState.Faulted, Message = $"Recording resources could not be finalized: {exception.Message}" });
            if (_landmarkWriter is not null) { try { await _landmarkWriter.DisposeAsync().ConfigureAwait(false); } catch { } _landmarkWriter = null; }
        }
        try { await SaveManifestAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) { SetStatus(Status with { State = GestureStudioRecordingState.Faulted, Message = $"Manifest finalization failed: {exception.Message}" }); return; }
        if (Status.State != GestureStudioRecordingState.Faulted)
            SetStatus(Status with { State = GestureStudioRecordingState.Idle, Message = $"Recording saved: {Interlocked.Read(ref _written):N0} frames, {Interlocked.Read(ref _dropped):N0} queue drops." });
    }

    public IReadOnlyList<(string Directory, GestureStudioSampleManifest Manifest)> ListSamples()
    {
        if (!Directory.Exists(_rootDirectory)) return [];
        var samples = new List<(string, GestureStudioSampleManifest)>();
        foreach (var path in Directory.EnumerateFiles(_rootDirectory, "manifest.json", SearchOption.AllDirectories))
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<GestureStudioSampleManifest>(File.ReadAllText(path), JsonOptions);
                if (manifest?.SchemaVersion == 1) samples.Add((Path.GetDirectoryName(path)!, manifest));
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return samples.OrderByDescending(sample => sample.Item2.CreatedAtUtc).ToArray();
    }

    public async Task UpdateSampleAsync(string sampleDirectory, string? reviewLabel, string? reviewNotes, bool discard, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(sampleDirectory, "manifest.json");
        var current = JsonSerializer.Deserialize<GestureStudioSampleManifest>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), JsonOptions)
            ?? throw new InvalidDataException("Sample manifest could not be read.");
        var updated = current with { ReviewLabel = reviewLabel?.Trim(), ReviewNotes = reviewNotes?.Trim(), Disposition = discard ? "Discarded" : "Preserved" };
        await WriteManifestAsync(path, updated, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Exports a CFR interval with JSONL rows remapped to the same zero-based output frame/timebase.</summary>
    public async Task<(string VideoPath, string LandmarksPath)> ExportTrimAsync(string sampleDirectory, double startSeconds, double endSeconds, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(startSeconds) || !double.IsFinite(endSeconds) || startSeconds < 0 || endSeconds <= startSeconds)
            throw new ArgumentOutOfRangeException(nameof(startSeconds), "Trim range must have a non-negative start and a later end.");
        var manifestPath = Path.Combine(sampleDirectory, "manifest.json");
        var manifest = JsonSerializer.Deserialize<GestureStudioSampleManifest>(await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false), JsonOptions)
            ?? throw new InvalidDataException("Sample manifest could not be read.");
        var startFrame = (long)Math.Floor(startSeconds * manifest.VideoFramesPerSecond);
        var endFrameExclusive = (long)Math.Ceiling(endSeconds * manifest.VideoFramesPerSecond);
        var videoPath = Path.Combine(sampleDirectory, "trimmed.mp4");
        var landmarkPath = Path.Combine(sampleDirectory, "trimmed.jsonl");
        await Task.Run(() =>
        {
            using var reader = new VideoCapture(Path.Combine(sampleDirectory, manifest.VideoFile));
            if (!reader.IsOpened()) throw new IOException("Could not open the original video for trimming.");
            var width = (int)reader.Get(VideoCaptureProperties.FrameWidth);
            var height = (int)reader.Get(VideoCaptureProperties.FrameHeight);
            var totalFrames = (long)reader.Get(VideoCaptureProperties.FrameCount);
            if (startFrame >= totalFrames) throw new ArgumentOutOfRangeException(nameof(startSeconds), "Trim start is beyond the recorded video.");
            endFrameExclusive = Math.Min(endFrameExclusive, totalFrames);
            using var writer = new VideoWriter(videoPath, VideoWriter.FourCC('m', 'p', '4', 'v'), manifest.VideoFramesPerSecond, new Size(width, height));
            if (!writer.IsOpened()) throw new IOException("OpenCV could not create the trimmed MP4.");
            using var frame = new Mat();
            reader.Set(VideoCaptureProperties.PosFrames, startFrame);
            for (var i = startFrame; i < endFrameExclusive && !cancellationToken.IsCancellationRequested; i++)
            {
                if (!reader.Read(frame) || frame.Empty()) break;
                writer.Write(frame);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }, cancellationToken).ConfigureAwait(false);

        using var input = new StreamReader(Path.Combine(sampleDirectory, manifest.LandmarksFile));
        await using var output = new StreamWriter(new FileStream(landmarkPath, FileMode.Create, FileAccess.Write, FileShare.Read, 32 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan));
        DateTimeOffset? trimCaptureStart = null;
        while (await input.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GestureStudioFrameRecord record;
            try { record = GestureStudioFrameRecord.ParseJsonLine(line); }
            catch (JsonException) { continue; }
            if (record.VideoFrameIndex < startFrame || record.VideoFrameIndex >= endFrameExclusive) continue;
            trimCaptureStart ??= record.CapturedAtUtc;
            var adjusted = record with { VideoFrameIndex = record.VideoFrameIndex - startFrame, ElapsedMilliseconds = Math.Max(0, (record.CapturedAtUtc - trimCaptureStart.Value).TotalMilliseconds) };
            await output.WriteLineAsync(adjusted.ToJsonLine()).ConfigureAwait(false);
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        await WriteManifestAsync(manifestPath, manifest with { TrimmedVideoFile = Path.GetFileName(videoPath), TrimmedLandmarksFile = Path.GetFileName(landmarkPath) }, cancellationToken).ConfigureAwait(false);
        return (videoPath, landmarkPath);
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            await StopSampleCoreAsync(CancellationToken.None).ConfigureAwait(false);
            _disposed = true;
            SetStatus(Status with { State = GestureStudioRecordingState.Disposed, Message = "Gesture Studio recording resources released." });
        }
        finally { _lifecycle.Release(); }
    }

    private async Task WriteFramesAsync(ChannelReader<HandTrackingFrame> reader, Guid sessionId, Guid sampleId)
    {
        DateTimeOffset? started = null;
        try
        {
            await foreach (var frame in reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (frame.Width != _width || frame.Height != _height) { Interlocked.Increment(ref _dropped); continue; }
                started ??= frame.CapturedAt;
                using var mat = new Mat(_height, _width, MatType.CV_8UC3);
                if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(frame.BgrPixels, out var pixels) && pixels.Array is not null)
                    System.Runtime.InteropServices.Marshal.Copy(pixels.Array, pixels.Offset, mat.Data, pixels.Count);
                else
                    System.Runtime.InteropServices.Marshal.Copy(frame.BgrPixels.ToArray(), 0, mat.Data, frame.BgrPixels.Length);
                _videoWriter!.Write(mat);
                var row = GestureStudioFrameRecord.FromFrame(sessionId, sampleId, _frameIndex++, started.Value, frame);
                await _landmarkWriter!.WriteLineAsync(row.ToJsonLine()).ConfigureAwait(false);
                Interlocked.Increment(ref _written);
                if ((Interlocked.Read(ref _written) & 15) == 0) SetStatus(Status with { StartedAtUtc = started });
            }
            await _landmarkWriter!.FlushAsync().ConfigureAwait(false);
            SetStatus(Status with { StartedAtUtc = started });
        }
        catch (Exception exception)
        {
            SetStatus(Status with { State = GestureStudioRecordingState.Faulted, Message = $"Recording failed: {exception.Message}" });
        }
    }

    private async Task SaveManifestAsync(CancellationToken cancellationToken)
    {
        if (_manifestPath is not null && _manifest is not null) await WriteManifestAsync(_manifestPath, _manifest, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteManifestAsync(string path, GestureStudioSampleManifest manifest, CancellationToken cancellationToken)
    {
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(manifest, JsonOptions), cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, true);
    }

    private void SetStatus(GestureStudioRecordingStatus status)
    {
        lock (_gate) _status = status with { FramesWritten = Interlocked.Read(ref _written), FramesDropped = Interlocked.Read(ref _dropped) };
        try { StatusChanged?.Invoke(Status); } catch { }
    }
}

/// <summary>Small OpenCV-backed decoder for explicit review playback; does not open a camera.</summary>
public sealed class GestureStudioVideoReader : IDisposable
{
    private readonly VideoCapture _capture;
    private readonly object _gate = new();
    private bool _disposed;
    public GestureStudioVideoReader(string path)
    {
        _capture = new VideoCapture(path);
        if (!_capture.IsOpened()) { _capture.Dispose(); throw new IOException("Could not open the selected sample video."); }
        Width = (int)_capture.Get(VideoCaptureProperties.FrameWidth);
        Height = (int)_capture.Get(VideoCaptureProperties.FrameHeight);
        FrameCount = (long)_capture.Get(VideoCaptureProperties.FrameCount);
        FramesPerSecond = _capture.Get(VideoCaptureProperties.Fps);
        if (!double.IsFinite(FramesPerSecond) || FramesPerSecond <= 0) FramesPerSecond = 30;
    }
    public int Width { get; }
    public int Height { get; }
    public long FrameCount { get; }
    public double FramesPerSecond { get; private set; }
    public bool TryReadFrame(long index, out byte[] pixels)
    {
        lock (_gate)
        {
            pixels = [];
            if (_disposed || index < 0 || index >= FrameCount) return false;
            using var frame = new Mat();
            _capture.Set(VideoCaptureProperties.PosFrames, index);
            if (!_capture.Read(frame) || frame.Empty()) return false;
            if (frame.Type() != MatType.CV_8UC3) Cv2.CvtColor(frame, frame, ColorConversionCodes.BGRA2BGR);
            pixels = new byte[checked(Width * Height * 3)];
            System.Runtime.InteropServices.Marshal.Copy(frame.Data, pixels, 0, pixels.Length);
            return true;
        }
    }
    public void Dispose() { lock (_gate) { if (_disposed) return; _disposed = true; _capture.Dispose(); } }
}
