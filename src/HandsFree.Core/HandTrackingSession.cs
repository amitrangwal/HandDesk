namespace HandsFree.Core;

/// <summary>Coordinates explicit tracking start/stop and exposes only the latest frame to the UI.</summary>
public sealed class HandTrackingSession : IAsyncDisposable
{
    private readonly IHandTrackingProvider _provider;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _stateGate = new();
    private HandTrackingStatus _status = new(HandTrackingState.Stopped, "Tracking is stopped.");
    private HandTrackingFrame? _latestFrame;
    private volatile bool _disposed;

    public HandTrackingSession(IHandTrackingProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _provider.FrameAvailable += OnFrameAvailable;
        _provider.StatusChanged += OnProviderStatusChanged;
        _provider.Faulted += OnProviderFaulted;
    }

    public event Action<HandTrackingStatus>? StatusChanged;

    public HandTrackingStatus Status
    {
        get { lock (_stateGate) return _status; }
    }

    public HandTrackingFrame? LatestFrame => Volatile.Read(ref _latestFrame);

    /// <summary>Explicitly releases the current identity lock so the next candidate can be selected.</summary>
    public void ResetHandSelection()
    {
        if (_disposed) return;
        _provider.ResetHandSelection();
        Interlocked.Exchange(ref _latestFrame, null);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed || Status.State is HandTrackingState.Starting or HandTrackingState.Running) return;
            SetStatus(HandTrackingState.Starting, "Starting camera and local hand tracking…");
            Interlocked.Exchange(ref _latestFrame, null);
            try
            {
                await _provider.StartAsync(cancellationToken).ConfigureAwait(false);
                if (!_disposed && Status.State != HandTrackingState.Faulted)
                    SetStatus(HandTrackingState.Running, "Camera is active. Looking for one hand…");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await StopProviderAfterFailureAsync().ConfigureAwait(false);
                SetStatus(HandTrackingState.Stopped, "Tracking start was cancelled.");
            }
            catch (Exception exception)
            {
                await StopProviderAfterFailureAsync().ConfigureAwait(false);
                SetStatus(HandTrackingState.Faulted, FriendlyError(exception));
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
            if (_disposed || Status.State is HandTrackingState.Stopped or HandTrackingState.Disposed) return;
            SetStatus(HandTrackingState.Stopping, "Stopping camera and releasing tracking resources…");
            Interlocked.Exchange(ref _latestFrame, null);
            try
            {
                await _provider.StopAsync(cancellationToken).ConfigureAwait(false);
                SetStatus(HandTrackingState.Stopped, "Tracking is stopped.");
            }
            catch (Exception exception)
            {
                SetStatus(HandTrackingState.Faulted, $"Tracking stopped with a cleanup error: {FriendlyError(exception)}");
            }
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
            SetStatus(HandTrackingState.Stopping, "Releasing camera and tracking resources…");
            Interlocked.Exchange(ref _latestFrame, null);
            try { await _provider.StopAsync().ConfigureAwait(false); }
            catch { /* Dispose still releases the provider even if camera shutdown reports an error. */ }
            _provider.FrameAvailable -= OnFrameAvailable;
            _provider.StatusChanged -= OnProviderStatusChanged;
            _provider.Faulted -= OnProviderFaulted;
            await _provider.DisposeAsync().ConfigureAwait(false);
            SetStatus(HandTrackingState.Disposed, "Tracking resources released.");
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private void OnFrameAvailable(HandTrackingFrame frame)
    {
        if (_disposed || frame.Width <= 0 || frame.Height <= 0
            || frame.BgrPixels.Length != (long)frame.Width * frame.Height * 3
            || frame.Result is null
            || (frame.Result.IsDetected && frame.Result.Landmarks.Count != 21)) return;
        Interlocked.Exchange(ref _latestFrame, frame);
    }

    private void OnProviderStatusChanged(string message)
    {
        var current = Status;
        if (!_disposed && current.State is HandTrackingState.Starting or HandTrackingState.Running)
            SetStatus(current.State, message);
    }

    private void OnProviderFaulted(string message) => _ = HandleProviderFaultAsync(message);

    private async Task HandleProviderFaultAsync(string message)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            Interlocked.Exchange(ref _latestFrame, null);
            try { await _provider.StopAsync().ConfigureAwait(false); }
            catch { /* Preserve the originating camera/inference error in the visible status. */ }
            SetStatus(HandTrackingState.Faulted, message);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task StopProviderAfterFailureAsync()
    {
        try { await _provider.StopAsync().ConfigureAwait(false); }
        catch { /* Preserve the startup error while still attempting provider cleanup. */ }
    }

    private void SetStatus(HandTrackingState state, string message)
    {
        HandTrackingStatus status;
        lock (_stateGate)
        {
            _status = status = new HandTrackingStatus(state, message);
        }
        StatusChanged?.Invoke(status);
    }

    private static string FriendlyError(Exception exception) =>
        exception is FileNotFoundException
            ? "A required local hand-tracking model is missing. Restore the bundled model files and restart tracking."
            : exception is UnauthorizedAccessException
                ? "Camera access was denied. Check Windows camera privacy settings and try again."
                : $"Could not start local hand tracking: {exception.Message}";
}
