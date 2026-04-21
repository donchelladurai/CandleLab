namespace CandleLab.Strategies;

/// <summary>
/// Configuration for the One Candle scalping strategy with breakout-based pyramiding.
/// All thresholds are tuned per instrument — defaults are reasonable starting points
/// for a major index on a 5-min timeframe.
/// </summary>
public sealed record OneCandleStrategyConfig
{
    /// <summary>
    /// How many recent candles to average for the "signal candle" size threshold.
    /// </summary>
    public int LookbackForAverage { get; init; } = 20;

    /// <summary>
    /// A candle qualifies as a signal if its body is at least this multiple of the
    /// recent average body. 1.5x is a reasonable starting point; raising it filters
    /// for stronger setups but reduces trade frequency.
    /// </summary>
    public decimal SignalBodyMultiplier { get; init; } = 1.5m;

    /// <summary>
    /// Minimum body-to-range ratio for a signal candle. 0.6 means "body is at least
    /// 60% of the candle range" — filters out doji-like candles even if they're large.
    /// </summary>
    public decimal MinBodyRatio { get; init; } = 0.6m;

    /// <summary>
    /// Volume must be at least this multiple of the recent average.
    /// Set to 0 to disable the volume filter.
    /// </summary>
    public decimal MinVolumeMultiplier { get; init; } = 1.2m;

    /// <summary>
    /// How many candles after the signal to wait for the entry trigger
    /// before abandoning the setup.
    /// </summary>
    public int EntryTriggerTimeoutBars { get; init; } = 3;

    /// <summary>
    /// Percentage of account equity to risk on the initial entry. 0.01 = 1%.
    /// </summary>
    public decimal RiskPerTrade { get; init; } = 0.01m;

    /// <summary>
    /// Maximum number of tranches (base + additions). 3 is a sensible cap.
    /// </summary>
    public int MaxTranches { get; init; } = 3;

    /// <summary>
    /// Minimum favourable move before the first pyramid is allowed.
    /// Measured in units of initial risk (R). 1.0 = "up by the size of the stop distance".
    /// </summary>
    public decimal MinRMultipleForFirstAdd { get; init; } = 1.0m;

    /// <summary>
    /// Minimum additional favourable move between subsequent adds, in R.
    /// </summary>
    public decimal MinRMultipleBetweenAdds { get; init; } = 1.0m;

    /// <summary>
    /// Size of each pyramid tranche relative to the base. 1.0 = equal-weight,
    /// &lt;1.0 = decreasing (classical pyramid). 0.6 means each add is 60% of base size.
    /// </summary>
    public decimal TrancheSizeMultiplier { get; init; } = 0.6m;

    /// <summary>
    /// When pyramiding, move the stop to lock in at least this R-multiple of profit
    /// on the combined position.
    /// </summary>
    public decimal LockInRMultipleOnAdd { get; init; } = 0.5m;
}
