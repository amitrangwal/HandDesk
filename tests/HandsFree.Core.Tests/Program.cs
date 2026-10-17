using HandsFree.Core;

var tests = new (string Name, Func<Task> Run)[]
{
    ("rejects a relative launch path", () => Invalid(new LaunchApplicationAction("app.exe"))),
    ("rejects out-of-range cursor coordinates", () => Invalid(new MoveCursorAction(32_768, 0))),
    ("rejects unsupported mouse buttons", () => Invalid(new ClickAction((MouseButton)99))),
    ("rejects invalid scroll increments", () => Invalid(new ScrollAction(30))),
    ("rejects excessive scroll deltas", () => Invalid(new ScrollAction(1_320))),
    ("routes a valid action to the adapter", RoutesValidAction),
    ("returns a clear result for unsupported actions", RejectsUnknownAction),
    ("maps adapter exceptions to action failures", MapsAdapterException)
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

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed record UnknownAction : ControlAction;

sealed class FakeAdapter : IPlatformControlAdapter
{
    public int CallCount { get; private set; }
    public ControlAction? LastAction { get; private set; }
    public ControlResult Result { get; init; } = ControlResult.Success();
    public Exception? Exception { get; init; }

    public ValueTask<ControlResult> ExecuteAsync(ControlAction action, CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastAction = action;
        if (Exception is not null)
        {
            throw Exception;
        }

        return ValueTask.FromResult(Result);
    }
}
