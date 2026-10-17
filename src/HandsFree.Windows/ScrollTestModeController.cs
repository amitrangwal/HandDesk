using HandsFree.Core;

namespace HandsFree.Windows;

public interface IGlobalShortcutRegistrar
{
    IDisposable Register(ScrollShortcutConfiguration configuration, Action<ScrollShortcutCommand> onShortcut);
}

/// <summary>Opt-in, single-event scroll testing driven by registered global shortcuts.</summary>
public sealed class ScrollTestModeController(IGlobalShortcutRegistrar registrar, IControlEngine controlEngine, ITargetWindowService targetWindowService) : IDisposable
{
    private readonly object _gate = new();
    private IDisposable? _registration;
    private int _generation;
    private bool _disposed;
    private bool _captureArmed;

    public bool IsEnabled
    {
        get { lock (_gate) return _registration is not null; }
    }

    public bool IsCaptureArmed
    {
        get { lock (_gate) return _captureArmed; }
    }

    public TargetWindow? CurrentTarget => targetWindowService.CurrentTarget;

    public event Action<ScrollShortcutCommand, ControlResult>? ActionCompleted;

    public ControlResult Enable(ScrollShortcutConfiguration configuration)
    {
        lock (_gate)
        {
            if (_disposed) return ControlResult.Failure(ControlErrorCode.ActionFailed, "Scroll test mode has been closed.");
            if (_registration is not null) return ControlResult.Failure(ControlErrorCode.InvalidInput, "Scroll test mode is already enabled.");
            if (configuration is null || configuration.CaptureTarget == configuration.ScrollDown
                || configuration.CaptureTarget == configuration.ScrollUp || configuration.ScrollDown == configuration.ScrollUp
                || !ScrollShortcutConfiguration.IsValid(configuration.CaptureTarget)
                || !ScrollShortcutConfiguration.IsValid(configuration.ScrollDown)
                || !ScrollShortcutConfiguration.IsValid(configuration.ScrollUp))
            {
                return ControlResult.Failure(ControlErrorCode.InvalidInput, "Provide three different shortcuts, each with a supported key and at least one modifier.");
            }

            var generation = ++_generation;
            try
            {
                _registration = registrar.Register(configuration, direction => OnShortcut(direction, generation));
                return ControlResult.Success();
            }
            catch (Exception exception)
            {
                return ControlResult.Failure(ControlErrorCode.ActionFailed, exception.Message);
            }
        }
    }

    public ControlResult ArmTargetCapture()
    {
        lock (_gate)
        {
            if (_registration is null) return ControlResult.Failure(ControlErrorCode.InvalidInput, "Enable scroll test mode before capturing a target.");
            targetWindowService.ClearTarget();
            _captureArmed = true;
            return ControlResult.Success();
        }
    }

    public ControlResult ClearTarget()
    {
        targetWindowService.ClearTarget();
        lock (_gate) _captureArmed = false;
        return ControlResult.Success();
    }

    public ControlResult Disable()
    {
        IDisposable? registration;
        lock (_gate)
        {
            registration = _registration;
            _registration = null;
            _generation++;
            _captureArmed = false;
        }

        try
        {
            registration?.Dispose();
            return ControlResult.Success();
        }
        catch (Exception exception)
        {
            return ControlResult.Failure(ControlErrorCode.ActionFailed, exception.Message);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Disable();
    }

    private void OnShortcut(ScrollShortcutCommand command, int generation)
    {
        lock (_gate)
        {
            if (_disposed || _registration is null || generation != _generation) return;
            if (command == ScrollShortcutCommand.CaptureTarget)
            {
                if (!_captureArmed) return;
                var result = targetWindowService.CaptureTargetUnderCursor();
                if (result.Succeeded) _captureArmed = false;
                ActionCompleted?.Invoke(command, result);
                return;
            }

            _ = ExecuteOneAsync(command);
        }
    }

    private async Task ExecuteOneAsync(ScrollShortcutCommand command)
    {
        var delta = command == ScrollShortcutCommand.ScrollUp ? 120 : -120;
        var result = await controlEngine.ExecuteAsync(new ScrollAction(delta)).ConfigureAwait(false);
        ActionCompleted?.Invoke(command, result);
    }
}
