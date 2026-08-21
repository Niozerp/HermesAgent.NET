using System.Collections.Concurrent;
using System.Text.Json;
using HermesAgent.Core.Abstractions;
using HermesAgent.Core.Models;
using Microsoft.Extensions.Logging;

namespace HermesAgent.Tools.Toolsets;

// ═══════════════════════════════════════════════════════════════════════════
// CRONJOB TOOLSET  (1 tool)
// ═══════════════════════════════════════════════════════════════════════════

public sealed record CronJob(
    string Id, string Schedule, string Task, string? Skill,
    bool Paused, DateTimeOffset CreatedAt, DateTimeOffset? LastRun);

/// <summary>Unified scheduled-task manager — create, list, update, pause, resume, run, remove.</summary>
public sealed class CronJobTool : ToolBase
{
    private static readonly ConcurrentDictionary<string, CronJob> _jobs = new();
    private readonly string _storeFile;

    public CronJobTool()
    {
        _storeFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".hermes", "cron.json");
        LoadFromDisk();
    }

    public override string Name => "cronjob";
    public override string Description =>
        "Unified scheduled-task manager. Actions: create, list, update, pause, resume, run, remove. " +
        "Supports skill-backed jobs. Cron runs happen in fresh sessions with no current-chat context.";

    public override ToolDefinition Definition => new()
    {
        Name = Name, Description = Description,
        Parameters = new Dictionary<string, ParameterDefinition>
        {
            ["action"]   = new() { Type = "string", Description = "create|list|update|pause|resume|run|remove", Enum = ["create","list","update","pause","resume","run","remove"] },
            ["id"]       = new() { Type = "string", Description = "Job ID (required for update/pause/resume/run/remove)" },
            ["schedule"] = new() { Type = "string", Description = "Cron expression or natural language (e.g. 'every day at 9am', '0 9 * * *')" },
            ["task"]     = new() { Type = "string", Description = "Task description to run on schedule" },
            ["skill"]    = new() { Type = "string", Description = "Optional skill name to attach to this job" }
        },
        Required = ["action"]
    };

    protected override Task<string> ExecuteCoreAsync(ToolCall call, CancellationToken ct)
    {
        return GetArgOrDefault(call, "action", "list") switch
        {
            "create"  => CreateJob(call),
            "list"    => ListJobs(),
            "update"  => UpdateJob(call),
            "pause"   => SetPaused(GetArg(call, "id"), true),
            "resume"  => SetPaused(GetArg(call, "id"), false),
            "run"     => RunJobNow(GetArg(call, "id")),
            "remove"  => RemoveJob(GetArg(call, "id")),
            var x     => Task.FromResult($"Unknown action '{x}'.")
        };
    }

    private Task<string> CreateJob(ToolCall call)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var job = new CronJob(
            Id: id,
            Schedule: GetArg(call, "schedule"),
            Task: GetArg(call, "task"),
            Skill: call.Arguments.TryGetValue("skill", out var s) ? s?.ToString() : null,
            Paused: false,
            CreatedAt: DateTimeOffset.UtcNow,
            LastRun: null);
        _jobs[id] = job;
        SaveToDisk();
        return Task.FromResult($"Created cron job #{id}: [{job.Schedule}] {job.Task}");
    }

    private Task<string> ListJobs()
    {
        if (_jobs.IsEmpty) return Task.FromResult("No cron jobs.");
        var lines = _jobs.Values.Select(j =>
            $"#{j.Id} [{(j.Paused ? "PAUSED" : "ACTIVE")}] {j.Schedule}\n  Task: {j.Task}" +
            (j.Skill != null ? $"\n  Skill: {j.Skill}" : "") +
            (j.LastRun.HasValue ? $"\n  Last run: {j.LastRun:yyyy-MM-dd HH:mm}" : ""));
        return Task.FromResult(string.Join("\n\n", lines));
    }

    private Task<string> UpdateJob(ToolCall call)
    {
        var id = GetArg(call, "id");
        if (!_jobs.TryGetValue(id, out var job)) return Task.FromResult($"Job #{id} not found.");
        var updated = job with
        {
            Schedule = call.Arguments.ContainsKey("schedule") ? GetArg(call, "schedule") : job.Schedule,
            Task = call.Arguments.ContainsKey("task") ? GetArg(call, "task") : job.Task,
            Skill = call.Arguments.ContainsKey("skill") ? GetArg(call, "skill") : job.Skill
        };
        _jobs[id] = updated;
        SaveToDisk();
        return Task.FromResult($"Job #{id} updated.");
    }

    private Task<string> SetPaused(string id, bool paused)
    {
        if (!_jobs.TryGetValue(id, out var job)) return Task.FromResult($"Job #{id} not found.");
        _jobs[id] = job with { Paused = paused };
        SaveToDisk();
        return Task.FromResult($"Job #{id} {(paused ? "paused" : "resumed")}.");
    }

    private Task<string> RunJobNow(string id)
    {
        if (!_jobs.TryGetValue(id, out var job)) return Task.FromResult($"Job #{id} not found.");
        _jobs[id] = job with { LastRun = DateTimeOffset.UtcNow };
        SaveToDisk();
        return Task.FromResult($"Job #{id} triggered manually: {job.Task}");
    }

    private Task<string> RemoveJob(string id)
    {
        var removed = _jobs.TryRemove(id, out _);
        SaveToDisk();
        return Task.FromResult(removed ? $"Job #{id} removed." : $"Job #{id} not found.");
    }

    private void LoadFromDisk()
    {
        if (!File.Exists(_storeFile)) return;
        try
        {
            var json = File.ReadAllText(_storeFile);
            var jobs = JsonSerializer.Deserialize<List<CronJob>>(json) ?? [];
            foreach (var j in jobs) _jobs[j.Id] = j;
        }
        catch { /* fresh start */ }
    }

    private void SaveToDisk()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_storeFile)!);
        File.WriteAllText(_storeFile, JsonSerializer.Serialize(_jobs.Values.ToList()));
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// DELEGATION TOOLSET  (1 tool)
// ═══════════════════════════════════════════════════════════════════════════

public sealed record SubagentTask(string Id, string Task, string? Skill, string Status, string? Result);

/// <summary>
/// Spawn one or more subagents to work on tasks in isolated contexts.
/// Each subagent gets its own conversation. Only the final summary is returned.
/// </summary>
public sealed class DelegateTaskTool : ToolBase
{
    private readonly Lazy<IAgent> _agent;
    private readonly ILogger<DelegateTaskTool> _log;

    // Lazy<IAgent> breaks the DI cycle: IEnumerable<ITool> -> DelegateTaskTool
    // -> IAgent (HermesAgentLoop) -> IEnumerable<ITool>. Resolving the agent is
    // deferred until the first actual delegation, when the tool list already exists.
    public DelegateTaskTool(Lazy<IAgent> agent, ILogger<DelegateTaskTool> log)
    {
        _agent = agent;
        _log = log;
    }

    public override string Name => "delegate_task";
    public override string Description =>
        "Spawn one or more subagents to work on tasks in isolated contexts. " +
        "Each subagent gets its own conversation and toolset. " +
        "Only the final summary is returned — intermediate tool results never enter your context window.";

    public override ToolDefinition Definition => new()
    {
        Name = Name, Description = Description,
        Parameters = new Dictionary<string, ParameterDefinition>
        {
            ["tasks"]           = new() { Type = "array",   Description = "List of task descriptions (strings) to delegate" },
            ["task"]            = new() { Type = "string",  Description = "Single task description (alternative to tasks array)" },
            ["skill"]           = new() { Type = "string",  Description = "Skill to load in the subagent (optional)" },
            ["parallel"]        = new() { Type = "boolean", Description = "Run tasks in parallel (default: true)" },
            ["timeout_seconds"] = new() { Type = "integer", Description = "Max seconds per subagent (default: 300)" }
        }
    };

    protected override async Task<string> ExecuteCoreAsync(ToolCall call, CancellationToken ct)
    {
        var timeout = int.TryParse(GetArgOrDefault(call, "timeout_seconds", "300"), out var t) ? t : 300;
        var parallel = !GetArgOrDefault(call, "parallel", "true").Equals("false", System.StringComparison.OrdinalIgnoreCase);

        // Collect tasks
        var tasks = new List<string>();
        if (call.Arguments.TryGetValue("tasks", out var tasksObj) && tasksObj is JsonElement je && je.ValueKind == JsonValueKind.Array)
            foreach (var item in je.EnumerateArray()) tasks.Add(item.GetString() ?? "");
        var singleTask = GetArgOrDefault(call, "task", "");
        if (!System.String.IsNullOrEmpty(singleTask)) tasks.Add(singleTask);

        if (tasks.Count == 0) return "Error: provide 'task' or 'tasks' parameter.";

        _log.LogInformation("Delegating {Count} task(s), parallel={Parallel}", tasks.Count, parallel);

        var results = new List<(string Task, string Result)>();

        if (parallel)
        {
            var subTasks = tasks.Select(async taskText =>
            {
                using var subCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                subCts.CancelAfter(TimeSpan.FromSeconds(timeout));
                try
                {
                    var result = await _agent.Value.RunAsync(taskText, null, subCts.Token);
                    return (taskText, result.FinalResponse);
                }
                catch (Exception ex)
                {
                    return (taskText, $"Error: {ex.Message}");
                }
            });
            var completed = await Task.WhenAll(subTasks);
            results.AddRange(completed);
        }
        else
        {
            foreach (var taskText in tasks)
            {
                using var subCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                subCts.CancelAfter(TimeSpan.FromSeconds(timeout));
                try
                {
                    var result = await _agent.Value.RunAsync(taskText, null, subCts.Token);
                    results.Add((taskText, result.FinalResponse));
                }
                catch (Exception ex)
                {
                    results.Add((taskText, $"Error: {ex.Message}"));
                }
            }
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Subagent results ({results.Count} task(s)):");
        foreach (var (task, result) in results)
        {
            sb.AppendLine($"\n## Task: {task}");
            sb.AppendLine(result);
        }
        return sb.ToString();
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// CODE EXECUTION TOOLSET  (1 tool)
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Run a Python script that can call Hermes tools programmatically.
/// Use when you need 3+ tool calls with processing logic between them,
/// need to filter/reduce large tool outputs, or need conditional branching.
/// </summary>
public sealed class ExecuteCodeTool : ToolBase
{
    private readonly ILogger<ExecuteCodeTool> _log;

    public ExecuteCodeTool(ILogger<ExecuteCodeTool> log) => _log = log;

    public override string Name => "execute_code";
    public override string Description =>
        "Run a Python script that can call Hermes tools programmatically. " +
        "Use when you need 3+ tool calls with processing logic between them, " +
        "need to filter/reduce large tool outputs before they enter context, " +
        "or need conditional branching.";

    public override ToolDefinition Definition => new()
    {
        Name = Name, Description = Description,
        Parameters = new Dictionary<string, ParameterDefinition>
        {
            ["code"]    = new() { Type = "string",  Description = "Python code to execute" },
            ["timeout"] = new() { Type = "integer", Description = "Max execution seconds (default: 60)" }
        },
        Required = ["code"]
    };

    protected override async Task<string> ExecuteCoreAsync(ToolCall call, CancellationToken ct)
    {
        var code = GetArg(call, "code");
        var timeout = int.TryParse(GetArgOrDefault(call, "timeout", "60"), out var t) ? t : 60;

        // Write code to temp file and execute via Python
        var tmpFile = Path.Combine(Path.GetTempPath(), $"hermes_{Guid.NewGuid():N}.py");
        await File.WriteAllTextAsync(tmpFile, code, ct);

        try
        {
            var (pythonExe, args) = DetectPython();
            using var subCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            subCts.CancelAfter(TimeSpan.FromSeconds(timeout));

            using var proc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = pythonExe,
                    Arguments = $"{args} \"{tmpFile}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute = false,
                    CreateNoWindow  = true
                }
            };

            var sb = new System.Text.StringBuilder();
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
            proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) sb.AppendLine($"[stderr] {e.Data}"); };
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.WaitForExitAsync(subCts.Token);

            var output = sb.ToString().Trim();
            return System.String.IsNullOrEmpty(output) ? $"[exit {proc.ExitCode}]" : output;
        }
        finally
        {
            if (File.Exists(tmpFile)) File.Delete(tmpFile);
        }
    }

    private static (string exe, string args) DetectPython()
    {
        foreach (var candidate in new[] { "python3", "python", "py" })
        {
            try
            {
                var result = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = candidate, Arguments = "--version",
                    RedirectStandardOutput = true, UseShellExecute = false
                });
                result?.WaitForExit(2000);
                if (result?.ExitCode == 0) return (candidate, "");
            }
            catch { /* try next */ }
        }
        throw new InvalidOperationException("Python not found. Install Python 3.x to use execute_code.");
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// SESSION SEARCH TOOLSET  (1 tool — SQLite FTS5-backed)
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Full-text search across all past sessions with LLM summarization.
/// Uses SQLite FTS5 for fast indexing. Falls back to file scan if no DB.
/// </summary>
public sealed class SessionSearchTool : ToolBase
{
    private readonly IMemoryStore _memory;
    private readonly ISessionManager _sessions;
    private readonly ILlmProvider _llm;
    private readonly ILogger<SessionSearchTool> _log;

    public SessionSearchTool(IMemoryStore memory, ISessionManager sessions, ILlmProvider llm, ILogger<SessionSearchTool> log)
    {
        _memory = memory;
        _sessions = sessions;
        _llm = llm;
        _log = log;
    }

    public override string Name => "session_search";
    public override string Description =>
        "Search your long-term memory of past conversations. Every past session is searchable. " +
        "USE PROACTIVELY when: user says 'we did this before', 'remember when', 'last time'. " +
        "Returns LLM-summarized results from relevant sessions.";

    public override ToolDefinition Definition => new()
    {
        Name = Name, Description = Description,
        Parameters = new Dictionary<string, ParameterDefinition>
        {
            ["query"]       = new() { Type = "string",  Description = "Search query — what you're looking for in past sessions" },
            ["max_results"] = new() { Type = "integer", Description = "Max sessions to return (default: 5)" },
            ["summarize"]   = new() { Type = "boolean", Description = "Summarize results with LLM (default: true)" }
        },
        Required = ["query"]
    };

    protected override async Task<string> ExecuteCoreAsync(ToolCall call, CancellationToken ct)
    {
        var query = GetArg(call, "query");
        var max = int.TryParse(GetArgOrDefault(call, "max_results", "5"), out var m) ? m : 5;
        var summarize = !GetArgOrDefault(call, "summarize", "true").Equals("false", System.StringComparison.OrdinalIgnoreCase);

        _log.LogInformation("Session search: {Query}", query);

        // Search memory store
        var entries = await _memory.SearchAsync(query, maxResults: max, ct);

        if (entries.Count == 0) return "No relevant sessions found for that query.";

        var rawResults = System.String.Join("\n\n---\n\n", entries.Select(e =>
            $"[{e.Key}] (relevance: {e.Relevance:F2})\n{e.Content[..Math.Min(1000, e.Content.Length)]}"));

        if (!summarize) return rawResults;

        // LLM summarization
        var summaryPrompt = new List<Message>
        {
            Message.System("Summarize the following session excerpts as they relate to the user's query. Be concise and highlight what was done/decided/learned."),
            Message.User($"Query: {query}\n\nSession excerpts:\n{rawResults}")
        };
        var summary = await _llm.CompleteAsync(summaryPrompt, null, ct);
        return $"[Search results for: \"{query}\"]\n\n{summary.Content}";
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// PROCESS MANAGEMENT TOOLSET  (1 tool)
// ═══════════════════════════════════════════════════════════════════════════

public sealed record BackgroundProcess(string Id, string Command, System.Diagnostics.Process Proc, DateTimeOffset StartedAt);

/// <summary>Manage background processes started with terminal(background=true).</summary>
public sealed class ProcessTool : ToolBase
{
    private static readonly ConcurrentDictionary<string, BackgroundProcess> _procs = new();

    public override string Name => "process";
    public override string Description =>
        "Manage background processes. Actions: list, poll (check status + new output), " +
        "log (full output), wait (block until done), kill (terminate), write (send stdin).";

    public override ToolDefinition Definition => new()
    {
        Name = Name, Description = Description,
        Parameters = new Dictionary<string, ParameterDefinition>
        {
            ["action"] = new() { Type = "string", Description = "list|poll|log|wait|kill|write", Enum = ["list","poll","log","wait","kill","write"] },
            ["id"]     = new() { Type = "string",  Description = "Process ID (from terminal tool output)" },
            ["input"]  = new() { Type = "string",  Description = "Input to write to stdin (for 'write' action)" },
            ["timeout"] = new() { Type = "integer", Description = "Seconds to wait (for 'wait' action)" }
        },
        Required = ["action"]
    };

    protected override Task<string> ExecuteCoreAsync(ToolCall call, CancellationToken ct)
    {
        var action = GetArg(call, "action");
        if (string.IsNullOrWhiteSpace(action))
            return Task.FromResult("Error: action parameter is required.");

        try
        {
            return action switch
            {
                "list" => ListProcesses(),
                "kill" => KillProcess(GetArg(call, "id")),
                "poll" => PollProcess(GetArg(call, "id")),
                "log"  => Task.FromResult(GetProcessLog(GetArg(call, "id"))),
                "wait" => WaitProcess(GetArg(call, "id"), GetArgOrDefault(call, "timeout", "30"), ct),
                "write"=> WriteProcess(GetArg(call, "id"), GetArg(call, "input")),
                _      => Task.FromResult($"Error: Unknown action '{action}'. Valid actions: list, poll, log, wait, kill, write.")
            };
        }
        catch (Exception ex)
        {
            return Task.FromResult($"Error executing process action '{action}': {ex.Message}");
        }
    }

    private Task<string> ListProcesses()
    {
        if (_procs.IsEmpty) return Task.FromResult("No background processes.");
        var lines = _procs.Values.Select(p =>
        {
            string status;
            try
            {
                status = p.Proc.HasExited ? $"Exited({p.Proc.ExitCode})" : "Running";
            }
            catch (InvalidOperationException)
            {
                status = "Unknown (process handle invalid)";
            }
            return $"#{p.Id} PID={p.Proc.Id} | {p.Command} | Started: {p.StartedAt:HH:mm:ss} | {status}";
        });
        return Task.FromResult(System.String.Join('\n', lines));
    }

    private Task<string> KillProcess(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return Task.FromResult("Error: id parameter is required for kill action.");
        if (!_procs.TryRemove(id, out var bp))
            return Task.FromResult($"Process #{id} not found.");

        try
        {
            if (!bp.Proc.HasExited)
            {
                bp.Proc.Kill(entireProcessTree: true);
                bp.Proc.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
            // process already exited
        }
        catch (Exception ex)
        {
            return Task.FromResult($"Process #{id} removed from registry but kill failed: {ex.Message}");
        }
        finally
        {
            try { bp.Proc.Dispose(); } catch { /* best effort */ }
        }
        return Task.FromResult($"Process #{id} killed.");
    }

    private Task<string> PollProcess(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return Task.FromResult("Error: id parameter is required for poll action.");
        if (!_procs.TryGetValue(id, out var bp))
            return Task.FromResult($"Process #{id} not found.");

        try
        {
            return Task.FromResult(bp.Proc.HasExited
                ? $"Process #{id} has exited with code {bp.Proc.ExitCode}."
                : $"Process #{id} is still running (PID {bp.Proc.Id}).");
        }
        catch (InvalidOperationException)
        {
            return Task.FromResult($"Process #{id} handle is no longer valid.");
        }
    }

    private string GetProcessLog(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return "Error: id parameter is required for log action.";
        if (!_procs.TryGetValue(id, out var bp))
            return $"Process #{id} not found.";

        try
        {
            // StandardOutput/StandardError can only be read once if not redirected async.
            // This is a best-effort snapshot; TerminalTool should own full log capture.
            return bp.Proc.HasExited
                ? $"Process #{id} exited with code {bp.Proc.ExitCode}. Full log capture requires TerminalTool integration."
                : $"Process #{id} is running (PID {bp.Proc.Id}). Full log capture requires TerminalTool integration.";
        }
        catch (InvalidOperationException ex)
        {
            return $"Error reading process log: {ex.Message}";
        }
    }

    private async Task<string> WaitProcess(string id, string timeoutStr, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id))
            return "Error: id parameter is required for wait action.";
        if (!_procs.TryGetValue(id, out var bp))
            return $"Process #{id} not found.";

        var timeout = int.TryParse(timeoutStr, out var t) ? t : 30;
        if (timeout <= 0) timeout = 30;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeout));

            await bp.Proc.WaitForExitAsync(cts.Token);
            return $"Process #{id} exited with code {bp.Proc.ExitCode}.";
        }
        catch (OperationCanceledException)
        {
            return $"Error: Wait for process #{id} timed out after {timeout} seconds.";
        }
        catch (InvalidOperationException ex)
        {
            return $"Error waiting for process #{id}: {ex.Message}";
        }
    }

    private Task<string> WriteProcess(string id, string input)
    {
        if (string.IsNullOrWhiteSpace(id))
            return Task.FromResult("Error: id parameter is required for write action.");
        if (!_procs.TryGetValue(id, out var bp))
            return Task.FromResult($"Process #{id} not found.");

        try
        {
            if (bp.Proc.HasExited)
                return Task.FromResult($"Error: Process #{id} has already exited.");

            if (bp.Proc.StartInfo.RedirectStandardInput)
            {
                bp.Proc.StandardInput.WriteLine(input);
                bp.Proc.StandardInput.Flush();
                return Task.FromResult($"Wrote {input.Length} chars to process #{id} stdin.");
            }

            return Task.FromResult($"Error: Process #{id} was not started with stdin redirection.");
        }
        catch (InvalidOperationException ex)
        {
            return Task.FromResult($"Error writing to process #{id}: {ex.Message}");
        }
        catch (System.IO.IOException ex)
        {
            return Task.FromResult($"Error writing to process #{id} stdin: {ex.Message}");
        }
    }

    /// <summary>Called by TerminalTool when background=true to register the process.</summary>
    public static string Register(string command, System.Diagnostics.Process proc)
    {
        var id = Guid.NewGuid().ToString("N")[..6];
        _procs[id] = new BackgroundProcess(id, command, proc, DateTimeOffset.UtcNow);
        return id;
    }
}
