namespace IntFlows.AgentFramework.Guardrails;

public sealed record GuardOptions
{
    public bool DetectPromptInjection { get; init; } = true;
    public bool MaskPii { get; init; } = true;
    public bool RestorePii { get; init; } = true;
    public int MaxInputCharacters { get; init; } = 100_000;
    public IIntentValidator? IntentValidator { get; init; }
}

public interface IIntentValidator
{
    ValueTask<bool> IsAllowedAsync(string input, CancellationToken cancellationToken = default);
}

public enum GuardBlockReason { PromptInjection, DisallowedIntent, InputTooLarge, UnsupportedContent }

public sealed class GuardrailException(GuardBlockReason reason)
    : InvalidOperationException($"Request blocked by guardrails: {reason}.")
{
    public GuardBlockReason Reason { get; } = reason;
}
