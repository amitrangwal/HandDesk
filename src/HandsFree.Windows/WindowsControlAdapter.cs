using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using HandsFree.Core;

namespace HandsFree.Windows;

/// <summary>Windows-only adapter for explicit, single-shot desktop actions.</summary>
public sealed class WindowsControlAdapter : IPlatformControlAdapter
{
    private const uint InputMouse = 0;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventRightDown = 0x0008;
    private const uint MouseEventRightUp = 0x0010;
    private const uint MouseEventMiddleDown = 0x0020;
    private const uint MouseEventMiddleUp = 0x0040;
    private const uint MouseEventWheel = 0x0800;

    public ValueTask<ControlResult> ExecuteAsync(ControlAction action, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ValueTask.FromResult(ControlResult.Failure(
                ControlErrorCode.UnsupportedPlatform, "Computer control is available only on Windows."));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(ControlResult.Failure(ControlErrorCode.Cancelled, "The action was cancelled."));
        }

        try
        {
            return ValueTask.FromResult(action switch
            {
                LaunchApplicationAction launch => Launch(launch),
                MoveCursorAction move => MoveCursor(move),
                ClickAction click => Click(click),
                ScrollAction scroll => Scroll(scroll),
                _ => ControlResult.Failure(ControlErrorCode.InvalidAction, "The requested action is unsupported.")
            });
        }
        catch (Win32Exception)
        {
            return ValueTask.FromResult(ControlResult.Failure(ControlErrorCode.ActionFailed, "Windows rejected the requested action."));
        }
        catch (Exception)
        {
            return ValueTask.FromResult(ControlResult.Failure(ControlErrorCode.ActionFailed, "The requested action could not be completed."));
        }
    }

    private static ControlResult Launch(LaunchApplicationAction action)
    {
        var startInfo = new ProcessStartInfo(action.ExecutablePath)
        {
            UseShellExecute = false
        };

        return Process.Start(startInfo) is null
            ? ControlResult.Failure(ControlErrorCode.ActionFailed, "Windows did not start the application.")
            : ControlResult.Success();
    }

    private static ControlResult MoveCursor(MoveCursorAction action) =>
        NativeMethods.SetCursorPos(action.X, action.Y)
            ? ControlResult.Success()
            : ControlResult.Failure(ControlErrorCode.ActionFailed, "Windows could not move the cursor to that position.");

    private static ControlResult Click(ClickAction action)
    {
        var flags = action.Button switch
        {
            MouseButton.Left => (MouseEventLeftDown, MouseEventLeftUp),
            MouseButton.Right => (MouseEventRightDown, MouseEventRightUp),
            MouseButton.Middle => (MouseEventMiddleDown, MouseEventMiddleUp),
            _ => throw new ArgumentOutOfRangeException(nameof(action.Button))
        };

        return SendMouseEvents(flags.Item1, flags.Item2)
            ? ControlResult.Success()
            : ControlResult.Failure(ControlErrorCode.ActionFailed, "Windows could not send the click.");
    }

    private static ControlResult Scroll(ScrollAction action) =>
        SendMouseEvent(MouseEventWheel, unchecked((uint)action.Delta))
            ? ControlResult.Success()
            : ControlResult.Failure(ControlErrorCode.ActionFailed, "Windows could not send the scroll event.");

    private static bool SendMouseEvent(uint flags, uint data = 0)
    {
        var input = CreateMouseInput(flags, data);
        return NativeMethods.SendInput(1, [input], Marshal.SizeOf<Input>()) == 1;
    }

    private static bool SendMouseEvents(uint firstFlags, uint secondFlags)
    {
        Input[] inputs = [CreateMouseInput(firstFlags), CreateMouseInput(secondFlags)];
        return NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == inputs.Length;
    }

    private static Input CreateMouseInput(uint flags, uint data = 0) => new()
    {
        Type = InputMouse,
        Data = new InputUnion
        {
            Mouse = new MouseInput { MouseData = data, Flags = flags }
        }
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint SendInput(uint inputCount, [In] Input[] inputs, int inputSize);
    }
}
