using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using HandsFree.Core;
using HandsFree.Windows;

namespace HandsFree.App;

public sealed class MainWindow : Window
{
    private readonly IControlEngine _controlEngine = new ControlEngine(new WindowsControlAdapter());
    private readonly TextBox _executablePath = new() { PlaceholderText = "Full path to an .exe", Width = 360 };
    private readonly TextBox _xCoordinate = new() { Text = "640", Width = 90 };
    private readonly TextBox _yCoordinate = new() { Text = "360", Width = 90 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };

    public MainWindow()
    {
        Title = "HandsFree";
        Width = 720;
        Height = 560;
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
        Content = new ScrollViewer { Content = controls };
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
        _status.Text = result.Succeeded ? "Action completed." : $"{result.ErrorCode}: {result.Message}";
    }
}
