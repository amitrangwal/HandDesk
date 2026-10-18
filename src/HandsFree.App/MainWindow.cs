using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia;
using Avalonia.Threading;
using Avalonia.Styling;
using HandsFree.Core;
using HandsFree.Windows;

namespace HandsFree.App;

public sealed class MainWindow : Window
{
    private readonly WindowsControlAdapter _windowsAdapter = new();
    private readonly IControlEngine _controlEngine;
    private readonly ScrollTestModeController _scrollTestMode;
    private readonly HandTrackingSession _handTracking;
    private readonly HandLandmarkDataCollector _landmarkCollector = new();
    private readonly IndexFingerPointerMapper _pointerMapper = new();
    private PointerMovementWindow _pointerWindow = PointerMovementWindow.Default;
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly DispatcherTimer _resourceTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly WindowsProcessResourceSampler _processResourceSampler = new();
    private readonly HandPreviewControl _handPreview = new() { Height = 300, MinHeight = 220 };
    private readonly SimulatedDesktopControl _simulatedDesktop = new() { Height = 220, MinHeight = 170 };
    private readonly TextBlock _pointerStatus = new() { Text = "Pointer simulation is disabled.", TextWrapping = TextWrapping.Wrap };
    private readonly Button _pointerToggleButton = new() { Content = "Enable pointer simulation", MinWidth = 190, IsEnabled = false };
    private readonly Slider _windowLeft = new() { Minimum = 0, Maximum = 1, Value = 0.2, Width = 220 };
    private readonly Slider _windowTop = new() { Minimum = 0, Maximum = 1, Value = 0.1, Width = 220 };
    private readonly Slider _windowWidth = new() { Minimum = 0.1, Maximum = 1, Value = 0.6, Width = 220 };
    private readonly Slider _windowHeight = new() { Minimum = 0.1, Maximum = 1, Value = 0.8, Width = 220 };
    private bool _updatingWindowControls;
    private bool _pointerSimulationEnabled;
    private readonly Button _startTrackingButton = new() { Content = "Start Tracking", MinWidth = 130 };
    private readonly Button _stopTrackingButton = new() { Content = "Stop Tracking", MinWidth = 130, IsEnabled = false };
    private readonly Button _resetHandSelectionButton = new() { Content = "Reset Hand Lock", MinWidth = 130, IsEnabled = false };
    private readonly TextBlock _trackingStatus = new() { Text = "Tracking is stopped.", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _processCpuMetric = new() { Text = "Process CPU: sampling…", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _memoryMetric = new() { Text = "Memory: sampling…", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _inferenceMetric = new() { Text = "Inference: idle", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _trackingRateMetric = new() { Text = "Tracking rate: idle", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _droppedFramesMetric = new() { Text = "Dropped frames: idle", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _trackingDiagnostics = new() { Text = "Tracking diagnostics: waiting for data.", TextWrapping = TextWrapping.Wrap };
    private readonly Button _resetDiagnosticsButton = new() { Content = "Reset diagnostics", MinWidth = 130 };
    private readonly TextBox _gestureLabel = new() { PlaceholderText = "Gesture label (required)", Width = 200 };
    private readonly Button _startCollectionButton = new() { Content = "Start Collection", MinWidth = 140, IsEnabled = false };
    private readonly Button _stopCollectionButton = new() { Content = "Stop Collection", MinWidth = 140, IsEnabled = false };
    private readonly TextBlock _collectionStatus = new() { Text = "Collection is stopped.", TextWrapping = TextWrapping.Wrap };
    private DateTimeOffset _lastPreviewTimestamp;
    private readonly TextBox _executablePath = new() { PlaceholderText = "Full path to an .exe", Width = 360 };
    private readonly TextBox _xCoordinate = new() { Text = "640", Width = 90 };
    private readonly TextBox _yCoordinate = new() { Text = "360", Width = 90 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _captureShortcut = new() { Text = "Ctrl+Alt+F9", Width = 145 };
    private readonly TextBox _scrollDownShortcut = new() { Text = "Ctrl+Alt+F10", Width = 145 };
    private readonly TextBox _scrollUpShortcut = new() { Text = "Ctrl+Alt+F11", Width = 145 };
    private readonly TextBlock _scrollModeStatus = new() { Text = "Scroll test mode: Disabled" };
    private readonly Button _scrollModeButton = new() { Content = "Enable scroll test mode", MinWidth = 190 };
    private readonly Button _captureTargetButton = new() { Content = "Capture Target Window", MinWidth = 190 };
    private readonly Button _clearTargetButton = new() { Content = "Clear Target", MinWidth = 110, IsEnabled = false };
    private readonly TextBlock _targetStatus = new() { Text = "Target: None captured", TextWrapping = TextWrapping.Wrap };
    private readonly Button _themeToggleButton = new() { Content = "Switch to dark theme", MinWidth = 160 };
    private bool _darkTheme;

    public MainWindow()
    {
        _controlEngine = new ControlEngine(_windowsAdapter);
        _scrollTestMode = new ScrollTestModeController(new WindowsGlobalShortcutRegistrar(), _controlEngine, _windowsAdapter);
        _handTracking = new HandTrackingSession(new WindowsHandTrackingProvider());
        _scrollTestMode.ActionCompleted += OnScrollActionCompleted;
        _handTracking.StatusChanged += OnTrackingStatusChanged;
        _landmarkCollector.StatusChanged += OnCollectionStatusChanged;
        _previewTimer.Tick += OnPreviewTimerTick;
        _resourceTimer.Tick += OnResourceTimerTick;
        Title = "HandsFree";
        Width = 720;
        Height = 720;
        MinWidth = 520;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var controls = new StackPanel
        {
            Margin = new Avalonia.Thickness(32),
            Spacing = 16,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { new TextBlock { Text = "HandsFree", FontSize = 32, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }, _themeToggleButton } },
                new TextBlock { Text = "A local-first foundation for hands-free interaction.", FontSize = 16 },
                HandTrackingControls(),
                ResourceMonitorControls(),
                new TextBlock { Text = "Manual Windows control test", FontSize = 20, FontWeight = FontWeight.Medium },
                new TextBlock { Text = "Each button performs one explicit action. Nothing runs automatically.", TextWrapping = TextWrapping.Wrap },
                LaunchRow(),
                CoordinateRow(),
                ActionButtons(),
                ScrollTestModeControls(),
                new Border
                {
                    Padding = new Avalonia.Thickness(16),
                    CornerRadius = new Avalonia.CornerRadius(8),
                    Classes = { "status-card" },
                    Child = _status
                }
            }
        };

        _status.Text = OperatingSystem.IsWindows()
            ? "Ready. Cursor and click buttons affect the Windows desktop."
            : "Ready. Windows control actions are unavailable on this platform.";
        _scrollModeButton.IsEnabled = OperatingSystem.IsWindows();
        _captureTargetButton.IsEnabled = false;
        if (!OperatingSystem.IsWindows()) _scrollModeStatus.Text = "Scroll test mode: Unavailable on this platform";
        _scrollModeButton.Click += (_, _) => ToggleScrollTestMode();
        _captureTargetButton.Click += (_, _) => ArmTargetCapture();
        _clearTargetButton.Click += (_, _) => ClearTarget();
        _themeToggleButton.Click += (_, _) => ToggleTheme();
        _startTrackingButton.IsEnabled = OperatingSystem.IsWindows();
        if (!OperatingSystem.IsWindows()) _trackingStatus.Text = "Local webcam tracking is available on Windows only.";
        _startTrackingButton.Click += async (_, _) => await StartTrackingAsync();
        _stopTrackingButton.Click += async (_, _) => await StopTrackingAsync();
        _resetHandSelectionButton.Click += (_, _) => ResetHandSelection();
        _resetDiagnosticsButton.Click += (_, _) => _handTracking.ResetDiagnostics();
        _startCollectionButton.Click += async (_, _) => await StartCollectionAsync();
        _stopCollectionButton.Click += async (_, _) => await StopCollectionAsync();
        _pointerToggleButton.Click += (_, _) => TogglePointerSimulation();
        _windowLeft.ValueChanged += (_, _) => UpdateMovementWindow();
        _windowTop.ValueChanged += (_, _) => UpdateMovementWindow();
        _windowWidth.ValueChanged += (_, _) => UpdateMovementWindow();
        _windowHeight.ValueChanged += (_, _) => UpdateMovementWindow();
        _handPreview.SetMovementWindow(_pointerWindow);
        Closed += (_, _) =>
        {
            _previewTimer.Stop();
            _resourceTimer.Stop();
            _resourceTimer.Tick -= OnResourceTimerTick;
            _processResourceSampler.Dispose();
            _scrollTestMode.Dispose();
            _handPreview.Dispose();
            _landmarkCollector.StatusChanged -= OnCollectionStatusChanged;
            _landmarkCollector.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _handTracking.DisposeAsync().AsTask().GetAwaiter().GetResult();
        };
        Content = new ScrollViewer { Content = controls };
        UpdateResourceMetrics();
        _resourceTimer.Start();
    }

    private Control HandTrackingControls() => new Border
    {
        Padding = new Avalonia.Thickness(16),
        CornerRadius = new Avalonia.CornerRadius(8),
        Classes = { "section-card" },
        Child = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "Local hand tracking", FontSize = 20, FontWeight = FontWeight.Medium },
                new TextBlock { Text = "Camera access starts only when requested. One hand and its 21 landmarks are shown locally; tracking never controls the desktop.", TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = "Preferred hand: Right hand only", FontWeight = FontWeight.Medium },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _startTrackingButton, _stopTrackingButton, _resetHandSelectionButton } },
                new TextBlock { Text = "Local landmark data collection", FontSize = 16, FontWeight = FontWeight.Medium },
                new TextBlock { Text = "Collect labeled 21-point samples to a local JSONL file. Collection starts only on request and never triggers computer-control actions.", TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _gestureLabel, _startCollectionButton, _stopCollectionButton } },
                _collectionStatus,
                _trackingStatus,
                _handPreview,
                new TextBlock { Text = "Index-finger pointer prototype (simulation only)", FontSize = 16, FontWeight = FontWeight.Medium },
                new TextBlock { Text = "Extend the index finger while curling the other fingers. Only fingertip 8 is used. Adjust the yellow movement window; the simulated desktop never sends OS input.", TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = "Window left", VerticalAlignment = VerticalAlignment.Center, Width = 90 }, _windowLeft } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = "Window top", VerticalAlignment = VerticalAlignment.Center, Width = 90 }, _windowTop } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = "Window width", VerticalAlignment = VerticalAlignment.Center, Width = 90 }, _windowWidth } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = "Window height", VerticalAlignment = VerticalAlignment.Center, Width = 90 }, _windowHeight } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _pointerToggleButton, _pointerStatus } },
                _simulatedDesktop
            }
        }
    };

    private Control ResourceMonitorControls() => new Border
    {
        Padding = new Avalonia.Thickness(14),
        CornerRadius = new Avalonia.CornerRadius(8),
        Classes = { "section-card" },
        Child = new StackPanel
        {
            Spacing = 5,
            Children =
            {
                new TextBlock { Text = "Resource monitor", FontWeight = FontWeight.Medium },
                _processCpuMetric,
                _memoryMetric,
                _inferenceMetric,
                _trackingRateMetric,
                _droppedFramesMetric,
                new TextBlock { Text = "Tracking diagnostics (session totals; reset before a test)", FontWeight = FontWeight.Medium, Margin = new Avalonia.Thickness(0, 6, 0, 0) },
                _trackingDiagnostics,
                _resetDiagnosticsButton
            }
        }
    };

    private void OnResourceTimerTick(object? sender, EventArgs args) => UpdateResourceMetrics();

    private void UpdateResourceMetrics()
    {
        UpdateTrackingDiagnostics();
        try
        {
            var process = _processResourceSampler.Sample();
            _processCpuMetric.Text = process.ProcessCpuPercent is { } cpu
                ? $"Process CPU (100% = one logical core): {cpu:F1}%"
                : "Process CPU (100% = one logical core): sampling baseline…";
            _memoryMetric.Text = $"Memory: working set {ToMiB(process.WorkingSetBytes):F1} MiB • managed heap {ToMiB(process.ManagedHeapBytes):F1} MiB";
        }
        catch (Exception exception)
        {
            _processCpuMetric.Text = "Process CPU: unavailable";
            _memoryMetric.Text = $"Memory: unavailable ({exception.Message})";
        }

        if (_handTracking.Status.State != HandTrackingState.Running)
        {
            _inferenceMetric.Text = "Inference latency: idle";
            _trackingRateMetric.Text = "Tracking FPS: idle";
            _droppedFramesMetric.Text = "Dropped frames: idle";
            return;
        }

        var metrics = _handTracking.Metrics;
        var collection = _landmarkCollector.Status;
        if (collection.State == HandLandmarkCollectionState.Recording)
            _collectionStatus.Text = $"{collection.Message} Samples saved: {collection.SamplesWritten:N0} • skipped: {collection.SamplesDropped:N0}";
        _inferenceMetric.Text = metrics.LastOnnxLatencyMilliseconds is { } last
            && metrics.AverageOnnxLatencyMilliseconds is { } average
            && metrics.P95OnnxLatencyMilliseconds is { } p95
            ? $"ONNX inference: last {last:F1} ms • avg {average:F1} ms • p95 {p95:F1} ms"
            : "ONNX inference: warming up…";
        _trackingRateMetric.Text = $"Tracking FPS: {metrics.FramesPerSecond:F1} • processed {metrics.ProcessedFrames:N0}";
        _droppedFramesMetric.Text = $"Dropped captured frames: {metrics.DroppedFrames:N0}";
    }

    private void UpdateTrackingDiagnostics()
    {
        var diagnostics = _handTracking.Diagnostics;
        var selected = diagnostics.SelectedHandConfidence is { } confidence
            ? $"selected confidence {confidence:P0}, handedness {diagnostics.SelectedHandedness:P0} (confidence {diagnostics.SelectedHandednessConfidence:P0})"
            : diagnostics.LastRejectedHandednessScore is { } score
                ? $"no selected hand; last rejected handedness score {score:P0} (confidence {diagnostics.LastRejectedHandednessConfidence:P0})"
                : "no selected-hand confidence available";
        _trackingDiagnostics.Text = $"State: {diagnostics.CurrentState} • {diagnostics.CurrentMessage}\n"
            + $"Palm candidates: score-qualified {diagnostics.PalmCandidatesBeforeNms:N0} → after NMS {diagnostics.PalmCandidatesAfterNms:N0} → top-four {diagnostics.PalmCandidatesAfterLimit:N0}\n"
            + $"Crops skipped: {diagnostics.InvalidCropsSkipped:N0} • pose processed: {diagnostics.HandPoseCandidatesProcessed:N0} • rejected by presence (<80%): {diagnostics.RejectedByPresenceThreshold:N0}\n"
            + $"Rejected by handedness: {diagnostics.RejectedByHandedness:N0} • spatial association: {diagnostics.RejectedBySpatialAssociation:N0}\n"
            + $"Frames: selected {diagnostics.FramesWithSelectedHand:N0} • no valid pose candidate {diagnostics.FramesWithNoCandidate:N0} • temporarily lost lock {diagnostics.FramesWithTemporarilyLostHand:N0}\n"
            + selected;
    }

    private static double ToMiB(long bytes) => bytes / (1024d * 1024d);

    private void ToggleTheme()
    {
        _darkTheme = !_darkTheme;
        Application.Current!.RequestedThemeVariant = _darkTheme ? ThemeVariant.Dark : ThemeVariant.Light;
        _themeToggleButton.Content = _darkTheme ? "Switch to light theme" : "Switch to dark theme";
    }

    private async Task StartTrackingAsync()
    {
        _startTrackingButton.IsEnabled = false;
        _trackingStatus.Text = "Starting camera and local inference…";
        await _handTracking.StartAsync();
        var running = _handTracking.Status.State == HandTrackingState.Running;
        _previewTimer.IsEnabled = running;
        _startTrackingButton.IsEnabled = !running && OperatingSystem.IsWindows();
        _stopTrackingButton.IsEnabled = running;
        _resetHandSelectionButton.IsEnabled = running;
        _startCollectionButton.IsEnabled = running;
        _pointerToggleButton.IsEnabled = running;
    }

    private async Task StopTrackingAsync()
    {
        await StopCollectionAsync();
        _previewTimer.Stop();
        _stopTrackingButton.IsEnabled = false;
        _resetHandSelectionButton.IsEnabled = false;
        await _handTracking.StopAsync();
        _handPreview.Clear();
        _pointerSimulationEnabled = false;
        _pointerToggleButton.Content = "Enable pointer simulation";
        _pointerMapper.Reset();
        _simulatedDesktop.UpdateState(new IndexFingerPointerState(false, false, null, IndexFingerPoseFailure.NoHand, "Pointer simulation is disabled."));
        _pointerStatus.Text = "Pointer simulation is disabled.";
        _lastPreviewTimestamp = default;
        _startTrackingButton.IsEnabled = OperatingSystem.IsWindows();
        _startCollectionButton.IsEnabled = false;
    }

    private void ResetHandSelection()
    {
        _handTracking.ResetHandSelection();
        _lastPreviewTimestamp = default;
        _handPreview.Clear();
        _trackingStatus.Text = "Hand lock reset. Waiting for a visible hand to select.";
    }

    private void OnTrackingStatusChanged(HandTrackingStatus status) => Dispatcher.UIThread.Post(() =>
    {
        _trackingStatus.Text = status.Message;
        if (status.State is HandTrackingState.Faulted or HandTrackingState.Stopped or HandTrackingState.Disposed)
        {
            _previewTimer.Stop();
            _startTrackingButton.IsEnabled = status.State != HandTrackingState.Disposed && OperatingSystem.IsWindows();
            _stopTrackingButton.IsEnabled = false;
            _resetHandSelectionButton.IsEnabled = false;
            _startCollectionButton.IsEnabled = false;
            _pointerToggleButton.IsEnabled = false;
            _pointerSimulationEnabled = false;
            _pointerToggleButton.Content = "Enable pointer simulation";
            _pointerMapper.Reset();
            _simulatedDesktop.UpdateState(new IndexFingerPointerState(false, false, null, IndexFingerPoseFailure.NoHand, "Tracking stopped; pointer simulation paused."));
            _pointerStatus.Text = "Tracking stopped; pointer simulation paused.";
            if (_landmarkCollector.Status.State == HandLandmarkCollectionState.Recording)
                _ = StopCollectionAsync();
        }
        else if (status.State == HandTrackingState.Running)
        {
            _startTrackingButton.IsEnabled = false;
            _stopTrackingButton.IsEnabled = true;
            _resetHandSelectionButton.IsEnabled = true;
            _startCollectionButton.IsEnabled = true;
            _pointerToggleButton.IsEnabled = true;
            _previewTimer.Start();
        }
    });

    private void OnPreviewTimerTick(object? sender, EventArgs args)
    {
        var frame = _handTracking.LatestFrame;
        if (frame is null || frame.CapturedAt == _lastPreviewTimestamp) return;
        _lastPreviewTimestamp = frame.CapturedAt;
        _handPreview.UpdateFrame(frame);
        if (_pointerSimulationEnabled)
        {
            var pointerState = _pointerMapper.Update(frame.Result, frame.IdentityState == HandTrackingIdentityState.Tracking,
                _pointerWindow, previewMirrored: true, desktopWidth: 1280, desktopHeight: 720);
            _simulatedDesktop.UpdateState(pointerState);
            _pointerStatus.Text = pointerState.Message;
        }
        if (frame.IdentityState == HandTrackingIdentityState.Tracking && frame.Result.IsDetected)
            _landmarkCollector.TryRecord(frame, _gestureLabel.Text ?? string.Empty);
        else
            _landmarkCollector.NotifyTrackingLost(frame.TrackingMessage);
        _trackingStatus.Text = frame.IdentityState switch
        {
            HandTrackingIdentityState.Tracking => $"Hand detected: 21 landmarks • {frame.Result.Confidence:P0} confidence • local CPU inference",
            HandTrackingIdentityState.Searching => frame.TrackingMessage,
            _ => frame.TrackingMessage
        };
    }

    private void TogglePointerSimulation()
    {
        _pointerSimulationEnabled = !_pointerSimulationEnabled;
        _pointerToggleButton.Content = _pointerSimulationEnabled ? "Disable pointer simulation" : "Enable pointer simulation";
        if (!_pointerSimulationEnabled)
        {
            _pointerMapper.Reset();
            _pointerStatus.Text = "Pointer simulation is disabled.";
            _simulatedDesktop.UpdateState(new IndexFingerPointerState(false, false, null, IndexFingerPoseFailure.NoHand, _pointerStatus.Text));
        }
        else
        {
            _pointerStatus.Text = "Pointer simulation enabled. Waiting for a recognized index pose.";
        }
    }

    private void UpdateMovementWindow()
    {
        if (_updatingWindowControls) return;
        var width = Math.Clamp(_windowWidth.Value, 0.1, 1);
        var height = Math.Clamp(_windowHeight.Value, 0.1, 1);
        var left = Math.Clamp(_windowLeft.Value, 0, 1 - width);
        var top = Math.Clamp(_windowTop.Value, 0, 1 - height);
        _updatingWindowControls = true;
        _windowLeft.Value = left;
        _windowTop.Value = top;
        _windowWidth.Value = width;
        _windowHeight.Value = height;
        _updatingWindowControls = false;
        _pointerWindow = new PointerMovementWindow(left, top, width, height);
        _handPreview.SetMovementWindow(_pointerWindow);
        _pointerMapper.Reset();
    }

    private async Task StartCollectionAsync()
    {
        if (_handTracking.Status.State != HandTrackingState.Running)
        {
            _collectionStatus.Text = "Start hand tracking before collecting samples.";
            return;
        }
        _startCollectionButton.IsEnabled = false;
        var started = await _landmarkCollector.StartAsync(_gestureLabel.Text ?? string.Empty);
        _startCollectionButton.IsEnabled = !started && _handTracking.Status.State == HandTrackingState.Running;
        _stopCollectionButton.IsEnabled = started;
        UpdateCollectionStatus();
    }

    private async Task StopCollectionAsync()
    {
        _stopCollectionButton.IsEnabled = false;
        await _landmarkCollector.StopAsync();
        _startCollectionButton.IsEnabled = _handTracking.Status.State == HandTrackingState.Running;
        UpdateCollectionStatus();
    }

    private void OnCollectionStatusChanged(HandLandmarkCollectionStatus status) => Dispatcher.UIThread.Post(() =>
    {
        _stopCollectionButton.IsEnabled = status.State == HandLandmarkCollectionState.Recording;
        _startCollectionButton.IsEnabled = status.State != HandLandmarkCollectionState.Recording
            && _handTracking.Status.State == HandTrackingState.Running;
        UpdateCollectionStatus();
    });

    private void UpdateCollectionStatus()
    {
        var status = _landmarkCollector.Status;
        var path = status.FilePath is null ? string.Empty : $" File: {status.FilePath}";
        _collectionStatus.Text = $"{status.Message} Samples saved: {status.SamplesWritten:N0} • skipped: {status.SamplesDropped:N0}.{path}";
    }

    private Control ScrollTestModeControls() => new Border
    {
        Padding = new Avalonia.Thickness(16),
        CornerRadius = new Avalonia.CornerRadius(8),
        Classes = { "section-card" },
        Child = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "Keyboard scroll test mode", FontSize = 18, FontWeight = FontWeight.Medium },
                new TextBlock { Text = "Enable only when ready. Choose Capture Target Window, move the pointer over scrollable content, then press its capture shortcut. Scroll events are posted to the captured child window; HandsFree will not move the pointer or change focus.", TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = "Capture", VerticalAlignment = VerticalAlignment.Center },
                        _captureShortcut,
                        new TextBlock { Text = "Down", VerticalAlignment = VerticalAlignment.Center },
                        _scrollDownShortcut,
                        new TextBlock { Text = "Up", VerticalAlignment = VerticalAlignment.Center },
                        _scrollUpShortcut
                    }
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { _captureTargetButton, _clearTargetButton }
                },
                _targetStatus,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 12,
                    Children = { _scrollModeButton, _scrollModeStatus }
                }
            }
        }
    };

    private void ToggleScrollTestMode()
    {
        if (_scrollTestMode.IsEnabled)
        {
            var disabled = _scrollTestMode.Disable();
            _captureShortcut.IsEnabled = true;
            _scrollDownShortcut.IsEnabled = true;
            _scrollUpShortcut.IsEnabled = true;
            _captureTargetButton.IsEnabled = false;
            _scrollModeButton.Content = "Enable scroll test mode";
            _scrollModeStatus.Text = disabled.Succeeded
                ? "Scroll test mode: Disabled"
                : $"Scroll test mode: Disable failed ({disabled.Message})";
            return;
        }

        if (!ScrollShortcutConfiguration.TryParse(
                _captureShortcut.Text ?? string.Empty,
                _scrollDownShortcut.Text ?? string.Empty,
                _scrollUpShortcut.Text ?? string.Empty,
                out var configuration,
                out var error))
        {
            _scrollModeStatus.Text = $"Scroll test mode: Disabled. {error}";
            return;
        }

        var enabled = _scrollTestMode.Enable(configuration!);
        if (!enabled.Succeeded)
        {
            _scrollModeStatus.Text = $"Scroll test mode: Disabled. {enabled.Message}";
            return;
        }

        _captureShortcut.IsEnabled = false;
        _scrollDownShortcut.IsEnabled = false;
        _scrollUpShortcut.IsEnabled = false;
        _captureTargetButton.IsEnabled = true;
        _scrollModeButton.Content = "Disable scroll test mode";
        _scrollModeStatus.Text = "Scroll test mode: Enabled";
    }

    private void ArmTargetCapture()
    {
        var result = _scrollTestMode.ArmTargetCapture();
        RefreshTargetStatus();
        _scrollModeStatus.Text = result.Succeeded
            ? $"Scroll test mode: Enabled. Move the pointer over the target and press {_captureShortcut.Text}."
            : $"Scroll test mode: Disabled. {result.Message}";
    }

    private void ClearTarget()
    {
        _scrollTestMode.ClearTarget();
        RefreshTargetStatus();
        _scrollModeStatus.Text = "Target cleared. Capture a target window before scrolling.";
    }

    private void OnScrollActionCompleted(ScrollShortcutCommand command, ControlResult result) => Dispatcher.UIThread.Post(() =>
    {
        RefreshTargetStatus();
        if (command == ScrollShortcutCommand.CaptureTarget)
        {
            _scrollModeStatus.Text = result.Succeeded
                ? "Target captured. Scroll events will be posted; visible scrolling is not verified."
                : $"Target capture failed: {result.Message}";
            return;
        }

        var direction = command == ScrollShortcutCommand.ScrollUp ? "Up" : "Down";
        _scrollModeStatus.Text = result.Succeeded
            ? $"Scroll {direction} event posted to captured target; visible scrolling is not verified."
            : $"Scroll {direction} failed: {result.Message}";
    });

    private void RefreshTargetStatus()
    {
        var target = _scrollTestMode.CurrentTarget;
        _targetStatus.Text = target is null
            ? "Target: None captured"
            : $"Target: {target.ProcessName} — {target.Title} (PID {target.ProcessId})";
        _captureTargetButton.Content = target is null ? "Capture Target Window" : "Recapture Target Window";
        _clearTargetButton.IsEnabled = target is not null;
    }

    private Control LaunchRow()
    {
        var button = new Button { Content = "Launch", MinWidth = 92 };
        button.Click += async (_, _) => await ExecuteAsync(new LaunchApplicationAction(_executablePath.Text ?? string.Empty));
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { _executablePath, button }
        };
    }

    private Control CoordinateRow() => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 8,
        Children =
        {
            new TextBlock { Text = "X", VerticalAlignment = VerticalAlignment.Center },
            _xCoordinate,
            new TextBlock { Text = "Y", VerticalAlignment = VerticalAlignment.Center },
            _yCoordinate,
            CreateButton("Move cursor", MoveCursorAsync)
        }
    };

    private Control ActionButtons() => new WrapPanel
    {
        Orientation = Orientation.Horizontal,
        ItemWidth = 112,
        ItemHeight = 42,
        Children =
        {
            CreateButton("Left click", () => ExecuteAsync(new ClickAction(MouseButton.Left))),
            CreateButton("Right click", () => ExecuteAsync(new ClickAction(MouseButton.Right))),
            CreateButton("Scroll up", () => ExecuteAsync(new ScrollAction(120))),
            CreateButton("Scroll down", () => ExecuteAsync(new ScrollAction(-120)))
        }
    };

    private static Button CreateButton(string label, Func<Task> action)
    {
        var button = new Button { Content = label, Margin = new Avalonia.Thickness(0, 0, 8, 8) };
        button.Click += async (_, _) => await action();
        return button;
    }

    private async Task MoveCursorAsync()
    {
        if (!int.TryParse(_xCoordinate.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
            || !int.TryParse(_yCoordinate.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
        {
            _status.Text = "InvalidInput: Enter whole-number cursor coordinates.";
            return;
        }

        await ExecuteAsync(new MoveCursorAction(x, y));
    }

    private async Task ExecuteAsync(ControlAction action)
    {
        var result = await _controlEngine.ExecuteAsync(action);
        _status.Text = action is ScrollAction
            ? result.Succeeded ? "Wheel event posted to captured target; visible scrolling is not verified." : $"{result.ErrorCode}: {result.Message}"
            : result.Succeeded ? "Action completed." : $"{result.ErrorCode}: {result.Message}";
        if (action is ScrollAction) RefreshTargetStatus();
    }
}
