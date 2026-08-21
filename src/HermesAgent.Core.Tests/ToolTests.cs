using FluentAssertions;
using HermesAgent.Tools;
using HermesAgent.Core.Models;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace HermesAgent.Core.Tests;

public class ToolTests
{
    private readonly ILogger<ShellTool> _shellLogger = Substitute.For<ILogger<ShellTool>>();

    [Fact]
    public async Task ReadFileTool_ReturnsError_WhenFileDoesNotExist()
    {
        var tool = new ReadFileTool();
        var call = new ToolCall { 
            Id = "1", 
            Name = "read_file", 
            Arguments = new Dictionary<string, object?> { ["path"] = "non-existent.txt" } 
        };

        var result = await tool.ExecuteAsync(call);

        result.Output.Should().Contain("Error");
        result.IsError.Should().BeFalse(); // Tool execution succeeded, but output reports error
    }

    [Fact]
    public async Task WriteFileTool_CreatesFile()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".txt");
        var tool = new WriteFileTool();
        var call = new ToolCall {
            Id = "2",
            Name = "write_file",
            Arguments = new Dictionary<string, object?> {
                ["path"] = path,
                ["content"] = "Hello unit tests"
            }
        };

        try {
            var result = await tool.ExecuteAsync(call);
            result.Output.Should().Contain("File written");
            File.Exists(path).Should().BeTrue();
            File.ReadAllText(path).Should().Be("Hello unit tests");
        } finally {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ListDirectoryTool_ReturnsVisibleOutput_WhenDirectoryIsEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);

        try
        {
            var tool = new ListDirectoryTool();
            var call = new ToolCall
            {
                Id = "3",
                Name = "list_directory",
                Arguments = new Dictionary<string, object?> { ["path"] = path }
            };

            var result = await tool.ExecuteAsync(call);

            result.Output.Should().Be("(directory is empty)");
        }
        finally
        {
            Directory.Delete(path);
        }
    }
}
