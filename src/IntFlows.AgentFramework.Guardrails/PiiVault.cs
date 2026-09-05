using System.Text.RegularExpressions;

namespace IntFlows.AgentFramework.Guardrails;

// One vault per model invocation; never stored on the shared middleware instance.
internal sealed class PiiVault
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

    public string Mask(string text) => Detector.Replace(text, match =>
    {
        if (_values.TryGetValue(match.Value, out var existing)) return existing;
        var type = Types.First(type => match.Groups[type].Success);
        var token = $"[[{type}_{_scope}_{_tokens.Count}]]";
        _tokens.Add(token, match.Value);
        _values.Add(match.Value, token);
        return token;
    });

    public string Restore(string text)
    {
        // Single pass: substituted values are never interpreted as additional tokens.
        return Regex.Replace(text, @"\[\[[A-Z_]+_[a-f0-9]{32}_\d+\]\]",
            match => _tokens.GetValueOrDefault(match.Value, match.Value),
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
    }
}
