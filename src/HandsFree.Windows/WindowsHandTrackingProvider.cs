using System.Runtime.InteropServices;
using System.Threading.Channels;
using HandsFree.Core;
using OpenCvSharp;

namespace HandsFree.Windows;

/// <summary>Explicit Windows Media Foundation camera capture with a single-slot inference handoff.</summary>
public sealed class WindowsHandTrackingProvider : IHandTrackingProvider
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SingleHandTrackingSelector _handSelector;
    private readonly HandTrackingMetricAccumulator _metrics = new();
    private readonly HandTrackingDiagnosticsAccumulator _diagnostics = new();
    private readonly object _selectionGate = new();
    private int _selectionGeneration;
    private int _diagnosticsGeneration;
    private CancellationTokenSource? _cancellation;
    private Channel<CapturedFrame>? _frames;
    private VideoCapture? _capture;
    private OnnxHandTrackingPipeline? _pipeline;
    private Task? _captureTask;
    private Task? _inferenceTask;
    private int _faultReported;
    private long _nextFrameSequence;
    private bool _disposed;

    public WindowsHandTrackingProvider(
        TimeSpan? recoveryWindow = null,
        HandTrackingPreference preference = HandTrackingPreference.RightOnly)
    {
        _handSelector = new SingleHandTrackingSelector(recoveryWindow, preference);
    }

    public event Action<HandTrackingFrame>? FrameAvailable;
    public event Action<string>? StatusChanged;
    public event Action<string>? Faulted;
    public HandTrackingMetricsSnapshot Metrics => _metrics.Snapshot();
    public HandTrackingDiagnosticsSnapshot Diagnostics => _diagnostics.Snapshot();

    public void ResetDiagnostics()
    {
        lock (_selectionGate)
        {
            Interlocked.Increment(ref _diagnosticsGeneration);
            _diagnostics.Reset();
        }
    }

    public void ResetHandSelection()
    {
        lock (_selectionGate)
        {
            Interlocked.Increment(ref _selectionGeneration);
            _handSelector.Reset();
        }
        StatusChanged?.Invoke("Hand lock reset. Waiting for a visible hand to select.");
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Local webcam tracking is currently available on Windows only.");
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_capture is not null) return;
            Interlocked.Exchange(ref _faultReported, 0);
            _metrics.Reset();
            _diagnostics.Reset();
            Interlocked.Exchange(ref _nextFrameSequence, 0);
            var modelRoot = Path.Combine(AppContext.BaseDirectory, "Models");
            var palmPath = Path.Combine(modelRoot, "PalmDetection", "palm_detection_mediapipe_2023feb.onnx");
            var handPath = Path.Combine(modelRoot, "HandPose", "handpose_estimation_mediapipe_2023feb.onnx");

            var pipeline = await Task.Run(() => new OnnxHandTrackingPipeline(palmPath, handPath), cancellationToken).ConfigureAwait(false);
            VideoCapture? capture = null;
            try
            {
                capture = await Task.Run(() => new VideoCapture(0, VideoCaptureAPIs.MSMF), cancellationToken).ConfigureAwait(false);
                if (!capture.IsOpened()) throw new InvalidOperationException("No camera is available, or Windows camera access was denied.");
                capture.Set(VideoCaptureProperties.FrameWidth, 640);
                capture.Set(VideoCaptureProperties.FrameHeight, 480);

                _pipeline = pipeline;
                _capture = capture;
                _cancellation = new CancellationTokenSource();
                _frames = Channel.CreateBounded<CapturedFrame>(new BoundedChannelOptions(1)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = true,
                    AllowSynchronousContinuations = false
                });
                var token = _cancellation.Token;
                _inferenceTask = Task.Run(() => InferenceLoopAsync(_frames.Reader, pipeline, token), CancellationToken.None);
                _captureTask = Task.Run(() => CaptureLoopAsync(capture, _frames.Writer, token), CancellationToken.None);
                StatusChanged?.Invoke("Camera connected. Local CPU inference is running.");
            }
            catch
            {
                capture?.Dispose();
                pipeline.Dispose();
                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_capture is null && _pipeline is null) return;
            _metrics.Reset();
            var cancellation = _cancellation;
            var frames = _frames;
            cancellation?.Cancel();
            frames?.Writer.TryComplete();
            try { _capture?.Release(); } catch { /* Continue releasing other camera resources. */ }

            var tasks = new[] { _captureTask, _inferenceTask }.Where(task => task is not null).Cast<Task>();
            try { await Task.WhenAll(tasks).WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellation?.IsCancellationRequested == true) { }
            catch (Exception) when (cancellation?.IsCancellationRequested == true) { }

            _metrics.Reset();

            _capture?.Dispose();
            _pipeline?.Dispose();
            cancellation?.Dispose();
            _capture = null;
            _pipeline = null;
            _cancellation = null;
            _frames = null;
            _captureTask = null;
            _inferenceTask = null;
            lock (_selectionGate)
            {
                Interlocked.Increment(ref _selectionGeneration);
                _handSelector.Reset();
            }
            StatusChanged?.Invoke("Camera and local inference resources released.");
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
        }
        finally { _lifecycle.Release(); }
        await StopAsync().ConfigureAwait(false);
    }

    private async Task CaptureLoopAsync(VideoCapture capture, ChannelWriter<CapturedFrame> writer, CancellationToken cancellationToken)
    {
        try
        {
            using var image = new Mat();
            var failures = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!capture.Read(image) || image.Empty())
                {
                    if (++failures >= 5) throw new InvalidOperationException("The camera stopped returning frames. Check that it is connected and not in use by another application.");
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                failures = 0;
                var bgr = new byte[checked(image.Rows * image.Cols * 3)];
                if (image.IsContinuous()) Marshal.Copy(image.Data, bgr, 0, bgr.Length);
                else
                {
                    var rowSize = image.Cols * 3;
                    var rows = image.Rows;
                    for (var row = 0; row < rows; row++)
                        Marshal.Copy(image.Ptr(row), bgr, row * rowSize, rowSize);
                }
                var sequence = Interlocked.Increment(ref _nextFrameSequence);
                writer.TryWrite(new CapturedFrame(image.Cols, image.Rows, bgr, DateTimeOffset.UtcNow, sequence));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested) { ReportFault($"Camera capture failed: {exception.Message}"); }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { }
        finally { writer.TryComplete(); }
    }

    private async Task InferenceLoopAsync(ChannelReader<CapturedFrame> reader, OnnxHandTrackingPipeline pipeline, CancellationToken cancellationToken)
    {
        try
        {
            long lastProcessedSequence = 0;
            await foreach (var frame in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var generation = Volatile.Read(ref _selectionGeneration);
                var diagnosticsGeneration = Volatile.Read(ref _diagnosticsGeneration);
                var detections = pipeline.InferCandidates(frame.Pixels, frame.Width, frame.Height, out var onnxInferenceTime, out var pipelineDiagnostics);
                var dropped = Math.Max(0, frame.Sequence - lastProcessedSequence - 1);
                lastProcessedSequence = frame.Sequence;
                _metrics.RecordFrame(onnxInferenceTime, frame.CapturedAt, dropped);
                lock (_selectionGate)
                {
                    // A reset during inference invalidates the in-flight result so it cannot
                    // restore an old identity or overwrite the cleared session frame.
                    if (generation != _selectionGeneration) continue;
                    var selection = _handSelector.Update(detections, frame.CapturedAt);
                    if (diagnosticsGeneration == _diagnosticsGeneration)
                        _diagnostics.RecordFrame(pipelineDiagnostics, selection, detections.Count > 0);
                    FrameAvailable?.Invoke(new HandTrackingFrame(
                        frame.Width,
                        frame.Height,
                        frame.Pixels,
                        selection.Result,
                        frame.CapturedAt,
                        selection.State,
                        selection.Message));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested) { ReportFault($"Local hand inference failed: {exception.Message}"); }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { }
    }

    private void ReportFault(string message)
    {
        if (Interlocked.Exchange(ref _faultReported, 1) != 0) return;
        _cancellation?.Cancel();
        _frames?.Writer.TryComplete();
        Faulted?.Invoke(message);
    }

    private sealed record CapturedFrame(int Width, int Height, byte[] Pixels, DateTimeOffset CapturedAt, long Sequence);
}
