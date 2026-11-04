namespace IntFlows.AgentFramework.Guardrails;

public sealed record GuardOptions
{
    public bool DetectPromptInjection { get; init; } = true;
    public bool MaskPii { get; init; } = true;
    public bool RestorePii { get; init; } = true;
    public int MaxInputCharacters { get; init; } = 100_000;
    public IIntentValidator? IntentValidator { get; init; }
    public string PolicyVersion { get; init; } = "unversioned";
    public Func<GuardDecision, CancellationToken, ValueTask>? OnDecision { get; init; }
    public ToolPolicy? Tools { get; init; }
    public IPiiDetector? PiiDetector { get; init; }
    /// <summary>Creates a fresh, exclusively owned vault for each request. The guard disposes it.</summary>
    public Func<IPiiVault>? PiiVaultFactory { get; init; }
}

public interface IIntentValidator
{
    ValueTask<bool> IsAllowedAsync(string input, CancellationToken cancellationToken = default);
}

public enum GuardBlockReason { PromptInjection, DisallowedIntent, InputTooLarge, UnsupportedContent, DisallowedTool }

public sealed class GuardrailException(GuardBlockReason reason)
    : InvalidOperationException($"Request blocked by guardrails: {reason}.")
{
    public GuardBlockReason Reason { get; } = reason;
}
