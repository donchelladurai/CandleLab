using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CandleLab.Domain;
using Microsoft.Extensions.Logging;

namespace CandleLab.MarketData;

/// <summary>
/// Downloads historical bars from Alpaca's Market Data API and writes them
/// to a CSV file in the same format <see cref="CsvMarketDataProvider"/>
/// reads. Decoupled from the backtest pipeline on purpose — you fetch once,
/// iterate on strategies offline.
///
/// Handles Alpaca's next-page-token pagination, 429 backoff, and maps the
/// JSON response to our existing CSV schema so the rest of the pipeline
/// doesn't need to know Alpaca exists.
///
/// This does NOT attempt to merge with an existing CSV. If the destination
/// file exists, fetch fails unless `overwrite: true` is passed — the safe
/// default when re-running an overnight parameter sweep.
/// </summary>
public sealed class AlpacaDataFetcher
{
    private const string BaseUrl = "https://data.alpaca.markets/v2";
    private const int BarsPerPage = 10_000; // Alpaca's max per call
    private const int MaxRetries = 4;

    private readonly HttpClient _http;
    private readonly ILogger<AlpacaDataFetcher>? _log;

    public AlpacaDataFetcher(string keyId, string secretKey, ILogger<AlpacaDataFetcher>? log = null)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        _http.DefaultRequestHeaders.Add("APCA-API-KEY-ID", keyId);
        _http.DefaultRequestHeaders.Add("APCA-API-SECRET-KEY", secretKey);
        _log = log;
    }

    /// <summary>
    /// Fetch bars for <paramref name="symbol"/> over the given time range and
    /// write to <paramref name="outputPath"/>.
    /// </summary>
    public async Task<FetchResult> FetchAsync(FetchRequest request, CancellationToken ct = default)
    {
        if (File.Exists(request.OutputPath) && !request.Overwrite)
        {
            throw new InvalidOperationException(
                $"Output file already exists: {request.OutputPath}. " +
                "Delete it, pass a different path, or rerun with overwrite=true.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(request.OutputPath)!);

        var timeframeParam = ToAlpacaTimeframe(request.Timeframe);
        var barsWritten = 0;
        var pages = 0;
        string? pageToken = null;

        // Write CSV header first. We stream rows as they arrive rather than
        // buffering in memory — a 12-month SPY 5-min fetch is ~20k bars which
        // is fine in-memory but makes this robust to multi-year requests.
        await using var writer = new StreamWriter(request.OutputPath, append: false, Encoding.UTF8);
        await writer.WriteLineAsync("timestamp,open,high,low,close,volume");

        var start = new DateTimeOffset(request.From.UtcDateTime, TimeSpan.Zero);
        var end = new DateTimeOffset(request.To.UtcDateTime, TimeSpan.Zero);

        do
        {
            ct.ThrowIfCancellationRequested();
            pages++;

            var url = BuildUrl(request.Symbol, timeframeParam, start, end, request.Feed, pageToken);
            var response = await FetchWithRetryAsync(url, ct);

            foreach (var bar in (IEnumerable<AlpacaBar>?)response.Bars ?? Array.Empty<AlpacaBar>())
            {
                // Alpaca timestamps are UTC ISO 8601; preserve the zone when writing.
                var ts = bar.Timestamp.ToUniversalTime()
                    .ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
                var line = string.Create(CultureInfo.InvariantCulture,
                    $"{ts},{bar.Open},{bar.High},{bar.Low},{bar.Close},{bar.Volume}");
                await writer.WriteLineAsync(line);
                barsWritten++;
            }

            pageToken = response.NextPageToken;

            if (barsWritten > 0 && barsWritten % 5_000 == 0)
            {
                _log?.LogInformation("  fetched {Bars} bars ({Pages} pages)...", barsWritten, pages);
            }
        }
        while (!string.IsNullOrEmpty(pageToken));

        return new FetchResult(
            Symbol: request.Symbol,
            BarsWritten: barsWritten,
            Pages: pages,
            OutputPath: request.OutputPath);
    }

    // ─── HTTP plumbing ──────────────────────────────────────────────────

    private static string BuildUrl(
        string symbol,
        string timeframe,
        DateTimeOffset start,
        DateTimeOffset end,
        string feed,
        string? pageToken)
    {
        // `adjustment=all` so historical OHLC reflects splits & dividends;
        // makes cross-year backtests honest without changing anything for
        // SPY which hasn't split since 2000.
        var q = new StringBuilder()
            .Append($"timeframe={Uri.EscapeDataString(timeframe)}")
            .Append($"&start={Uri.EscapeDataString(start.ToString("o", CultureInfo.InvariantCulture))}")
            .Append($"&end={Uri.EscapeDataString(end.ToString("o", CultureInfo.InvariantCulture))}")
            .Append($"&limit={BarsPerPage}")
            .Append($"&adjustment=all")
            .Append($"&feed={Uri.EscapeDataString(feed)}");
        if (!string.IsNullOrEmpty(pageToken))
        {
            q.Append($"&page_token={Uri.EscapeDataString(pageToken)}");
        }
        return $"{BaseUrl}/stocks/{Uri.EscapeDataString(symbol)}/bars?{q}";
    }

    private async Task<AlpacaBarsResponse> FetchWithRetryAsync(string url, CancellationToken ct)
    {
        var attempt = 0;
        while (true)
        {
            attempt++;
            HttpResponseMessage response;
            try
            {
                response = await _http.GetAsync(url, ct);
            }
            catch (HttpRequestException ex) when (attempt < MaxRetries)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                _log?.LogWarning("HTTP error on attempt {Attempt}: {Err}. Retrying in {Delay}s.",
                    attempt, ex.Message, delay.TotalSeconds);
                await Task.Delay(delay, ct);
                continue;
            }

            if ((int)response.StatusCode == 429 && attempt < MaxRetries)
            {
                // Alpaca's free tier is 200 req/min; we shouldn't hit this in
                // normal use (a 12-month SPY fetch is ~3 calls), but if we
                // do, back off exponentially.
                var retryAfter = response.Headers.RetryAfter?.Delta
                    ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                _log?.LogWarning("Rate limited. Sleeping {Delay}s.", retryAfter.TotalSeconds);
                await Task.Delay(retryAfter, ct);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException(
                    $"Alpaca API returned {(int)response.StatusCode} {response.StatusCode}: {body}");
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var parsed = JsonSerializer.Deserialize<AlpacaBarsResponse>(json, JsonOpts)
                ?? throw new InvalidOperationException("Alpaca returned empty response.");
            return parsed;
        }
    }

    // ─── Timeframe mapping ──────────────────────────────────────────────

    public static string ToAlpacaTimeframe(Timeframe tf) => tf switch
    {
        Timeframe.OneMinute      => "1Min",
        Timeframe.FiveMinutes    => "5Min",
        Timeframe.FifteenMinutes => "15Min",
        Timeframe.ThirtyMinutes  => "30Min",
        Timeframe.OneHour        => "1Hour",
        Timeframe.FourHours      => "4Hour",
        Timeframe.Daily          => "1Day",
        _ => throw new ArgumentOutOfRangeException(nameof(tf), tf, "Unsupported timeframe for Alpaca."),
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
}

public sealed record FetchRequest(
    string Symbol,
    Timeframe Timeframe,
    DateTimeOffset From,
    DateTimeOffset To,
    string Feed, // "iex" or "sip"
    string OutputPath,
    bool Overwrite = false);

public sealed record FetchResult(
    string Symbol,
    int BarsWritten,
    int Pages,
    string OutputPath);

// ─── JSON DTOs (internal) ───────────────────────────────────────────────

internal sealed record AlpacaBarsResponse
{
    [JsonPropertyName("bars")]
    public List<AlpacaBar>? Bars { get; init; }

    [JsonPropertyName("next_page_token")]
    public string? NextPageToken { get; init; }
}

internal sealed record AlpacaBar
{
    [JsonPropertyName("t")]
    public DateTimeOffset Timestamp { get; init; }

    [JsonPropertyName("o")]
    public decimal Open { get; init; }

    [JsonPropertyName("h")]
    public decimal High { get; init; }

    [JsonPropertyName("l")]
    public decimal Low { get; init; }

    [JsonPropertyName("c")]
    public decimal Close { get; init; }

    [JsonPropertyName("v")]
    public long Volume { get; init; }
}
