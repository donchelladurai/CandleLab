using System.Text.Json;
using System.Text.Json.Serialization;

namespace CandleLab.MarketData;

/// <summary>
/// Loads Alpaca API credentials from a local JSON file. The file is
/// gitignored so credentials don't leak into source control.
/// </summary>
public sealed record AlpacaCredentials(string KeyId, string SecretKey)
{
    /// <summary>
    /// Find and load credentials. Searches (in order) an explicit path, the
    /// current working directory, and each ancestor up to 6 levels. Throws
    /// with a helpful message if not found or the file is malformed.
    /// </summary>
    public static AlpacaCredentials Load(string? explicitPath = null)
    {
        var path = ResolvePath(explicitPath);
        if (path is null)
        {
            throw new FileNotFoundException(
                "alpaca.json not found. Copy alpaca.json.example in the solution root " +
                "to alpaca.json and fill in your paper-trading API key + secret from " +
                "https://app.alpaca.markets/paper/dashboard/overview.");
        }

        var json = File.ReadAllText(path);
        var dto = JsonSerializer.Deserialize<Dto>(json, Opts)
            ?? throw new InvalidOperationException($"Could not parse {path} as JSON.");

        if (string.IsNullOrWhiteSpace(dto.KeyId) ||
            string.IsNullOrWhiteSpace(dto.SecretKey) ||
            dto.KeyId.Contains("YOUR_", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{path} exists but keyId/secretKey are missing or still placeholders. " +
                "Fill in your actual credentials.");
        }

        return new AlpacaCredentials(dto.KeyId, dto.SecretKey);
    }

    private static string? ResolvePath(string? explicitPath)
    {
        if (!string.IsNullOrEmpty(explicitPath))
        {
            return File.Exists(explicitPath) ? explicitPath : null;
        }

        var dir = Directory.GetCurrentDirectory();
        for (var i = 0; i < 6 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "alpaca.json");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed record Dto
    {
        [JsonPropertyName("keyId")]
        public string KeyId { get; init; } = "";

        [JsonPropertyName("secretKey")]
        public string SecretKey { get; init; } = "";
    }
}
