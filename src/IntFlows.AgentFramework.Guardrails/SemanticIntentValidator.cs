using Microsoft.Extensions.AI;

namespace IntFlows.AgentFramework.Guardrails;

/// <summary>Allows input when its embedding matches an allowed intent description.</summary>
/// <remarks>The caller owns the generator; use a local provider to keep input on-device.</remarks>
public sealed class SemanticIntentValidator : IIntentValidator
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;
    private readonly string[] _descriptions;
    private readonly double _threshold;

    public SemanticIntentValidator(IEmbeddingGenerator<string, Embedding<float>> generator,
        IEnumerable<string> allowedIntentDescriptions, double threshold = 0.7)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(allowedIntentDescriptions);
        _descriptions = allowedIntentDescriptions.ToArray();
        if (_descriptions.Length == 0 || _descriptions.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Provide at least one non-empty intent description.", nameof(allowedIntentDescriptions));
        if (!double.IsFinite(threshold) || threshold < -1 || threshold > 1)
            throw new ArgumentOutOfRangeException(nameof(threshold));
        _generator = generator;
        _threshold = threshold;
    }

    public async ValueTask<bool> IsAllowedAsync(string input, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(input)) return false;
        var embeddings = await _generator.GenerateAsync([input, .. _descriptions], cancellationToken: cancellationToken).ConfigureAwait(false);
        if (embeddings.Count != _descriptions.Length + 1)
            throw new InvalidOperationException("Embedding provider returned an unexpected vector count.");
        return embeddings.Skip(1).Any(e => Cosine(embeddings[0].Vector.Span, e.Vector.Span) >= _threshold);
    }

    private static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length == 0 || a.Length != b.Length) return double.NaN;
        double dot = 0, aa = 0, bb = 0;
        for (var i = 0; i < a.Length; i++) { dot += (double)a[i] * b[i]; aa += (double)a[i] * a[i]; bb += (double)b[i] * b[i]; }
        return aa == 0 || bb == 0 ? double.NaN : dot / Math.Sqrt(aa * bb);
    }
}
