namespace CandleLab.Strategies;

/// <summary>
/// How to trade the signal candle after the opening-range breakout.
/// </summary>
public enum BreakoutMode
{
    /// <summary>
    /// Original interpretation. After an up-breakout, wait for a bearish
    /// rejection candle → enter SHORT (stop at rejection's high). After a
    /// down-breakout, wait for a bullish rejection → enter LONG. This is
    /// the "failed breakout / sweep and reverse" pattern.
    /// </summary>
    Reversal,

    /// <summary>
    /// After an up-breakout, wait for a bullish continuation candle → enter
    /// LONG (stop at continuation candle's low). After a down-breakout, wait
    /// for a bearish continuation → enter SHORT. Classic "breakout joined
    /// after confirmation" pattern.
    /// </summary>
    Continuation,
}

/// <summary>
/// Configuration for the Opening Range Manipulation Candle strategy.
///
/// Defaults are tuned for SPY/QQQ 5-min bars on US cash hours with the
/// first 15 minutes forming the opening range. Thresholds are a starting
/// point — real data will tell us if they need tightening.
/// </summary>
public sealed record OpeningRangeManipulationStrategyConfig
{
    // ─── Opening range ──────────────────────────────────────────────────

    /// <summary>
    /// Length of the opening range in minutes. Must be a whole multiple of
    /// the bar timeframe (e.g. 15 for three 5-min bars).
    /// </summary>
    public int OpeningRangeMinutes { get; init; } = 15;

    /// <summary>
    /// How long the opening range's rectangle stays valid as a reference
    /// level, measured from the range's start. Beyond this, we abandon
    /// the setup for the day.
    /// </summary>
    public int OpeningRangeValidForMinutes { get; init; } = 90;

    // ─── Manipulation candle filter (Step 2) ────────────────────────────

    /// <summary>
    /// The opening 15-min candle's total size (high - low) must be at least
    /// this fraction of the 14-day ATR for the setup to qualify. 0.25 = 25%.
    /// The idea: the opening range needs to be a "meaningful" move on a daily
    /// scale, not a quiet overnight drift. This replaces the v0.1-v0.7
    /// wick-based filter, which was a misinterpretation of the original spec.
    /// </summary>
    public decimal MinCandleSizeRatioOfDailyAtr { get; init; } = 0.25m;

    /// <summary>
    /// Daily ATR lookback. Standard is 14 daily bars.
    /// </summary>
    public int DailyAtrPeriod { get; init; } = 14;

    /// <summary>
    /// Rejection-candle filter: ATR lookback in 15-min bars, used only for
    /// body-size normalisation in downstream signal checks.
    /// </summary>
    public int AtrPeriod { get; init; } = 14;

    // ─── Rejection signal candle (Step 5) ───────────────────────────────

    /// <summary>
    /// Require a second candle after the rejection to confirm the reversal
    /// before entering. When false (the default), we enter on the rejection
    /// candle itself — more trades, more fakeouts.
    /// </summary>
    public bool RequireConfirmationCandle { get; init; } = false;

    /// <summary>
    /// Reversal (fade the breakout) or Continuation (join the breakout after
    /// confirmation). Default Reversal matches v0.1+ behaviour.
    /// </summary>
    public BreakoutMode Mode { get; init; } = BreakoutMode.Reversal;

    /// <summary>Rejection signal: body size vs recent average body.</summary>
    public decimal SignalBodyMultiplier { get; init; } = 1.5m;

    /// <summary>Rejection signal: minimum body/range ratio.</summary>
    public decimal MinBodyRatio { get; init; } = 0.6m;

    /// <summary>Rejection signal: volume vs recent average volume. 0 disables.</summary>
    public decimal MinVolumeMultiplier { get; init; } = 1.2m;

    /// <summary>How many recent 5-min bars to average for signal-candle thresholds.</summary>
    public int SignalLookback { get; init; } = 20;

    /// <summary>
    /// Entry stop-order expiry from the rejection candle, in bars.
    /// If the rejection's far-side is not breached within this many bars,
    /// the setup is abandoned.
    /// </summary>
    public int EntryTriggerTimeoutBars { get; init; } = 3;

    // ─── Higher-timeframe filter (hybrid #2) ────────────────────────────

    /// <summary>
    /// Only take trades aligned with the daily regime:
    ///   yesterday's close above the N-day SMA → longs only
    ///   yesterday's close below the N-day SMA → shorts only
    /// Setup skipped when the regime is not yet established (warmup).
    /// </summary>
    public bool UseHigherTimeframeFilter { get; init; } = true;

    /// <summary>Lookback for the daily SMA used by the HTF filter.</summary>
    public int HtfMaPeriod { get; init; } = 20;

    // ─── Sizing & pyramiding (same shape as OneCandleStrategy) ──────────

    /// <summary>Percentage of account equity risked on initial entry. 0.01 = 1%.</summary>
    public decimal RiskPerTrade { get; init; } = 0.01m;

    /// <summary>Maximum tranches (base + adds).</summary>
    public int MaxTranches { get; init; } = 3;

    /// <summary>Min favourable move (in R) before the first pyramid add.</summary>
    public decimal MinRMultipleForFirstAdd { get; init; } = 1.0m;

    /// <summary>Min additional favourable move (in R) between subsequent adds.</summary>
    public decimal MinRMultipleBetweenAdds { get; init; } = 1.0m;

    /// <summary>Size of each pyramid tranche as a multiple of the base.</summary>
    public decimal TrancheSizeMultiplier { get; init; } = 0.6m;

    /// <summary>On each add, lock in at least this R-multiple of profit.</summary>
    public decimal LockInRMultipleOnAdd { get; init; } = 0.5m;
}
