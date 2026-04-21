using CandleLab.Domain;
using CandleLab.Execution;
using CandleLab.MarketData;
using CandleLab.Strategies;

namespace CandleLab.Backtesting;

public sealed record BacktestConfig
{
    public required string Symbol { get; init; }
    public required Timeframe Timeframe { get; init; }
    public required decimal StartingCapital { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }

    /// <summary>
    /// How many candles of history to retain for the strategy. Must be at least
    /// the strategy's longest lookback. Keeping this bounded prevents unbounded growth.
    /// </summary>
    public int HistoryWindow { get; init; } = 200;

    /// <summary>
    /// If set, bars whose timestamps fall outside the US regular session
    /// (9:30-16:00 America/New_York) are skipped. Pass false when backtesting
    /// non-US markets, futures, or crypto where 24h sessions are meaningful.
    /// </summary>
    public bool ClipToUsRegularSession { get; init; } = true;
}

/// <summary>
/// Centralised helper for deciding whether a bar falls inside the US equity
/// regular cash session. Respects DST automatically via TimeZoneInfo.
/// </summary>
internal static class UsRegularSession
{
    private static readonly TimeZoneInfo Eastern =
        TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");

    public static bool Contains(DateTimeOffset utc)
    {
        var et = TimeZoneInfo.ConvertTime(utc, Eastern);
        // 9:30:00 inclusive, 16:00:00 exclusive. Matches how standard
        // minute/5-min bars are timestamped at the start of the interval.
        var minutes = et.Hour * 60 + et.Minute;
        return minutes >= 9 * 60 + 30 && minutes < 16 * 60;
    }
}

public sealed class BacktestEngine
{
    private readonly IMarketDataProvider _data;
    private readonly IStrategy _strategy;
    private readonly IExecutor _executor;

    public BacktestEngine(IMarketDataProvider data, IStrategy strategy, IExecutor executor)
    {
        _data = data;
        _strategy = strategy;
        _executor = executor;
    }

    public async Task<BacktestResult> RunAsync(
        BacktestConfig config, CancellationToken ct = default)
    {
        var history = new Queue<Candle>(config.HistoryWindow);
        var equityCurve = new List<EquityPoint>();
        var allClosedTrades = new List<ClosedTrade>();
        var recordedBars = new List<Candle>();
        var recordedSnapshots = new List<object?>();

        var visualisable = _strategy as IVisualisableStrategy;

        DateTimeOffset? firstTimestamp = null;
        DateTimeOffset? lastTimestamp = null;

        await foreach (var candle in _data.StreamCandlesAsync(
            config.Symbol, config.Timeframe, config.From, config.To, ct))
        {
            if (config.ClipToUsRegularSession && !UsRegularSession.Contains(candle.Timestamp))
            {
                continue;
            }

            firstTimestamp ??= candle.Timestamp;
            lastTimestamp = candle.Timestamp;

            // Build the context BEFORE enqueueing the current candle into history —
            // history should contain only prior candles, not the current one.
            var snapshot = _executor.Snapshot;
            var context = new StrategyContext(
                currentCandle: candle,
                history: history.ToArray(),
                openPosition: snapshot.OpenPosition,
                highSinceEntry: snapshot.OpenPosition is null ? null : snapshot.HighSinceEntry,
                lowSinceEntry: snapshot.OpenPosition is null ? null : snapshot.LowSinceEntry,
                accountEquity: snapshot.Equity);

            var signals = _strategy.OnCandle(context).ToList();
            var closedThisBar = _executor.ProcessBar(candle, signals);
            allClosedTrades.AddRange(closedThisBar);

            // Track equity after processing.
            var postSnapshot = _executor.Snapshot;
            equityCurve.Add(new EquityPoint(candle.Timestamp, postSnapshot.Equity, postSnapshot.Cash));

            // Record bar + strategy snapshot for the visualiser.
            recordedBars.Add(candle);
            recordedSnapshots.Add(visualisable?.GetSnapshot());

            // Maintain rolling history window.
            history.Enqueue(candle);
            while (history.Count > config.HistoryWindow)
            {
                history.Dequeue();
            }
        }

        var metrics = MetricsCalculator.Compute(allClosedTrades, equityCurve, config.StartingCapital);

        return new BacktestResult
        {
            StrategyName = _strategy.Name,
            Symbol = config.Symbol,
            StartTime = firstTimestamp ?? DateTimeOffset.MinValue,
            EndTime = lastTimestamp ?? DateTimeOffset.MinValue,
            StartingCapital = config.StartingCapital,
            EndingEquity = _executor.Snapshot.Equity,
            Trades = allClosedTrades,
            EquityCurve = equityCurve,
            Metrics = metrics,
            Bars = recordedBars,
            StrategySnapshots = recordedSnapshots,
        };
    }

    private sealed class StrategyContext : IStrategyContext
    {
        public StrategyContext(
            Candle currentCandle,
            IReadOnlyList<Candle> history,
            Position? openPosition,
            decimal? highSinceEntry,
            decimal? lowSinceEntry,
            decimal accountEquity)
        {
            CurrentCandle = currentCandle;
            History = history;
            OpenPosition = openPosition;
            HighSinceEntry = highSinceEntry;
            LowSinceEntry = lowSinceEntry;
            AccountEquity = accountEquity;
        }

        public Candle CurrentCandle { get; }
        public IReadOnlyList<Candle> History { get; }
        public Position? OpenPosition { get; }
        public decimal? HighSinceEntry { get; }
        public decimal? LowSinceEntry { get; }
        public decimal AccountEquity { get; }
    }
}
