using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using HandsFree.Core;

namespace HandsFree.Windows;

/// <summary>Windows-only adapter for explicit, single-shot desktop actions.</summary>
public sealed class WindowsControlAdapter : IPlatformControlAdapter, ITargetWindowService
{
    private const uint InputMouse = 0;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventRightDown = 0x0008;
    private const uint MouseEventRightUp = 0x0010;
    private const uint MouseEventMiddleDown = 0x0020;
    private const uint MouseEventMiddleUp = 0x0040;
    private const uint WmMouseWheel = 0x020A;
    private const uint GaRoot = 2;
    private readonly object _targetGate = new();
    private TargetWindow? _target;

    public TargetWindow? CurrentTarget
    {
        get { lock (_targetGate) return _target; }
    }

    public ControlResult CaptureTargetUnderCursor()
    {
        if (!OperatingSystem.IsWindows())
            return ControlResult.Failure(ControlErrorCode.UnsupportedPlatform, "Target capture is available only on Windows.");

        try
        {
            if (!NativeMethods.GetCursorPos(out var point))
                return ControlResult.Failure(ControlErrorCode.ActionFailed, $"Could not read the cursor position: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");

            var scrollWindow = NativeMethods.WindowFromPoint(point);
            if (scrollWindow == IntPtr.Zero)
                return ControlResult.Failure(ControlErrorCode.InvalidInput, "No window was found under the pointer. Move over the target content and try again.");

            var topLevel = NativeMethods.GetAncestor(scrollWindow, GaRoot);
            if (topLevel == IntPtr.Zero)
                return ControlResult.Failure(ControlErrorCode.InvalidInput, "Could not resolve a top-level window under the pointer.");

            _ = NativeMethods.GetWindowThreadProcessId(topLevel, out var processId);
            if (processId == 0 || processId == unchecked((uint)Environment.ProcessId))
                return ControlResult.Failure(ControlErrorCode.InvalidInput, "Capture a different application, not HandsFree or the desktop.");

            var titleBuffer = new StringBuilder(512);
            _ = NativeMethods.GetWindowText(topLevel, titleBuffer, titleBuffer.Capacity);
            var processName = GetProcessName(processId);
            var title = string.IsNullOrWhiteSpace(titleBuffer.ToString()) ? processName : titleBuffer.ToString();
            var target = new TargetWindow(topLevel, scrollWindow, title, processName, processId, point.X, point.Y);
            lock (_targetGate) _target = target;
            return ControlResult.Success();
        }
        catch (Exception exception)
        {
            return ControlResult.Failure(ControlErrorCode.ActionFailed, $"Target capture failed: {exception.Message}");
        }
    }

    public void ClearTarget()
    {
        lock (_targetGate) _target = null;
    }

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

    private ControlResult Scroll(ScrollAction action)
    {
        lock (_targetGate)
        {
            if (_target is null)
                return ControlResult.Failure(ControlErrorCode.InvalidInput, "Capture a target window before scrolling.");

            if (!IsTargetValid(_target))
            {
                _target = null;
                return ControlResult.Failure(ControlErrorCode.InvalidInput, "The captured target window is no longer valid. Capture it again.");
            }

            var wheelDelta = unchecked((ushort)action.Delta);
            var wParam = new UIntPtr((uint)wheelDelta << 16);
            var packedPoint = unchecked((int)((uint)(ushort)_target.ScreenX | ((uint)(ushort)_target.ScreenY << 16)));
            if (!NativeMethods.PostMessage(_target.ScrollHandle, WmMouseWheel, wParam, new IntPtr(packedPoint)))
            {
                var error = Marshal.GetLastWin32Error();
                if (!IsTargetValid(_target))
                {
                    _target = null;
                    return ControlResult.Failure(ControlErrorCode.InvalidInput, "The captured target window closed. Capture it again.");
                }
                return ControlResult.Failure(ControlErrorCode.ActionFailed, $"Windows could not post the wheel event to the captured window: {new Win32Exception(error).Message}");
            }

            return ControlResult.Success();
        }
    }

    private static bool IsTargetValid(TargetWindow target)
    {
        if (!NativeMethods.IsWindow(target.TopLevelHandle) || !NativeMethods.IsWindow(target.ScrollHandle)
            || NativeMethods.GetAncestor(target.ScrollHandle, GaRoot) != target.TopLevelHandle)
            return false;

        _ = NativeMethods.GetWindowThreadProcessId(target.TopLevelHandle, out var rootProcessId);
        _ = NativeMethods.GetWindowThreadProcessId(target.ScrollHandle, out var childProcessId);
        return rootProcessId == target.ProcessId && childProcessId == target.ProcessId;
    }

    private static string GetProcessName(uint processId)
    {
        try { return Process.GetProcessById(checked((int)processId)).ProcessName; }
        catch { return $"Process {processId}"; }
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

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorPos(out NativePoint point);

        [DllImport("user32.dll")]
        internal static extern IntPtr WindowFromPoint(NativePoint point);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetAncestor(IntPtr window, uint flags);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostMessage(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }
}
