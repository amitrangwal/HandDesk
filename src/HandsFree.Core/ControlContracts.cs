namespace HandsFree.Core;

public abstract record ControlAction;

public sealed record LaunchApplicationAction(string ExecutablePath) : ControlAction;

public sealed record MoveCursorAction(int X, int Y) : ControlAction;

public enum MouseButton
{
    Left,
    Right,
    Middle
}

public sealed record ClickAction(MouseButton Button = MouseButton.Left) : ControlAction;

/// <summary>Scroll wheel delta in Windows wheel units (120 units per notch).</summary>
public sealed record ScrollAction(int Delta) : ControlAction;

public enum ControlErrorCode
{
    None,
    InvalidAction,
    InvalidInput,
    UnsupportedPlatform,
    ActionFailed,
    Cancelled
}

public sealed record ControlResult(bool Succeeded, ControlErrorCode ErrorCode = ControlErrorCode.None, string? Message = null)
{
    public static ControlResult Success() => new(true);

    public static ControlResult Failure(ControlErrorCode errorCode, string message) => new(false, errorCode, message);
}

public interface IControlEngine
{
    ValueTask<ControlResult> ExecuteAsync(ControlAction action, CancellationToken cancellationToken = default);
}

/// <summary>Platform boundary used by the central, platform-independent control engine.</summary>
public interface IPlatformControlAdapter
{
    ValueTask<ControlResult> ExecuteAsync(ControlAction action, CancellationToken cancellationToken = default);
}
