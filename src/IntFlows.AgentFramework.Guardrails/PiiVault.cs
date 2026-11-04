using System.Text.RegularExpressions;

namespace IntFlows.AgentFramework.Guardrails;

// One vault per model invocation; never stored on the shared middleware instance.
public sealed class PiiVault(IPiiDetector? detector = null) : IPiiVault
{
    private static readonly Regex Detector = new(
        @"(?<EMAIL>[\w.!#$%&'*+/=?^`{|}~-]+@[\w-]+(?:\.[\w-]+)+)|" +
        @"(?<AU_PHONE>(?<!\d)(?:0[23478]|\+61[ -]?[23478])(?:[ -]?\d){8}(?!\d))|" +
        @"(?<CARD>(?<!\d)(?:\d[ -]?){12,18}\d(?!\d))|" +
        @"(?<ABN>(?<!\d)\d{2}[ -]?\d{3}[ -]?\d{3}[ -]?\d{3}(?!\d))|" +
        @"(?<MEDICARE>(?<!\d)\d{4}[ -]?\d{5}[ -]?\d(?!\d))|" +
        @"(?<TFN>(?<!\d)\d{3}[ -]?\d{3}[ -]?\d{3}(?!\d))",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
    private static readonly string[] Types = ["EMAIL", "AU_PHONE", "CARD", "ABN", "MEDICARE", "TFN"];
    private readonly string _scope = Guid.NewGuid().ToString("N");
    private readonly Dictionary<string, string> _tokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    private bool _disposed;
    public int Count => _tokens.Count;

    public static string Redact(string text, string replacement = "[REDACTED]", IPiiDetector? detector = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(replacement);
        using var vault = new PiiVault(detector);
        var masked = vault.Mask(text);
        foreach (var token in vault._tokens.Keys) masked = masked.Replace(token, replacement, StringComparison.Ordinal);
        return masked;
    }

    public string Mask(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(text);
        // Keep the built-in patterns and augment them with application detections.
        var matches = Detector.Matches(text).Select(match => new PiiMatch(match.Index, match.Length,
            Types.First(type => match.Groups[type].Success)))
            .Concat(detector?.Detect(text) ?? [])
            .OrderBy(match => match.Start).ThenByDescending(match => match.Length).ToArray();
        foreach (var match in matches)
        {
            if (match.Start < 0 || match.Length <= 0 || match.Start > text.Length - match.Length ||
                string.IsNullOrEmpty(match.Type) || match.Type.Any(c => c is not (>= 'A' and <= 'Z') and not '_'))
                throw new InvalidOperationException("PII detector returned an invalid span or type.");
        }
        // Merge overlaps so a second detection cannot leave a sensitive suffix exposed.
        var spans = new List<PiiMatch>();
        foreach (var match in matches)
        {
            if (spans.Count > 0 && match.Start < spans[^1].Start + spans[^1].Length)
            {
                var previous = spans[^1];
                spans[^1] = previous with { Length = Math.Max(previous.Start + previous.Length,
                    match.Start + match.Length) - previous.Start };
            }
            else spans.Add(match);
        }
        var result = new System.Text.StringBuilder();
        var position = 0;
        foreach (var match in spans)
        {
            result.Append(text, position, match.Start - position);
            var value = text.Substring(match.Start, match.Length);
            if (!_values.TryGetValue(value, out var token))
            {
                token = $"[[{match.Type}_{_scope}_{_tokens.Count}]]";
                _tokens.Add(token, value);
                _values.Add(value, token);
            }
            result.Append(token);
            position = match.Start + match.Length;
        }
        result.Append(text, position, text.Length - position);
        return result.ToString();
    }

    public void Dispose()
    {
        _disposed = true;
        _tokens.Clear();
        _values.Clear();
    }
    public string Restore(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Single pass: substituted values are never interpreted as additional tokens.
        return Regex.Replace(text, @"\[\[[A-Z_]+_[a-f0-9]{32}_\d+\]\]",
            match => _tokens.GetValueOrDefault(match.Value, match.Value),
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
    }
}
