# IntFlows Guardrails for Microsoft Agent Framework

[![NuGet](https://img.shields.io/nuget/vpre/IntFlows.AgentFramework.Guardrails.svg)](https://www.nuget.org/packages/IntFlows.AgentFramework.Guardrails)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](https://www.apache.org/licenses/LICENSE-2.0)

Lightweight guardrail middleware for C# agents built with Microsoft Agent Framework.
It intercepts calls at the `IChatClient` boundary, blocks common prompt-injection
attempts, masks personally identifiable information before it reaches the model,
and optionally restricts requests to allowed intents.

> **Preview:** `0.1.0-preview.3` adds pluggable PII detection and vaults to the in-memory implementation.
> Its public API and behavior may change before version 1.0.

## Features

- Prompt-injection detection before model invocation
- Reversible PII masking for email addresses, Australian phone numbers, and
  candidate TFN, ABN, Medicare and payment-card numbers
- PII restoration in responses and structured tool arguments
- PII masking of tool results before the next model call
- Isolated, per-model-call in-memory token vaults
- Custom intent policies through `IIntentValidator`
- Semantic intent matching through `SemanticIntentValidator`
- Exact-name tool allow/block policies checked before tool dispatch
- Typed block reasons through `GuardrailException`
- Tool allow, block, PII-redact, and application-approval policies
- Versioned, content-free decision events for audit integrations
- Normal and buffered streaming Agent Framework calls

Custom vault providers can be supplied through `IPiiVault`. No Redis adapter ships with the package.

## Requirements

- .NET 10 or later
- Microsoft Agent Framework 1.20.0 or a compatible release
- An `IChatClient` supplied by your model provider

## Installation

```bash
dotnet add package IntFlows.AgentFramework.Guardrails --version 0.1.0-preview.3
```

Or add a package reference:

```xml
<PackageReference Include="IntFlows.AgentFramework.Guardrails" Version="0.1.0-preview.3" />
```

## Quick start

Wrap the provider's `IChatClient` before constructing `ChatClientAgent`:

```csharp
using IntFlows.AgentFramework.Guardrails;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

// Create this with Azure OpenAI, OpenAI, Ollama, or another provider.
IChatClient modelClient = CreateYourChatClient();

using var guardedClient = modelClient
    .AsBuilder()
    .UseGuardrails(new GuardOptions
    {
        DetectPromptInjection = true,
        MaskPii = true,
        RestorePii = true,
        MaxInputCharacters = 100_000
    })
    .Build();

var agent = new ChatClientAgent(
    guardedClient,
    instructions: "Help users build integration workflows.");

try
{
    var response = await agent.RunAsync(
        "Email jane@example.com about the Azure Blob workflow.");

    Console.WriteLine(response.Text);
}
catch (GuardrailException exception)
{
    Console.WriteLine($"Request blocked: {exception.Reason}");
}
```

The model receives an opaque value similar to:

```text
Email [[EMAIL_991d40d7d2f34f5ba6200650d31fb929_0]] about the Azure Blob workflow.
```

The application receives the restored response containing `jane@example.com`.
The original caller-owned messages are not mutated.

## Intent validation

Intent checking is disabled until an `IIntentValidator` is configured. Use the
built-in semantic validator with any compatible embedding generator:

```csharp
var guardOptions = new GuardOptions
{
    IntentValidator = new SemanticIntentValidator(
        embeddingGenerator,
        allowedIntentDescriptions:
        [
            "Integrate Azure Blob Storage, APIs, and business workflows",
            "Diagnose failures in an existing integration"
        ],
        threshold: 0.7)
};
```

It embeds the latest masked user message and the allowed descriptions, then uses
cosine similarity. Tune the threshold against examples from your application.
The embedding generator can be local or remote and remains owned by the caller.

For deterministic business rules, implement the policy interface:

```csharp
public sealed class SupportIntentValidator : IIntentValidator
{
    public ValueTask<bool> IsAllowedAsync(
        string input,
        CancellationToken cancellationToken = default)
    {
        var allowed = input.Contains("support", StringComparison.OrdinalIgnoreCase);
        return ValueTask.FromResult(allowed);
    }
}
```

## Configuration

| Option | Default | Purpose |
| --- | ---: | --- |
| `DetectPromptInjection` | `true` | Reject common attempts to override instructions or expose secrets. |
| `MaskPii` | `true` | Replace detected PII before every model request. |
| `RestorePii` | `true` | Restore request PII in responses and tool arguments. |
| `MaxInputCharacters` | `100000` | Reject requests whose scanned text exceeds this budget. |
| `IntentValidator` | `null` | Optional application-specific or semantic intent policy. |
| `PiiDetector` | `null` | Optional detector augmenting built-in PII matching. |
| `PiiVaultFactory` | `null` | Creates an owned PII vault per model invocation. |
| `Tools` | `null` | Optional exact-name tool policy; configured policies default to blocking unlisted tools. |

Blocked calls throw `GuardrailException` without including the original input.
Reasons are `PromptInjection`, `DisallowedIntent`, `InputTooLarge`,
`UnsupportedContent`, and `DisallowedTool`. Input blocks occur before model invocation;
tool-response blocks occur after the model returns and before tool dispatch.

## Model-response tool policies

Configure tool rules before passing the guarded client to your agent:

```csharp
var options = new GuardOptions
{
    Tools = new ToolPolicy
    {
        DefaultAction = ToolAction.Block,
        Rules = new Dictionary<string, ToolAction>
        {
            ["searchDocs"] = ToolAction.Allow,
            ["deleteTicket"] = ToolAction.Block
        }
    }
};
```

Names are exact and case-sensitive. With `Tools = null`, tool calls remain allowed.
Rules are copied when the client is constructed. Every call in a model response
must be allowed before the response is returned, so a blocked call rejects the
whole batch with `DisallowedTool`, including buffered streaming responses. This
check runs even when PII masking or restoration is disabled. The model has already
run when a tool response is blocked; no calls in that response reach the agent.
These rules govern calls returned through this client. Direct tool execution and
application authorization remain the application's responsibility. Use
`UseGuardrailTools` below for approval and PII-redaction policies at execution.

## Tool calls

Place the guard around the provider client passed to `ChatClientAgent`. The agent
owns the function-invocation loop while the guard sees each model call:

```text
user input -> mask -> model -> restore tool arguments -> tool
tool result -> mask -> model -> restore final response -> application
```

Supported content consists of text, function calls, and JSON-serializable function
results. Images, audio, files, and other content types currently fail closed.

### Tool execution policies

Add function middleware to the agent builder using exact function names:

```csharp
var guardedAgent = new ChatClientAgent(
        guardedClient,
        instructions: "Help with support requests.",
        tools: [searchDocs, updateTicket, deleteTicket])
    .AsBuilder()
    .UseGuardrailTools(new ToolGuardOptions
    {
        PolicyVersion = "support-tools-v1",
        DefaultAction = GuardAction.Block,
        Rules = new Dictionary<string, GuardAction>
        {
            ["searchDocs"] = GuardAction.Allow,
            ["updateTicket"] = GuardAction.Redact,
            ["deleteTicket"] = GuardAction.ApprovalRequired
        },
        ApproveAsync = async (call, cancellationToken) =>
        {
            // Ask a trusted, application-owned approval service.
            return await approvals.IsApprovedAsync(
                call.ToolName, call.Arguments, cancellationToken);
        }
    })
    .Build();
```

| Action | Behavior |
| --- | --- |
| `Allow` | Execute the tool with its arguments unchanged. |
| `Block` | Stop before executing the tool. |
| `Redact` | Replace detected PII in nested string arguments with `[REDACTED]`, then execute. |
| `ApprovalRequired` | Execute only when `ApproveAsync` returns `true`. |

`DefaultAction = GuardAction.Block` creates an allowlist. With the default `Allow`,
unlisted tools continue to run. Approval belongs to trusted application code; do
not derive approval from model output. Redaction is irreversible and may make an
argument invalid for its tool schema. Blocked and denied policies return a
`GuardToolBlockedResult` and set the Agent Framework function loop to terminate.
This prevents the tool from running and avoids sending its failure through another
model call. Inspect the emitted decision to handle the policy outcome in application code.

## Pluggable PII detection and vaults

Implement `IPiiDetector` to return spans in the original text. Spans use UTF-16
character offsets and positive lengths; types contain uppercase letters and
underscores, such as `NAME`. Custom detections augment the built-in regex matches.
Overlapping spans are merged before masking. Invalid spans fail before the model
is called. Shared detectors must be thread-safe; detector lifetime belongs to the caller.

```csharp
public sealed class CustomerNameDetector : IPiiDetector
{
    public IEnumerable<PiiMatch> Detect(string text)
    {
        var start = text.IndexOf("Jane Smith", StringComparison.Ordinal);
        if (start >= 0) yield return new PiiMatch(start, "Jane Smith".Length, "NAME");
    }
}
```

Pass the same detector to `GuardOptions.PiiDetector` for model masking and
`ToolGuardOptions.PiiDetector` for tool redaction. Model-backed detectors can
implement this interface, but no classifier, model download, or inference runtime
is bundled. Detection is synchronous in this preview.

The default public `PiiVault` implements `IPiiVault` and can also be used directly:

```csharp
var detector = new CustomerNameDetector();
var options = new GuardOptions { PiiDetector = detector };
using var vault = new PiiVault(detector);
var masked = vault.Mask("Jane Smith: jane@example.com");
var restored = vault.Restore(masked);
```

For custom storage, set `GuardOptions.PiiVaultFactory` to a factory returning a
fresh `IPiiVault` for every model invocation. A vault owns detection, masking,
restoration, and its distinct token namespace. Do not configure `PiiDetector`
alongside the factory: compose detection inside the custom vault instead. The
factory must return a non-null, exclusively owned scope; shared vault instances
are unsupported. The guard disposes the scope after restoration, including blocked
requests and model failures. No vault is created with `MaskPii = false`.

A custom adapter controls storage retention, expiry, and cleanup in `Dispose`.
It must never restore tokens belonging to another scope. These interfaces do not
provide a Redis implementation or cross-call persistence automatically. The built-in
vault clears its dictionaries on disposal; this is not cryptographic erasure.

## Decision events

Both model guardrails and tool policies can emit audit decisions. Events contain
identifiers, timing, policy version, action, and reason only. They never include
prompt text, PII values, or tool arguments.

```csharp
var modelOptions = new GuardOptions
{
    PolicyVersion = "support-model-v1",
    OnDecision = async (decision, cancellationToken) =>
        await auditStore.WriteAsync(decision, cancellationToken)
};

var toolOptions = new ToolGuardOptions
{
    PolicyVersion = "support-tools-v1",
    OnDecision = async (decision, cancellationToken) =>
        await auditStore.WriteAsync(decision, cancellationToken)
};
```

`GuardDecision.SchemaVersion` is currently `"1"`. Callbacks are awaited so an
application can choose fail-closed auditing: callback failures stop processing.

## Run the example

The repository includes an offline `ChatClientAgent` example using a fake model.
It requires no credentials, model download, Redis instance, or cloud subscription:

```bash
dotnet build
dotnet run --project examples/Guardrails.Example
dotnet run --project examples/Guardrails.Example -- "Azure workflow for jane@example.com"
```

With no command-line prompt, the example executes seven deterministic scenarios:

1. PII masking and response restoration without a tool call
2. An allowed lookup tool receiving restored PII
3. A redacted tool receiving `[REDACTED]`
4. A side-effect tool denied by the application approval callback
5. An unlisted tool stopped by the default-block policy
6. A prompt-injection request blocked before the model
7. An unrelated intent blocked before the model

The console shows every content-free audit decision, the masked text received by
the model, and the arguments received by tools. The scripted `IChatClient` controls
only model responses; `ChatClientAgent`, its function loop, `AIFunctionFactory`,
chat middleware, and function middleware are all the real Agent Framework APIs.

Run the executable behavioral checks with:

```bash
dotnet run --project tests/Guardrails.Tests
```

The check process exits with a nonzero code on failure.

## Preview limitations

- Prompt-injection and PII checks use regular expressions. They can miss harmful
  or sensitive input and can reject benign input.
- Numeric PII matches are candidates and are not checksum-validated. Names,
  addresses, and model-based PII classification require a custom detector.
- JSON string values are transformed. Field names, tool names and schemas,
  provider options, and opaque provider state are not scanned.
- System and developer messages are masked but exempt from injection checks. The
  latest user message is checked by the configured intent policy.
- Streaming calls are buffered so placeholders can be restored atomically.
- Raw provider payloads and extension metadata are omitted from transformed
  messages so unscanned copies of the input are not forwarded.
- Default vaults last for one model invocation. External persistence requires a
  custom adapter. No Redis adapter, audit store, or cryptographic erasure is bundled.
- Restored PII is visible to application code, tools, logs, and agent history.
  Set `RestorePii = false` when downstream code should receive opaque tokens.

Combine this library with provider safety controls, tool authorization,
least-privilege credentials, output validation, secure logging, and tests for your
application's threat model.

## Contributing

Issues and pull requests are welcome. Validate changes with:

```bash
dotnet build
dotnet run --project tests/Guardrails.Tests
```

## License

Licensed under the [Apache License 2.0](https://www.apache.org/licenses/LICENSE-2.0).

See Microsoft's [Agent Framework middleware documentation](https://learn.microsoft.com/en-us/agent-framework/concepts/agents/middleware/)
for more information about middleware composition.
