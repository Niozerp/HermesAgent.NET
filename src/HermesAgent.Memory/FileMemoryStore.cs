using System.Text.Json;
using System.Text.Json.Serialization;
using HermesAgent.Core.Abstractions;
using HermesAgent.Core.Configuration;
using HermesAgent.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HermesAgent.Memory;

/// <summary>
/// File-based memory store. Persistent memories are stored as Markdown files.
/// Uses simple keyword-based relevance ranking for search (no vector DB required).
/// </summary>
public sealed class FileMemoryStore : IMemoryStore
{
    private readonly string _dataDir;
    private readonly ILogger<FileMemoryStore> _logger;

    public FileMemoryStore(IOptions<HermesOptions> options, ILogger<FileMemoryStore> logger)
    {
        _dataDir = options.Value.DataDirectory;
        _logger = logger;
        Directory.CreateDirectory(_dataDir);
    }

    public async Task SaveMemoryAsync(string key, string content, CancellationToken ct = default)
    {
        var path = GetPath(key);
        await File.WriteAllTextAsync(path, content, ct);
        _logger.LogDebug("Saved memory '{Key}'", key);
    }

    public async Task<string?> LoadMemoryAsync(string key, CancellationToken ct = default)
    {
        var path = GetPath(key);
        if (!File.Exists(path)) return null;
        return await File.ReadAllTextAsync(path, ct);
    }

    public async Task<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int maxResults = 10, CancellationToken ct = default)
    {
        var terms = query.ToLowerInvariant().Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        var results = new List<MemoryEntry>();

        if (!Directory.Exists(_dataDir)) return results;

        foreach (var file in Directory.GetFiles(_dataDir, "*.md"))
        {
            var content = await File.ReadAllTextAsync(file, ct);
            var key = Path.GetFileNameWithoutExtension(file);
            var relevance = CalculateRelevance(content, terms);
            if (relevance > 0)
            {
                results.Add(new MemoryEntry
                {
                    Key = key,
                    Content = content,
                    CreatedAt = File.GetCreationTimeUtc(file),
                    Relevance = relevance
                });
            }
        }

        return results
            .OrderByDescending(r => r.Relevance)
            .Take(maxResults)
            .ToList();
    }

    public async Task AppendMemoryAsync(string content, CancellationToken ct = default)
    {
        var path = GetPath("MEMORY");
        await File.AppendAllTextAsync(path,
            $"\n- [{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm}] {content}",
            ct);
        _logger.LogInformation("Appended memory entry");
    }

    private string GetPath(string key)
        => Path.Combine(_dataDir, $"{key.ToUpperInvariant()}.md");

    private static double CalculateRelevance(string content, string[] terms)
    {
        if (terms.Length == 0) return 0;
        var lower = content.ToLowerInvariant();
        double score = 0;
        foreach (var term in terms)
        {
            int count = 0;
            int index = 0;
            while ((index = lower.IndexOf(term, index, System.StringComparison.Ordinal)) != -1)
            {
                count++;
                index += term.Length;
            }
            score += count;
        }
        return score / terms.Length;
    }
}

/// <summary>
/// File-based session manager. Sessions are serialized as JSON files,
/// allowing cross-session search and recall.
/// </summary>
public sealed class FileSessionManager : ISessionManager
{
    private readonly string _sessionsDir;
    private readonly ILlmProvider _llm;
    private readonly ILogger<FileSessionManager> _logger;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public FileSessionManager(IOptions<HermesOptions> options, ILlmProvider llm, ILogger<FileSessionManager> logger)
    {
        _sessionsDir = Path.Combine(options.Value.DataDirectory, "sessions");
        _llm = llm;
        _logger = logger;
        Directory.CreateDirectory(_sessionsDir);
    }

    public Task<Conversation> StartSessionAsync(Guid? sessionId = null, CancellationToken ct = default)
    {
        var conversation = new Conversation(sessionId ?? Guid.NewGuid());
        _logger.LogInformation("Started session {Id}", conversation.Id);
        return Task.FromResult(conversation);
    }

    public async Task SaveSessionAsync(Conversation conversation, CancellationToken ct = default)
    {
        var path = GetSessionPath(conversation.Id);
        var dto = new SessionDto
        {
            Id = conversation.Id,
            Title = conversation.Title,
            CreatedAt = conversation.CreatedAt,
            UpdatedAt = conversation.UpdatedAt,
            Messages = conversation.Messages.Select(m => new MessageDto
            {
                Role = m.Role,
                Content = m.Content,
                Timestamp = m.Timestamp,
                ToolCalls = m.ToolCalls,
                ToolCallId = m.ToolCallId,
                ToolName = m.ToolName
            }).ToList()
        };

        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(dto, JsonOpts), ct);
        _logger.LogDebug("Saved session {Id}", conversation.Id);
    }

    public async Task<Conversation?> LoadSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        var path = GetSessionPath(sessionId);
        if (!File.Exists(path)) return null;

        var json = await File.ReadAllTextAsync(path, ct);
        var dto = JsonSerializer.Deserialize<SessionDto>(json, JsonOpts);
        if (dto is null) return null;

        var conversation = new Conversation(dto.Id);
        var pendingToolCallIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var msg in dto.Messages)
        {
            if (msg.ToolCalls is { Count: > 0 })
            {
                foreach (var call in msg.ToolCalls)
                    pendingToolCallIds.Add(call.Id);
            }

            if (msg.Role == "tool" &&
                (string.IsNullOrWhiteSpace(msg.ToolCallId) || !pendingToolCallIds.Remove(msg.ToolCallId)))
            {
                _logger.LogWarning("Skipped orphaned tool result while repairing session {Id}", sessionId);
                continue;
            }

            if (msg.Role == "assistant" && string.IsNullOrWhiteSpace(msg.Content) && msg.ToolCalls is not { Count: > 0 })
                continue;

            conversation.AddMessage(new Message
            {
                Role = msg.Role,
                Content = msg.Content,
                Timestamp = msg.Timestamp,
                ToolCalls = msg.ToolCalls,
                ToolCallId = msg.ToolCallId,
                ToolName = msg.ToolName
            });
        }
        conversation.Title = dto.Title;
        return conversation;
    }

    public async Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(int maxResults = 50, CancellationToken ct = default)
    {
        var results = new List<SessionSummary>();
        if (!Directory.Exists(_sessionsDir)) return results;

        foreach (var file in Directory.GetFiles(_sessionsDir, "*.json")
                     .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                     .Take(maxResults))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file, ct);
                var dto = JsonSerializer.Deserialize<SessionDto>(json, JsonOpts);
                if (dto is not null)
                {
                    results.Add(new SessionSummary
                    {
                        Id = dto.Id,
                        Title = dto.Title,
                        CreatedAt = dto.CreatedAt,
                        UpdatedAt = dto.UpdatedAt,
                        MessageCount = dto.Messages.Count
                    });
                }
            }
            catch { /* skip malformed */ }
        }
        return results;
    }

    public async Task<string> SummarizeSessionAsync(Conversation conversation, CancellationToken ct = default)
    {
        var content = string.Join("\n\n", conversation.Messages
            .Where(m => m.Role is "user" or "assistant")
            .Select(m => $"[{m.Role}]: {m.Content}"));

        var response = await _llm.CompleteAsync([
            Message.System("Summarize the following conversation in 2-3 sentences."),
            Message.User(content)
        ], null, ct);

        return response.Content;
    }

    public Task DeleteSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        var path = GetSessionPath(sessionId);
        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }

    private string GetSessionPath(Guid id) => Path.Combine(_sessionsDir, $"{id}.json");

    private sealed class SessionDto
    {
        public Guid Id { get; set; }
        public string? Title { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public List<MessageDto> Messages { get; set; } = [];
    }

    private sealed class MessageDto
    {
        public required string Role { get; set; }
        public required string Content { get; set; }
        public DateTimeOffset Timestamp { get; set; }
        public IReadOnlyList<ToolCall>? ToolCalls { get; set; }
        public string? ToolCallId { get; set; }
        public string? ToolName { get; set; }
    }
}

/// <summary>
/// Tool that allows the agent to save and recall memories during a session.
/// </summary>
public sealed class MemoryTools
{
    private readonly IMemoryStore _memory;

    public MemoryTools(IMemoryStore memory) => _memory = memory;

    public IEnumerable<HermesAgent.Core.Abstractions.ITool> GetTools()
    {
        yield return new SaveMemoryToolImpl(_memory);
        yield return new RecallMemoryToolImpl(_memory);
        yield return new SearchMemoryToolImpl(_memory);
    }

    private sealed class SaveMemoryToolImpl(IMemoryStore mem) : HermesAgent.Tools.ToolBase
    {
        public override string Name => "save_memory";
        public override string Description => "Save important information to persistent memory for future sessions.";
        public override HermesAgent.Core.Abstractions.ToolDefinition Definition => new()
        {
            Name = Name, Description = Description,
            Parameters = new Dictionary<string, HermesAgent.Core.Abstractions.ParameterDefinition>
            {
                ["content"] = new() { Type = "string", Description = "The information to remember" }
            },
            Required = ["content"]
        };

        protected override async Task<string> ExecuteCoreAsync(HermesAgent.Core.Models.ToolCall call, CancellationToken ct)
        {
            await mem.AppendMemoryAsync(GetArg(call, "content"), ct);
            return "Memory saved.";
        }
    }

    private sealed class RecallMemoryToolImpl(IMemoryStore mem) : HermesAgent.Tools.ToolBase
    {
        public override string Name => "recall_memory";
        public override string Description => "Read the full contents of the persistent memory file.";
        public override HermesAgent.Core.Abstractions.ToolDefinition Definition => new()
        {
            Name = Name, Description = Description,
            Parameters = new Dictionary<string, HermesAgent.Core.Abstractions.ParameterDefinition>()
        };

        protected override async Task<string> ExecuteCoreAsync(HermesAgent.Core.Models.ToolCall call, CancellationToken ct)
        {
            var mem2 = await mem.LoadMemoryAsync("MEMORY", ct);
            return string.IsNullOrWhiteSpace(mem2) ? "No memories stored yet." : mem2;
        }
    }

    private sealed class SearchMemoryToolImpl(IMemoryStore mem) : HermesAgent.Tools.ToolBase
    {
        public override string Name => "search_memory";
        public override string Description => "Search past sessions and memories for relevant information.";
        public override HermesAgent.Core.Abstractions.ToolDefinition Definition => new()
        {
            Name = Name, Description = Description,
            Parameters = new Dictionary<string, HermesAgent.Core.Abstractions.ParameterDefinition>
            {
                ["query"] = new() { Type = "string", Description = "Search query" }
            },
            Required = ["query"]
        };

        protected override async Task<string> ExecuteCoreAsync(HermesAgent.Core.Models.ToolCall call, CancellationToken ct)
        {
            var results = await mem.SearchAsync(GetArg(call, "query"), maxResults: 5, ct);
            if (results.Count == 0) return "No relevant memories found.";
            return string.Join("\n---\n", results.Select(r => $"[{r.Key}] {r.Content[..Math.Min(500, r.Content.Length)]}"));
        }
    }
}
