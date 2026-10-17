using HandsFree.Core;
using HandsFree.Windows;

var tests = new (string Name, Func<Task> Run)[]
{
    ("rejects a relative launch path", () => Invalid(new LaunchApplicationAction("app.exe"))),
    ("rejects out-of-range cursor coordinates", () => Invalid(new MoveCursorAction(32_768, 0))),
    ("rejects unsupported mouse buttons", () => Invalid(new ClickAction((MouseButton)99))),
    ("rejects invalid scroll increments", () => Invalid(new ScrollAction(30))),
    ("rejects excessive scroll deltas", () => Invalid(new ScrollAction(1_320))),
    ("routes a valid action to the adapter", RoutesValidAction),
    ("returns a clear result for unsupported actions", RejectsUnknownAction),
    ("maps adapter exceptions to action failures", MapsAdapterException),
    ("parses capture and scroll shortcut mapping", ParsesShortcutConfiguration),
    ("rejects invalid shortcut configuration", RejectsInvalidShortcutConfiguration),
    ("captures, clears, and scrolls a target through fake services", ScrollModeLifecycle),
    ("leaves scroll mode disabled when registration fails", ScrollModeRegistrationFailure)
};

var failures = 0;
foreach (var (name, run) in tests)
{
    try
    {
        await run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.WriteLine($"FAIL {name}: {exception.Message}");
    }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} checks passed.");
return failures == 0 ? 0 : 1;

static async Task Invalid(ControlAction action)
{
    var adapter = new FakeAdapter();
    var result = await new ControlEngine(adapter).ExecuteAsync(action);
    Assert(!result.Succeeded && result.ErrorCode == ControlErrorCode.InvalidInput, "Expected InvalidInput.");
    Assert(adapter.CallCount == 0, "Invalid actions must not reach the platform adapter.");
}

static async Task RoutesValidAction()
{
    var expected = ControlResult.Success();
    var adapter = new FakeAdapter { Result = expected };
    var action = new MoveCursorAction(-150, 240);
    var result = await new ControlEngine(adapter).ExecuteAsync(action);
    Assert(ReferenceEquals(result, expected), "The adapter result should be returned unchanged.");
    Assert(adapter.CallCount == 1 && ReferenceEquals(adapter.LastAction, action), "The action should be sent once to the adapter.");
}

static async Task RejectsUnknownAction()
{
    var adapter = new FakeAdapter();
    var result = await new ControlEngine(adapter).ExecuteAsync(new UnknownAction());
    Assert(!result.Succeeded && result.ErrorCode == ControlErrorCode.InvalidAction, "Expected InvalidAction.");
    Assert(adapter.CallCount == 0, "Unknown actions must not reach the adapter.");
}

static async Task MapsAdapterException()
{
    var adapter = new FakeAdapter { Exception = new InvalidOperationException("private implementation detail") };
    var result = await new ControlEngine(adapter).ExecuteAsync(new ClickAction());
    Assert(!result.Succeeded && result.ErrorCode == ControlErrorCode.ActionFailed, "Expected ActionFailed.");
    Assert(result.Message is not null && !result.Message.Contains("private implementation detail", StringComparison.Ordinal), "Internal exception details should not leak.");
}

static Task ParsesShortcutConfiguration()
{
    Assert(ScrollShortcutConfiguration.TryParse("Ctrl+Alt+F9", "Ctrl+Alt+F10", "Ctrl+Alt+F11", out var configuration, out _), "Expected valid shortcut configuration.");
    Assert(configuration!.CaptureTarget == new GlobalShortcut(ShortcutModifiers.Control | ShortcutModifiers.Alt, 0x78), "Capture shortcut was parsed incorrectly.");
    Assert(configuration.ScrollDown == new GlobalShortcut(ShortcutModifiers.Control | ShortcutModifiers.Alt, 0x79), "Scroll Down key was parsed incorrectly.");
    Assert(configuration.ScrollUp == new GlobalShortcut(ShortcutModifiers.Control | ShortcutModifiers.Alt, 0x7A), "Scroll Up key was parsed incorrectly.");
    return Task.CompletedTask;
}

static Task RejectsInvalidShortcutConfiguration()
{
    Assert(!ScrollShortcutConfiguration.TryParse("F9", "Ctrl+F10", "Ctrl+F11", out _, out _), "A shortcut without modifiers must be rejected.");
    Assert(!ScrollShortcutConfiguration.TryParse("Ctrl+Alt+F12", "Ctrl+Alt+F10", "Ctrl+Alt+F11", out _, out _), "Reserved F12 must be rejected.");
    Assert(!ScrollShortcutConfiguration.TryParse("Ctrl+F9", "Control+F9", "Ctrl+F11", out _, out _), "Duplicate shortcuts must be rejected.");
    Assert(!ScrollShortcutConfiguration.TryParse("Ctrl++F9", "Ctrl+F10", "Ctrl+F11", out _, out _), "Empty shortcut components must be rejected.");
    return Task.CompletedTask;
}

static Task ScrollModeLifecycle()
{
    var registrar = new FakeShortcutRegistrar();
    var adapter = new FakeAdapter();
    var engine = new ControlEngine(adapter);
    using var mode = new ScrollTestModeController(registrar, engine, adapter);
    var configuration = ScrollShortcutConfiguration.Default;

    Assert(!mode.IsEnabled, "Mode must be disabled initially.");
    Assert(!mode.ArmTargetCapture().Succeeded, "Capture must require enabled mode.");
    Assert(mode.Enable(configuration).Succeeded && mode.IsEnabled, "Mode should enable successfully.");
    Assert(!mode.Enable(configuration).Succeeded && registrar.RegisterCount == 1, "Enabling twice must not register duplicates.");
    Assert(mode.ArmTargetCapture().Succeeded && mode.IsCaptureArmed, "Capture button should arm one target capture.");
    registrar.Fire(ScrollShortcutCommand.CaptureTarget);
    Assert(mode.CurrentTarget is not null && !mode.IsCaptureArmed, "Capture shortcut should record a target and disarm capture.");
    registrar.Fire(ScrollShortcutCommand.ScrollUp);
    registrar.Fire(ScrollShortcutCommand.ScrollDown);
    Assert(adapter.Actions.Count == 2, "Each scroll shortcut should submit one action.");
    Assert(adapter.Actions[0] is ScrollAction { Delta: 120 }, "Up shortcut should submit one bounded upward wheel event.");
    Assert(adapter.Actions[1] is ScrollAction { Delta: -120 }, "Down shortcut should submit one bounded downward wheel event.");

    Assert(mode.Disable().Succeeded && !mode.IsEnabled, "Mode should disable successfully.");
    Assert(registrar.DisposeCount == 1, "Disabling must dispose the global registrations.");
    registrar.Fire(ScrollShortcutCommand.ScrollUp);
    Assert(adapter.Actions.Count == 2, "A callback after disabling must not submit input.");
    Assert(mode.Enable(configuration).Succeeded && registrar.RegisterCount == 2, "Mode should be re-enableable after unregistering.");
    registrar.FireAt(0, ScrollShortcutCommand.ScrollDown);
    Assert(adapter.Actions.Count == 2, "A stale callback from a previous registration must be ignored.");
    registrar.FireAt(1, ScrollShortcutCommand.ScrollUp);
    Assert(adapter.Actions.Count == 3 && adapter.Actions[2] is ScrollAction { Delta: 120 }, "The current registration should submit exactly one action.");
    mode.ClearTarget();
    Assert(mode.CurrentTarget is null, "Clear Target should clear captured state.");
    ControlResult? scrollResult = null;
    mode.ActionCompleted += (command, result) => { if (command == ScrollShortcutCommand.ScrollDown) scrollResult = result; };
    registrar.FireAt(1, ScrollShortcutCommand.ScrollDown);
    Assert(adapter.Actions.Count == 4 && scrollResult?.ErrorCode == ControlErrorCode.InvalidInput, "Scrolling without a target must fail safely.");
    Assert(mode.ArmTargetCapture().Succeeded && mode.CurrentTarget is null, "Recapture should clear the previous target while awaiting a new capture.");
    registrar.FireAt(1, ScrollShortcutCommand.CaptureTarget);
    Assert(mode.CurrentTarget is not null, "Recapture should restore the target state.");
    adapter.InvalidateTarget = true;
    registrar.FireAt(1, ScrollShortcutCommand.ScrollDown);
    Assert(mode.CurrentTarget is null && scrollResult?.Message?.Contains("Capture it again", StringComparison.Ordinal) == true, "An invalid target must be reported, cleared, and require recapture.");
    Assert(mode.Disable().Succeeded && registrar.DisposeCount == 2, "The replacement registration should also be disposed.");
    return Task.CompletedTask;
}

static Task ScrollModeRegistrationFailure()
{
    var registrar = new FakeShortcutRegistrar { RegistrationException = new InvalidOperationException("Shortcut is already in use.") };
    var adapter = new FakeAdapter();
    using var mode = new ScrollTestModeController(registrar, new ControlEngine(adapter), adapter);
    var result = mode.Enable(ScrollShortcutConfiguration.Default);
    Assert(!result.Succeeded && !mode.IsEnabled, "Registration failure must leave the mode disabled.");
    return Task.CompletedTask;
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed record UnknownAction : ControlAction;

sealed class FakeAdapter : IPlatformControlAdapter, ITargetWindowService
{
    public int CallCount { get; private set; }
    public ControlAction? LastAction { get; private set; }
    public ControlResult Result { get; init; } = ControlResult.Success();
    public Exception? Exception { get; init; }
    public TargetWindow? CurrentTarget { get; private set; }
    public bool InvalidateTarget { get; set; }
    public List<ControlAction> Actions { get; } = [];

    public ControlResult CaptureTargetUnderCursor()
    {
        CurrentTarget = new TargetWindow((nint)101, (nint)102, "Example document", "example", 44, 500, 300);
        return ControlResult.Success();
    }

    public void ClearTarget() => CurrentTarget = null;

    public ValueTask<ControlResult> ExecuteAsync(ControlAction action, CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastAction = action;
        Actions.Add(action);
        if (Exception is not null)
        {
            throw Exception;
        }

        if (action is ScrollAction && InvalidateTarget)
        {
            InvalidateTarget = false;
            CurrentTarget = null;
            return ValueTask.FromResult(ControlResult.Failure(ControlErrorCode.InvalidInput, "The captured target window is no longer valid. Capture it again."));
        }

        if (action is ScrollAction && CurrentTarget is null)
            return ValueTask.FromResult(ControlResult.Failure(ControlErrorCode.InvalidInput, "Capture a target window before scrolling."));

        return ValueTask.FromResult(Result);
    }
}

sealed class FakeShortcutRegistrar : IGlobalShortcutRegistrar
{
    private readonly List<Action<ScrollShortcutCommand>> _callbacks = [];
    public int RegisterCount { get; private set; }
    public int DisposeCount { get; private set; }
    public Exception? RegistrationException { get; init; }

    public IDisposable Register(ScrollShortcutConfiguration configuration, Action<ScrollShortcutCommand> onShortcut)
    {
        RegisterCount++;
        if (RegistrationException is not null) throw RegistrationException;
        _callbacks.Add(onShortcut);
        return new CallbackRegistration(() => DisposeCount++);
    }

    public void Fire(ScrollShortcutCommand command) => _callbacks.LastOrDefault()?.Invoke(command);
    public void FireAt(int registrationIndex, ScrollShortcutCommand command) => _callbacks[registrationIndex](command);

    private sealed class CallbackRegistration(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
