using HandsFree.Core;

namespace HandsFree.Windows;

public sealed record TargetWindow(
    nint TopLevelHandle,
    nint ScrollHandle,
    string Title,
    string ProcessName,
    uint ProcessId,
    int ScreenX,
    int ScreenY);

public interface ITargetWindowService
{
    TargetWindow? CurrentTarget { get; }
    ControlResult CaptureTargetUnderCursor();
    void ClearTarget();
}

public enum ScrollShortcutCommand
{
    CaptureTarget,
    ScrollDown,
    ScrollUp
}
