using System.Runtime.CompilerServices;
using HermesAgent.Agent.Providers;
using HermesAgent.Core.Abstractions;
using HermesAgent.Core.Configuration;
using HermesAgent.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HermesAgent.Agent;

/// <summary>
/// Core agent loop — orchestrates the LLM, tools, memory, and skills
/// into a self-improving agentic loop inspired by Hermes Agent.
/// </summary>
public sealed class HermesAgentLoop : IAgent
{
    private readonly ILlmProvider _llm;
    private readonly IEnumerable<ITool> _tools;
    private readonly IMemoryStore _memory;
    private readonly ISkillManager _skillManager;
    private readonly ISessionManager _sessionManager;
    private readonly IContextCompressor _contextCompressor;
    private readonly ISystemPromptBuilder _promptBuilder;
    private readonly HermesOptions _options;
    private readonly ILogger<HermesAgentLoop> _logger;

    public HermesAgentLoop(
        ILlmProvider llm,
        IEnumerable<ITool> tools,
        IMemoryStore memory,
        ISkillManager skillManager,
        ISessionManager sessionManager,
        IContextCompressor contextCompressor,
        ISystemPromptBuilder promptBuilder,
        IOptions<HermesOptions> options,
        ILogger<HermesAgentLoop> logger)
    {
        _llm = llm;
        _tools = tools;
        _memory = memory;
        _skillManager = skillManager;
        _sessionManager = sessionManager;
        _contextCompressor = contextCompressor;
        _promptBuilder = promptBuilder;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<AgentRunResult> RunAsync(string userInput, Guid? sessionId = null, CancellationToken ct = default)
    {
        var results = new List<ToolResult>();
        var startTime = DateTimeOffset.UtcNow;
        var conversation = await GetOrCreateConversationAsync(sessionId, ct);
        string finalResponse = string.Empty;
        int turn = 0;

        conversation.AddMessage(Message.User(userInput));

        var toolDefs = _tools.Select(t => t.Definition).ToList();

        while (turn < _options.Agent.MaxTurns && !ct.IsCancellationRequested)
        {
            turn++;
            _logger.LogDebug("Agent turn {Turn}/{Max}", turn, _options.Agent.MaxTurns);

            await MaybeCompressContextAsync(conversation, ct);

            var messages = await BuildMessagesAsync(conversation, ct);
            // Reserve the last configured turn for a user-visible answer. This
            // prevents the loop from ending immediately after a tool result.
            var availableTools = turn < _options.Agent.MaxTurns ? toolDefs : null;
            var response = await _llm.CompleteAsync(messages, availableTools, ct);

            if (response.ToolCalls.Count > 0)
            {
                conversation.AddMessage(Message.AssistantToolCalls(response.Content, response.ToolCalls));
            }
            else if (!string.IsNullOrEmpty(response.Content))
            {
                finalResponse = response.Content;
                conversation.AddMessage(Message.Assistant(response.Content));
            }

            if (response.ToolCalls.Count == 0)
            {
                if (string.IsNullOrWhiteSpace(response.Content))
                    throw new InvalidOperationException("The LLM returned neither text nor a tool call.");
                break;
            }

            var toolResults = await ExecuteToolsParallelAsync(response.ToolCalls, ct);
            results.AddRange(toolResults);

            foreach (var tr in toolResults)
                conversation.AddMessage(Message.ToolResult(tr.ToolCallId, tr.ToolName, tr.Output));

            if (turn % _options.Agent.SkillNudgeIntervalTurns == 0 && _options.Agent.EnableSkillNudging)
                await NudgeSkillCreationAsync(conversation, ct);
        }

        await _sessionManager.SaveSessionAsync(conversation, ct);

        return new AgentRunResult
        {
            FinalResponse = finalResponse,
            SessionId = conversation.Id,
            TurnsUsed = turn,
            ToolResults = results,
            Duration = DateTimeOffset.UtcNow - startTime,
            WasInterrupted = ct.IsCancellationRequested
        };
    }

    public async IAsyncEnumerable<AgentEvent> RunStreamingAsync(
        string userInput,
        Guid? sessionId = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var conversation = await GetOrCreateConversationAsync(sessionId, ct);
        conversation.AddMessage(Message.User(userInput));

        var toolDefs = _tools.Select(t => t.Definition).ToList();
        var startTime = DateTimeOffset.UtcNow;
        var allToolResults = new List<ToolResult>();
        int turn = 0;

        while (turn < _options.Agent.MaxTurns && !ct.IsCancellationRequested)
        {
            turn++;
            await MaybeCompressContextAsync(conversation, ct);
            var messages = await BuildMessagesAsync(conversation, ct);

            var fullText = new System.Text.StringBuilder();
            var toolCallMap = new Dictionary<int, (string? Id, string? Name, System.Text.StringBuilder Args)>();
            // Reserve the last configured turn for synthesis instead of
            // allowing a final tool call with no turn left to explain it.
            var availableTools = turn < _options.Agent.MaxTurns ? toolDefs : null;
            await foreach (var evt in _llm.StreamAsync(messages, availableTools, ct))
            {
                if (evt is LlmStreamEvent.ContentDelta content)
                {
                    fullText.Append(content.Delta);
                    yield return new AgentEvent.TextDelta(content.Delta);
                }
                else if (evt is LlmStreamEvent.ToolCallDelta tool)
                {
                    if (!toolCallMap.TryGetValue(tool.Index, out var entry))
                    {
                        entry = (tool.Id, tool.Name, new System.Text.StringBuilder());
                        toolCallMap[tool.Index] = entry;
                    }

                    if (tool.Id != null) entry = (tool.Id, entry.Name, entry.Args);
                    if (tool.Name != null) entry = (entry.Id, tool.Name, entry.Args);
                    if (tool.ArgumentsDelta != null) entry.Args.Append(tool.ArgumentsDelta);

                    toolCallMap[tool.Index] = entry;
                }
            }

            List<ToolCall>? fallbackToolCalls = null;
            if (toolCallMap.Count == 0 && fullText.Length == 0)
            {
                // Some OpenAI-compatible endpoints occasionally close a
                // nominal streaming response without emitting any usable SSE
                // deltas. Retry the same turn once through the non-streaming
                // endpoint so the CLI never silently accepts an empty turn.
                _logger.LogWarning("Streaming returned no text or tool calls; retrying non-streaming");
                var fallback = await _llm.CompleteAsync(messages, availableTools, ct);
                if (!string.IsNullOrEmpty(fallback.Content))
                {
                    fullText.Append(fallback.Content);
                    yield return new AgentEvent.TextDelta(fallback.Content);
                }

                if (fallback.ToolCalls.Count > 0)
                    fallbackToolCalls = fallback.ToolCalls.ToList();
            }

            if (toolCallMap.Count == 0 && fallbackToolCalls is null)
            {
                var assistantMsg = fullText.ToString();
                if (string.IsNullOrWhiteSpace(assistantMsg))
                    throw new InvalidOperationException("The LLM returned neither text nor a tool call.");

                conversation.AddMessage(Message.Assistant(assistantMsg));
                break;
            }

            var collectedToolCalls = fallbackToolCalls ??
                toolCallMap.OrderBy(k => k.Key).Select(kvp =>
                {
                    var val = kvp.Value;
                    return new ToolCall
                    {
                        Id = val.Id ?? Guid.NewGuid().ToString(),
                        Name = val.Name ?? string.Empty,
                        Arguments = OpenAiCompatibleProvider.ParseArguments(val.Args.ToString())
                    };
                }).ToList();

            conversation.AddMessage(Message.AssistantToolCalls(fullText.ToString(), collectedToolCalls));

            // Execute collected tool calls
            foreach (var toolCall in collectedToolCalls)
            {
                yield return new AgentEvent.ToolStarted(toolCall.Name, toolCall.Arguments);
                
                var toolResult = await ExecuteSingleToolAsync(toolCall, ct);
                allToolResults.Add(toolResult);
                conversation.AddMessage(Message.ToolResult(toolResult.ToolCallId, toolResult.ToolName, toolResult.Output));
                
                yield return new AgentEvent.ToolCompleted(toolResult);
            }

            yield return new AgentEvent.TurnCompleted(turn);

            // A few compatible providers report "stop" even when they emit
            // tool calls. The presence of tool calls is authoritative: after
            // executing them, always ask the model for the follow-up answer.
        }

        await _sessionManager.SaveSessionAsync(conversation, ct);

        var finalResult = new AgentRunResult
        {
            FinalResponse = conversation.Messages.LastOrDefault(m => m.Role == "assistant")?.Content ?? string.Empty,
            SessionId = conversation.Id,
            TurnsUsed = turn,
            ToolResults = allToolResults,
            Duration = DateTimeOffset.UtcNow - startTime,
            WasInterrupted = ct.IsCancellationRequested
        };

        yield return new AgentEvent.AgentFinished(finalResult);
    }

    private async Task<Conversation> GetOrCreateConversationAsync(Guid? sessionId, CancellationToken ct)
    {
        if (sessionId.HasValue)
        {
            var existing = await _sessionManager.LoadSessionAsync(sessionId.Value, ct);
            if (existing is not null) return existing;
        }

        return await _sessionManager.StartSessionAsync(sessionId, ct);
    }

    private async Task<IReadOnlyList<Message>> BuildMessagesAsync(Conversation conversation, CancellationToken ct)
    {
        var systemPrompt = await _promptBuilder.BuildAsync(conversation, ct);
        var messages = new List<Message> { Message.System(systemPrompt) };
        messages.AddRange(GetProtocolSafeHistory(conversation.Messages));
        return messages;
    }

    private IEnumerable<Message> GetProtocolSafeHistory(IReadOnlyList<Message> history)
    {
        for (var i = 0; i < history.Count; i++)
        {
            var message = history[i];

            if (message.Role == "tool")
            {
                _logger.LogWarning("Skipped orphaned tool result while building LLM context");
                continue;
            }

            if (message.Role != "assistant" || message.ToolCalls is not { Count: > 0 })
            {
                if (message.Role == "assistant" && string.IsNullOrWhiteSpace(message.Content))
                    continue;

                yield return message;
                continue;
            }

            var expectedIds = message.ToolCalls.Select(call => call.Id).ToHashSet(StringComparer.Ordinal);
            var toolMessages = new List<Message>();
            var cursor = i + 1;
            while (cursor < history.Count && history[cursor].Role == "tool")
            {
                var toolMessage = history[cursor];
                if (!string.IsNullOrWhiteSpace(toolMessage.ToolCallId) &&
                    expectedIds.Remove(toolMessage.ToolCallId))
                {
                    toolMessages.Add(toolMessage);
                }
                cursor++;
            }

            if (expectedIds.Count > 0)
            {
                _logger.LogWarning("Skipped incomplete assistant tool-call exchange while building LLM context");
                i = cursor - 1;
                continue;
            }

            yield return message;
            foreach (var toolMessage in toolMessages)
                yield return toolMessage;
            i = cursor - 1;
        }
    }

    private async Task MaybeCompressContextAsync(Conversation conversation, CancellationToken ct)
    {
        if (!_options.Agent.AutoCompressContext)
            return;
        if (conversation.TokenEstimate < _options.Agent.CompressThresholdTokens)
            return;

        _logger.LogInformation("Compressing context (estimated {Tokens} tokens)", conversation.TokenEstimate);
        await _contextCompressor.CompressAsync(conversation, ct);
    }

    private async Task<ToolResult> ExecuteSingleToolAsync(ToolCall call, CancellationToken ct)
    {
        var tool = _tools.FirstOrDefault(t => t.Name == call.Name);
        if (tool is null)
        {
            return new ToolResult
            {
                ToolCallId = call.Id,
                ToolName = call.Name,
                Output = $"Error: Tool '{call.Name}' not found.",
                IsError = true,
                Duration = System.TimeSpan.Zero
            };
        }
        return await tool.ExecuteAsync(call, ct);
    }

    private async Task<List<ToolResult>> ExecuteToolsParallelAsync(IReadOnlyList<ToolCall> calls, CancellationToken ct)
    {
        var tasks = calls.Select(call => ExecuteSingleToolAsync(call, ct));
        var allResults = await Task.WhenAll(tasks);
        return allResults.ToList();
    }

    private async Task NudgeSkillCreationAsync(Conversation conversation, CancellationToken ct)
    {
        _logger.LogDebug("Nudging skill creation after {N} turns", _options.Agent.SkillNudgeIntervalTurns);
        var nudge = """
            [SYSTEM NUDGE] Consider whether any patterns or procedures from this conversation 
            should be saved as a skill for future sessions. Use the create_skill tool if applicable.
            """;
        conversation.AddMessage(new Message { Role = "system", Content = nudge });
        await Task.CompletedTask;
    }
}
