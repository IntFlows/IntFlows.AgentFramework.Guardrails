namespace IntFlows.AgentFramework.Guardrails;

public enum ToolAction { Allow, Block }

/// <summary>Exact, case-sensitive tool rules. Unlisted tools use DefaultAction.</summary>
public sealed record ToolPolicy
{
    public ToolAction DefaultAction { get; init; } = ToolAction.Block;
    public IReadOnlyDictionary<string, ToolAction> Rules { get; init; }
        = new Dictionary<string, ToolAction>();
}
