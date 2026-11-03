using Blocker.Core;
using Xunit;

namespace Blocker.Core.Tests;

public class BlockConfigParserTests
{
    [Fact]
    public void TryParse_ValidJson_ShouldReturnTrueAndPopulatedConfig()
    {
        string json = """
        {
            "BlockedProcesses": [
                "notepad.exe",
                "discord"
            ]
        }
        """;

        bool success = BlockConfigParser.TryParse(json, out BlockConfig? config, out string? errorMessage);

        Assert.True(success);
        Assert.Null(errorMessage);
        Assert.NotNull(config);
        Assert.Equal(2, config!.BlockedProcesses.Count);
        Assert.Contains("notepad.exe", config.BlockedProcesses);
        Assert.Contains("discord", config.BlockedProcesses);
    }

    [Fact]
    public void TryParse_CaseInsensitivePropertyNames_ShouldSucceed()
    {
        string json = """
        {
            "blockedprocesses": [
                "notepad"
            ]
        }
        """;

        bool success = BlockConfigParser.TryParse(json, out BlockConfig? config, out string? errorMessage);

        Assert.True(success);
        Assert.Null(errorMessage);
        Assert.NotNull(config);
        Assert.Single(config!.BlockedProcesses);
        Assert.Equal("notepad", config.BlockedProcesses[0]);
    }

    [Fact]
    public void TryParse_EmptyJsonObject_ShouldReturnTrueWithEmptyList()
    {
        string json = "{}";

        bool success = BlockConfigParser.TryParse(json, out BlockConfig? config, out string? errorMessage);

        Assert.True(success);
        Assert.Null(errorMessage);
        Assert.NotNull(config);
        Assert.NotNull(config!.BlockedProcesses);
        Assert.Empty(config.BlockedProcesses);
    }

    [Theory]
    [InlineData("{ \"BlockedProcesses\": [ \"notepad.exe\"")] // missing closing bracket/brace
    [InlineData("{ \"BlockedProcesses\": \"not an array\" }")] // invalid structure
    [InlineData("{ invalid json }")]
    [InlineData("<xml>not json</xml>")]
    public void TryParse_MalformedJson_ShouldReturnFalseAndSetErrorMessage(string malformedJson)
    {
        bool success = BlockConfigParser.TryParse(malformedJson, out BlockConfig? config, out string? errorMessage);

        Assert.False(success);
        Assert.Null(config);
        Assert.NotNull(errorMessage);
        Assert.NotEmpty(errorMessage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void TryParse_NullOrWhitespace_ShouldReturnFalseAndSetErrorMessage(string? emptyInput)
    {
        bool success = BlockConfigParser.TryParse(emptyInput, out BlockConfig? config, out string? errorMessage);

        Assert.False(success);
        Assert.Null(config);
        Assert.NotNull(errorMessage);
    }
}
