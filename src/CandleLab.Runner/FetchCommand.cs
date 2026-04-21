using System.Globalization;
using CandleLab.Domain;
using CandleLab.MarketData;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace CandleLab.Runner;

/// <summary>
/// CLI handler for `dotnet run -- fetch symbols=SPY,QQQ feeds=iex,sip from=2025-04-20 to=2026-04-19`.
///
/// Writes one CSV per (symbol, feed) pair. File naming convention is
/// <c>{symbol}_{feed}_{timeframe}.csv</c>, which lets the existing
/// CsvMarketDataProvider load it via <c>symbol=SPY_iex</c> arg style.
/// </summary>
internal static class FetchCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var map = args
            .Where(a => a.Contains('=', StringComparison.Ordinal))
            .Select(a => a.Split('=', 2))
            .ToDictionary(p => p[0].TrimStart('-').ToLowerInvariant(), p => p[1]);

        using var loggerFactory = LoggerFactory.Create(b =>
        {
            b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
            b.SetMinimumLevel(LogLevel.Information);
        });
        var log = loggerFactory.CreateLogger("fetch");

        try
        {
            var symbols = (map.GetValueOrDefault("symbols") ?? map.GetValueOrDefault("symbol") ?? "SPY")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var feeds = (map.GetValueOrDefault("feeds") ?? map.GetValueOrDefault("feed") ?? "iex")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(f => f.ToLowerInvariant())
                .ToArray();
            var timeframe = Enum.Parse<Timeframe>(
                map.GetValueOrDefault("tf") ?? "FiveMinutes", ignoreCase: true);

            // Default to (approximately) the last 12 months, with a 1-day
            // gap at the end to stay comfortably outside the free-tier
            // 15-minute lag for SIP historical.
            var now = DateTimeOffset.UtcNow;
            var defaultTo = new DateTimeOffset(
                now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero).AddDays(-1);
            var to = ParseDate(map.GetValueOrDefault("to"), defaultTo);
            var from = ParseDate(map.GetValueOrDefault("from"), to.AddDays(-365));

            var outputDir = map.GetValueOrDefault("out") ?? "data";
            var overwrite = bool.Parse(map.GetValueOrDefault("overwrite") ?? "false");

            var credentials = AlpacaCredentials.Load(map.GetValueOrDefault("credentials"));
            var fetcher = new AlpacaDataFetcher(
                credentials.KeyId,
                credentials.SecretKey,
                loggerFactory.CreateLogger<AlpacaDataFetcher>());

            log.LogInformation("Fetching {Symbols} × {Feeds} over {From:yyyy-MM-dd} → {To:yyyy-MM-dd} ({Tf})",
                string.Join(',', symbols), string.Join(',', feeds), from, to, timeframe);

            var totalBars = 0;
            var totalFiles = 0;

            foreach (var symbol in symbols)
            {
                foreach (var feed in feeds)
                {
                    if (feed != "iex" && feed != "sip")
                    {
                        log.LogWarning("Skipping unknown feed '{Feed}' (expected iex or sip).", feed);
                        continue;
                    }

                    var fileSymbol = $"{symbol}_{feed}";
                    var outputPath = Path.Combine(outputDir, $"{fileSymbol}_{timeframe}.csv");

                    log.LogInformation("→ {Symbol} [{Feed}] → {Path}", symbol, feed, outputPath);
                    var result = await fetcher.FetchAsync(new FetchRequest(
                        Symbol: symbol,
                        Timeframe: timeframe,
                        From: from,
                        To: to,
                        Feed: feed,
                        OutputPath: outputPath,
                        Overwrite: overwrite));

                    log.LogInformation("  ✓ {Bars:N0} bars in {Pages} page(s)", result.BarsWritten, result.Pages);
                    totalBars += result.BarsWritten;
                    totalFiles++;
                }
            }

            log.LogInformation("Done. {Files} file(s), {Bars:N0} bars total.", totalFiles, totalBars);
            log.LogInformation("To backtest: symbol=SPY_iex (or SPY_sip / QQQ_iex / QQQ_sip) strategy=openingrange");
            return 0;
        }
        catch (FileNotFoundException ex)
        {
            log.LogError("{Msg}", ex.Message);
            return 1;
        }
        catch (InvalidOperationException ex)
        {
            log.LogError("{Msg}", ex.Message);
            return 2;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Fetch failed");
            return 3;
        }
    }

    private static DateTimeOffset ParseDate(string? raw, DateTimeOffset fallback) =>
        string.IsNullOrEmpty(raw)
            ? fallback
            : DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
