using Microsoft.Agents.AI;

namespace IntFlows.AgentFramework.Guardrails;

public enum GuardKind { Injection, Intent, Pii, Tool }
public enum GuardAction { Allow, Block, Redact, ApprovalRequired }
public enum GuardReasonCode
{
    InjectionPattern, InjectionClear, IntentAllowed, IntentRejected,
    PiiDetected, PiiClear, ToolAllowed, ToolBlocked, ToolRedacted,
    ToolApprovalRequired, ToolApproved, ToolApprovalDenied, ToolPolicyError
}

/// <summary>A content-free audit event. It never includes prompts, arguments, or PII values.</summary>
public sealed record GuardDecision(
    string SchemaVersion,
    Guid DecisionId,
    DateTimeOffset Timestamp,
    GuardKind Guard,
    string PolicyVersion,
    GuardAction Action,
    GuardReasonCode ReasonCode,
    double LatencyMilliseconds,
    double? Confidence = null);

public sealed record ToolPolicyContext(
    string ToolName,
    IReadOnlyDictionary<string, object?> Arguments,
    AIAgent Agent);

public sealed record ToolGuardOptions
{
    public IPiiDetector? PiiDetector { get; init; }
    public GuardAction DefaultAction { get; init; } = GuardAction.Allow;
    public IReadOnlyDictionary<string, GuardAction> Rules { get; init; } =
        new Dictionary<string, GuardAction>(StringComparer.Ordinal);
    public Func<ToolPolicyContext, CancellationToken, ValueTask<bool>>? ApproveAsync { get; init; }
    public string PolicyVersion { get; init; } = "unversioned";
    public Func<GuardDecision, CancellationToken, ValueTask>? OnDecision { get; init; }
}

/// <summary>Returned by function middleware when a tool is stopped before execution.</summary>
public sealed record GuardToolBlockedResult(Guid DecisionId, GuardReasonCode ReasonCode)
{
    public string Status { get; } = "BLOCKED";
}
