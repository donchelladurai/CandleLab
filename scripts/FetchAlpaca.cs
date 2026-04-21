#!/usr/bin/env dotnet
#:package System.CommandLine@2.0.0-beta5.25306.1

// Alpaca historical data fetcher for CandleLab.
//
// Requires .NET 10 SDK (file-based apps). Run directly:
//
//     export APCA_API_KEY_ID=your_key
//     export APCA_API_SECRET_KEY=your_secret
//     dotnet run scripts/FetchAlpaca.cs -- --symbol SPY --timeframe 5Min --days 60
//
// On Linux/macOS you can also chmod +x and run ./FetchAlpaca.cs directly.
//
// Writes data/<SYMBOL>_<CandleLabTimeframe>.csv in the exact format
// CsvMarketDataProvider expects.

using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

var symbolOption = new Option<string>("--symbol")
{
    Description = "e.g. SPY, AAPL, QQQ",
    Required = true,
};
var timeframeOption = new Option<string>("--timeframe")
{
    Description = "Alpaca timeframe code",
    DefaultValueFactory = _ => "5Min",
};
timeframeOption.AcceptOnlyFromAmong("1Min", "5Min", "15Min", "30Min", "1Hour", "1Day");

var daysOption = new Option<int>("--days")
{
    Description = "Lookback window in days",
    DefaultValueFactory = _ => 60,
};
var outOption = new Option<string>("--out")
{
    Description = "Output directory",
    DefaultValueFactory = _ => "data",
};

var root = new RootCommand("Fetch historical bars from Alpaca into CandleLab CSV format.")
{
    symbolOption, timeframeOption, daysOption, outOption,
};
root.SetAction(async parseResult =>
{
    var symbol = parseResult.GetValue(symbolOption)!;
    var timeframe = parseResult.GetValue(timeframeOption)!;
    var days = parseResult.GetValue(daysOption);
    var outputDir = parseResult.GetValue(outOption)!;
    return await FetchAsync(symbol, timeframe, days, outputDir);
});

return await root.Parse(args).InvokeAsync();

static async Task<int> FetchAsync(string symbol, string timeframe, int days, string outputDir)
{
    var keyId = Environment.GetEnvironmentVariable("APCA_API_KEY_ID");
    var secret = Environment.GetEnvironmentVariable("APCA_API_SECRET_KEY");
    if (string.IsNullOrWhiteSpace(keyId) || string.IsNullOrWhiteSpace(secret))
    {
        Console.Error.WriteLine("Set APCA_API_KEY_ID and APCA_API_SECRET_KEY.");
        return 1;
    }

    var candleLabTf = ToCandleLabTimeframe(timeframe);
    var end = DateTimeOffset.UtcNow;
    var start = end.AddDays(-days);

    Console.WriteLine(CultureInfo.InvariantCulture,
        $"Fetching {symbol} {timeframe} from {start:yyyy-MM-dd} to {end:yyyy-MM-dd}...");

    using var http = new HttpClient { BaseAddress = new Uri("https://data.alpaca.markets/") };
    http.DefaultRequestHeaders.Add("APCA-API-KEY-ID", keyId);
    http.DefaultRequestHeaders.Add("APCA-API-SECRET-KEY", secret);

    Directory.CreateDirectory(outputDir);
    var path = Path.Combine(outputDir, $"{symbol}_{candleLabTf}.csv");

    await using var writer = new StreamWriter(path);
    await writer.WriteLineAsync("timestamp,open,high,low,close,volume");

    string? pageToken = null;
    var totalBars = 0;
    var totalPages = 0;

    do
    {
        var url = BuildUrl(symbol, timeframe, start, end, pageToken);
        using var response = await http.GetAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            Console.Error.WriteLine(CultureInfo.InvariantCulture,
                $"Alpaca returned {(int)response.StatusCode}: {body}");
            return 2;
        }

        var page = await response.Content.ReadFromJsonAsync<BarsResponse>();
        if (page?.Bars is null) break;

        foreach (var bar in page.Bars)
        {
            // ISO-8601 with offset — matches CsvMarketDataProvider.
            var ts = bar.Timestamp.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
            await writer.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"{ts},{bar.Open:F4},{bar.High:F4},{bar.Low:F4},{bar.Close:F4},{bar.Volume}"));
            totalBars++;
        }

        pageToken = page.NextPageToken;
        totalPages++;
    }
    while (pageToken is not null);

    Console.WriteLine(CultureInfo.InvariantCulture,
        $"Wrote {totalBars} bars across {totalPages} page(s) to {path}");
    return 0;
}

static string BuildUrl(string symbol, string timeframe, DateTimeOffset start, DateTimeOffset end, string? pageToken)
{
    var qs = new List<string>
    {
        $"symbols={Uri.EscapeDataString(symbol)}",
        $"timeframe={Uri.EscapeDataString(timeframe)}",
        $"start={Uri.EscapeDataString(start.ToString("O"))}",
        $"end={Uri.EscapeDataString(end.ToString("O"))}",
        "adjustment=raw",
        "feed=iex", // free-tier feed; swap to 'sip' if you're on a paid plan
        "limit=10000",
    };
    if (!string.IsNullOrEmpty(pageToken))
    {
        qs.Add($"page_token={Uri.EscapeDataString(pageToken)}");
    }
    return "v2/stocks/bars?" + string.Join("&", qs);
}

static string ToCandleLabTimeframe(string alpacaTf) => alpacaTf switch
{
    "1Min"  => "OneMinute",
    "5Min"  => "FiveMinutes",
    "15Min" => "FifteenMinutes",
    "30Min" => "ThirtyMinutes",
    "1Hour" => "OneHour",
    "1Day"  => "Daily",
    _ => throw new ArgumentOutOfRangeException(nameof(alpacaTf)),
};

internal sealed record BarsResponse
{
    [JsonPropertyName("bars")]
    public Dictionary<string, Bar[]>? RawBars { get; init; }

    [JsonPropertyName("next_page_token")]
    public string? NextPageToken { get; init; }

    // Alpaca returns { "bars": { "SPY": [...] }, "next_page_token": "..." }
    // Flatten to a single sequence — we only ever request one symbol at a time.
    [JsonIgnore]
    public IEnumerable<Bar>? Bars => RawBars?.SelectMany(kv => kv.Value);
}

internal sealed record Bar
{
    [JsonPropertyName("t")] public DateTimeOffset Timestamp { get; init; }
    [JsonPropertyName("o")] public decimal Open { get; init; }
    [JsonPropertyName("h")] public decimal High { get; init; }
    [JsonPropertyName("l")] public decimal Low { get; init; }
    [JsonPropertyName("c")] public decimal Close { get; init; }
    [JsonPropertyName("v")] public long Volume { get; init; }
}
