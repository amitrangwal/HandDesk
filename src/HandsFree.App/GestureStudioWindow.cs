using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using HandsFree.Core;
using HandsFree.Windows;

namespace HandsFree.App;

/// <summary>Local sample recorder and reviewer that subscribes to the existing tracking session.</summary>
public sealed class GestureStudioWindow : Window
{
    private readonly HandTrackingSession _tracking;
    private readonly GestureStudioStore _store;
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly TextBox _label = new() { PlaceholderText = "Gesture label (required)", MinWidth = 180 };
    private readonly TextBox _notes = new() { PlaceholderText = "Optional session/sample notes", MinWidth = 220 };
    private readonly TextBlock _session = new();
    private readonly TextBlock _recordStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _trackingStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly HandPreviewControl _livePreview = new() { Height = 250, MinHeight = 180, MaxHeight = 340 };
    private readonly HandPreviewControl _reviewPreview = new() { Height = 250, MinHeight = 180, MaxHeight = 340 };
    private readonly Button _start = new() { Content = "Start Recording", MinHeight = 30 };
    private readonly Button _stop = new() { Content = "Stop Recording", MinHeight = 30, IsEnabled = false };
    private readonly Button _play = new() { Content = "Play", MinHeight = 30, IsEnabled = false };
    private readonly Button _previous = new() { Content = "◀ Frame", MinHeight = 30, IsEnabled = false };
    private readonly Button _next = new() { Content = "Frame ▶", MinHeight = 30, IsEnabled = false };
    private readonly Button _trim = new() { Content = "Export trimmed interval", MinHeight = 30, IsEnabled = false };
    private readonly Button _previewTrim = new() { Content = "Preview interval", MinHeight = 30, IsEnabled = false };
    private readonly Button _refresh = new() { Content = "Refresh samples", MinHeight = 30 };
    private readonly Button _reviewLabelButton = new() { Content = "Save reviewed label", MinHeight = 30, IsEnabled = false };
    private readonly Button _discard = new() { Content = "Mark discarded", MinHeight = 30, IsEnabled = false };
    private readonly TextBox _reviewLabel = new() { PlaceholderText = "Reviewed label", MinWidth = 150 };
    private readonly TextBox _reviewNotes = new() { PlaceholderText = "Review notes", MinWidth = 180 };
    private readonly TextBox _trimStart = new() { Text = "0", Width = 72 };
    private readonly TextBox _trimEnd = new() { Text = "5", Width = 72 };
    private readonly Slider _seek = new() { Minimum = 0, Maximum = 0, IsEnabled = false };
    private readonly TextBlock _time = new() { Text = "00:00.000" };
    private readonly TextBlock _sourceTimestamp = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _reviewStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _overlayEnabled = new() { Content = "Show landmark overlay", IsChecked = true, IsEnabled = false };
    private readonly ListBox _samples = new() { Width = 440, MinHeight = 100, MaxHeight = 170 };
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private int _reviewDecodeGeneration;
    private int _reviewDecodeBusy;
    private HandTrackingFrame? _latestFrame;
    private DateTimeOffset _lastLiveTimestamp;
    private GestureStudioRecordingStatus _recordingStatus;
    private IReadOnlyList<(string Directory, GestureStudioSampleManifest Manifest)> _sampleRows = [];
    private GestureStudioVideoReader? _reader;
    private GestureStudioSampleManifest? _selectedManifest;
    private string? _selectedDirectory;
    private GestureStudioLandmarkReader? _landmarkReader;
    private long _reviewFrame;
    private long? _playbackEndFrame;
    private bool _isPlaying;
    private bool _overlayVisible = true;
    private bool _allowClose;
    private bool _closeInProgress;
    private Task? _finalizeTask;

    public GestureStudioWindow(HandTrackingSession tracking, GestureStudioStore? store = null)
    {
        _tracking = tracking ?? throw new ArgumentNullException(nameof(tracking));
        _store = store ?? new GestureStudioStore();
        _recordingStatus = _store.Status;
        Title = "Gesture Studio — HandsFree";
        Width = 1050;
        Height = 820;
        MinWidth = 650;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var content = new StackPanel { Margin = new Thickness(14), Spacing = 9, Children =
        {
            new TextBlock { Text = "Gesture Studio", FontSize = 22, FontWeight = FontWeight.SemiBold },
            new TextBlock { Text = $"Local-only data • {_store.RootDirectory}", TextWrapping = TextWrapping.Wrap, FontSize = 12 },
            Section("Session setup", new WrapPanel { Children = { _label, _notes, _session } }),
            Section("Recording", new StackPanel { Spacing = 5, Children =
            {
                new WrapPanel { Children = { _start, _stop } }, _recordStatus, _trackingStatus, _livePreview
            } }),
            Section("Review and trim", new StackPanel { Spacing = 5, Children =
            {
                new WrapPanel { Children = { _samples, _refresh } }, _reviewPreview,
                new WrapPanel { Children = { _play, _previous, _next, _overlayEnabled, _time } }, _sourceTimestamp, _seek,
                new WrapPanel { Children = { new TextBlock { Text = "Start (sec)", VerticalAlignment = VerticalAlignment.Center }, _trimStart, new TextBlock { Text = "End (sec)", VerticalAlignment = VerticalAlignment.Center }, _trimEnd, _previewTrim, _trim } },
                _reviewStatus
            } }),
            Section("Sample management", new StackPanel { Spacing = 5, Children =
            {
                new TextBlock { Text = "Review metadata is stored in the manifest. Discard marks a sample; original files are preserved.", TextWrapping = TextWrapping.Wrap, FontSize = 12 },
                new WrapPanel { Children = { _reviewLabel, _reviewNotes, _reviewLabelButton, _discard } }
            } })
        }};
        Content = new ScrollViewer { VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = content };

        _session.Text = $"Session ID: {_sessionId:N}";
        _trackingStatus.Text = _tracking.Status.Message;
        _recordStatus.Text = "Recording is stopped. Start tracking before recording.";
        _tracking.FrameAvailable += OnFrameAvailable;
        _tracking.StatusChanged += OnTrackingStatusChanged;
        _uiTimer.Tick += OnUiTimerTick;
        _uiTimer.Start();
        _samples.SelectionChanged += OnSampleSelected;
        _start.Click += async (_, _) => await StartRecordingAsync();
        _stop.Click += async (_, _) => await StopRecordingAsync();
        _play.Click += (_, _) => TogglePlayback();
        _previous.Click += (_, _) => SeekTo(_reviewFrame - 1);
        _next.Click += (_, _) => SeekTo(_reviewFrame + 1);
        _overlayEnabled.IsCheckedChanged += (_, _) => { _overlayVisible = _overlayEnabled.IsChecked == true; RenderReviewFrame(); };
        _seek.PropertyChanged += (_, args) => { if (args.Property == Slider.ValueProperty && !_isPlaying) SeekTo((long)_seek.Value); };
        _refresh.Click += (_, _) => RefreshSamples();
        _reviewLabelButton.Click += async (_, _) => await UpdateSelectedSampleAsync(discard: false);
        _discard.Click += async (_, _) => await UpdateSelectedSampleAsync(discard: true);
        _trim.Click += async (_, _) => await ExportTrimAsync();
        _previewTrim.Click += (_, _) => PreviewTrimInterval();
        _store.StatusChanged += OnRecordingStatusChanged;
        RefreshSamples();
        Closed += OnClosed;
        Closing += OnClosing;
    }

    public async Task CloseForOwnerAsync()
    {
        await FinalizeOnceAsync();
        if (_allowClose) return;
        _allowClose = true;
        Close();
    }

    private static Control Section(string heading, Control child) => new Border
    {
        Padding = new Thickness(9),
        CornerRadius = new CornerRadius(7),
        Classes = { "section-card" },
        Child = new StackPanel { Spacing = 5, Children = { new TextBlock { Text = heading, FontSize = 15, FontWeight = FontWeight.SemiBold }, child } }
    };

    private void OnFrameAvailable(HandTrackingFrame frame)
    {
        Volatile.Write(ref _latestFrame, frame);
        _store.TryWriteFrame(frame);
    }

    private void OnTrackingStatusChanged(HandTrackingStatus status) => Dispatcher.UIThread.Post(() =>
    {
        _trackingStatus.Text = status.Message;
        if (status.State is HandTrackingState.Stopped or HandTrackingState.Faulted or HandTrackingState.Disposed)
        {
            _start.IsEnabled = false;
            Volatile.Write(ref _latestFrame, null);
            _livePreview.Clear();
            if (_store.Status.State == GestureStudioRecordingState.Recording) _ = StopRecordingAsync();
        }
        else if (status.State == HandTrackingState.Running) _start.IsEnabled = _store.Status.State != GestureStudioRecordingState.Recording;
    });

    private void OnUiTimerTick(object? sender, EventArgs args)
    {
        var frame = Volatile.Read(ref _latestFrame);
        if (frame is not null && frame.CapturedAt != _lastLiveTimestamp)
        {
            _lastLiveTimestamp = frame.CapturedAt;
            _livePreview.UpdateFrame(frame);
            _trackingStatus.Text = frame.IdentityState == HandTrackingIdentityState.Tracking && frame.Result.IsDetected
                ? $"Selected hand visible • {frame.Result.Confidence:P0} confidence"
                : frame.TrackingMessage;
        }
        _recordStatus.Text = _recordingStatus.State == GestureStudioRecordingState.Recording
            ? $"Recording • {ElapsedText(_recordingStatus.StartedAtUtc)} • {_recordingStatus.FramesWritten:N0} frames • {_recordingStatus.FramesDropped:N0} queue drops"
            : _recordingStatus.Message;
        if (_isPlaying && _reader is not null)
        {
            if (_reviewFrame + 1 >= Math.Min(_reader.FrameCount, _playbackEndFrame ?? _reader.FrameCount)) _isPlaying = false;
            else SeekTo(_reviewFrame + 1);
            _play.Content = _isPlaying ? "Pause" : "Play";
        }
    }

    private async Task StartRecordingAsync()
    {
        if (_tracking.Status.State != HandTrackingState.Running) { _recordStatus.Text = "Start tracking before recording."; return; }
        var frame = Volatile.Read(ref _latestFrame) ?? _tracking.LatestFrame;
        if (frame is null) { _recordStatus.Text = "Waiting for the first camera frame."; return; }
        _start.IsEnabled = false;
        var started = await _store.StartSampleAsync(_sessionId, _label.Text ?? "", _notes.Text, frame.Width, frame.Height);
        _stop.IsEnabled = started;
        if (!started) _start.IsEnabled = _tracking.Status.State == HandTrackingState.Running;
        if (started) _recordStatus.Text = "Recording started. Frames without a selected hand are written with landmarks=null.";
    }

    private async Task StopRecordingAsync()
    {
        _stop.IsEnabled = false;
        await _store.StopSampleAsync();
        _start.IsEnabled = _tracking.Status.State == HandTrackingState.Running;
        RefreshSamples();
    }

    private void OnRecordingStatusChanged(GestureStudioRecordingStatus status) => Dispatcher.UIThread.Post(() =>
    {
        _recordingStatus = status;
        _stop.IsEnabled = status.State == GestureStudioRecordingState.Recording;
        _start.IsEnabled = status.State != GestureStudioRecordingState.Recording && _tracking.Status.State == HandTrackingState.Running;
        _recordStatus.Text = status.Message;
    });

    private void RefreshSamples()
    {
        try
        {
            _sampleRows = _store.ListSamples();
            _samples.ItemsSource = _sampleRows.Select(row => $"{row.Manifest.CreatedAtUtc.LocalDateTime:g} • {row.Manifest.Label} • {row.Manifest.Disposition} • {row.Manifest.SampleId:N}").ToArray();
        }
        catch (Exception exception) { _reviewStatus.Text = $"Could not list local samples: {exception.Message}"; }
    }

    private async void OnSampleSelected(object? sender, SelectionChangedEventArgs args)
    {
        DisposeReader();
        _landmarkReader?.Dispose();
        _landmarkReader = null;
        var index = _samples.SelectedIndex;
        _selectedDirectory = null;
        _selectedManifest = null;
        _play.IsEnabled = _previous.IsEnabled = _next.IsEnabled = _overlayEnabled.IsEnabled = _trim.IsEnabled = _previewTrim.IsEnabled = _reviewLabelButton.IsEnabled = _discard.IsEnabled = false;
        if (index < 0 || index >= _sampleRows.Count) return;
        var row = _sampleRows[index];
        _selectedDirectory = row.Directory;
        _selectedManifest = row.Manifest;
        try
        {
            var videoPath = Path.Combine(row.Directory, row.Manifest.TrimmedVideoFile ?? row.Manifest.VideoFile);
            var jsonPath = Path.Combine(row.Directory, row.Manifest.TrimmedLandmarksFile ?? row.Manifest.LandmarksFile);
            var loaded = await Task.Run(() =>
            {
                var reader = new GestureStudioVideoReader(videoPath);
                try { return (Reader: reader, Landmarks: new GestureStudioLandmarkReader(jsonPath)); }
                catch { reader.Dispose(); throw; }
            });
            if (!string.Equals(_selectedDirectory, row.Directory, StringComparison.Ordinal)) { loaded.Reader.Dispose(); loaded.Landmarks.Dispose(); return; }
            _reader = loaded.Reader;
            _landmarkReader = loaded.Landmarks;
            _reviewFrame = 0;
            _seek.Minimum = 0;
            _seek.Maximum = Math.Max(0, _reader.FrameCount - 1);
            _seek.IsEnabled = _reader.FrameCount > 0;
            _play.IsEnabled = _previous.IsEnabled = _next.IsEnabled = _overlayEnabled.IsEnabled = _trim.IsEnabled = _previewTrim.IsEnabled = _reviewLabelButton.IsEnabled = _discard.IsEnabled = true;
            _reviewLabel.Text = row.Manifest.ReviewLabel ?? "";
            _reviewNotes.Text = row.Manifest.ReviewNotes ?? "";
            _trimStart.Text = "0";
            _trimEnd.Text = (_reader.FrameCount / _reader.FramesPerSecond).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            RenderReviewFrame();
            _reviewStatus.Text = $"Loaded {row.Manifest.Label}: {_reader.FrameCount:N0} frames at {_reader.FramesPerSecond:F1} fps.";
        }
        catch (Exception exception) { _reviewStatus.Text = $"Could not open sample: {exception.Message}"; DisposeReader(); }
    }

    private void SeekTo(long frame)
    {
        if (_reader is null || _reader.FrameCount <= 0) return;
        _reviewFrame = Math.Clamp(frame, 0, _reader.FrameCount - 1);
        if (!_isPlaying) _seek.Value = _reviewFrame;
        _time.Text = TimeSpan.FromSeconds(_reviewFrame / _reader.FramesPerSecond).ToString(@"mm\:ss\.fff");
        RenderReviewFrame();
    }

    private void RenderReviewFrame()
    {
        var reader = _reader;
        if (reader is null || Interlocked.Exchange(ref _reviewDecodeBusy, 1) != 0) return;
        var requested = _reviewFrame;
        var generation = Interlocked.Increment(ref _reviewDecodeGeneration);
        var landmarkReader = _landmarkReader;
        _ = Task.Run(() => reader.TryReadFrame(requested, out var pixels)
            ? (Success: true, Pixels: pixels, Record: landmarkReader?.ReadAt(requested))
                : (Success: false, Pixels: Array.Empty<byte>(), Record: (GestureStudioFrameRecord?)null))
            .ContinueWith(task => Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (generation == Volatile.Read(ref _reviewDecodeGeneration) && ReferenceEquals(reader, _reader)
                        && task.Status == TaskStatus.Faulted)
                        _reviewStatus.Text = $"Review frame could not be read: {task.Exception?.GetBaseException().Message}";
                    else if (generation == Volatile.Read(ref _reviewDecodeGeneration) && ReferenceEquals(reader, _reader)
                        && task.Status == TaskStatus.RanToCompletion && !task.Result.Success)
                        _reviewStatus.Text = "The selected video frame could not be decoded.";
                    if (task.Status == TaskStatus.RanToCompletion && task.Result.Success && generation == Volatile.Read(ref _reviewDecodeGeneration)
                        && ReferenceEquals(reader, _reader))
                    {
                        _reviewPreview.UpdateImage(reader.Width, reader.Height, task.Result.Pixels, _overlayVisible ? task.Result.Record?.Landmarks : null, true, false);
                        _sourceTimestamp.Text = task.Result.Record is { } record
                            ? $"Captured: {record.CapturedAtUtc:yyyy-MM-dd HH:mm:ss.fff 'UTC'} • state: {record.TrackingState} • hand: {(record.IsDetected ? $"{record.Confidence:P0} confidence" : record.MissingReason)}"
                            : "Captured timestamp: unavailable for this frame.";
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _reviewDecodeBusy, 0);
                    if (ReferenceEquals(reader, _reader) && requested != _reviewFrame) RenderReviewFrame();
                }
            }), TaskScheduler.Default);
    }

    private void TogglePlayback()
    {
        if (_reader is null) return;
        _isPlaying = !_isPlaying;
        _play.Content = _isPlaying ? "Pause" : "Play";
    }

    private void PreviewTrimInterval()
    {
        if (_reader is null || !double.TryParse(_trimStart.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var start)
            || !double.TryParse(_trimEnd.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var end)
            || start < 0 || end <= start)
        { _reviewStatus.Text = "Enter a valid trim interval before previewing it."; return; }
        var first = (long)Math.Floor(start * _reader.FramesPerSecond);
        _playbackEndFrame = Math.Min(_reader.FrameCount, (long)Math.Ceiling(end * _reader.FramesPerSecond));
        if (first >= _playbackEndFrame) { _reviewStatus.Text = "The selected interval is outside this video."; return; }
        _isPlaying = false;
        _play.Content = "Play";
        SeekTo(first);
        _isPlaying = true;
        _play.Content = "Pause";
        _reviewStatus.Text = $"Previewing selected interval {start:F2}–{end:F2} seconds.";
    }

    private async Task ExportTrimAsync()
    {
        if (_selectedDirectory is null || _reader is null) return;
        if (!double.TryParse(_trimStart.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var start)
            || !double.TryParse(_trimEnd.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var end))
        { _reviewStatus.Text = "Enter valid start and end seconds."; return; }
        _trim.IsEnabled = false;
        try
        {
            var paths = await _store.ExportTrimAsync(_selectedDirectory, start, end);
            _reviewStatus.Text = $"Trim exported: {Path.GetFileName(paths.VideoPath)} and {Path.GetFileName(paths.LandmarksPath)}.";
            RefreshSamples();
        }
        catch (Exception exception) { _reviewStatus.Text = $"Trim failed: {exception.Message}"; }
        finally { _trim.IsEnabled = true; }
    }

    private async Task UpdateSelectedSampleAsync(bool discard)
    {
        if (_selectedDirectory is null) return;
        try
        {
            await _store.UpdateSampleAsync(_selectedDirectory, _reviewLabel.Text, _reviewNotes.Text, discard);
            _reviewStatus.Text = discard ? "Sample marked discarded; original files preserved." : "Reviewed label and notes saved.";
            RefreshSamples();
        }
        catch (Exception exception) { _reviewStatus.Text = $"Could not update sample manifest: {exception.Message}"; }
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        if (_closeInProgress) return;
        _closeInProgress = true;
        await FinalizeOnceAsync();
        if (_allowClose) return;
        _allowClose = true;
        Close();
    }

    private Task FinalizeOnceAsync() => _finalizeTask ??= FinalizeAsync();

    private async Task FinalizeAsync()
    {
        _uiTimer.Stop();
        _tracking.FrameAvailable -= OnFrameAvailable;
        _tracking.StatusChanged -= OnTrackingStatusChanged;
        _store.StatusChanged -= OnRecordingStatusChanged;
        await _store.DisposeAsync();
    }

    private void OnClosed(object? sender, EventArgs args) { DisposeReader(); _livePreview.Dispose(); _reviewPreview.Dispose(); }
    private void DisposeReader() { Interlocked.Increment(ref _reviewDecodeGeneration); _reader?.Dispose(); _reader = null; _landmarkReader?.Dispose(); _landmarkReader = null; _reviewPreview.Clear(); }
    private static string ElapsedText(DateTimeOffset? start) => start is null ? "00:00" : (DateTimeOffset.UtcNow - start.Value).ToString(@"mm\:ss");
}

/// <summary>Sequential bounded-memory JSONL reader; seeking backward replays from the start on a worker thread.</summary>
internal sealed class GestureStudioLandmarkReader : IDisposable
{
    private readonly object _gate = new();
    private readonly string _path;
    private StreamReader? _reader;
    private long _currentIndex = -1;
    private GestureStudioFrameRecord? _current;

    public GestureStudioLandmarkReader(string path) { _path = path; _reader = new StreamReader(path); }

    public GestureStudioFrameRecord? ReadAt(long index)
    {
        lock (_gate)
        {
            if (_reader is null || index < 0) return null;
            if (index < _currentIndex)
            {
                _reader.Dispose();
                _reader = new StreamReader(_path);
                _currentIndex = -1;
                _current = null;
            }
            while (_currentIndex < index)
            {
                var line = _reader.ReadLine();
                if (line is null) { _current = null; return null; }
                _current = GestureStudioFrameRecord.ParseJsonLine(line);
                _currentIndex++;
            }
            return _current?.VideoFrameIndex == index ? _current : null;
        }
    }

    public void Dispose() { lock (_gate) { _reader?.Dispose(); _reader = null; } }
}
