using System;
using System.IO;
using System.Text.Json;
using Blocker.Core;
using Xunit;

namespace Blocker.Core.Tests;

public class ExtensionIdTests
{
    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "Blocker.sln")))
            {
                return dir;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException("Could not find repository root containing Blocker.sln");
    }

    [Fact]
    public void ManifestKey_ComputesToMatchingExtensionId()
    {
        string repoRoot = FindRepoRoot();
        string manifestPath = Path.Combine(repoRoot, "extension", "manifest.json");
        string expectedIdPath = Path.Combine(repoRoot, "extension", "EXTENSION_ID.txt");

        Assert.True(File.Exists(manifestPath), $"manifest.json not found at {manifestPath}");
        Assert.True(File.Exists(expectedIdPath), $"EXTENSION_ID.txt not found at {expectedIdPath}");

        string manifestJson = File.ReadAllText(manifestPath);
        using JsonDocument doc = JsonDocument.Parse(manifestJson);
        string? key = doc.RootElement.GetProperty("key").GetString();
        Assert.False(string.IsNullOrWhiteSpace(key));

        string computedId = ExtensionHelper.ComputeExtensionIdFromManifestKey(key!);
        string expectedId = File.ReadAllText(expectedIdPath).Trim();

        Assert.Equal(32, computedId.Length);
        Assert.Equal(expectedId, computedId);
    }
}
