using System.Text.Json;

namespace Blocker.Core;

public static class BlockConfigParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static bool TryParse(string? json, out BlockConfig? config, out string? errorMessage)
    {
        config = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            errorMessage = "Configuration content is empty or whitespace.";
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<BlockConfig>(json, JsonOptions);
            if (parsed == null)
            {
                errorMessage = "Deserialized configuration resulted in null.";
                return false;
            }

            if (parsed.BlockedProcesses == null)
            {
                parsed.BlockedProcesses = new List<string>();
            }

            config = parsed;
            return true;
        }
        catch (JsonException ex)
        {
            errorMessage = $"Malformed JSON: {ex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            errorMessage = $"Error parsing configuration: {ex.Message}";
            return false;
        }
    }
}
