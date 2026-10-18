using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using HandsFree.Core;

namespace HandsFree.App;

/// <summary>Visual-only desktop surface for the index-finger pointer prototype.</summary>
internal sealed class SimulatedDesktopControl : Control
{
    private IndexFingerPointerState _state = new(false, false, null, IndexFingerPoseFailure.NoHand, "Pointer simulation is idle.");

    public SimulatedDesktopControl()
    {
        ClipToBounds = true;
    }

    public void UpdateState(IndexFingerPointerState state)
    {
        _state = state;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var background = GetBrush("PreviewBackgroundBrush", "#FF202124");
        var foreground = GetBrush("AppForegroundBrush", "#FFF1F0F7");
        var accent = GetBrush("SelectionBrush", "#FF7898EA");
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(background, bounds);
        const double desktopAspect = 16d / 9d;
        var scale = Math.Min(Bounds.Width / 1280d, Bounds.Height / 720d);
        var screenWidth = 1280 * scale;
        var screenHeight = 720 * scale;
        var screen = new Rect((Bounds.Width - screenWidth) / 2, (Bounds.Height - screenHeight) / 2, screenWidth, screenHeight);
        if (screen.Width <= 0 || screen.Height <= 0 || Math.Abs(screen.Width / screen.Height - desktopAspect) > 0.01) return;
        context.FillRectangle(background, screen);
        context.DrawRectangle(null, new Pen(foreground, 1.5), screen.Deflate(1));

        var topBarHeight = Math.Min(22 * scale, screen.Height * 0.1);
        context.FillRectangle(accent, new Rect(screen.X + 1, screen.Y + 1, Math.Max(0, screen.Width - 2), topBarHeight));
        context.DrawText(new FormattedText("SIMULATED DESKTOP  •  NO OS INPUT", System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, Typeface.Default, Math.Max(8, 11 * scale), foreground), new Point(screen.X + 10 * scale, screen.Y + 5 * scale));

        if (_state.Pointer is not { } pointer) return;
        var x = screen.X + pointer.X / 1280 * screen.Width;
        var y = screen.Y + pointer.Y / 720 * screen.Height;
        context.DrawEllipse(Brushes.Transparent, new Pen(Brushes.DeepSkyBlue, 3 * scale), new Point(x, y), 9 * scale, 9 * scale);
        context.DrawEllipse(Brushes.DeepSkyBlue, null, new Point(x, y), 3 * scale, 3 * scale);
    }

    private IBrush GetBrush(string key, string fallback) =>
        Avalonia.Application.Current?.TryGetResource(key, ActualThemeVariant, out var resource) == true && resource is IBrush brush
            ? brush
            : new SolidColorBrush(Color.Parse(fallback));
}
