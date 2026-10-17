namespace HandsFree.Core;

public sealed record GestureInput(string Name);

public sealed record VoiceInput(string Text);

public sealed record UserIntent(string Name, string? Argument = null);

public interface IGestureInputProvider
{
    IAsyncEnumerable<GestureInput> ReadGesturesAsync(CancellationToken cancellationToken = default);
}

public interface IVoiceInputProvider
{
    IAsyncEnumerable<VoiceInput> ReadUtterancesAsync(CancellationToken cancellationToken = default);
}

public interface IIntentInterpreter
{
    ValueTask<UserIntent?> InterpretAsync(string input, CancellationToken cancellationToken = default);
}

public interface IActionExecutor
{
    ValueTask ExecuteAsync(UserIntent intent, CancellationToken cancellationToken = default);
}
