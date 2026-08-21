using System.Diagnostics;
using HermesAgent.Core.Abstractions;
using HermesAgent.Core.Models;
using Microsoft.Extensions.Logging;

namespace HermesAgent.Tools;

/// <summary>Base class for all Hermes tools.</summary>
public abstract class ToolBase : ITool
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract ToolDefinition Definition { get; }

    public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // Guard against null call
            if (call is null)
            {
                return new ToolResult
                {
                    ToolCallId = string.Empty,
                    ToolName = Name,
                    Output = "Error: Tool call was null.",
                    IsError = true,
                    Duration = sw.Elapsed
                };
            }

            // Guard against missing/empty tool name
            if (string.IsNullOrWhiteSpace(call.Name))
            {
                return new ToolResult
                {
                    ToolCallId = call.Id,
                    ToolName = Name,
                    Output = "Error: Tool call name was missing or empty.",
                    IsError = true,
                    Duration = sw.Elapsed
                };
            }

            // Guard against null arguments dictionary
            if (call.Arguments is null)
            {
                return new ToolResult
                {
                    ToolCallId = call.Id,
                    ToolName = call.Name,
                    Output = "Error: Tool call arguments dictionary was null.",
                    IsError = true,
                    Duration = sw.Elapsed
                };
            }

            var output = await ExecuteCoreAsync(call, ct);
            return new ToolResult
            {
                ToolCallId = call.Id,
                ToolName = call.Name,
                Output = output ?? string.Empty,
                IsError = false,
                Duration = sw.Elapsed
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new ToolResult
            {
                ToolCallId = call.Id,
                ToolName = call.Name,
                Output = "Error: Tool execution was cancelled (timeout or caller aborted).",
                IsError = true,
                Duration = sw.Elapsed
            };
        }
        catch (Exception ex)
        {
            // Unwrap aggregate exceptions for clearer error messages
            var message = ex is AggregateException agg
                ? string.Join("; ", agg.InnerExceptions.Select(e => e.Message))
                : ex.Message;

            return new ToolResult
            {
                ToolCallId = call.Id,
                ToolName = call.Name,
                Output = $"Error: {message}",
                IsError = true,
                Duration = sw.Elapsed
            };
        }
    }

    protected abstract Task<string> ExecuteCoreAsync(ToolCall call, CancellationToken ct);

    protected static string GetArg(ToolCall call, string key)
    {
        if (call?.Arguments is null) return string.Empty;
        return call.Arguments.TryGetValue(key, out var val) ? val?.ToString() ?? string.Empty : string.Empty;
    }

    protected static string GetArgOrDefault(ToolCall call, string key, string defaultValue)
    {
        if (call?.Arguments is null) return defaultValue;
        return call.Arguments.TryGetValue(key, out var val) && val is not null ? val.ToString()! : defaultValue;
    }
}

/// <summary>Executes shell commands (bash on Unix, cmd/powershell on Windows).</summary>
public sealed class ShellTool : ToolBase
{
    private readonly ILogger<ShellTool> _logger;
    private readonly string _workingDirectory;

    public ShellTool(ILogger<ShellTool> logger)
    {
        _logger = logger;
        _workingDirectory = Directory.GetCurrentDirectory();
    }

    public override string Name => "run_command";
    public override string Description => "Run a shell command and return its output. Use for file operations, running scripts, or interacting with the system.";

    public override ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new Dictionary<string, ParameterDefinition>
        {
            ["command"] = new() { Type = "string", Description = "The shell command to run" },
            ["timeout_seconds"] = new() { Type = "integer", Description = "Max seconds to wait (default: 30)" }
        },
        Required = ["command"]
    };

    protected override async Task<string> ExecuteCoreAsync(ToolCall call, CancellationToken ct)
    {
        var command = GetArg(call, "command");
        if (string.IsNullOrWhiteSpace(command))
            return "Error: command parameter is required and cannot be empty.";

        var timeout = int.TryParse(GetArgOrDefault(call, "timeout_seconds", "30"), out var t) ? t : 30;
        if (timeout <= 0) timeout = 30;
        if (timeout > 600) timeout = 600; // hard cap at 10 minutes

        _logger.LogInformation("Executing command: {Command}", command);

        var (fileName, args) = OperatingSystem.IsWindows()
            ? ("cmd.exe", $"/c {command}")
            : ("/bin/bash", $"-c \"{command.Replace("\"", "\\\"")}\"");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeout));

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                WorkingDirectory = _workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        var sb = new System.Text.StringBuilder();
        var outputLock = new object();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) lock (outputLock) sb.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) lock (outputLock) sb.AppendLine($"[stderr] {e.Data}");
        };
        process.Exited += (_, _) => tcs.TrySetResult(true);

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return $"Error: Failed to start process '{fileName}': {ex.Message}";
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            // Wait for exit OR cancellation, whichever comes first
            using (cts.Token.Register(() => tcs.TrySetCanceled()))
            {
                await tcs.Task;
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout or cancellation — kill the process tree
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000); // give it 5s to die
                }
            }
            catch { /* best effort */ }

            lock (outputLock)
            {
                var partial = sb.ToString().Trim();
                return $"Error: Command timed out after {timeout} seconds." +
                       (partial.Length > 0 ? $"\nPartial output:\n{partial}" : string.Empty);
            }
        }

        // Process exited normally
        var exitCode = process.ExitCode;
        lock (outputLock)
        {
            var output = sb.ToString().Trim();
            return output.Length > 0 ? output : $"[exit code {exitCode}]";
        }
    }
}

/// <summary>Reads file contents.</summary>
public sealed class ReadFileTool : ToolBase
{
    public override string Name => "read_file";
    public override string Description => "Read the contents of a file from the filesystem.";

    public override ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new Dictionary<string, ParameterDefinition>
        {
            ["path"] = new() { Type = "string", Description = "Path to the file to read" },
            ["max_lines"] = new() { Type = "integer", Description = "Maximum lines to read (default: all)" }
        },
        Required = ["path"]
    };

    protected override async Task<string> ExecuteCoreAsync(ToolCall call, CancellationToken ct)
    {
        var path = GetArg(call, "path");
        if (!File.Exists(path))
            return $"Error: File '{path}' does not exist.";

        var maxLines = int.TryParse(GetArgOrDefault(call, "max_lines", "0"), out var m) ? m : 0;

        if (maxLines > 0)
        {
            var lines = await File.ReadAllLinesAsync(path, ct);
            return string.Join('\n', lines.Take(maxLines));
        }

        return await File.ReadAllTextAsync(path, ct);
    }
}

/// <summary>Writes or creates files.</summary>
public sealed class WriteFileTool : ToolBase
{
    public override string Name => "write_file";
    public override string Description => "Write content to a file. Creates directories as needed.";

    public override ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new Dictionary<string, ParameterDefinition>
        {
            ["path"] = new() { Type = "string", Description = "Path to write the file to" },
            ["content"] = new() { Type = "string", Description = "Content to write to the file" },
            ["append"] = new() { Type = "boolean", Description = "If true, append instead of overwrite (default: false)" }
        },
        Required = ["path", "content"]
    };

    protected override async Task<string> ExecuteCoreAsync(ToolCall call, CancellationToken ct)
    {
        var path = GetArg(call, "path");
        var content = GetArg(call, "content");
        var append = GetArgOrDefault(call, "append", "false").Equals("true", StringComparison.OrdinalIgnoreCase);

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        if (append)
            await File.AppendAllTextAsync(path, content + Environment.NewLine, ct);
        else
            await File.WriteAllTextAsync(path, content, ct);

        return $"File written: {path} ({content.Length} bytes)";
    }
}

/// <summary>Lists directory contents.</summary>
public sealed class ListDirectoryTool : ToolBase
{
    public override string Name => "list_directory";
    public override string Description => "List files and directories in a given path.";

    public override ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new Dictionary<string, ParameterDefinition>
        {
            ["path"] = new() { Type = "string", Description = "Directory path to list" },
            ["pattern"] = new() { Type = "string", Description = "Glob pattern filter (e.g. '*.cs')" }
        },
        Required = ["path"]
    };

    protected override Task<string> ExecuteCoreAsync(ToolCall call, CancellationToken ct)
    {
        var path = GetArg(call, "path");
        var pattern = GetArgOrDefault(call, "pattern", "*");

        if (!Directory.Exists(path))
            return Task.FromResult($"Error: Directory '{path}' does not exist.");

        var entries = Directory.GetFileSystemEntries(path, pattern)
            .Select(e => new FileInfo(e))
            .Select(fi => fi.Attributes.HasFlag(FileAttributes.Directory)
                ? $"[DIR]  {fi.Name}"
                : $"[FILE] {fi.Name} ({fi.Length:N0} bytes)")
            .OrderBy(x => x);

        return Task.FromResult(string.Join('\n', entries));
    }
}

/// <summary>Searches files with grep-like functionality.</summary>
public sealed class SearchFilesTool : ToolBase
{
    public override string Name => "search_files";
    public override string Description => "Search for text patterns across files recursively.";

    public override ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new Dictionary<string, ParameterDefinition>
        {
            ["pattern"] = new() { Type = "string", Description = "Text or pattern to search for" },
            ["path"] = new() { Type = "string", Description = "Directory to search in" },
            ["file_pattern"] = new() { Type = "string", Description = "File glob pattern (e.g. '*.cs')" }
        },
        Required = ["pattern", "path"]
    };

    protected override async Task<string> ExecuteCoreAsync(ToolCall call, CancellationToken ct)
    {
        var pattern = GetArg(call, "pattern");
        var path = GetArg(call, "path");
        var filePattern = GetArgOrDefault(call, "file_pattern", "*.*");

        if (!Directory.Exists(path))
            return $"Error: Directory '{path}' does not exist.";

        var results = new List<string>();
        var files = Directory.GetFiles(path, filePattern, SearchOption.AllDirectories);

        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) break;
            var lines = await File.ReadAllLinesAsync(file, ct);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    results.Add($"{file}:{i + 1}: {lines[i].Trim()}");
            }
        }

        return results.Count == 0
            ? "No matches found."
            : string.Join('\n', results.Take(100)); // cap output
    }
}

/// <summary>HTTP GET requests for web access.</summary>
public sealed class WebFetchTool : ToolBase
{
    private readonly HttpClient _http;

    public WebFetchTool(HttpClient http) => _http = http;

    public override string Name => "web_fetch";
    public override string Description => "Fetch content from a URL via HTTP GET.";

    public override ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new Dictionary<string, ParameterDefinition>
        {
            ["url"] = new() { Type = "string", Description = "The URL to fetch" },
            ["max_chars"] = new() { Type = "integer", Description = "Max characters to return (default: 5000)" }
        },
        Required = ["url"]
    };

    protected override async Task<string> ExecuteCoreAsync(ToolCall call, CancellationToken ct)
    {
        var url = GetArg(call, "url");
        var maxChars = int.TryParse(GetArgOrDefault(call, "max_chars", "5000"), out var m) ? m : 5000;

        using var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(ct);
        return content.Length <= maxChars ? content : content[..maxChars] + "\n[truncated]";
    }
}
