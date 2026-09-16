using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace IntFlows.AgentFramework.Guardrails;

public static class ToolGuardMiddleware
{
    /// <summary>Adds allow, block, redact, and application-approval policies to agent tool calls.</summary>
    public static AIAgentBuilder UseGuardrailTools(this AIAgentBuilder builder, ToolGuardOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);

        return builder.Use(async (agent, context, next, cancellationToken) =>
        {
            var started = Stopwatch.GetTimestamp();
            var name = context.Function.Name;
            var action = options.Rules.TryGetValue(name, out var configured)
                ? configured : options.DefaultAction;

            async ValueTask<GuardDecision> Decide(GuardAction finalAction, GuardReasonCode reason)
            {
                var decision = new GuardDecision("1", Guid.NewGuid(), DateTimeOffset.UtcNow,
                    GuardKind.Tool, options.PolicyVersion, finalAction, reason,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                if (options.OnDecision is not null)
                    await options.OnDecision(decision, cancellationToken).ConfigureAwait(false);
                return decision;
            }

            async ValueTask<object?> Stop(GuardAction finalAction, GuardReasonCode reason)
            {
                context.Terminate = true;
                var decision = await Decide(finalAction, reason);
                return new GuardToolBlockedResult(decision.DecisionId, decision.ReasonCode);
            }

            if (action == GuardAction.Block)
                return await Stop(GuardAction.Block, GuardReasonCode.ToolBlocked);

            if (action == GuardAction.ApprovalRequired)
            {
                if (options.ApproveAsync is null)
                    return await Stop(GuardAction.ApprovalRequired, GuardReasonCode.ToolApprovalRequired);

                bool approved;
                try
                {
                    var snapshot = context.Arguments.ToDictionary(pair => pair.Key,
                        pair => CloneValue(pair.Value), StringComparer.Ordinal);
                    approved = await options.ApproveAsync(
                        new ToolPolicyContext(name, snapshot, agent), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch
                {
                    return await Stop(GuardAction.Block, GuardReasonCode.ToolPolicyError);
                }

                if (!approved)
                    return await Stop(GuardAction.Block, GuardReasonCode.ToolApprovalDenied);
            }

            if (action == GuardAction.Redact)
            {
                foreach (var key in context.Arguments.Keys.ToArray())
                    context.Arguments[key] = TransformValue(context.Arguments[key], text => PiiVault.Redact(text));
            }

            var reason = action switch
            {
                GuardAction.Redact => GuardReasonCode.ToolRedacted,
                GuardAction.ApprovalRequired => GuardReasonCode.ToolApproved,
                _ => GuardReasonCode.ToolAllowed
            };
            await Decide(action == GuardAction.ApprovalRequired ? GuardAction.Allow : action, reason);
            return await next(context, cancellationToken).ConfigureAwait(false);
        });
    }

    private static void Validate(ToolGuardOptions options)
    {
        if (options.DefaultAction is not (GuardAction.Allow or GuardAction.Block or
            GuardAction.Redact or GuardAction.ApprovalRequired))
            throw new ArgumentOutOfRangeException(nameof(options));
        if (options.Rules.Any(rule => string.IsNullOrWhiteSpace(rule.Key) ||
            rule.Value is not (GuardAction.Allow or GuardAction.Block or GuardAction.Redact or GuardAction.ApprovalRequired)))
            throw new ArgumentException("Tool rules must have a non-empty exact name and valid action.", nameof(options));
    }

    private static object? CloneValue(object? value) => TransformValue(value, static text => text);

    private static object? TransformValue(object? value, Func<string, string> transform)
    {
        if (value is null) return null;
        if (value is string text) return transform(text);
        var node = JsonSerializer.SerializeToNode(value);
        JsonNode? Visit(JsonNode? current)
        {
            if (current is JsonValue scalar && scalar.TryGetValue<string>(out var text))
                return JsonValue.Create(transform(text));
            if (current is JsonObject obj)
                foreach (var key in obj.Select(pair => pair.Key).ToArray()) obj[key] = Visit(obj[key]);
            else if (current is JsonArray array)
                for (var index = 0; index < array.Count; index++) array[index] = Visit(array[index]);
            return current;
        }
        return JsonSerializer.SerializeToElement(Visit(node));
    }
}
