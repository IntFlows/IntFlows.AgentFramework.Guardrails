namespace IntFlows.AgentFramework.Guardrails;

/// <summary>A detected span, measured in UTF-16 characters in the original input.</summary>
public sealed record PiiMatch(int Start, int Length, string Type);

/// <summary>Implementations shared by clients must support concurrent calls.</summary>
public interface IPiiDetector
{
    IEnumerable<PiiMatch> Detect(string text);
}

/// <summary>A single invocation's token storage and transformation scope.</summary>
public interface IPiiVault : IDisposable
{
    int Count { get; }
    string Mask(string text);
    string Restore(string text);
}
