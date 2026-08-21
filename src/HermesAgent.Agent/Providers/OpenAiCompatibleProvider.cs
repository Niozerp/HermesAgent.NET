using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HermesAgent.Core.Abstractions;
using HermesAgent.Core.Configuration;
using HermesAgent.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HermesAgent.Agent.Providers;

/// <summary>
/// OpenAI-compatible LLM provider. Supports OpenAI, OpenRouter, Nous Portal,
/// NVIDIA NIM, MiniMax, Kimi/Moonshot, and any OpenAI-spec endpoint.
/// </summary>
public sealed class OpenAiCompatibleProvider : ILlmProvider
{
    private readonly HttpClient _http;
    private readonly LlmOptions _options;
    private readonly ILogger<OpenAiCompatibleProvider> _logger;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public string Name => _options.Provider;

    public OpenAiCompatibleProvider(HttpClient http, IOptions<HermesOptions> options, ILogger<OpenAiCompatibleProvider> logger)
    {
        _http = http;
        _options = options.Value.Llm;
        _logger = logger;

        var baseUrl = _options.BaseUrl ?? GetDefaultBaseUrl(_options.Provider);

                // .NET Uri: when BaseAddress has no trailing slash, a relative URI like
                // "chat/completions" replaces the *last segment* of the base path instead of
                // appending.  Ensure trailing slash so relative endpoint appends correctly.
                var baseUri = baseUrl.TrimEnd('/') + "/";
                _http.BaseAddress = new Uri(baseUri);

                _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {_options.ApiKey}");
                _http.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);

                // Resolve the chat-completions endpoint suffix once.
                // Default OpenAI-style base URLs (e.g. https://api.openai.com) get "v1/..."
                // appended; base URLs that already carry a version segment (e.g. /v4, /paas/v4)
                // get just "chat/completions" to avoid double-version paths.
                var trimmed = baseUrl.TrimEnd('/');
                _chatEndpoint = trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                    || trimmed.EndsWith("/v2", StringComparison.OrdinalIgnoreCase)
                    || trimmed.EndsWith("/v3", StringComparison.OrdinalIgnoreCase)
                    || trimmed.EndsWith("/v4", StringComparison.OrdinalIgnoreCase)
                    || trimmed.Contains("/v1/", StringComparison.OrdinalIgnoreCase)
                    || trimmed.Contains("/paas/", StringComparison.OrdinalIgnoreCase)
                    ? "chat/completions"
                    : "v1/chat/completions";

                _logger.LogInformation("Provider configured: base={BaseUri} endpoint={Endpoint} model={Model}",
            baseUri, _chatEndpoint, _options.Model);
            }

            private readonly string _chatEndpoint;

    public async Task<LlmResponse> CompleteAsync(IReadOnlyList<Message> messages, IReadOnlyList<ToolDefinition>? tools = null, CancellationToken ct = default)
    {
        var request = BuildRequest(messages, tools, stream: false);
        var json = JsonSerializer.Serialize(request, JsonOpts);

        _logger.LogDebug("Sending {MessageCount} messages to {Provider}/{Model}", messages.Count, Name, _options.Model);

        using var response = await _http.PostAsync(_chatEndpoint,
            new StringContent(json, Encoding.UTF8, "application/json"), ct);

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<OpenAiChatResponse>(JsonOpts, ct)
            ?? throw new InvalidOperationException("Empty response from LLM");

        return MapResponse(result);
    }

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        IReadOnlyList<Message> messages,
        IReadOnlyList<ToolDefinition>? tools = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var request = BuildRequest(messages, tools, stream: true);
        var json = JsonSerializer.Serialize(request, JsonOpts);

        using var requestMsg = new HttpRequestMessage(HttpMethod.Post, _chatEndpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        using var response = await _http.SendAsync(requestMsg, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null || !line.StartsWith("data: ", StringComparison.Ordinal))
                continue;

            var data = line["data: ".Length..];
            if (data == "[DONE]")
                break;

            OpenAiStreamChunk? chunk;
            try { chunk = JsonSerializer.Deserialize<OpenAiStreamChunk>(data, JsonOpts); }
            catch { continue; }

            var choice = chunk?.Choices?.FirstOrDefault();
            if (choice is null) continue;

            // Handle content delta
            var delta = choice.Delta?.Content;
            if (!string.IsNullOrEmpty(delta))
                yield return new LlmStreamEvent.ContentDelta(delta);

            // Handle tool call deltas
            if (choice.Delta?.ToolCalls is not null)
            {
                foreach (var tc in choice.Delta.ToolCalls)
                {
                    yield return new LlmStreamEvent.ToolCallDelta(
                        tc.Index,
                        tc.Id,
                        tc.Function?.Name,
                        tc.Function?.Arguments);
                }
            }

            // Handle finish reason
            if (!string.IsNullOrEmpty(choice.FinishReason))
                yield return new LlmStreamEvent.Completed(choice.FinishReason);
        }
    }

    private object BuildRequest(IReadOnlyList<Message> messages, IReadOnlyList<ToolDefinition>? tools, bool stream)
    {
        var msgs = messages.Select(m => new
        {
            role = m.Role,
            content = m.Content
        }).ToArray();

        if (tools is null || tools.Count == 0)
        {
            return new
            {
                model = _options.Model,
                messages = msgs,
                temperature = _options.Temperature,
                // max_tokens = _options.MaxTokens,
                stream
            };
        }

        var toolDefs = tools.Select(t => new
        {
            type = "function",
            function = new
            {
                name = t.Name,
                description = t.Description,
                parameters = new
                {
                    type = "object",
                    properties = t.Parameters.ToDictionary(
                        p => p.Key,
                        p => (object)new { type = p.Value.Type, description = p.Value.Description }),
                    required = t.Required
                }
            }
        }).ToArray();

        return new
        {
            model = _options.Model,
            messages = msgs,
            tools = toolDefs,
            temperature = _options.Temperature,
            // max_tokens = _options.MaxTokens,
            stream
        };
    }

    private static LlmResponse MapResponse(OpenAiChatResponse r)
    {
        var choice = r.Choices?.FirstOrDefault();
        var toolCalls = choice?.Message?.ToolCalls?.Select(tc => new ToolCall
        {
            Id = tc.Id ?? Guid.NewGuid().ToString(),
            Name = tc.Function?.Name ?? string.Empty,
            Arguments = ParseArguments(tc.Function?.Arguments)
        }).ToList() ?? [];

        return new LlmResponse
        {
            Content = choice?.Message?.Content ?? string.Empty,
            ToolCalls = toolCalls,
            FinishReason = choice?.FinishReason,
            Usage = r.Usage is not null
                ? new LlmUsage(r.Usage.PromptTokens, r.Usage.CompletionTokens)
                : null
        };
    }

    public static IReadOnlyDictionary<string, object?> ParseArguments(string? argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson))
            return new Dictionary<string, object?>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(argsJson, JsonOpts)
                   ?? new Dictionary<string, object?>();
        }
        catch
        {
            return new Dictionary<string, object?>();
        }
    }

    private static string GetDefaultBaseUrl(string provider) => provider.ToLowerInvariant() switch
    {
        "openai" => "https://api.openai.com",
        "openrouter" => "https://openrouter.ai/api",
        "nous" or "nous-portal" => "https://portal.nousresearch.com/api",
        "nvidia" or "nim" => "https://integrate.api.nvidia.com",
        "anthropic" => "https://api.anthropic.com",
        _ => throw new InvalidOperationException($"Unknown provider '{provider}'. Set Hermes:Llm:BaseUrl explicitly.")
    };

    // DTO models
    private sealed class OpenAiChatResponse
    {
        [JsonPropertyName("choices")] public List<Choice>? Choices { get; set; }
        [JsonPropertyName("usage")] public UsageDto? Usage { get; set; }
    }

    private sealed class Choice
    {
        [JsonPropertyName("message")] public MessageDto? Message { get; set; }
        [JsonPropertyName("finish_reason")] public string? FinishReason { get; set; }
    }

    private sealed class MessageDto
    {
        [JsonPropertyName("content")] public string? Content { get; set; }
        [JsonPropertyName("tool_calls")] public List<ToolCallDto>? ToolCalls { get; set; }
    }

    private sealed class ToolCallDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("index")] public int Index { get; set; }
        [JsonPropertyName("function")] public FunctionDto? Function { get; set; }
    }

    private sealed class FunctionDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("arguments")] public string? Arguments { get; set; }
    }

    private sealed class UsageDto
    {
        [JsonPropertyName("prompt_tokens")] public int PromptTokens { get; set; }
        [JsonPropertyName("completion_tokens")] public int CompletionTokens { get; set; }
    }

    private sealed class OpenAiStreamChunk
    {
        [JsonPropertyName("choices")] public List<StreamChoice>? Choices { get; set; }
    }

    private sealed class StreamChoice
    {
        [JsonPropertyName("delta")] public StreamDelta? Delta { get; set; }
        [JsonPropertyName("finish_reason")] public string? FinishReason { get; set; }
    }

    private sealed class StreamDelta
    {
        [JsonPropertyName("content")] public string? Content { get; set; }
        [JsonPropertyName("tool_calls")] public List<ToolCallDto>? ToolCalls { get; set; }
    }
}
