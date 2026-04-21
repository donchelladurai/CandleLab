using CandleLab.Domain;

namespace CandleLab.Backtesting;

public sealed record EquityPoint(DateTimeOffset Timestamp, decimal Equity, decimal Cash);

public sealed record BacktestResult
{
    public required string StrategyName { get; init; }
    public required string Symbol { get; init; }
    public required DateTimeOffset StartTime { get; init; }
    public required DateTimeOffset EndTime { get; init; }
    public required decimal StartingCapital { get; init; }
    public required decimal EndingEquity { get; init; }
    public required IReadOnlyList<ClosedTrade> Trades { get; init; }
    public required IReadOnlyList<EquityPoint> EquityCurve { get; init; }
    public required BacktestMetrics Metrics { get; init; }

    /// <summary>All bars seen during the backtest, in time order.</summary>
    public IReadOnlyList<Candle> Bars { get; init; } = Array.Empty<Candle>();

    /// <summary>
    /// Strategy snapshot after each bar (aligned index-by-index with <see cref="Bars"/>).
    /// Null if the strategy does not implement IVisualisableStrategy.
    /// </summary>
    public IReadOnlyList<object?> StrategySnapshots { get; init; } = Array.Empty<object?>();

    public decimal TotalReturn => EndingEquity - StartingCapital;
    public decimal TotalReturnPercent =>
        StartingCapital == 0 ? 0 : (EndingEquity - StartingCapital) / StartingCapital * 100m;
}

public sealed record BacktestMetrics
{
    public required int TotalTrades { get; init; }
    public required int WinningTrades { get; init; }
    public required int LosingTrades { get; init; }
    public required decimal WinRate { get; init; }
    public required decimal AverageWin { get; init; }
    public required decimal AverageLoss { get; init; }
    public required decimal ProfitFactor { get; init; }
    public required decimal ExpectancyPerTrade { get; init; }
    public required decimal MaxDrawdown { get; init; }
    public required decimal MaxDrawdownPercent { get; init; }
    public required decimal SharpeRatio { get; init; }
    public required TimeSpan AverageTradeDuration { get; init; }
    public required decimal AveragePyramidTranches { get; init; }
}
