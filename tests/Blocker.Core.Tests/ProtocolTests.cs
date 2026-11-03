using System.Text.Json;
using Blocker.Core;
using Xunit;

namespace Blocker.Core.Tests;

public class ProtocolTests
{
    [Fact]
    public void Request_SerializationRoundTrip_PreservesProperties()
    {
        var original = new Request
        {
            Command = CommandNames.StartSession,
            Args = new Dictionary<string, string>
            {
                ["profile"] = "Work",
                ["minutes"] = "25",
                ["locked"] = "true"
            }
        };

        string json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<Request>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(deserialized);
        Assert.Equal(original.Command, deserialized.Command);
        Assert.NotNull(deserialized.Args);
        Assert.Equal("Work", deserialized.Args["profile"]);
        Assert.Equal("25", deserialized.Args["minutes"]);
        Assert.Equal("true", deserialized.Args["locked"]);
    }

    [Fact]
    public void Response_Success_SerializationRoundTrip()
    {
        var original = Response.Success("test-payload");
        string json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<Response>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(deserialized);
        Assert.True(deserialized.Ok);
        Assert.Null(deserialized.Error);
        Assert.Equal("test-payload", deserialized.Data);
    }

    [Fact]
    public void Response_Fail_SerializationRoundTrip()
    {
        var original = Response.Fail("Something failed");
        string json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<Response>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(deserialized);
        Assert.False(deserialized.Ok);
        Assert.Equal("Something failed", deserialized.Error);
        Assert.Null(deserialized.Data);
    }

    [Fact]
    public void ActiveWebRulesResponse_SharedSampleHostReply_MatchesContract()
    {
        string samplePath = Path.Combine(AppContext.BaseDirectory, "shared", "sample-host-reply.json");
        if (!File.Exists(samplePath))
        {
            samplePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "shared", "sample-host-reply.json"));
            if (!File.Exists(samplePath))
            {
                samplePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "shared", "sample-host-reply.json"));
            }
        }

        Assert.True(File.Exists(samplePath), $"Sample host reply file not found at: {samplePath}");

        string json = File.ReadAllText(samplePath);
        var parsed = JsonSerializer.Deserialize<ActiveWebRulesResponse>(json);

        Assert.NotNull(parsed);
        Assert.True(parsed.Ok);
        Assert.Equal(42, parsed.RulesVersion);
        Assert.Single(parsed.ActiveProfiles);

        var profile = parsed.ActiveProfiles[0];
        Assert.Equal("Focus", profile.Name);
        Assert.False(profile.BlockAllWebsites);
        Assert.Equal(2, profile.WebRules.Count);
        Assert.Equal("example.com", profile.WebRules[0].Pattern);
        Assert.False(profile.WebRules[0].Allow);
        Assert.Equal("example.com/allowed", profile.WebRules[1].Pattern);
        Assert.True(profile.WebRules[1].Allow);
        Assert.Equal("2026-10-29T18:00:00Z", profile.ActiveUntil);

        // Verify serialization preserves all key names
        string serialized = JsonSerializer.Serialize(parsed);
        using var doc = JsonDocument.Parse(serialized);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("ok", out _));
        Assert.True(root.TryGetProperty("rulesVersion", out _));
        Assert.True(root.TryGetProperty("activeProfiles", out var profArr));
        var firstProf = profArr[0];
        Assert.True(firstProf.TryGetProperty("name", out _));
        Assert.True(firstProf.TryGetProperty("blockAllWebsites", out _));
        Assert.True(firstProf.TryGetProperty("webRules", out var rulesArr));
        Assert.True(firstProf.TryGetProperty("activeUntil", out _));
        var firstRule = rulesArr[0];
        Assert.True(firstRule.TryGetProperty("pattern", out _));
        Assert.True(firstRule.TryGetProperty("allow", out _));
    }

    [Fact]
    public void ActiveWebRulesResponse_IncludesExtensionTheme()
    {
        var resp = new ActiveWebRulesResponse
        {
            RulesVersion = 10,
            ExtensionTheme = "DeepBlack"
        };
        string json = JsonSerializer.Serialize(resp);
        Assert.Contains("\"extensionTheme\":\"DeepBlack\"", json);

        var restored = JsonSerializer.Deserialize<ActiveWebRulesResponse>(json);
        Assert.NotNull(restored);
        Assert.Equal("DeepBlack", restored.ExtensionTheme);
    }
}
