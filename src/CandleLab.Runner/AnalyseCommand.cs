using CandleLab.Backtesting;
using CandleLab.Domain;
using CandleLab.Execution;
using CandleLab.MarketData;
using CandleLab.Strategies;
using Microsoft.Extensions.Logging;

namespace CandleLab.Runner;

/// <summary>
/// CLI handler for `dotnet run -- analyse symbols=SPY,QQQ modes=reversal,continuation out=./analysis.html`.
///
/// Runs the full grid of (symbol × mode) backtests and writes a single
/// self-contained HTML file with commentary placeholders, the 4-run grid
/// tables, and an embedded per-run chart switcher. Intended as the artifact
/// to attach when publishing the write-up.
/// </summary>
internal static class AnalyseCommand
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
        var log = loggerFactory.CreateLogger("analyse");

        try
        {
            var symbols = (map.GetValueOrDefault("symbols") ?? "SPY_iex,QQQ_iex")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var modes = (map.GetValueOrDefault("modes") ?? "reversal,continuation")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(m => Enum.Parse<BreakoutMode>(m, ignoreCase: true))
                .ToArray();

            var dataPath = map.GetValueOrDefault("data") ?? "data";
            var outputPath = map.GetValueOrDefault("out") ?? "analysis.html";
            var timeframe = Timeframe.FiveMinutes;
            var capital = 10_000m;

            var meta = new AnalysisMeta(
                Title: map.GetValueOrDefault("title") ?? "Opening-Range Manipulation Candle on SPY/QQQ",
                Subtitle: map.GetValueOrDefault("subtitle") ?? "12 months of real-data validation for a popular intraday setup",
                Author: map.GetValueOrDefault("author") ?? "Don Chelladurai",
                Year: int.Parse(map.GetValueOrDefault("year") ?? "2026"),
                GithubUrl: map.GetValueOrDefault("repo") ?? "https://github.com/donchelladurai/RailPen");

            var runs = new List<(string Label, BacktestResult Result)>();

            foreach (var symbol in symbols)
            {
                foreach (var mode in modes)
                {
                    var label = $"{symbol.Replace("_iex", "").Replace("_sip", "")} {mode}";
                    log.LogInformation("→ {Label}", label);
                    var result = await RunBacktestAsync(symbol, mode, dataPath, timeframe, capital);
                    runs.Add((label, result));
                    log.LogInformation("  {Trades} trades, net ${Net:F2}, win {WinRate:F1}%",
                        result.Metrics.TotalTrades, result.TotalReturn, result.Metrics.WinRate * 100m);
                }
            }

            await AnalysisReportWriter.WriteAsync(runs, meta, outputPath);
            log.LogInformation("Analysis report written to {Path}", outputPath);
            return 0;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Analyse failed: {Msg}", ex.Message);
            return 1;
        }
    }

    private static async Task<BacktestResult> RunBacktestAsync(
        string symbol, BreakoutMode mode, string dataPath, Timeframe tf, decimal capital)
    {
        // Same relaxed filter config as the "RELAXED" launch profiles, so the
        // analysis artifact reflects the same data the write-up talks about.
        var strategy = new OpeningRangeManipulationStrategy(
            symbol,
            new OpeningRangeManipulationStrategyConfig
            {
                OpeningRangeMinutes = 15,
                OpeningRangeValidForMinutes = 90,
                MinCandleSizeRatioOfDailyAtr = 0.10m,
                AtrPeriod = 14,
                RequireConfirmationCandle = false,
                Mode = mode,
                SignalBodyMultiplier = 1.2m,
                MinBodyRatio = 0.45m,
                MinVolumeMultiplier = 0m,
                UseHigherTimeframeFilter = false,
                HtfMaPeriod = 20,
                RiskPerTrade = 0.01m,
                MaxTranches = 3,
                MinRMultipleForFirstAdd = 1.0m,
                TrancheSizeMultiplier = 0.6m,
                LockInRMultipleOnAdd = 0.5m,
            });

        var costs = new ExecutionCosts
        {
            SpreadPerSide = 0.005m,
            CommissionPerContractPerSide = 0m,
            StopSlippage = 0.02m,
            DailyFinancingRate = 0m,
        };

        var data = new CsvMarketDataProvider(dataPath, tf);
        var executor = new BacktestExecutor(capital, costs);
        var engine = new BacktestEngine(data, strategy, executor);

        return await engine.RunAsync(new BacktestConfig
        {
            Symbol = symbol,
            Timeframe = tf,
            StartingCapital = capital,
            HistoryWindow = 200,
        });
    }
}
