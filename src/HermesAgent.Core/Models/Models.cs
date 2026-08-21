using System.Text.Json.Serialization;

namespace HermesAgent.Core.Models;

/// <summary>Represents a single message in a conversation.</summary>
public sealed record Message
{
    public required string Role { get; init; }
    public required string Content { get; init; }
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }
    public string? ToolCallId { get; init; }
    public string? ToolName { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }

    public static Message User(string content) => new() { Role = "user", Content = content };
    public static Message Assistant(string content) => new() { Role = "assistant", Content = content };
    public static Message AssistantToolCalls(string content, IReadOnlyList<ToolCall> toolCalls) => new()
    {
        Role = "assistant",
        Content = content,
        ToolCalls = toolCalls
    };
    public static Message System(string content) => new() { Role = "system", Content = content };
    public static Message ToolResult(string toolCallId, string toolName, string result) => new()
    {
        Role = "tool",
        Content = result,
        ToolCallId = toolCallId,
        ToolName = toolName,
        Metadata = new Dictionary<string, object?> { ["tool_name"] = toolName }
    };

    // Kept for callers that do not yet have a tool-call ID. New agent-loop code
    // must use the overload above so OpenAI-compatible APIs can correlate results.
    public static Message ToolResult(string toolName, string result) =>
        ToolResult(string.Empty, toolName, result);
}

/// <summary>Represents a full conversation session.</summary>
public sealed class Conversation
{
    public Guid Id { get; }
    public string? Title { get; set; }
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;
    private readonly List<Message> _messages = [];
    public IReadOnlyList<Message> Messages => _messages.AsReadOnly();

    public Conversation() : this(Guid.NewGuid()) { }
    public Conversation(Guid id) => Id = id;

    public void AddMessage(Message message)
    {
        _messages.Add(message);
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void Clear()
    {
        _messages.Clear();
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public int TokenEstimate => _messages.Sum(m => m.Content.Length / 4);
}

/// <summary>Represents a tool call from the LLM.</summary>
public sealed record ToolCall
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyDictionary<string, object?> Arguments { get; init; }
}

/// <summary>Represents the result of a tool execution.</summary>
public sealed record ToolResult
{
    public required string ToolCallId { get; init; }
    public required string ToolName { get; init; }
    public required string Output { get; init; }
    public bool IsError { get; init; }
    public TimeSpan Duration { get; init; }
}

/// <summary>LLM provider response.</summary>
public sealed record LlmResponse
{
    public required string Content { get; init; }
    public IReadOnlyList<ToolCall> ToolCalls { get; init; } = [];
    public string? FinishReason { get; init; }
    public LlmUsage? Usage { get; init; }
}

public sealed record LlmUsage(int PromptTokens, int CompletionTokens)
{
    public int TotalTokens => PromptTokens + CompletionTokens;
}

/// <summary>Agent run result.</summary>
public sealed record AgentRunResult
{
    public required string FinalResponse { get; init; }
    public Guid SessionId { get; init; }
    public int TurnsUsed { get; init; }
    public IReadOnlyList<ToolResult> ToolResults { get; init; } = [];
    public TimeSpan Duration { get; init; }
    public bool WasInterrupted { get; init; }
}
