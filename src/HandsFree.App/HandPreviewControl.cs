using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using HandsFree.Core;

namespace HandsFree.App;

/// <summary>Draws a mirrored camera preview and 21-point overlay without connecting tracking to controls.</summary>
internal sealed class HandPreviewControl : Control, IDisposable
{
    private static readonly (int A, int B)[] Connections =
    [
        (0, 1), (1, 2), (2, 3), (3, 4), (0, 5), (5, 6), (6, 7), (7, 8),
        (5, 9), (9, 10), (10, 11), (11, 12), (9, 13), (13, 14), (14, 15),
        (15, 16), (13, 17), (17, 18), (18, 19), (19, 20), (0, 17)
    ];

    private WriteableBitmap? _bitmap;
    private byte[] _convertedBuffer = [];
    private IReadOnlyList<HandLandmark> _landmarks = Array.Empty<HandLandmark>();

    public HandPreviewControl()
    {
        ClipToBounds = true;
    }

    public void UpdateFrame(HandTrackingFrame frame)
    {
        if (_bitmap is null || _bitmap.PixelSize.Width != frame.Width || _bitmap.PixelSize.Height != frame.Height)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(new PixelSize(frame.Width, frame.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        }

        using (var buffer = _bitmap.Lock())
        {
            var source = frame.BgrPixels.Span;
            if (_convertedBuffer.Length != checked(frame.Width * frame.Height * 4))
                _convertedBuffer = new byte[frame.Width * frame.Height * 4];
            for (var sourceIndex = 0; sourceIndex < source.Length; sourceIndex += 3)
            {
                var sourcePixel = sourceIndex / 3;
                var x = sourcePixel % frame.Width;
                var y = sourcePixel / frame.Width;
                var mirroredPixel = y * frame.Width + (frame.Width - 1 - x);
                var outputIndex = mirroredPixel * 4;
                _convertedBuffer[outputIndex] = source[sourceIndex + 2];
                _convertedBuffer[outputIndex + 1] = source[sourceIndex + 1];
                _convertedBuffer[outputIndex + 2] = source[sourceIndex];
                _convertedBuffer[outputIndex + 3] = 255;
            }

            var rowBytes = frame.Width * 4;
            if (buffer.RowBytes == rowBytes) Marshal.Copy(_convertedBuffer, 0, buffer.Address, _convertedBuffer.Length);
            else
                for (var row = 0; row < frame.Height; row++)
                    Marshal.Copy(_convertedBuffer, row * rowBytes, buffer.Address + row * buffer.RowBytes, rowBytes);
        }

        _landmarks = frame.Result.IsDetected ? frame.Result.Landmarks : Array.Empty<HandLandmark>();
        InvalidateVisual();
    }

    public void Clear()
    {
        _bitmap?.Dispose();
        _bitmap = null;
        _convertedBuffer = [];
        _landmarks = Array.Empty<HandLandmark>();
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var previewBrush = Avalonia.Application.Current?.TryGetResource("PreviewBackgroundBrush", ActualThemeVariant, out var resource) == true
            ? resource as IBrush ?? Brushes.Black
            : Brushes.Black;
        context.FillRectangle(previewBrush, new Rect(Bounds.Size));
        if (_bitmap is null) return;

        var imageWidth = _bitmap.PixelSize.Width;
        var imageHeight = _bitmap.PixelSize.Height;
        var scale = Math.Min(Bounds.Width / imageWidth, Bounds.Height / imageHeight);
        var width = imageWidth * scale;
        var height = imageHeight * scale;
        var left = (Bounds.Width - width) / 2;
        var top = (Bounds.Height - height) / 2;
        var imageRect = new Rect(left, top, width, height);
        context.DrawImage(_bitmap, new Rect(0, 0, imageWidth, imageHeight), imageRect);
        if (_landmarks.Count != 21) return;

        Point At(int index) => new(left + (1 - _landmarks[index].X) * width, top + _landmarks[index].Y * height);
        var bonePen = new Pen(Brushes.Lime, 2);
        foreach (var (a, b) in Connections) context.DrawLine(bonePen, At(a), At(b));
        foreach (var landmark in _landmarks)
        {
            var point = new Point(left + (1 - landmark.X) * width, top + landmark.Y * height);
            context.DrawEllipse(Brushes.Yellow, new Pen(Brushes.Black, 1), point, 3.5, 3.5);
        }
    }

    public void Dispose()
    {
        _bitmap?.Dispose();
        _bitmap = null;
        _convertedBuffer = [];
    }
}
