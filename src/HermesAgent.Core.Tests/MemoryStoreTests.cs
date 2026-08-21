using FluentAssertions;
using HermesAgent.Memory;
using HermesAgent.Core.Configuration;
using HermesAgent.Core.Models;
using HermesAgent.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace HermesAgent.Core.Tests;

public class MemoryStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileMemoryStore _store;

    public MemoryStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "hermes_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var options = Options.Create(new HermesOptions { DataDirectory = _tempDir });
        var logger = Substitute.For<ILogger<FileMemoryStore>>();
        _store = new FileMemoryStore(options, logger);
    }

    [Fact]
    public async Task SaveAndLoadMemory_Works()
    {
        await _store.SaveMemoryAsync("TEST", "Some content");
        var content = await _store.LoadMemoryAsync("TEST");
        content.Should().Be("Some content");
    }

    [Fact]
    public async Task SearchAsync_FindsRelevantContent()
    {
        await _store.SaveMemoryAsync("FRUITS", "Apple, Banana, Orange");
        await _store.SaveMemoryAsync("VEGGIES", "Carrot, Potato");

        var results = await _store.SearchAsync("Banana");
        
        results.Should().NotBeEmpty();
        results[0].Key.Should().Be("FRUITS");
        results[0].Content.Should().Contain("Apple");
    }

    [Fact]
    public async Task AppendMemoryAsync_AppendsToMemoryFile()
    {
        await _store.AppendMemoryAsync("First entry");
        await _store.AppendMemoryAsync("Second entry");

        var content = await _store.LoadMemoryAsync("MEMORY");
        content.Should().Contain("First entry");
        content.Should().Contain("Second entry");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }
}

public class SessionManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileSessionManager _manager;

    public SessionManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "hermes_session_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var options = Options.Create(new HermesOptions { DataDirectory = _tempDir });
        var llm = Substitute.For<ILlmProvider>();
        var logger = Substitute.For<ILogger<FileSessionManager>>();
        _manager = new FileSessionManager(options, llm, logger);
    }

    [Fact]
    public async Task SaveAndLoadSession_PreservesMessages()
    {
        var conv = new Conversation();
        conv.AddMessage(Message.User("Ping"));
        conv.AddMessage(Message.Assistant("Pong"));
        conv.Title = "Test Session";

        await _manager.SaveSessionAsync(conv);

        var loaded = await _manager.LoadSessionAsync(conv.Id);
        
        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(conv.Id);
        loaded.Title.Should().Be("Test Session");
        loaded.Messages.Should().HaveCount(2);
        loaded.Messages[0].Content.Should().Be("Ping");
    }

    [Fact]
    public async Task SaveAndLoadSession_PreservesToolCallProtocolFields()
    {
        var conv = new Conversation();
        var call = new ToolCall
        {
            Id = "call-1",
            Name = "calc",
            Arguments = new Dictionary<string, object?> { ["expression"] = "2+2" }
        };
        conv.AddMessage(Message.User("Calculate 2+2"));
        conv.AddMessage(Message.AssistantToolCalls(string.Empty, [call]));
        conv.AddMessage(Message.ToolResult(call.Id, call.Name, "4"));
        conv.AddMessage(Message.Assistant("The answer is 4."));

        await _manager.SaveSessionAsync(conv);
        var loaded = await _manager.LoadSessionAsync(conv.Id);

        loaded.Should().NotBeNull();
        loaded!.Messages[1].ToolCalls.Should().ContainSingle();
        loaded.Messages[1].ToolCalls![0].Id.Should().Be("call-1");
        loaded.Messages[2].ToolCallId.Should().Be("call-1");
        loaded.Messages[2].ToolName.Should().Be("calc");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }
}

public class SqliteSessionStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly HermesAgent.Memory.Sqlite.SqliteSessionStore _store;

    public SqliteSessionStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "hermes_sqlite_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var options = Options.Create(new HermesOptions { DataDirectory = _tempDir });
        var llm = Substitute.For<ILlmProvider>();
        var logger = Substitute.For<ILogger<HermesAgent.Memory.Sqlite.SqliteSessionStore>>();
        _store = new HermesAgent.Memory.Sqlite.SqliteSessionStore(options, llm, logger);
    }

    [Fact]
    public async Task SaveAndLoadSession_PreservesToolCallProtocolFields()
    {
        var conv = new Conversation();
        var call = new ToolCall
        {
            Id = "call-1",
            Name = "list_directory",
            Arguments = new Dictionary<string, object?> { ["path"] = "." }
        };
        conv.AddMessage(Message.User("List this directory"));
        conv.AddMessage(Message.AssistantToolCalls(string.Empty, [call]));
        conv.AddMessage(Message.ToolResult(call.Id, call.Name, "file.txt"));

        await _store.SaveSessionAsync(conv);
        var loaded = await _store.LoadSessionAsync(conv.Id);

        loaded.Should().NotBeNull();
        loaded!.Messages.Should().HaveCount(3);
        loaded.Messages[1].ToolCalls.Should().ContainSingle();
        loaded.Messages[1].ToolCalls![0].Id.Should().Be("call-1");
        loaded.Messages[2].ToolCallId.Should().Be("call-1");
        loaded.Messages[2].ToolName.Should().Be("list_directory");
    }

    [Fact]
    public async Task LoadSession_DropsLegacyOrphanedToolProtocolFragments()
    {
        var conv = new Conversation();
        conv.AddMessage(Message.User("Old request"));
        conv.AddMessage(Message.Assistant(string.Empty));
        conv.AddMessage(Message.ToolResult("legacy_tool", "old result"));

        await _store.SaveSessionAsync(conv);
        var loaded = await _store.LoadSessionAsync(conv.Id);

        loaded.Should().NotBeNull();
        loaded!.Messages.Should().ContainSingle();
        loaded.Messages[0].Role.Should().Be("user");
    }

    public void Dispose()
    {
        _store.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }
}
