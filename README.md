# IntFlows Guardrails for Microsoft Agent Framework

[![NuGet](https://img.shields.io/nuget/vpre/IntFlows.AgentFramework.Guardrails.svg)](https://www.nuget.org/packages/IntFlows.AgentFramework.Guardrails)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](https://www.apache.org/licenses/LICENSE-2.0)

Lightweight guardrail middleware for C# agents built with Microsoft Agent Framework.
It intercepts calls at the `IChatClient` boundary, blocks common prompt-injection
attempts, masks personally identifiable information before it reaches the model,
and optionally restricts requests to allowed intents.

> **Preview:** `0.1.0-preview.1` contains the initial in-memory implementation.
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
- Typed block reasons through `GuardrailException`
- Normal and buffered streaming Agent Framework calls

Redis and other persistent vault providers are outside the preview scope.

## Requirements

- .NET 10 or later
- Microsoft Agent Framework 1.20.0 or a compatible release
- An `IChatClient` supplied by your model provider

## Installation

```bash
dotnet add package IntFlows.AgentFramework.Guardrails --version 0.1.0-preview.1
```

Or add a package reference:

```xml
<PackageReference Include="IntFlows.AgentFramework.Guardrails" Version="0.1.0-preview.1" />
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

Blocked calls throw `GuardrailException` without including the original input.
Reasons are `PromptInjection`, `DisallowedIntent`, `InputTooLarge`, and
`UnsupportedContent`. Blocked input never reaches the model.

## Tool calls

Place the guard around the provider client passed to `ChatClientAgent`. The agent
owns the function-invocation loop while the guard sees each model call:

```text
user input -> mask -> model -> restore tool arguments -> tool
tool result -> mask -> model -> restore final response -> application
```

Supported content consists of text, function calls, and JSON-serializable function
results. Images, audio, files, and other content types currently fail closed.

## Run the example

The repository includes an offline `ChatClientAgent` example using a fake model.
It requires no credentials, model download, Redis instance, or cloud subscription:

```bash
dotnet build
dotnet run --project examples/Guardrails.Example
dotnet run --project examples/Guardrails.Example -- "Azure workflow for jane@example.com"
```

Run the executable behavioral checks with:

```bash
dotnet run --project tests/Guardrails.Tests
```

The check process exits with a nonzero code on failure.

## Preview limitations

- Prompt-injection and PII checks use regular expressions. They can miss harmful
  or sensitive input and can reject benign input.
- Numeric PII matches are candidates and are not checksum-validated. Names,
  addresses, and model-based PII classification are not included yet.
- JSON string values are transformed. Field names, tool names and schemas,
  provider options, and opaque provider state are not scanned.
- System and developer messages are masked but exempt from injection checks. The
  latest user message is checked by the configured intent policy.
- Streaming calls are buffered so placeholders can be restored atomically.
- Raw provider payloads and extension metadata are omitted from transformed
  messages so unscanned copies of the input are not forwarded.
- Each vault lasts for one model invocation. There is no persistence, Redis,
  cross-process recovery, audit store, or cryptographic erasure.
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
