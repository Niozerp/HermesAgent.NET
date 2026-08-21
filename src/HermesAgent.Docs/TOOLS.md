# Hermes Agent Tool Reference 🛠️

Hermes includes over 35 built-in tools. Tools are categorized into "Toolsets".

## File System (`HermesAgent.Tools`)
- `run_command`: Execute shell commands (bash/cmd).
- `read_file`: Read text from disk.
- `write_file`: Write or append text.
- `patch`: Perform precise search-and-replace using fuzzy matching.
- `list_directory`: List files and metadata.
- `search_files`: Recursive content search (grep style).

## Web & Information Retrieval
- `web_search`: Search the internet (defaults to DuckDuckGo Lite).
- `web_extract`: Extract main content/text from any URL or PDF.
- `web_fetch`: Raw HTTP GET for data fetching.

## Headless Browser (Playwright)
*Requires Playwright setup.*
- `browser_navigate`: Open a URL.
- `browser_snapshot`: Get a text-based accessibility tree.
- `browser_click`: Click element by ref ID.
- `browser_type`: Enter text into inputs.
- `browser_press`: Press specific keys (Enter, Escape).
- `browser_scroll`: Navigate long pages.
- `browser_vision`: Analyze a specific part of the viewport with Vision AI.

## Memory & Skills
- `save_memory`: Explicitly persist a fact to long-term memory.
- `recall_memory`: View all explicit memories.
- `search_memory`: Semantic/Keyword search across history.
- `create_skill`: Save a successful complex workflow as a reusable skill.
- `list_skills`: See available capabilities.

## Advanced Agent Tools
- `cronjob`: Schedule recurring tasks.
- `delegate_task`: Spawn sub-agents to solve sub-problems in parallel.
- `execute_code`: Run Python scripts for complex data processing.
- `session_search`: Search through conversation logs.
- `mixture_of_agents`: Solve extremely hard problems by querying multiple high-frontier models and aggregating results.

## Media & Vision
- `image_generate`: Generate images from prompts.
- `vision_analyze`: High-level description of images or base64 data.
- `text_to_speech`: Convert responses to audio files.

## Error Handling & Safety

All tools inherit from `ToolBase`, which provides a standardized execution wrapper:

- **Null-safety guards**: Validates `ToolCall`, `ToolCall.Name`, and `ToolCall.Arguments` before execution. Missing or malformed calls return a descriptive error result instead of throwing.
- **Cancellation propagation**: `OperationCanceledException` is caught and reported as a timeout/cancellation error, never silently swallowed.
- **Exception unwrapping**: `AggregateException` inner messages are flattened into the error string for clearer diagnostics.
- **Null output protection**: If a tool returns `null`, it is normalized to `string.Empty` to prevent downstream null-reference issues.

### `run_command` (ShellTool) — Process Safety

| Feature | Behavior |
|--------|----------|
| Empty command | Returns `"Error: command parameter is required and cannot be empty."` |
| Timeout clamp | `timeout_seconds` is clamped to `[1, 600]`. Default is 30s. |
| Process start failure | Caught and returned as a descriptive error (e.g., executable not found). |
| Timeout / Cancellation | Kills the entire process tree (`Kill(entireProcessTree: true)`), waits up to 5s for termination, and returns any partial output captured before the timeout. |
| Thread-safe output | stdout/stderr are appended under a lock to prevent interleaved corruption. |
| Exit reporting | If the process exits with no output, returns `[exit code N]`. |

### `process` (ProcessTool) — Background Process Registry

| Action | Safety |
|--------|--------|
| `list` | Handles `InvalidOperationException` from stale process handles. |
| `kill` | Validates `id`, checks `HasExited` before kill, disposes the `Process` object, and reports partial failures. |
| `poll` | Validates `id`, catches `InvalidOperationException` for invalid handles. |
| `wait` | Validates `id`, clamps timeout to positive value, respects caller `CancellationToken`, reports timeout distinctly from exit. |
| `write` | Validates `id`, checks `HasExited`, verifies `RedirectStandardInput`, catches `IOException` on broken pipes. |
| Unknown action | Returns explicit error listing valid actions. |

> **Note**: The `log` action is currently a stub that reports process status; full stdout/stderr log capture requires `TerminalTool` integration.

---

## Tool Signature Example: `patch`
| Parameter | Type | Required | Description |
|---|---|---|---|
| `path` | string | Yes | Path to the file to edit. |
| `old_content` | string | Yes | The exact text to find. |
| `new_content` | string | Yes | The replacement text. |
