using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using HandsFree.Core;
using HandsFree.Windows;

namespace HandsFree.App;

public sealed class MainWindow : Window
{
    private readonly WindowsControlAdapter _windowsAdapter = new();
    private readonly IControlEngine _controlEngine;
    private readonly ScrollTestModeController _scrollTestMode;
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

    public MainWindow()
    {
        _controlEngine = new ControlEngine(_windowsAdapter);
        _scrollTestMode = new ScrollTestModeController(new WindowsGlobalShortcutRegistrar(), _controlEngine, _windowsAdapter);
        _scrollTestMode.ActionCompleted += OnScrollActionCompleted;
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
                new TextBlock { Text = "HandsFree", FontSize = 32, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = "A local-first foundation for hands-free interaction.", FontSize = 16 },
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
                    Background = new SolidColorBrush(Color.Parse("#F1F3F5")),
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
        Closed += (_, _) => _scrollTestMode.Dispose();
        Content = new ScrollViewer { Content = controls };
    }

    private Control ScrollTestModeControls() => new Border
    {
        Padding = new Avalonia.Thickness(16),
        CornerRadius = new Avalonia.CornerRadius(8),
        Background = new SolidColorBrush(Color.Parse("#F1F3F5")),
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
