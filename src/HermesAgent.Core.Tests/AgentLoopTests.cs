using FluentAssertions;
using HermesAgent.Agent;
using HermesAgent.Core.Abstractions;
using HermesAgent.Core.Configuration;
using HermesAgent.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace HermesAgent.Core.Tests;

public class AgentLoopTests
{
    private readonly ILlmProvider _llm = Substitute.For<ILlmProvider>();
    private readonly IMemoryStore _memory = Substitute.For<IMemoryStore>();
    private readonly ISkillManager _skills = Substitute.For<ISkillManager>();
    private readonly ISessionManager _sessions = Substitute.For<ISessionManager>();
    private readonly IContextCompressor _compressor = Substitute.For<IContextCompressor>();
    private readonly ISystemPromptBuilder _promptBuilder = Substitute.For<ISystemPromptBuilder>();
    private readonly IOptions<HermesOptions> _options;
    private readonly ILogger<HermesAgentLoop> _logger = Substitute.For<ILogger<HermesAgentLoop>>();

    public AgentLoopTests()
    {
        var hermesOptions = new HermesOptions
        {
            Agent = new AgentOptions { MaxTurns = 5 }
        };
        _options = Options.Create(hermesOptions);
        
        _sessions.StartSessionAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new Conversation());
        _promptBuilder.BuildAsync(Arg.Any<Conversation>(), Arg.Any<CancellationToken>())
            .Returns("You are Hermes.");
    }

    [Fact]
    public async Task RunAsync_ReturnsLlmResponse()
    {
        // Arrange
        var loop = new HermesAgentLoop(_llm, [], _memory, _skills, _sessions, _compressor, _promptBuilder, _options, _logger);
        
        _llm.CompleteAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<IReadOnlyList<ToolDefinition>>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse { Content = "Hello, world!", FinishReason = "stop" });

        // Act
        var result = await loop.RunAsync("Hi");

        // Assert
        result.FinalResponse.Should().Be("Hello, world!");
        result.TurnsUsed.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_ExecutesTool_WhenRequested()
    {
        // Arrange
        var tool = Substitute.For<ITool>();
        tool.Name.Returns("calc");
        tool.Definition.Returns(new ToolDefinition { 
            Name = "calc", 
            Description = "Add", 
            Parameters = new Dictionary<string, ParameterDefinition>() 
        });
        tool.ExecuteAsync(Arg.Any<ToolCall>(), Arg.Any<CancellationToken>())
            .Returns(new ToolResult { ToolCallId = "1", ToolName = "calc", Output = "4", Duration = TimeSpan.FromMilliseconds(10) });

        var loop = new HermesAgentLoop(_llm, [tool], _memory, _skills, _sessions, _compressor, _promptBuilder, _options, _logger);

        // Turn 1: Return a tool call
        // Turn 2: Final stop
        _llm.CompleteAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<IReadOnlyList<ToolDefinition>>(), Arg.Any<CancellationToken>())
            .Returns(
                new LlmResponse 
                { 
                    Content = "Thinking...", 
                    ToolCalls = [new ToolCall { Id = "1", Name = "calc", Arguments = new Dictionary<string, object?>() }],
                    FinishReason = "tool_use"
                },
                new LlmResponse 
                { 
                    Content = "The answer is 4", 
                    FinishReason = "stop" 
                }
            );

        // Act
        var result = await loop.RunAsync("What is 2+2?");

        // Assert
        result.TurnsUsed.Should().Be(2);
        result.ToolResults.Should().HaveCount(1);
        result.ToolResults[0].Output.Should().Be("4");

        var secondRequest = _llm.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ILlmProvider.CompleteAsync))
            .Select(c => (IReadOnlyList<Message>)c.GetArguments()[0]!)
            .Last();
        secondRequest.Should().Contain(m => m.Role == "assistant" &&
            m.ToolCalls!.Single().Id == "1");
        secondRequest.Should().Contain(m => m.Role == "tool" &&
            m.ToolCallId == "1" && m.Content == "4");
    }

    [Fact]
    public async Task RunAsync_UsesExistingSession_WhenProvided()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var session = new Conversation(sessionId);
        _sessions.LoadSessionAsync(sessionId, Arg.Any<CancellationToken>()).Returns(session);

        var loop = new HermesAgentLoop(_llm, [], _memory, _skills, _sessions, _compressor, _promptBuilder, _options, _logger);
        _llm.CompleteAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<IReadOnlyList<ToolDefinition>>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse { Content = "Ack", FinishReason = "stop" });

        // Act
        await loop.RunAsync("test", sessionId);

        // Assert
        await _sessions.Received().LoadSessionAsync(sessionId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunStreamingAsync_ContinuesAfterToolCall_WhenProviderReportsStop()
    {
        var tool = Substitute.For<ITool>();
        tool.Name.Returns("calc");
        tool.Definition.Returns(new ToolDefinition
        {
            Name = "calc",
            Description = "Calculate",
            Parameters = new Dictionary<string, ParameterDefinition>()
        });
        tool.ExecuteAsync(Arg.Any<ToolCall>(), Arg.Any<CancellationToken>())
            .Returns(new ToolResult
            {
                ToolCallId = "call-1",
                ToolName = "calc",
                Output = "4",
                Duration = TimeSpan.Zero
            });

        _llm.StreamAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<IReadOnlyList<ToolDefinition>?>(), Arg.Any<CancellationToken>())
            .Returns(
                Stream(
                    new LlmStreamEvent.ToolCallDelta(0, "call-1", "calc", "{}"),
                    new LlmStreamEvent.Completed("stop")),
                Stream(
                    new LlmStreamEvent.ContentDelta("The answer is 4."),
                    new LlmStreamEvent.Completed("stop")));

        var loop = new HermesAgentLoop(_llm, [tool], _memory, _skills, _sessions, _compressor, _promptBuilder, _options, _logger);
        var events = new List<AgentEvent>();
        await foreach (var evt in loop.RunStreamingAsync("What is 2+2?"))
            events.Add(evt);

        events.OfType<AgentEvent.TextDelta>().Select(e => e.Delta)
            .Should().Contain("The answer is 4.");
        events.OfType<AgentEvent.AgentFinished>().Single().Result.FinalResponse
            .Should().Be("The answer is 4.");
        _llm.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(ILlmProvider.StreamAsync))
            .Should().Be(2);
    }

    [Fact]
    public async Task RunStreamingAsync_RetriesNonStreaming_WhenStreamIsEmpty()
    {
        _llm.StreamAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<IReadOnlyList<ToolDefinition>?>(), Arg.Any<CancellationToken>())
            .Returns(Stream(new LlmStreamEvent.Completed("stop")));
        _llm.CompleteAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<IReadOnlyList<ToolDefinition>?>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse { Content = "Recovered response", FinishReason = "stop" });

        var loop = new HermesAgentLoop(_llm, [], _memory, _skills, _sessions, _compressor, _promptBuilder, _options, _logger);
        var events = new List<AgentEvent>();
        await foreach (var evt in loop.RunStreamingAsync("Hello"))
            events.Add(evt);

        events.OfType<AgentEvent.TextDelta>().Single().Delta.Should().Be("Recovered response");
        events.OfType<AgentEvent.AgentFinished>().Single().Result.FinalResponse
            .Should().Be("Recovered response");
    }

    private static async IAsyncEnumerable<LlmStreamEvent> Stream(params LlmStreamEvent[] events)
    {
        foreach (var evt in events)
        {
            yield return evt;
            await Task.Yield();
        }
    }
}
