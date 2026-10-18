using HandsFree.Core;
using HandsFree.Windows;
using System.Text.Json;

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
    ("recognizes a conservative index-only pointer pose", IndexPointerPoseDetection),
    ("maps pointer coordinates at movement-window edges and mirrors consistently", IndexPointerCoordinateMapping),
    ("pauses outside the window and reanchors without a re-entry jump", IndexPointerOutOfWindowRecovery),
    ("serializes labeled local landmark samples and validates labels", LandmarkCollectorSerializationAndLabels),
    ("does not record fabricated samples during tracking loss", LandmarkCollectorSkipsTrackingLoss),
    ("drains and closes the sample writer on shutdown", LandmarkCollectorWriterShutdown),
    ("calculates bounded tracking metrics and resets session counters", TrackingMetricsCalculationAndReset),
    ("aggregates tracking-stage diagnostics and resets counters", TrackingDiagnosticsAggregationAndReset),
    ("calculates process CPU per logical core from time deltas", ProcessCpuMetricCalculation),
    ("right-hand preference acquires a confident right hand", RightHandPreferenceAcquires),
    ("left-hand detections cannot acquire a right-only lock", LeftHandCannotAcquireRightOnlyLock),
    ("left hand cannot replace the locked right hand when order changes", LeftHandCannotReplaceRightHand),
    ("unknown handedness is rejected with a useful status", UnknownHandednessIsRejected),
    ("keeps the initial hand when another hand has higher confidence", KeepsSelectedHandOverHigherConfidenceDetection),
    ("associates the same hand when detection order changes", AssociatesAcrossDetectionOrderChanges),
    ("recovers the selected hand after brief occlusion", RecoversAfterBriefOcclusion),
    ("recovers the locked hand before the default one-second deadline", RecoversWithinDefaultRecoveryWindow),
    ("expires the identity lock after one second and searches fresh", ExpiresIdentityLockAndSearchesFresh),
    ("coordinates explicit tracking start, frame and stop using a fake provider", HandTrackingSessionLifecycle),
    ("releases resources after a provider fault", HandTrackingFaultCleanup),
    ("cleans up the provider when tracking startup fails", HandTrackingStartupFailureCleanup),
    ("validates tensor shapes against the bundled ONNX models", InspectHandTrackingModelShapes),
    ("rejects incompatible ONNX output metadata and values", RejectsInvalidOnnxOutputContracts)
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
    Assert(shapes.Keys.Where(key => key.StartsWith("hand.output.", StringComparison.Ordinal)).Select(key => key[12..])
        .SequenceEqual(["Identity", "Identity_1", "Identity_2", "Identity_3"]),
        "The bundled hand-pose output order must match the verified OpenCV Zoo semantic order.");
    using var pipeline = new OnnxHandTrackingPipeline(
        Path.Combine(modelRoot, "PalmDetection", "palm_detection_mediapipe_2023feb.onnx"),
        Path.Combine(modelRoot, "HandPose", "handpose_estimation_mediapipe_2023feb.onnx"));
    var blank = pipeline.Infer(new byte[320 * 240 * 3], 320, 240);
    Assert(!blank.IsDetected && blank.Landmarks.Count == 0, "A blank offline frame should produce a valid no-hand result.");
    return Task.CompletedTask;
}

static Task RejectsInvalidOnnxOutputContracts()
{
    OnnxHandTrackingPipeline.ValidateOutputNames("Hand pose inference", ["Identity_3", "Identity_2", "Identity_1", "Identity"],
        ["Identity", "Identity_1", "Identity_2", "Identity_3"]);
    ExpectInvalidData(() => OnnxHandTrackingPipeline.ValidateOutputNames("Hand pose inference", ["Identity", "Identity_1", "Unexpected"],
        ["Identity", "Identity_1", "Identity_2"]), "Unexpected or missing runtime output names must be rejected.");

    var expected = new Dictionary<string, int[]>(StringComparer.Ordinal)
    {
        ["Identity"] = [1, 2016, 18],
        ["Identity_1"] = [1, 2016, 1]
    };
    OnnxHandTrackingPipeline.ValidateOutputMetadata("Palm detector", new ModelTensorMetadata[]
    {
        new("Identity_1", typeof(float), [1, 2016, 1]),
        new("Identity", typeof(float), [1, 2016, 18])
    }, expected);

    ExpectInvalidData(() => OnnxHandTrackingPipeline.ValidateOutputMetadata("Palm detector", new ModelTensorMetadata[]
    {
        new("Identity", typeof(float), [1, 2016, 1]),
        new("Identity_1", typeof(float), [1, 2016, 18])
    }, expected), "Name-to-shape swaps must fail instead of binding by enumeration order.");
    ExpectInvalidData(() => OnnxHandTrackingPipeline.ValidateOutputMetadata("Palm detector", new ModelTensorMetadata[]
    {
        new("Identity", typeof(double), [1, 2016, 18]),
        new("Identity_1", typeof(float), [1, 2016, 1])
    }, expected), "Non-float model outputs must be rejected.");
    ExpectInvalidData(() => OnnxHandTrackingPipeline.ValidateOutputMetadata("Palm detector", new ModelTensorMetadata[]
    {
        new("Identity", typeof(float), [2016, 18]),
        new("Identity_1", typeof(float), [1, 2016, 1])
    }, expected), "An incorrect output rank must be rejected.");
    ExpectInvalidData(() => OnnxHandTrackingPipeline.ValidateOutputMetadata("Palm detector", new ModelTensorMetadata[]
    {
        new("Identity", typeof(float), [1, 2016, 18]),
        new("Unexpected", typeof(float), [1, 2016, 1])
    }, expected), "Missing or unexpected output names must be rejected.");
    ExpectInvalidData(() => OnnxHandTrackingPipeline.ValidateOutputMetadata("Palm detector", new ModelTensorMetadata[]
    {
        new("Identity", typeof(float), [1, 2016, 18]),
        new("Identity", typeof(float), [1, 2016, 18])
    }, expected), "Duplicate output names must be rejected.");

    OnnxHandTrackingPipeline.ValidateOutputValues("Palm detector", new ModelTensorMetadata("Identity_1", typeof(float), [1, 2, 1]), [1, 2, 1], [0.1f, 0.9f]);
    ExpectInvalidData(() => OnnxHandTrackingPipeline.ValidateOutputValues("Hand pose model", new ModelTensorMetadata("Identity", typeof(float), [1, 63]), [1, 63], new float[62]),
        "Malformed output lengths must be rejected.");
    ExpectInvalidData(() => OnnxHandTrackingPipeline.ValidateOutputValues("Hand pose model", new ModelTensorMetadata("Identity", typeof(float), [63]), [1, 63], new float[63]),
        "Incorrect runtime output ranks must be rejected.");
    var nonFiniteValues = new float[63];
    nonFiniteValues[11] = float.NaN;
    ExpectInvalidData(() => OnnxHandTrackingPipeline.ValidateOutputValues("Hand pose model", new ModelTensorMetadata("Identity", typeof(float), [1, 63]), [1, 63], nonFiniteValues),
        "Non-finite landmark output values must be rejected.");
    Assert(OnnxHandTrackingPipeline.RequireProbability(0.75f, "test score", "test") == 0.75f, "A valid classification score should be preserved.");
    ExpectInvalidData(() => OnnxHandTrackingPipeline.RequireProbability(float.NaN, "test score", "test"), "Non-finite classification scores must be rejected.");
    ExpectInvalidData(() => OnnxHandTrackingPipeline.RequireProbability(1.01f, "test score", "test"), "Out-of-range classification scores must be rejected.");
    return Task.CompletedTask;
}

static void ExpectInvalidData(Action action, string failureMessage)
{
    try
    {
        action();
        throw new InvalidOperationException(failureMessage);
    }
    catch (InvalidDataException) { }
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

static Task IndexPointerPoseDetection()
{
    var valid = CreateIndexPoseHand();
    var detected = IndexFingerPoseDetector.Detect(valid);
    Assert(detected.IsRecognized && detected.Fingertip == new SimulatedPointerPosition(valid.Landmarks[8].X, valid.Landmarks[8].Y),
        "A straight index with the other fingers and thumb curled should use landmark 8.");
    Assert(IndexFingerPoseDetector.Detect(WithConfidence(valid, 0.7f)).Failure == IndexFingerPoseFailure.LowConfidence,
        "An uncertain detection should be rejected.");

    var openMiddle = valid.Landmarks.ToArray();
    openMiddle[12] = new HandLandmark(openMiddle[10].X, 0.3f, 0);
    openMiddle[11] = new HandLandmark(openMiddle[10].X, 0.4f, 0);
    Assert(HandTrackingResult.TryCreate(openMiddle, 0.95f, 0.95f, out var invalid) &&
        IndexFingerPoseDetector.Detect(invalid).Failure == IndexFingerPoseFailure.OtherFingerNotCurled,
        "An extended middle finger should reject the index-pointer pose.");

    var curledIndex = valid.Landmarks.ToArray();
    curledIndex[8] = new HandLandmark(curledIndex[6].X, curledIndex[6].Y + 0.02f, 0);
    Assert(HandTrackingResult.TryCreate(curledIndex, 0.95f, 0.95f, out invalid) &&
        IndexFingerPoseDetector.Detect(invalid).Failure == IndexFingerPoseFailure.IndexNotExtended,
        "A curled index should be rejected.");
    return Task.CompletedTask;
}

static Task IndexPointerCoordinateMapping()
{
    var window = PointerMovementWindow.Default;
    var topLeft = IndexFingerPointerMapper.TryMap(new SimulatedPointerPosition(window.Left, window.Top), window, 1280, 720);
    var bottomRight = IndexFingerPointerMapper.TryMap(new SimulatedPointerPosition(window.Left + window.Width, window.Top + window.Height), window, 1280, 720);
    Assert(topLeft == new SimulatedPointerPosition(0, 0), "The movement-window top-left edge should map to the desktop origin.");
    Assert(bottomRight is { } edge && Math.Abs(edge.X - 1280) < 0.001 && Math.Abs(edge.Y - 720) < 0.001,
        "The bottom-right edge should map to the desktop far edge.");
    Assert(IndexFingerPointerMapper.TryMap(new SimulatedPointerPosition(window.Left - 0.001, 0.5), window, 1280, 720) is null,
        "An out-of-window point must be rejected rather than clamped.");

    const double rawX = 0.7;
    var displayedMirrored = new SimulatedPointerPosition(1 - rawX, 0.5);
    var mirrored = IndexFingerPointerMapper.TryMap(displayedMirrored, window, 1280, 720);
    Assert(mirrored is { } mapped && Math.Abs(mapped.X - ((0.3 - window.Left) / window.Width * 1280)) < 0.001,
        "Mirrored preview x should map using the displayed, mirrored coordinate.");
    var previewMirrored = IndexFingerPointerMapper.TryMapPreviewPoint(190, 100, 50, 0, 200, 200, window, true, 1280, 720);
    Assert(previewMirrored is { } previewMapped && Math.Abs(previewMapped.X - ((0.3 - window.Left) / window.Width * 1280)) < 0.001,
        "Preview-coordinate mapping should apply camera-preview mirroring consistently.");

    var fromSmallPreview = IndexFingerPointerMapper.TryMapPreviewPoint(150, 100, 50, 0, 200, 200, window, false, 1280, 720);
    var fromLargePreview = IndexFingerPointerMapper.TryMapPreviewPoint(375, 250, 125, 0, 500, 500, window, false, 1280, 720);
    Assert(fromSmallPreview == fromLargePreview, "The same normalized point must map consistently across preview sizes.");
    Assert(IndexFingerPointerMapper.TryMapPreviewPoint(15, 100, 50, 0, 200, 200, window, false, 1280, 720) is null,
        "Letterbox space outside the camera image must not be treated as an in-window point.");
    return Task.CompletedTask;
}

static Task IndexPointerOutOfWindowRecovery()
{
    var mapper = new IndexFingerPointerMapper();
    var window = PointerMovementWindow.Default;
    var initial = mapper.Update(CreateIndexPoseHand(), true, window, previewMirrored: true, 1280, 720);
    Assert(initial.PoseRecognized && initial.FingertipInsideWindow && initial.Pointer is not null, "A valid in-window pose should initialize the simulated pointer.");
    var movedTipOut = CreateIndexPoseHand(fingertipX: 0.95f);
    var outside = mapper.Update(movedTipOut, true, window, previewMirrored: true, 1280, 720);
    Assert(outside.PoseRecognized && !outside.FingertipInsideWindow && outside.Pointer == initial.Pointer,
        "Leaving the window should pause without clamping or moving the simulated pointer.");
    var reentered = mapper.Update(CreateIndexPoseHand(), true, window, previewMirrored: true, 1280, 720);
    Assert(reentered.FingertipInsideWindow && reentered.Pointer == initial.Pointer,
        "Re-entry should re-anchor at the last simulated position without a jump.");
    var next = mapper.Update(CreateIndexPoseHand(fingertipX: 0.41f), true, window, previewMirrored: true, 1280, 720);
    Assert(next.Pointer != reentered.Pointer, "After re-anchoring, subsequent fingertip motion should move the simulated pointer.");

    var lost = mapper.Update(HandTrackingResult.NoHand, false, window, previewMirrored: true, 1280, 720);
    Assert(!lost.PoseRecognized && lost.Pointer == next.Pointer && lost.Message.Contains("paused", StringComparison.OrdinalIgnoreCase),
        "Tracking loss should pause movement and preserve the visual marker.");
    return Task.CompletedTask;
}

static HandTrackingResult CreateIndexPoseHand(float fingertipX = 0.39f)
{
    var points = new[]
    {
        new HandLandmark(0.50f, 0.80f, 0), // 0 wrist
        new HandLandmark(0.47f, 0.75f, 0), new HandLandmark(0.44f, 0.70f, 0), new HandLandmark(0.42f, 0.67f, 0),
        new HandLandmark(0.48f, 0.68f, 0), // 4 curled thumb tip
        new HandLandmark(0.42f, 0.55f, 0), new HandLandmark(0.40f, 0.40f, 0), new HandLandmark(0.39f, 0.25f, 0),
        new HandLandmark(fingertipX, 0.12f, 0), // 8 index tip
        new HandLandmark(0.48f, 0.54f, 0), new HandLandmark(0.47f, 0.46f, 0), new HandLandmark(0.46f, 0.55f, 0), new HandLandmark(0.48f, 0.60f, 0),
        new HandLandmark(0.54f, 0.55f, 0), new HandLandmark(0.55f, 0.47f, 0), new HandLandmark(0.55f, 0.56f, 0), new HandLandmark(0.56f, 0.62f, 0),
        new HandLandmark(0.61f, 0.58f, 0), new HandLandmark(0.63f, 0.54f, 0), new HandLandmark(0.64f, 0.62f, 0), new HandLandmark(0.65f, 0.68f, 0)
    };
    if (!HandTrackingResult.TryCreate(points, 0.95f, 0.95f, out var result)) throw new InvalidOperationException("Invalid synthetic index pose.");
    return result!;
}

static async Task LandmarkCollectorSerializationAndLabels()
{
    var directory = Path.Combine(Path.GetTempPath(), "HandsFreeTests", Guid.NewGuid().ToString("N"));
    try
    {
        await using var collector = new HandLandmarkDataCollector(directory);
        Assert(!await collector.StartAsync("  "), "Collection must reject an empty label.");
        Assert(await collector.StartAsync("  open palm  "), "A valid gesture label should start a session.");
        var hand = CreateHand(0.5f, 0.5f, 0.91f, 0.96f);
        var timestamp = DateTimeOffset.UtcNow;
        Assert(collector.TryRecord(TrackingFrame(hand, timestamp), "open palm"), "A selected hand should queue as a sample.");
        Assert(!collector.TryRecord(TrackingFrame(hand, timestamp.AddMilliseconds(1)), "  "), "An unlabeled sample must be rejected.");
        await collector.StopAsync();
        var status = collector.Status;
        Assert(status.SamplesWritten == 1 && status.State == HandLandmarkCollectionState.Stopped, "Stop should flush the accepted sample and report its count.");
        var line = await File.ReadAllTextAsync(status.FilePath!);
        using var json = JsonDocument.Parse(line);
        var root = json.RootElement;
        Assert(root.GetProperty("schemaVersion").GetInt32() == 1, "Sample schema version should be explicit.");
        Assert(root.GetProperty("label").GetString() == "open palm", "The normalized label should be serialized.");
        Assert(root.GetProperty("sampleId").GetInt64() == 1 && root.GetProperty("sessionId").GetGuid() == status.SessionId,
            "Sample and collection session identifiers should be serialized.");
        Assert(root.GetProperty("landmarks").GetArrayLength() == 21, "All 21 points should be serialized.");
        var first = root.GetProperty("landmarks")[0];
        Assert(first.TryGetProperty("x", out _) && first.TryGetProperty("y", out _) && first.TryGetProperty("z", out _), "Each point must preserve x/y/z.");
        Assert(root.GetProperty("confidence").GetSingle() == hand.Confidence
            && root.GetProperty("handednessConfidence").GetSingle() == hand.HandednessConfidence,
            "Confidence and handedness metadata should be retained.");
        Assert(Path.GetDirectoryName(status.FilePath) == directory, "The injected local directory should be used.");
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}

static async Task LandmarkCollectorSkipsTrackingLoss()
{
    var directory = Path.Combine(Path.GetTempPath(), "HandsFreeTests", Guid.NewGuid().ToString("N"));
    try
    {
        await using var collector = new HandLandmarkDataCollector(directory);
        Assert(await collector.StartAsync("wave"), "Collection should start with a label.");
        collector.NotifyTrackingLost("Selected hand is temporarily lost.");
        Assert(collector.Status.Message.Contains("paused", StringComparison.OrdinalIgnoreCase), "Tracking loss should be visible in collection status.");
        Assert(!collector.TryRecord(new HandTrackingFrame(1, 1, new byte[3], HandTrackingResult.NoHand,
            DateTimeOffset.UtcNow, HandTrackingIdentityState.TemporarilyLost, "Selected hand is temporarily lost."), "wave"),
            "A frame without a selected hand must not produce a sample.");
        var hand = CreateHand(0.5f, 0.5f, 0.9f);
        Assert(collector.TryRecord(TrackingFrame(hand, DateTimeOffset.UtcNow), "wave"), "Collection should resume when the selected hand returns.");
        await collector.StopAsync();
        Assert(collector.Status.SamplesWritten == 1, "Only the real detected-hand frame should be saved.");
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}

static async Task LandmarkCollectorWriterShutdown()
{
    var directory = Path.Combine(Path.GetTempPath(), "HandsFreeTests", Guid.NewGuid().ToString("N"));
    try
    {
        var collector = new HandLandmarkDataCollector(directory);
        Assert(await collector.StartAsync("point"), "Collection should start.");
        var hand = CreateHand(0.45f, 0.55f, 0.88f);
        for (var i = 0; i < 40; i++)
            Assert(collector.TryRecord(TrackingFrame(hand, DateTimeOffset.UtcNow.AddMilliseconds(i)), "point"), "The bounded writer should accept a normal short batch.");
        await collector.DisposeAsync();
        Assert(collector.Status.State == HandLandmarkCollectionState.Disposed && collector.Status.SamplesWritten == 40,
            "Disposal should drain queued samples and release writer resources.");
        var lines = await File.ReadAllLinesAsync(collector.Status.FilePath!);
        Assert(lines.Length == 40, "Every accepted sample should be flushed before shutdown.");
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}

static HandTrackingFrame TrackingFrame(HandTrackingResult result, DateTimeOffset timestamp) =>
    new(1, 1, new byte[3], result, timestamp, HandTrackingIdentityState.Tracking, "Hand detected.");

static Task TrackingMetricsCalculationAndReset()
{
    var metrics = new HandTrackingMetricAccumulator();
    var start = DateTimeOffset.UnixEpoch;
    metrics.RecordFrame(TimeSpan.FromMilliseconds(10), start, 0);
    metrics.RecordFrame(TimeSpan.FromMilliseconds(20), start.AddMilliseconds(50), 2);
    metrics.RecordFrame(TimeSpan.FromMilliseconds(30), start.AddMilliseconds(100), 1);
    var snapshot = metrics.Snapshot();
    Assert(snapshot.ProcessedFrames == 3 && snapshot.DroppedFrames == 3, "Frame and drop counts should be accumulated exactly.");
    Assert(Math.Abs(snapshot.FramesPerSecond - 20) < 0.001, "Tracking FPS should use processed-frame timestamps.");
    Assert(snapshot.LastOnnxLatencyMilliseconds == 30 && snapshot.AverageOnnxLatencyMilliseconds == 20 && snapshot.P95OnnxLatencyMilliseconds == 30,
        "Last-frame latency, rolling average, and p95 should use ONNX timing samples.");
    metrics.Reset();
    var idle = metrics.Snapshot();
    Assert(idle == HandTrackingMetricsSnapshot.Idle, "Reset should clear session counters and latency samples.");
    return Task.CompletedTask;
}

static Task TrackingDiagnosticsAggregationAndReset()
{
    var diagnostics = new HandTrackingDiagnosticsAccumulator();
    var selector = new SingleHandTrackingSelector();
    var right = CreateHand(0.2f, 0.5f, 0.9f, handedness: 0.95f);
    var left = CreateHand(0.8f, 0.5f, 0.9f, handedness: 0.05f);
    var first = selector.Update([right, left], DateTimeOffset.UnixEpoch);
    diagnostics.RecordFrame(new HandTrackingPipelineDiagnostics(12, 5, 4, 1, 4, 1), first, true);
    var second = selector.Update([left], DateTimeOffset.UnixEpoch.AddMilliseconds(100));
    diagnostics.RecordFrame(new HandTrackingPipelineDiagnostics(0, 0, 0, 0, 0, 0), second, true);
    var third = selector.Update(Array.Empty<HandTrackingResult>(), DateTimeOffset.UnixEpoch.AddSeconds(2));
    diagnostics.RecordFrame(HandTrackingPipelineDiagnostics.Empty, third, false);
    var snapshot = diagnostics.Snapshot();
    Assert(snapshot.Frames == 3 && snapshot.PalmCandidatesBeforeNms == 12 && snapshot.PalmCandidatesAfterNms == 5 && snapshot.PalmCandidatesAfterLimit == 4,
        "Palm candidate stage counts should be summed exactly.");
    Assert(snapshot.InvalidCropsSkipped == 1 && snapshot.HandPoseCandidatesProcessed == 4 && snapshot.RejectedByPresenceThreshold == 1,
        "Crop and pose-stage decisions should be accumulated.");
    Assert(snapshot.RejectedByHandedness == 2 && snapshot.FramesWithSelectedHand == 1 && snapshot.FramesWithTemporarilyLostHand == 1 && snapshot.FramesWithNoCandidate == 1,
        "Selection filters and frame outcomes should be counted.");
    Assert(snapshot.CurrentState == HandTrackingIdentityState.Searching && snapshot.SelectedHandConfidence is null,
        "Snapshot should expose the current lost state without stale selected confidence.");
    diagnostics.Reset();
    Assert(diagnostics.Snapshot() == HandTrackingDiagnosticsSnapshot.Empty, "Reset should clear totals, current state and confidence values.");
    return Task.CompletedTask;
}

static Task ProcessCpuMetricCalculation()
{
    Assert(Math.Abs(ProcessCpuUsage.CalculatePercent(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1))!.Value - 25) < 0.001,
        "CPU percent should be calculated from process CPU-time and wall-time deltas.");
    Assert(ProcessCpuUsage.CalculatePercent(TimeSpan.FromSeconds(1), TimeSpan.Zero) is null,
        "A zero elapsed interval should have no CPU percentage.");
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

static Task RecoversWithinDefaultRecoveryWindow()
{
    var selector = new SingleHandTrackingSelector();
    var start = DateTimeOffset.UnixEpoch;
    var selected = CreateHand(0.42f, 0.5f, 0.9f, handedness: 0.95f);
    selector.Update([selected], start);
    var lost = selector.Update([], start.AddMilliseconds(100));
    var recovered = selector.Update([WithConfidence(selected, 0.7f)], start.AddMilliseconds(999));
    Assert(lost.State == HandTrackingIdentityState.TemporarilyLost && !lost.Result.IsDetected, "No detections should retain the lock and enter recovery.");
    Assert(recovered.State == HandTrackingIdentityState.Tracking && recovered.Result.IsDetected, "The original hand should recover just before the default one-second expiry.");
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
    Assert(session.Metrics == HandTrackingMetricsSnapshot.Idle, "Tracking metrics should be idle before the session starts.");
    await session.StartAsync();
    Assert(provider.StartCount == 1 && session.Status.State == HandTrackingState.Running, "Explicit start should start the provider once.");
    provider.Metrics = new HandTrackingMetricsSnapshot(4, 1, 30, 9, 10, 15);
    Assert(session.Metrics.ProcessedFrames == 4, "Running sessions should expose their provider's current tracking metrics.");
    var pixels = new byte[2 * 2 * 3];
    provider.Emit(new HandTrackingFrame(2, 2, pixels, HandTrackingResult.NoHand, DateTimeOffset.UtcNow));
    Assert(session.LatestFrame?.Width == 2, "A valid latest frame should reach the shared session.");
    provider.Emit(new HandTrackingFrame(2, 2, new byte[3], HandTrackingResult.NoHand, DateTimeOffset.UtcNow));
    Assert(session.LatestFrame?.BgrPixels.Length == 12, "Malformed frame data must be ignored.");
    session.ResetHandSelection();
    Assert(provider.ResetCount == 1 && session.LatestFrame is null, "Explicit hand reset should reach the provider and clear the stale frame.");
    provider.FrameOnStop = new HandTrackingFrame(2, 2, pixels, HandTrackingResult.NoHand, DateTimeOffset.UtcNow);
    provider.FaultOnStop = "A shutdown-time camera callback should be ignored.";
    await session.StopAsync();
    Assert(provider.StopCount == 1 && session.LatestFrame is null && session.Status.State == HandTrackingState.Stopped, "Stop should clear frames and release the provider.");
    Assert(session.Metrics == HandTrackingMetricsSnapshot.Idle, "Stopped sessions should expose idle tracking metrics.");
    Assert(session.LatestFrame is null, "A final provider frame emitted during shutdown must not remain cached.");
    await session.StartAsync();
    Assert(session.Status.State == HandTrackingState.Running && session.LatestFrame is null && session.Metrics == HandTrackingMetricsSnapshot.Idle,
        "Restart should begin with no stale frame or prior-session metrics.");
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
    provider.StartException = null;
    await session.StartAsync();
    Assert(session.Status.State == HandTrackingState.Running && session.LatestFrame is null && session.Metrics == HandTrackingMetricsSnapshot.Idle,
        "A later explicit start should recover from camera startup failure with fresh session state.");
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
    await session.StartAsync();
    Assert(session.Status.State == HandTrackingState.Running && session.LatestFrame is null && session.Metrics == HandTrackingMetricsSnapshot.Idle,
        "A session should recover from a provider fault with fresh frame and metric state on explicit restart.");
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
    public HandTrackingMetricsSnapshot Metrics { get; set; } = HandTrackingMetricsSnapshot.Idle;
    public HandTrackingDiagnosticsSnapshot Diagnostics { get; private set; } = HandTrackingDiagnosticsSnapshot.Empty;
    public int StartCount { get; private set; }
    public int StopCount { get; private set; }
    public int DisposeCount { get; private set; }
    public int ResetCount { get; private set; }
    public Exception? StartException { get; set; }
    public HandTrackingFrame? FrameOnStop { get; set; }
    public string? FaultOnStop { get; set; }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        StartCount++;
        if (StartException is not null) throw StartException;
        Metrics = HandTrackingMetricsSnapshot.Idle;
        StatusChanged?.Invoke("Fake camera connected.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        StopCount++;
        if (FrameOnStop is { } frame) FrameAvailable?.Invoke(frame);
        if (FaultOnStop is { } fault) FaultEvent?.Invoke(fault);
        Metrics = HandTrackingMetricsSnapshot.Idle;
        return Task.CompletedTask;
    }

    public void ResetHandSelection() => ResetCount++;
    public void ResetDiagnostics() => Diagnostics = HandTrackingDiagnosticsSnapshot.Empty;

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }

    public void Emit(HandTrackingFrame frame) => FrameAvailable?.Invoke(frame);
    public void EmitFault(string message) => FaultEvent?.Invoke(message);
}
