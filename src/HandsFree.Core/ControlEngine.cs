namespace HandsFree.Core;

public sealed class ControlEngine(IPlatformControlAdapter adapter) : IControlEngine
{
    private const int MaximumCoordinate = 32_767;
    private const int WheelDeltaPerNotch = 120;
    private const int MaximumScrollDelta = 1_200;

    public async ValueTask<ControlResult> ExecuteAsync(ControlAction action, CancellationToken cancellationToken = default)
    {
        if (action is null)
        {
            return ControlResult.Failure(ControlErrorCode.InvalidAction, "An action is required.");
        }

        var validationError = Validate(action);
        if (validationError is not null)
        {
            return validationError;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ControlResult.Failure(ControlErrorCode.Cancelled, "The action was cancelled.");
        }

        try
        {
            return await adapter.ExecuteAsync(action, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ControlResult.Failure(ControlErrorCode.Cancelled, "The action was cancelled.");
        }
        catch (Exception)
        {
            return ControlResult.Failure(ControlErrorCode.ActionFailed, "The platform adapter failed to execute the action.");
        }
    }

    private static ControlResult? Validate(ControlAction action) => action switch
    {
        LaunchApplicationAction launch when string.IsNullOrWhiteSpace(launch.ExecutablePath)
            || !Path.IsPathFullyQualified(launch.ExecutablePath)
            => ControlResult.Failure(ControlErrorCode.InvalidInput, "Provide a fully qualified executable path."),
        MoveCursorAction move when move.X is < -MaximumCoordinate or > MaximumCoordinate
            || move.Y is < -MaximumCoordinate or > MaximumCoordinate
            => ControlResult.Failure(ControlErrorCode.InvalidInput, "Cursor coordinates must be between -32767 and 32767."),
        ClickAction click when !Enum.IsDefined(click.Button)
            => ControlResult.Failure(ControlErrorCode.InvalidInput, "The requested mouse button is unsupported."),
        ScrollAction scroll when scroll.Delta == 0
            || scroll.Delta % WheelDeltaPerNotch != 0
            || Math.Abs((long)scroll.Delta) > MaximumScrollDelta
            => ControlResult.Failure(ControlErrorCode.InvalidInput, "Scroll delta must be a non-zero multiple of 120, up to 1200."),
        LaunchApplicationAction or MoveCursorAction or ClickAction or ScrollAction => null,
        _ => ControlResult.Failure(ControlErrorCode.InvalidAction, "The requested action is unsupported.")
    };
}
