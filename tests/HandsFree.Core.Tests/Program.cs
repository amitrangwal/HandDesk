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
    ("leaves scroll mode disabled when registration fails", ScrollModeRegistrationFailure),
    ("validates a 21-point hand result", HandTrackingResultValidation),
    ("right-hand preference acquires a confident right hand", RightHandPreferenceAcquires),
    ("left-hand detections cannot acquire a right-only lock", LeftHandCannotAcquireRightOnlyLock),
    ("left hand cannot replace the locked right hand when order changes", LeftHandCannotReplaceRightHand),
    ("unknown handedness is rejected with a useful status", UnknownHandednessIsRejected),
    ("keeps the initial hand when another hand has higher confidence", KeepsSelectedHandOverHigherConfidenceDetection),
    ("associates the same hand when detection order changes", AssociatesAcrossDetectionOrderChanges),
    ("recovers the selected hand after brief occlusion", RecoversAfterBriefOcclusion),
    ("expires the identity lock after one second and searches fresh", ExpiresIdentityLockAndSearchesFresh),
    ("coordinates explicit tracking start, frame and stop using a fake provider", HandTrackingSessionLifecycle),
    ("releases resources after a provider fault", HandTrackingFaultCleanup),
    ("cleans up the provider when tracking startup fails", HandTrackingStartupFailureCleanup),
    ("validates tensor shapes against the bundled ONNX models", InspectHandTrackingModelShapes)
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

static Task InspectHandTrackingModelShapes()
{
    var modelRoot = Path.Combine(AppContext.BaseDirectory, "Models");
    var shapes = OnnxHandTrackingPipeline.InspectModelShapes(
        Path.Combine(modelRoot, "PalmDetection", "palm_detection_mediapipe_2023feb.onnx"),
        Path.Combine(modelRoot, "HandPose", "handpose_estimation_mediapipe_2023feb.onnx"));
    Assert(shapes["palm.input.input_1"].SequenceEqual([1, 192, 192, 3]), "Palm model input shape did not match the OpenCV Zoo reference.");
    Assert(shapes["palm.output.Identity"].SequenceEqual([1, 2016, 18]), "Palm regression output shape did not match the bundled ONNX model.");
    Assert(shapes["palm.output.Identity_1"].SequenceEqual([1, 2016, 1]), "Palm score output shape did not match the bundled ONNX model.");
    Assert(shapes["hand.input.input_1"].SequenceEqual([1, 224, 224, 3]), "Hand model input shape did not match the OpenCV Zoo reference.");
    Assert(shapes["hand.output.Identity"].SequenceEqual([1, 63]), "Hand screen landmarks must contain 21 xyz points.");
    Assert(shapes["hand.output.Identity_1"].SequenceEqual([1, 1]) && shapes["hand.output.Identity_2"].SequenceEqual([1, 1]), "Hand presence and handedness must each be scalar outputs.");
    Assert(shapes["hand.output.Identity_3"].SequenceEqual([1, 63]), "Hand world landmarks must contain 21 xyz points.");
    using var pipeline = new OnnxHandTrackingPipeline(
        Path.Combine(modelRoot, "PalmDetection", "palm_detection_mediapipe_2023feb.onnx"),
        Path.Combine(modelRoot, "HandPose", "handpose_estimation_mediapipe_2023feb.onnx"));
    var blank = pipeline.Infer(new byte[320 * 240 * 3], 320, 240);
    Assert(!blank.IsDetected && blank.Landmarks.Count == 0, "A blank offline frame should produce a valid no-hand result.");
    return Task.CompletedTask;
}

static Task HandTrackingResultValidation()
{
    var points = Enumerable.Range(0, 21).Select(index => new HandLandmark(index / 20f, index / 20f, -0.1f)).ToArray();
    Assert(HandTrackingResult.TryCreate(points, 0.95f, 1f, out var result), "A valid 21-point hand result should be accepted.");
    Assert(result!.IsDetected && result.Landmarks.Count == 21 && result.Confidence == 0.95f, "The accepted hand result was not retained correctly.");
    Assert(!HandTrackingResult.TryCreate(points[..20], 0.9f, 0f, out _), "A result with fewer than 21 landmarks must be rejected.");
    var invalidPoints = points.ToArray();
    invalidPoints[5] = new HandLandmark(float.NaN, 0.5f, 0f);
    Assert(!HandTrackingResult.TryCreate(invalidPoints, 0.9f, 0f, out _), "Non-finite landmarks must be rejected.");
    Assert(!HandTrackingResult.TryCreate(points, 1.1f, 0f, out _), "Invalid confidence must be rejected.");
    return Task.CompletedTask;
}

static Task KeepsSelectedHandOverHigherConfidenceDetection()
{
    var selector = new SingleHandTrackingSelector();
    var start = DateTimeOffset.UnixEpoch;
    var firstHand = CreateHand(0.25f, 0.5f, 0.85f);
    var secondHand = CreateHand(0.75f, 0.5f, 0.99f);
    var first = selector.Update([firstHand], start);
    var second = selector.Update([WithConfidence(firstHand, 0.70f), secondHand], start.AddMilliseconds(33));
    Assert(first.State == HandTrackingIdentityState.Tracking && first.Result.Bounds == firstHand.Bounds, "The first visible hand should establish the identity lock.");
    Assert(second.State == HandTrackingIdentityState.Tracking && second.Result.Bounds == firstHand.Bounds, "A newly detected higher-confidence hand must not take over the existing lock.");
    return Task.CompletedTask;
}

static Task RightHandPreferenceAcquires()
{
    var selector = new SingleHandTrackingSelector();
    var right = CreateHand(0.4f, 0.5f, 0.9f, handedness: 0.95f);
    var selected = selector.Update([right], DateTimeOffset.UnixEpoch);
    Assert(selector.Preference == HandTrackingPreference.RightOnly, "Right hand only must be the default preference.");
    Assert(selected.State == HandTrackingIdentityState.Tracking && selected.Result.Bounds == right.Bounds, "A confident right hand should acquire the default lock.");
    return Task.CompletedTask;
}

static Task LeftHandCannotAcquireRightOnlyLock()
{
    var selector = new SingleHandTrackingSelector();
    var left = CreateHand(0.4f, 0.5f, 0.99f, handedness: 0.05f);
    var result = selector.Update([left], DateTimeOffset.UnixEpoch);
    Assert(result.State == HandTrackingIdentityState.Searching && !result.Result.IsDetected, "A left hand must not acquire a right-only identity lock.");
    Assert(result.Message.Contains("Left hand detected", StringComparison.Ordinal), "The waiting status should explain why the visible left hand was ignored.");
    return Task.CompletedTask;
}

static Task LeftHandCannotReplaceRightHand()
{
    var selector = new SingleHandTrackingSelector();
    var start = DateTimeOffset.UnixEpoch;
    var lockedRight = CreateHand(0.3f, 0.5f, 0.8f, handedness: 0.95f);
    var competingLeft = CreateHand(0.3f, 0.5f, 0.99f, handedness: 0.05f);
    var movedRight = CreateHand(0.5f, 0.5f, 0.75f, handedness: 0.95f);
    selector.Update([lockedRight], start);
    var changedOrder = selector.Update([competingLeft, movedRight], start.AddMilliseconds(40));
    Assert(changedOrder.State == HandTrackingIdentityState.Tracking && changedOrder.Result.Bounds == movedRight.Bounds, "The left candidate must be filtered before association even when it appears first and has higher confidence.");
    return Task.CompletedTask;
}

static Task UnknownHandednessIsRejected()
{
    var selector = new SingleHandTrackingSelector();
    var uncertain = CreateHand(0.4f, 0.5f, 0.99f, handedness: 0.55f);
    var result = selector.Update([uncertain], DateTimeOffset.UnixEpoch);
    Assert(result.State == HandTrackingIdentityState.Searching && !result.Result.IsDetected, "Low-confidence handedness must not be treated as right-handed.");
    Assert(result.Message.Contains("handedness is uncertain", StringComparison.Ordinal), "The waiting status should report uncertain handedness.");
    return Task.CompletedTask;
}

static Task AssociatesAcrossDetectionOrderChanges()
{
    var selector = new SingleHandTrackingSelector();
    var start = DateTimeOffset.UnixEpoch;
    var left = CreateHand(0.3f, 0.5f, 0.9f);
    var right = CreateHand(0.7f, 0.5f, 0.9f);
    selector.Update([right, left], start);
    var swapped = selector.Update([WithConfidence(left, 0.99f), WithConfidence(right, 0.75f)], start.AddMilliseconds(40));
    Assert(swapped.State == HandTrackingIdentityState.Tracking && swapped.Result.Bounds == right.Bounds, "Detection order and confidence changes must not switch the selected hand.");
    return Task.CompletedTask;
}

static Task RecoversAfterBriefOcclusion()
{
    var selector = new SingleHandTrackingSelector(TimeSpan.FromMilliseconds(500));
    var start = DateTimeOffset.UnixEpoch;
    var selected = CreateHand(0.4f, 0.5f, 0.9f, handedness: 0.95f);
    var other = CreateHand(0.8f, 0.5f, 0.99f, handedness: 0.05f);
    selector.Update([selected], start);
    var occluded = selector.Update([other], start.AddMilliseconds(150));
    var returned = selector.Update([other, WithConfidence(selected, 0.75f)], start.AddMilliseconds(300));
    Assert(occluded.State == HandTrackingIdentityState.TemporarilyLost && !occluded.Result.IsDetected, "A different high-confidence hand during brief occlusion must not take over the retained identity.");
    Assert(occluded.Message.Contains("Recovering", StringComparison.Ordinal) && occluded.Message.Contains("Other-hand detections are ignored", StringComparison.Ordinal), "The UI message should identify the active recovery period and explain that other hands are ignored.");
    Assert(returned.State == HandTrackingIdentityState.Tracking && returned.Result.Bounds == selected.Bounds, "The matching hand should resume tracking within the recovery window.");
    return Task.CompletedTask;
}

static Task ExpiresIdentityLockAndSearchesFresh()
{
    var selector = new SingleHandTrackingSelector();
    var start = DateTimeOffset.UnixEpoch;
    var selected = CreateHand(0.2f, 0.5f, 0.9f, handedness: 0.95f);
    var other = CreateHand(0.8f, 0.5f, 0.999f, handedness: 0.95f);
    selector.Update([selected], start);
    var recovering = selector.Update([other], start.AddMilliseconds(999));
    var expired = selector.Update([], start.AddSeconds(1));
    var afterExpiry = selector.Update([other], start.AddMilliseconds(1020));
    selector.Reset();
    var afterReset = selector.Update([selected], start.AddMilliseconds(1040));
    Assert(recovering.State == HandTrackingIdentityState.TemporarilyLost && !recovering.Result.IsDetected, "A different hand must not replace the selected identity during the one-second recovery window.");
    Assert(expired.State == HandTrackingIdentityState.Searching && expired.Message.Contains("identity expired", StringComparison.Ordinal), "At one second without a matching hand, the selector should release the lock and report fresh search.");
    Assert(afterExpiry.State == HandTrackingIdentityState.Tracking && afterExpiry.Result.Bounds == other.Bounds, "A currently visible right hand should be selected fresh after expiry without pressing reset.");
    Assert(selector.Preference == HandTrackingPreference.RightOnly, "Explicit reset should clear the identity lock without changing the configured preference.");
    Assert(afterReset.State == HandTrackingIdentityState.Tracking && afterReset.Result.Bounds == selected.Bounds, "Explicit reset should still allow immediate fresh selection.");
    return Task.CompletedTask;
}

static HandTrackingResult CreateHand(float centerX, float centerY, float confidence, float handedness = 0.95f)
{
    var points = Enumerable.Range(0, 21).Select(index => new HandLandmark(
        centerX + ((index % 5) - 2) * 0.015f,
        centerY + ((index / 5) - 2) * 0.02f,
        0f));
    if (!HandTrackingResult.TryCreate(points, confidence, handedness, out var result)) throw new InvalidOperationException("Test hand creation failed.");
    return result!;
}

static HandTrackingResult WithConfidence(HandTrackingResult source, float confidence)
{
    if (!HandTrackingResult.TryCreate(source.Landmarks, confidence, source.Handedness, out var result)) throw new InvalidOperationException("Test confidence update failed.");
    return result!;
}

static async Task HandTrackingSessionLifecycle()
{
    var provider = new FakeHandTrackingProvider();
    await using var session = new HandTrackingSession(provider);
    await session.StartAsync();
    Assert(provider.StartCount == 1 && session.Status.State == HandTrackingState.Running, "Explicit start should start the provider once.");
    var pixels = new byte[2 * 2 * 3];
    provider.Emit(new HandTrackingFrame(2, 2, pixels, HandTrackingResult.NoHand, DateTimeOffset.UtcNow));
    Assert(session.LatestFrame?.Width == 2, "A valid latest frame should reach the shared session.");
    provider.Emit(new HandTrackingFrame(2, 2, new byte[3], HandTrackingResult.NoHand, DateTimeOffset.UtcNow));
    Assert(session.LatestFrame?.BgrPixels.Length == 12, "Malformed frame data must be ignored.");
    session.ResetHandSelection();
    Assert(provider.ResetCount == 1 && session.LatestFrame is null, "Explicit hand reset should reach the provider and clear the stale frame.");
    await session.StopAsync();
    Assert(provider.StopCount == 1 && session.LatestFrame is null && session.Status.State == HandTrackingState.Stopped, "Stop should clear frames and release the provider.");
    await session.DisposeAsync();
    await session.DisposeAsync();
    Assert(provider.DisposeCount == 1 && session.Status.State == HandTrackingState.Disposed, "Session and provider disposal should be idempotent.");
}

static async Task HandTrackingStartupFailureCleanup()
{
    var provider = new FakeHandTrackingProvider { StartException = new UnauthorizedAccessException("camera access") };
    await using var session = new HandTrackingSession(provider);
    await session.StartAsync();
    Assert(session.Status.State == HandTrackingState.Faulted && session.Status.Message.Contains("Camera access was denied", StringComparison.Ordinal), "Camera permission failures should be clearly reported.");
    Assert(provider.StopCount == 1, "A failed start must ask the provider to clean up resources.");
}

static async Task HandTrackingFaultCleanup()
{
    var provider = new FakeHandTrackingProvider();
    await using var session = new HandTrackingSession(provider);
    var faulted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    session.StatusChanged += status => { if (status.State == HandTrackingState.Faulted) faulted.TrySetResult(); };
    await session.StartAsync();
    provider.EmitFault("The camera disconnected.");
    await faulted.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Assert(provider.StopCount == 1 && session.Status.Message == "The camera disconnected.", "Provider faults should stop resources and preserve a clear status.");
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

sealed class FakeHandTrackingProvider : IHandTrackingProvider
{
    public event Action<HandTrackingFrame>? FrameAvailable;
    public event Action<string>? StatusChanged;
    private event Action<string>? FaultEvent;
    public event Action<string>? Faulted { add => FaultEvent += value; remove => FaultEvent -= value; }
    public int StartCount { get; private set; }
    public int StopCount { get; private set; }
    public int DisposeCount { get; private set; }
    public int ResetCount { get; private set; }
    public Exception? StartException { get; init; }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        StartCount++;
        if (StartException is not null) throw StartException;
        StatusChanged?.Invoke("Fake camera connected.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        StopCount++;
        return Task.CompletedTask;
    }

    public void ResetHandSelection() => ResetCount++;

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }

    public void Emit(HandTrackingFrame frame) => FrameAvailable?.Invoke(frame);
    public void EmitFault(string message) => FaultEvent?.Invoke(message);
}
