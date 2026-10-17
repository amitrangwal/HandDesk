using System.ComponentModel;
using System.Runtime.InteropServices;

namespace HandsFree.Windows;

/// <summary>Uses RegisterHotKey on a dedicated message thread; it installs no keyboard hook and does no polling.</summary>
public sealed class WindowsGlobalShortcutRegistrar : IGlobalShortcutRegistrar
{
    public IDisposable Register(ScrollShortcutConfiguration configuration, Action<ScrollShortcutCommand> onShortcut)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(onShortcut);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Global shortcuts are available only on Windows.");
        return new Registration(configuration, onShortcut);
    }

    private sealed class Registration : IDisposable
    {
        private const uint ModNoRepeat = 0x4000;
        private const uint WmHotKey = 0x0312;
        private const uint WmQuit = 0x0012;
        private const uint PmNoRemove = 0x0000;
        private const int CaptureTargetId = 1;
        private const int ScrollDownId = 2;
        private const int ScrollUpId = 3;

        private readonly ScrollShortcutConfiguration _configuration;
        private readonly Action<ScrollShortcutCommand> _onShortcut;
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _started = new(false);
        private Exception? _startupError;
        private uint _threadId;
        private int _disposed;

        public Registration(ScrollShortcutConfiguration configuration, Action<ScrollShortcutCommand> onShortcut)
        {
            _configuration = configuration;
            _onShortcut = onShortcut;
            _thread = new Thread(MessageLoop) { IsBackground = true, Name = "HandsFree global shortcuts" };
            _thread.Start();
            _started.Wait();
            if (_startupError is not null)
            {
                _thread.Join();
                _started.Dispose();
                throw _startupError;
            }
        }

        private void MessageLoop()
        {
            _threadId = NativeMethods.GetCurrentThreadId();
            var registeredUp = false;
            var registeredDown = false;
            var registeredCapture = false;
            try
            {
                _ = NativeMethods.PeekMessage(out _, IntPtr.Zero, 0, 0, PmNoRemove); // Create this thread's queue before signaling readiness.
                registeredCapture = RegisterHotKey(CaptureTargetId, _configuration.CaptureTarget);
                registeredDown = RegisterHotKey(ScrollDownId, _configuration.ScrollDown);
                registeredUp = RegisterHotKey(ScrollUpId, _configuration.ScrollUp);
                _started.Set();

                while (true)
                {
                    var result = NativeMethods.GetMessage(out var message, IntPtr.Zero, 0, 0);
                    if (result == 0) break;
                    if (result == -1) throw new Win32Exception(Marshal.GetLastWin32Error(), "The global shortcut message loop failed.");
                    if (message.Message == WmHotKey)
                    {
                        var command = (int)message.WParam switch
                        {
                            CaptureTargetId => ScrollShortcutCommand.CaptureTarget,
                            ScrollDownId => ScrollShortcutCommand.ScrollDown,
                            ScrollUpId => ScrollShortcutCommand.ScrollUp,
                            _ => (ScrollShortcutCommand?)null
                        };
                        if (command is null) continue;
                        try { _onShortcut(command.Value); }
                        catch { /* An application callback must not terminate the native message loop. */ }
                    }
                }
            }
            catch (Exception exception)
            {
                _startupError = exception;
                _started.Set();
            }
            finally
            {
                if (registeredCapture) NativeMethods.UnregisterHotKey(IntPtr.Zero, CaptureTargetId);
                if (registeredDown) NativeMethods.UnregisterHotKey(IntPtr.Zero, ScrollDownId);
                if (registeredUp) NativeMethods.UnregisterHotKey(IntPtr.Zero, ScrollUpId);
            }
        }

        private static bool RegisterHotKey(int id, GlobalShortcut shortcut)
        {
            var modifiers = (uint)shortcut.Modifiers | ModNoRepeat;
            if (!NativeMethods.RegisterHotKey(IntPtr.Zero, id, modifiers, shortcut.VirtualKey))
            {
                var error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error, "Could not register the scroll shortcut. It may already be in use.");
            }
            return true;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (_thread.IsAlive && !NativeMethods.PostThreadMessage(_threadId, WmQuit, UIntPtr.Zero, IntPtr.Zero))
            {
                var error = Marshal.GetLastWin32Error();
                if (_thread.IsAlive) throw new Win32Exception(error, "Could not stop the global shortcut message loop.");
            }
            _thread.Join();
            _started.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr HWnd;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public NativePoint Point;
        public uint Private;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern int GetMessage(out NativeMessage message, IntPtr hWnd, uint minFilter, uint maxFilter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PeekMessage(out NativeMessage message, IntPtr hWnd, uint minFilter, uint maxFilter, uint removeMessage);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostThreadMessage(uint threadId, uint message, UIntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();
    }
}
