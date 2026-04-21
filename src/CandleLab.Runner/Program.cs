using CandleLab.Backtesting;
using CandleLab.Domain;
using CandleLab.Execution;
using CandleLab.MarketData;
using CandleLab.Strategies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CandleLab.Runner;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // First positional arg is an optional verb. Default = run a backtest.
        if (args.Length > 0 && string.Equals(args[0], "fetch", StringComparison.OrdinalIgnoreCase))
        {
            return await FetchCommand.RunAsync(args.Skip(1).ToArray());
        }
        if (args.Length > 0 && string.Equals(args[0], "analyse", StringComparison.OrdinalIgnoreCase))
        {
            return await AnalyseCommand.RunAsync(args.Skip(1).ToArray());
        }

        var runArgs = RunArgs.Parse(args);

        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        });

        builder.Services.AddSingleton<IMarketDataProvider>(_ =>
            new CsvMarketDataProvider(runArgs.DataPath, runArgs.Timeframe));

        builder.Services.AddTransient<IStrategy>(_ => BuildStrategy(runArgs));

        var costs = new ExecutionCosts
        {
            SpreadPerSide = runArgs.SpreadPerSide,
            CommissionPerContractPerSide = 0m,
            StopSlippage = runArgs.StopSlippage,
            DailyFinancingRate = 0m,
        };

        builder.Services.AddTransient<IExecutor>(_ => new BacktestExecutor(runArgs.StartingCapital, costs));
        builder.Services.AddTransient<BacktestEngine>();

        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILogger<object>>();
        var engine = host.Services.GetRequiredService<BacktestEngine>();
        var strategy = host.Services.GetRequiredService<IStrategy>();

        logger.LogInformation("Starting backtest: {Strategy} on {Symbol} ({Timeframe})",
            strategy.Name, runArgs.Symbol, runArgs.Timeframe);

        var config = new BacktestConfig
        {
            Symbol = runArgs.Symbol,
            Timeframe = runArgs.Timeframe,
            StartingCapital = runArgs.StartingCapital,
            From = runArgs.From,
            To = runArgs.To,
            HistoryWindow = 200,
        };

        try
        {
            var result = await engine.RunAsync(config);

            Console.WriteLine();
            Console.WriteLine(ReportWriter.FormatSummary(result));

            var journalPath = Path.Combine(runArgs.OutputDir, "trades.csv");
            var equityPath = Path.Combine(runArgs.OutputDir, "equity.csv");
            var reportPath = Path.Combine(runArgs.OutputDir, "report.html");
            Directory.CreateDirectory(runArgs.OutputDir);

            await ReportWriter.WriteTradeJournalAsync(result, journalPath);
            await ReportWriter.WriteEquityCurveAsync(result, equityPath);
            await HtmlReportWriter.WriteAsync(result, reportPath);

            logger.LogInformation("Trade journal : {Path}", journalPath);
            logger.LogInformation("Equity curve  : {Path}", equityPath);
            logger.LogInformation("HTML report   : {Path}", reportPath);

            return 0;
        }
        catch (FileNotFoundException ex)
        {
            logger.LogError("{Message}", ex.Message);
            logger.LogError("Expected file: <data-dir>/<symbol>_<timeframe>.csv");
            logger.LogError("e.g. data/SPX_FiveMinutes.csv");
            return 1;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Backtest failed");
            return 2;
        }
    }

    private static IStrategy BuildStrategy(RunArgs a) => a.Strategy switch
    {
        StrategyChoice.OneCandle => new OneCandleStrategy(a.Symbol, new OneCandleStrategyConfig
        {
            RiskPerTrade = 0.01m,
            SignalBodyMultiplier = 1.5m,
            MinBodyRatio = 0.6m,
            MinVolumeMultiplier = 1.2m,
            MaxTranches = 3,
            MinRMultipleForFirstAdd = 1.0m,
            MinRMultipleBetweenAdds = 1.0m,
            TrancheSizeMultiplier = 0.6m,
            LockInRMultipleOnAdd = 0.5m,
        }),

        StrategyChoice.OpeningRange => BuildOpeningRangeStrategy(a),

        _ => throw new ArgumentOutOfRangeException(nameof(a.Strategy)),
    };

    private static IStrategy BuildOpeningRangeStrategy(RunArgs a)
    {
        OpeningRangeManipulationStrategy.DebugLog = a.Debug;
        return new OpeningRangeManipulationStrategy(
            a.Symbol,
            new OpeningRangeManipulationStrategyConfig
            {
                OpeningRangeMinutes = 15,
                OpeningRangeValidForMinutes = 90,
                MinWickRatioOfAtr = a.WickRatio,
                AtrPeriod = 14,
                RequireConfirmationCandle = false,
                Mode = a.Mode,
                SignalBodyMultiplier = a.BodyMult,
                MinBodyRatio = a.BodyRatio,
                MinVolumeMultiplier = a.VolMult,
                UseHigherTimeframeFilter = !a.NoHtf,
                HtfMaPeriod = 20,
                RiskPerTrade = 0.01m,
                MaxTranches = 3,
                MinRMultipleForFirstAdd = 1.0m,
                TrancheSizeMultiplier = 0.6m,
                LockInRMultipleOnAdd = 0.5m,
            });
    }
}

internal enum StrategyChoice
{
    OneCandle,
    OpeningRange,
}

internal sealed record RunArgs(
    string DataPath,
    string OutputDir,
    string Symbol,
    Timeframe Timeframe,
    decimal StartingCapital,
    decimal SpreadPerSide,
    decimal StopSlippage,
    DateTimeOffset? From,
    DateTimeOffset? To,
    StrategyChoice Strategy,
    bool Debug,
    bool NoHtf,
    // OpeningRange filter thresholds (exposed for parameter sweeps).
    decimal WickRatio,
    decimal BodyRatio,
    decimal BodyMult,
    decimal VolMult,
    BreakoutMode Mode)
{
    public static RunArgs Parse(string[] args)
    {
        var map = args
            .Where(a => a.Contains('=', StringComparison.Ordinal))
            .Select(a => a.Split('=', 2))
            .ToDictionary(p => p[0].TrimStart('-').ToLowerInvariant(), p => p[1]);

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        decimal Dec(string key, string fallback) =>
            decimal.Parse(map.GetValueOrDefault(key) ?? fallback, inv);

        return new RunArgs(
            DataPath: map.GetValueOrDefault("data") ?? "data",
            OutputDir: map.GetValueOrDefault("out") ?? "output",
            Symbol: map.GetValueOrDefault("symbol") ?? "SPX",
            Timeframe: Enum.Parse<Timeframe>(
                map.GetValueOrDefault("tf") ?? "FiveMinutes", ignoreCase: true),
            StartingCapital: Dec("capital", "10000"),
            // Defaults are SPY-sized (~$500 instrument). For SPX synthetic data
            // at ~$4700, pass spread=0.5 slippage=1.0 explicitly.
            SpreadPerSide: Dec("spread", "0.005"),
            StopSlippage: Dec("slippage", "0.02"),
            From: map.TryGetValue("from", out var f) ? DateTimeOffset.Parse(f) : null,
            To: map.TryGetValue("to", out var t) ? DateTimeOffset.Parse(t) : null,
            Strategy: ParseStrategy(map.GetValueOrDefault("strategy") ?? "onecandle"),
            Debug: bool.Parse(map.GetValueOrDefault("debug") ?? "false"),
            NoHtf: bool.Parse(map.GetValueOrDefault("nohtf") ?? "false"),
            WickRatio: Dec("wickratio", "0.25"),
            BodyRatio: Dec("bodyratio", "0.6"),
            BodyMult: Dec("bodymult", "1.5"),
            VolMult: Dec("volmult", "1.2"),
            Mode: Enum.Parse<BreakoutMode>(
                map.GetValueOrDefault("mode") ?? "Reversal", ignoreCase: true));
    }

    private static StrategyChoice ParseStrategy(string raw) => raw.ToLowerInvariant() switch
    {
        "onecandle" or "onecandlestrategy" or "1c" => StrategyChoice.OneCandle,
        "openingrange" or "orm" or "openingrangemanipulation" => StrategyChoice.OpeningRange,
        _ => throw new ArgumentException(
            $"Unknown strategy '{raw}'. Valid: onecandle | openingrange"),
    };
}
