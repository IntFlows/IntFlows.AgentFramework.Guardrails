using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace IntFlows.AgentFramework.Guardrails;

/// <summary>Guards each model request, including requests following tool execution.</summary>
public sealed class GuardrailChatClient : DelegatingChatClient
{
    private static readonly Regex Injection = new(
        @"\b(?:ignore|disregard|forget)\s+(?:(?:all|the|your|previous|earlier|prior)\s+)*instructions\b|" +
        @"\b(?:reveal|print|show|expose)\s+(?:(?:the|your)\s+)?(?:system prompt|developer message|secrets)\b|" +
        @"\b(?:export|give me|reveal)\s+(?:the\s+)?api\s*key\b|" +
        @"\b(?:bypass|disable|override)\s+(?:the\s+)?(?:security|safety|safeguards)\b|" +
        @"\b(?:you are (?:now )?a hacker|do anything now|dan mode)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
    private readonly GuardOptions _options;

    public GuardrailChatClient(IChatClient innerClient, GuardOptions? options = null) : base(innerClient)
    {
        _options = options ?? new();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaxInputCharacters);
    }

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        cancellationToken.ThrowIfCancellationRequested();
        var vault = new PiiVault();
        var length = 0;
        string Prepare(string text)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (text.Length > _options.MaxInputCharacters - length)
                throw new GuardrailException(GuardBlockReason.InputTooLarge);
            length += text.Length;
            return _options.MaskPii ? vault.Mask(text) : text;
        }

        var input = messages.ToList();
        var prepared = TransformMessages(input, Prepare, rejectUnsupported: true);
        // Inspect the whole non-system history so a trailing message cannot hide an attack.
        if (_options.DetectPromptInjection)
        {
            foreach (var message in input.Where(m => m.Role != ChatRole.System && m.Role != new ChatRole("developer")))
            {
                var segments = new List<string>();
                TransformMessages([message], text =>
                {
                    segments.Add(text);
                    return text;
                }, rejectUnsupported: true);
                if (Injection.IsMatch(string.Join("\n", segments)))
                    throw new GuardrailException(GuardBlockReason.PromptInjection);
            }
        }

        var latestUser = prepared.LastOrDefault(m => m.Role == ChatRole.User);
        if (_options.IntentValidator is { } validator && latestUser is not null &&
            !await validator.IsAllowedAsync(latestUser.Text, cancellationToken).ConfigureAwait(false))
            throw new GuardrailException(GuardBlockReason.DisallowedIntent);

        // Clone options as well; callers may reuse them across concurrent requests.
        var response = await InnerClient.GetResponseAsync(prepared, options?.Clone(), cancellationToken).ConfigureAwait(false);
        if (!_options.MaskPii || !_options.RestorePii) return response;
        return new ChatResponse(TransformMessages(response.Messages, vault.Restore, rejectUnsupported: false))
        {
            ResponseId = response.ResponseId,
            ConversationId = response.ConversationId,
            ModelId = response.ModelId,
            CreatedAt = response.CreatedAt,
            FinishReason = response.FinishReason,
            Usage = response.Usage,
            AdditionalProperties = response.AdditionalProperties?.Clone()
        };
    }

    // V1 deliberately buffers: placeholders split across provider chunks are restored atomically.
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (var update in response.ToChatResponseUpdates())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
        }
    }

    private static List<ChatMessage> TransformMessages(IEnumerable<ChatMessage> messages,
        Func<string, string> transform, bool rejectUnsupported)
    {
        return messages.Select(message =>
        {
            var copy = message.Clone();
            copy.Contents = message.Contents.Select<AIContent, AIContent>(content => content switch
            {
                TextContent text => new TextContent(transform(text.Text)),
                FunctionCallContent call => new FunctionCallContent(call.CallId, call.Name,
                    call.Arguments?.ToDictionary(pair => pair.Key, pair => TransformValue(pair.Value, transform))),
                FunctionResultContent result => new FunctionResultContent(result.CallId, TransformValue(result.Result, transform)),
                _ when rejectUnsupported => throw new GuardrailException(GuardBlockReason.UnsupportedContent),
                _ => content
            }).ToList();
            // Raw provider payloads may contain the original unmasked text.
            copy.RawRepresentation = null;
            copy.AdditionalProperties = null;
            return copy;
        }).ToList();
    }

    private static object? TransformValue(object? value, Func<string, string> transform)
    {
        if (value is null) return null;
        if (value is string text) return transform(text);
        var node = JsonSerializer.SerializeToNode(value);
        JsonNode? Visit(JsonNode? current)
        {
            if (current is JsonValue scalar && scalar.TryGetValue<string>(out var s))
                return JsonValue.Create(transform(s));
            if (current is JsonObject obj)
            {
                foreach (var key in obj.Select(pair => pair.Key).ToArray()) obj[key] = Visit(obj[key]);
            }
            else if (current is JsonArray array)
            {
                for (var i = 0; i < array.Count; i++) array[i] = Visit(array[i]);
            }
            return current;
        }
        return JsonSerializer.SerializeToElement(Visit(node));
    }
}

public static class GuardrailExtensions
{
    public static ChatClientBuilder UseGuardrails(this ChatClientBuilder builder, GuardOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Use(inner => new GuardrailChatClient(inner, options));
    }
}
