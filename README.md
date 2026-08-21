# Hermes Agent — .NET Edition (Vibe Coded) ☤

A complete **.NET 9** implementation of [NousResearch/hermes-agent](https://github.com/NousResearch/hermes-agent) — the self-improving AI agent with persistent memory, skills, and a CLI.

## Architecture

```
HermesAgent/
├── src/
│   ├── HermesAgent.Core/        # Models, abstractions, config (zero deps)
│   ├── HermesAgent.Agent/       # Agent loop, LLM providers, context compression
│   ├── HermesAgent.Tools/       # 35+ tools across 5 toolsets
│   ├── HermesAgent.Skills/      # Skills manager + skill tools
│   ├── HermesAgent.Memory/      # SQLite FTS5 memory + session manager
│   └── HermesAgent.Cli/         # Interactive CLI (Spectre.Console TUI)
├── skills/                      # Bundled skill library
│   ├── github/                  # PR workflow, code review
│   ├── autonomous-ai-agents/    # Claude Code, delegation patterns
│   ├── data-science/            # Jupyter, pandas workflows
│   ├── creative/                # Excalidraw diagramming
│   ├── media/                   # YouTube content extraction
│   ├── devops/                  # Webhooks, CI/CD
│   └── leisure/                 # find-nearby (OpenStreetMap)
└── tests/
    └── HermesAgent.Core.Tests/  # Unit + integration tests
```

## Tools (35 total)

|Toolset|Tools|
|-|-|
|**File**|`run\_command`, `read\_file`, `write\_file`, `patch`, `list\_directory`, `search\_files`|
|**Web**|`web\_search`, `web\_extract`, `web\_fetch`|
|**Browser**|`browser\_navigate`, `browser\_snapshot`, `browser\_click`, `browser\_type`, `browser\_press`, `browser\_scroll`, `browser\_back`, `browser\_console`, `browser\_get\_images`, `browser\_vision`, `browser\_dialog`, `browser\_cdp`|
|**Memory**|`save\_memory`, `recall\_memory`, `search\_memory`|
|**Skills**|`create\_skill`, `improve\_skill`, `list\_skills`, `read\_skill`|
|**Agent**|`clarify`, `todo`, `cronjob`, `delegate\_task`, `execute\_code`, `session\_search`, `process`|
|**Media**|`image\_generate`, `text\_to\_speech`, `vision\_analyze`|
|**Comms**|`send\_message`, `mixture\_of\_agents`|

## Quick Start

```bash
git clone <this-repo> \&\& cd HermesAgent
cp .env.example .env \&\& nano .env   # set HERMES\_API\_KEY

# CLI
dotnet run --project src/HermesAgent.Cli

# Verbose debug tracing (DI registration, tool resolution, tool execution timing)
# When hunting a hang: the LAST [DBG] line before the freeze is the culprit —
# a "START" with no matching "OK" means that step never finished.
HERMES_DEBUG=1 dotnet run --project src/HermesAgent.Cli
```

## Configuration

Priority order (highest wins):

|Priority|Source|
|-|-|
|1|Command-line args|
|2|Environment variables (`HERMES\_Llm\_\_ApiKey=...`)|
|3|`./app.config` (XML, CWD)|
|4|`\~/.hermes/app.config` (XML, user-level)|
|5|`\~/.hermes/config.json`|
|6|`appsettings.json`|

### app.config (3 supported formats)

**Format 1 — colon keys:**

```xml
<appSettings>
  <add key="Hermes:Llm:Provider" value="openrouter" />
  <add key="Hermes:Llm:ApiKey"   value="sk-or-..." />
</appSettings>
```

**Format 2 — dot notation (auto-normalized):**

```xml
<appSettings>
  <add key="hermes.llm.provider" value="nous" />
  <add key="hermes.agent.maxTurns" value="30" />
</appSettings>
```

**Format 3 — structured XML:**

```xml
<hermesSettings>
  <llm provider="openai" model="gpt-4o" apiKey="sk-..." />
  <agent maxTurns="50" enableSkillNudging="true" />
  <memory enabled="true" />
  <skills enabled="true" autoCreateSkills="true" />
</hermesSettings>
```

## Supported LLM Providers

|Name|Provider value|Notes|
|-|-|-|
|OpenAI|`openai`|GPT-4o, o1, o3|
|OpenRouter|`openrouter`|200+ models, single key|
|Nous Portal|`nous`|Hermes-3 models|
|NVIDIA NIM|`nvidia`|Llama, Mistral, etc.|
|Anthropic|`anthropic`|Claude family|
|Local (Ollama)|`custom` + BaseUrl|Any OpenAI-compat endpoint|

## Running Tests

```bash
dotnet test
```

## Adding a Custom Tool

```csharp
public sealed class MyTool : ToolBase
{
    public override string Name        => "my\_tool";
    public override string Description => "Does something useful.";
    public override ToolDefinition Definition => new()
    {
        Name = Name, Description = Description,
        Parameters = new Dictionary<string, ParameterDefinition>
        {
            \["input"] = new() { Type = "string", Description = "Input value" }
        },
        Required = \["input"]
    };

    protected override async Task<string> ExecuteCoreAsync(ToolCall call, CancellationToken ct)
        => $"Result: {GetArg(call, "input")}";
}
```

Register in `ServiceRegistration.cs` and add to the `IEnumerable<ITool>` list.

## License

MIT — inspired by [Nous Research](https://nousresearch.com)'s Hermes Agent.

