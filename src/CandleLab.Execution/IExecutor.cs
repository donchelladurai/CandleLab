using CandleLab.Domain;

namespace CandleLab.Execution;

/// <summary>
/// Executes signals produced by a strategy. In backtest this simulates fills;
/// in live trading this translates to broker API calls.
/// </summary>
public interface IExecutor
{
    /// <summary>Account state before processing any signals.</summary>
    ExecutorSnapshot Snapshot { get; }

    /// <summary>
    /// Process one bar:
    ///   1. Check stop/take-profit hits against intra-bar range.
    ///   2. Apply any signals that trigger on this bar.
    /// Returns any closed trades that resulted.
    /// </summary>
    IReadOnlyList<ClosedTrade> ProcessBar(Candle candle, IReadOnlyList<Signal> signals);
}

/// <summary>
/// Snapshot of executor state at a point in time.
/// </summary>
public sealed record ExecutorSnapshot(
    decimal Cash,
    decimal Equity,
    Position? OpenPosition,
    decimal HighSinceEntry,
    decimal LowSinceEntry,
    int TotalTrades);

/// <summary>
/// Cost and slippage model for the backtest. Realistic values matter —
/// a zero-cost backtest will mislead you.
/// </summary>
public sealed record ExecutionCosts
{
    /// <summary>
    /// Bid-ask spread in price units (not pips/points). For US 500 at 5800, a
    /// 0.5-point spread = 0.5m. Applied on every entry AND every exit.
    /// </summary>
    public decimal SpreadPerSide { get; init; } = 0.5m;

    /// <summary>
    /// Commission per contract per side (entry and exit). Many CFD brokers have
    /// zero commission on indices but charge it on shares.
    /// </summary>
    public decimal CommissionPerContractPerSide { get; init; } = 0m;

    /// <summary>
    /// Additional slippage modelled on stop-loss hits. In fast moves, stops
    /// routinely slip 1-3 extra points beyond the stop price.
    /// </summary>
    public decimal StopSlippage { get; init; } = 1.0m;

    /// <summary>
    /// Overnight financing charge, as a daily fraction of notional.
    /// 0.0001 = 1 bps/day = ~3.6% p.a. Typical IG-style rate for long positions.
    /// Set to 0 to ignore (fine for intraday-only scalping).
    /// </summary>
    public decimal DailyFinancingRate { get; init; } = 0m;
}
