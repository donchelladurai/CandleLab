using CandleLab.Domain;

namespace CandleLab.Strategies;

/// <summary>
/// Opening Range Manipulation Candle reversal strategy.
///
/// FLOW (one shot per trading day):
///   1. Aggregate the first OpeningRangeMinutes (default 15) of bars into a
///      synthetic opening candle.
///   2. Check the "manipulation candle" filter: both wicks must be at least
///      MinWickRatioOfAtr of the 15-min ATR. No rejection at both extremes
///      → no setup today. The "manipulation" framing is narrative; what
///      we're really measuring is elevated rejection symmetry on a volatile
///      open — which tends to precede range-bound sessions.
///   3. Draw a rectangle at the opening candle's [high, low]. Valid for
///      OpeningRangeValidForMinutes from the open.
///   4. Wait for price to close outside the rectangle (either side).
///   5. After a breakout, wait for a strong signal candle in the OPPOSITE
///      direction — the "rejection" or "sweep-and-reverse" signal.
///      Optionally require a second confirmation candle.
///   6. Enter as a stop-order at the rejection candle's far extreme, stop
///      on the near extreme. Example: breakout was up → we wait for a
///      bearish rejection candle → enter short on break of its low,
///      stop at its high.
///   7. Pyramid on continuation (same mechanics as OneCandleStrategy).
///
/// HTF FILTER (optional, enabled by default):
///   Only take trades aligned with the daily regime.
///     Yesterday's close > N-day SMA → longs only
///     Yesterday's close < N-day SMA → shorts only
///   Reduces counter-trend whipsaws at the cost of trade count.
///
/// STATE MANAGEMENT:
///   Per-day phase machine resets on UTC day change. Cross-day state
///   (15-min ATR buffer, daily close history) persists for filter continuity.
///   Position-related state (initial risk, tranche count) is reconstructed
///   from the observed Position on first sight — same pattern as
///   OneCandleStrategy, avoids the sync bug we fixed in v0.
///
/// ASSUMPTIONS:
///   Data is cash-hours-only. First bar of each UTC calendar day is treated
///   as the session open. For futures or 24h instruments this needs
///   re-thinking.
/// </summary>
public sealed class OpeningRangeManipulationStrategy : IStrategy, IVisualisableStrategy
{
    object? IVisualisableStrategy.GetSnapshot() => GetSnapshot();

    private readonly OpeningRangeManipulationStrategyConfig _cfg;
    private readonly string _symbol;

    // Cross-day trackers.
    private readonly Queue<decimal> _recent15MinTrueRanges = new();
    private decimal? _previous15MinClose;
    private readonly List<Candle> _currentFifteenMinSlice = new();
    private readonly Queue<decimal> _recentDailyCloses = new();
    private decimal? _yesterdayClose;
    private decimal? _yesterdayMaAtClose; // SMA computed at yesterday's close

    /// <summary>
    /// Per-minute-of-day rolling volume history. Key = minute of day (0-1439),
    /// value = queue of volume observations from the N most recent sessions
    /// where that minute produced a bar. Size-capped at HtfMaPeriod observations
    /// per slot. Cheap (~80 slots × 20 obs × 8 bytes ≈ 13 KB).
    /// </summary>
    private readonly Dictionary<int, Queue<decimal>> _volumeHistoryByMinute = new();

    // Per-day state.
    private DateOnly? _currentDay;
    private Phase _phase;
    private readonly List<Candle> _openingRangeBars = new();
    private decimal _rectangleTop;
    private decimal _rectangleBottom;
    private DateTimeOffset _rectangleExpiresAt;
    private Side? _breakoutSide;
    private Candle? _pendingConfirmation; // holds a rejection candle while waiting for confirmation

    // Position-tracking (same pattern as OneCandleStrategy).
    private DateTimeOffset? _currentPositionOpenedAt;
    private int _trancheCountThisPosition;
    private decimal _initialRisk;
    private decimal? _lastAddPrice;

    public OpeningRangeManipulationStrategy(string symbol, OpeningRangeManipulationStrategyConfig cfg)
    {
        _symbol = symbol;
        _cfg = cfg;
    }

    public string Name =>
        $"OpeningRange{_cfg.Mode}" + (_cfg.UseHigherTimeframeFilter ? "+HTF" : "");

    public IEnumerable<Signal> OnCandle(IStrategyContext ctx)
    {
        var candle = ctx.CurrentCandle;
        var day = DateOnly.FromDateTime(candle.Timestamp.UtcDateTime);

        // ── Cross-day bookkeeping (runs every bar) ───────────────────────
        if (_currentDay != day)
        {
            OnDayChange(day);
        }
        UpdateFifteenMinAggregation(candle);

        // We always track the latest close as today's close-so-far; it
        // becomes "yesterday's close" when the next day's bar arrives.
        _todayLatestClose = candle.Close;

        // Relative-volume tracker: keep a rolling history of volume observations
        // for each minute-of-day slot. "Is this bar's volume high?" then becomes
        // "is it high vs this same minute over the last N sessions?" — which
        // correctly handles the opening spike and late-day drift.
        UpdateMinuteOfDayVolumeHistory(candle);

        // ── Position management (pyramid) takes precedence ───────────────
        if (ctx.OpenPosition is { } pos)
        {
            if (_currentPositionOpenedAt != pos.OpenedAt)
            {
                _currentPositionOpenedAt = pos.OpenedAt;
                _initialRisk = Math.Abs(pos.Tranches[0].EntryPrice - pos.StopLoss);
                _trancheCountThisPosition = 1;
                _lastAddPrice = null;
            }
            var pyramid = TryPyramid(ctx, pos);
            if (pyramid is not null) yield return pyramid;
            yield break;
        }

        if (_currentPositionOpenedAt.HasValue)
        {
            _currentPositionOpenedAt = null;
            _trancheCountThisPosition = 0;
            _initialRisk = 0m;
            _lastAddPrice = null;
        }

        // ── Per-day phase machine ────────────────────────────────────────
        switch (_phase)
        {
            case Phase.AggregatingOpeningRange:
                _openingRangeBars.Add(candle);
                CheckOpeningRangeComplete();
                break;

            case Phase.RectangleActive:
                if (candle.Timestamp >= _rectangleExpiresAt)
                {
                    if (DebugLog)
                        Console.WriteLine($"[{candle.Timestamp:d}] Rectangle expired without breakout");
                    _phase = Phase.Done;
                    break;
                }
                if (candle.Close > _rectangleTop)
                {
                    _breakoutSide = Side.Long;
                    _phase = Phase.AwaitingRejection;
                    if (DebugLog)
                        Console.WriteLine($"[{candle.Timestamp:HH:mm}] UP-breakout @ {candle.Close:F2} > {_rectangleTop:F2}");
                }
                else if (candle.Close < _rectangleBottom)
                {
                    _breakoutSide = Side.Short;
                    _phase = Phase.AwaitingRejection;
                    if (DebugLog)
                        Console.WriteLine($"[{candle.Timestamp:HH:mm}] DOWN-breakout @ {candle.Close:F2} < {_rectangleBottom:F2}");
                }
                break;

            case Phase.AwaitingRejection:
                if (candle.Timestamp >= _rectangleExpiresAt)
                {
                    if (DebugLog)
                        Console.WriteLine($"[{candle.Timestamp:d}] Rectangle expired without rejection");
                    _phase = Phase.Done;
                    break;
                }
                var entry = TryBuildEntry(ctx, candle);
                if (entry is not null)
                {
                    _phase = Phase.Done;
                    yield return entry;
                }
                break;
        }
    }

    private decimal _todayLatestClose; // updated each bar

    // ─── Day change handling ────────────────────────────────────────────

    private void OnDayChange(DateOnly newDay)
    {
        // Finalise yesterday's data for the HTF filter.
        if (_currentDay.HasValue && _todayLatestClose > 0m)
        {
            _recentDailyCloses.Enqueue(_todayLatestClose);
            while (_recentDailyCloses.Count > _cfg.HtfMaPeriod)
            {
                _recentDailyCloses.Dequeue();
            }

            // Snapshot the MA at yesterday's close for today's decisions.
            _yesterdayClose = _todayLatestClose;
            _yesterdayMaAtClose = _recentDailyCloses.Count >= _cfg.HtfMaPeriod
                ? _recentDailyCloses.Average()
                : null;
        }

        // Reset per-day state for the new day.
        _currentDay = newDay;
        _phase = Phase.AggregatingOpeningRange;
        _openingRangeBars.Clear();
        _rectangleTop = 0m;
        _rectangleBottom = 0m;
        _rectangleExpiresAt = default;
        _breakoutSide = null;
        _pendingConfirmation = null;

        // Discard any partial 15-min slice from the previous session.
        // _previous15MinClose is kept intact — it's the close of the last
        // *completed* 15-min bar, and legitimately captures the overnight
        // gap in the next TR calculation.
        _currentFifteenMinSlice.Clear();
    }

    // ─── Opening-range aggregation ──────────────────────────────────────

    private void CheckOpeningRangeComplete()
    {
        var barDurationMinutes = (int)_openingRangeBars[0].Timeframe.ToTimeSpan().TotalMinutes;
        if (barDurationMinutes == 0) barDurationMinutes = 5; // safety
        var barsNeeded = _cfg.OpeningRangeMinutes / barDurationMinutes;
        if (_openingRangeBars.Count < barsNeeded) return;

        // Build the synthetic opening candle.
        var first = _openingRangeBars[0];
        var last = _openingRangeBars[^1];
        var top = _openingRangeBars.Max(b => b.High);
        var bottom = _openingRangeBars.Min(b => b.Low);
        var open = first.Open;
        var close = last.Close;

        var upperWick = top - Math.Max(open, close);
        var lowerWick = Math.Min(open, close) - bottom;

        var atr = CurrentFifteenMinAtr();
        if (atr is null || atr <= 0m)
        {
            if (DebugLog)
                Console.WriteLine($"[{first.Timestamp:d}] OR skipped: ATR not warm (have {_recent15MinTrueRanges.Count})");
            _phase = Phase.Done;
            return;
        }

        var threshold = atr.Value * _cfg.MinWickRatioOfAtr;
        if (upperWick < threshold || lowerWick < threshold)
        {
            if (DebugLog)
                Console.WriteLine($"[{first.Timestamp:d}] OR skipped: wicks U={upperWick:F2} L={lowerWick:F2} < thr={threshold:F2} (ATR={atr:F2})");
            _phase = Phase.Done;
            return;
        }

        _rectangleTop = top;
        _rectangleBottom = bottom;
        _rectangleExpiresAt = first.Timestamp + TimeSpan.FromMinutes(_cfg.OpeningRangeValidForMinutes);
        _phase = Phase.RectangleActive;
        if (DebugLog)
            Console.WriteLine($"[{first.Timestamp:d}] Rectangle active: [{_rectangleBottom:F2}, {_rectangleTop:F2}] expires {_rectangleExpiresAt:HH:mm} (ATR={atr:F2})");
    }

    /// <summary>Toggle for diagnostic output during backtests.</summary>
    public static bool DebugLog { get; set; } = false;

    // ─── Rejection candle + entry construction ──────────────────────────

    private EntrySignal? TryBuildEntry(IStrategyContext ctx, Candle candle)
    {
        if (_breakoutSide is null) return null;

        // Signal direction depends on mode:
        //   Reversal    → opposite to breakout (fade the move)
        //   Continuation → same as breakout (join the move)
        var signalSide = _cfg.Mode == BreakoutMode.Reversal
            ? _breakoutSide.Value.Opposite()
            : _breakoutSide.Value;

        if (!IsRejectionSignal(ctx, candle, signalSide))
        {
            _pendingConfirmation = null; // failed to confirm
            return null;
        }

        if (_cfg.RequireConfirmationCandle && _pendingConfirmation is null)
        {
            _pendingConfirmation = candle;
            return null;
        }

        var signalCandle = _pendingConfirmation ?? candle;

        if (_cfg.UseHigherTimeframeFilter && !HtfPermits(signalSide))
        {
            return null;
        }

        // Entry/stop placement. In both modes the trigger is the candle's far
        // extreme in the signal direction and the stop is the near extreme.
        //   Long:  trigger = high, stop = low
        //   Short: trigger = low,  stop = high
        var trigger = signalSide == Side.Long ? signalCandle.High : signalCandle.Low;
        var stop    = signalSide == Side.Long ? signalCandle.Low  : signalCandle.High;

        var stopDistance = Math.Abs(trigger - stop);
        if (stopDistance == 0m) return null;

        var riskBudget = ctx.AccountEquity * _cfg.RiskPerTrade;
        var quantity = (int)Math.Floor(riskBudget / stopDistance);
        if (quantity < 1) return null;

        var expiresAt = candle.Timestamp
            + candle.Timeframe.ToTimeSpan() * _cfg.EntryTriggerTimeoutBars;

        return new EntrySignal(
            Timestamp: candle.Timestamp,
            Symbol: _symbol,
            Side: signalSide,
            TriggerPrice: trigger,
            StopLoss: stop,
            TakeProfit: null,
            Quantity: quantity,
            Reason: $"ORM {_cfg.Mode} {signalSide} after {_breakoutSide} breakout")
        {
            ExpiresAt = expiresAt,
        };
    }

    private bool IsRejectionSignal(IStrategyContext ctx, Candle candle, Side expectedDirection)
    {
        if (ctx.History.Count < _cfg.SignalLookback) return false;
        if (candle.IsDoji) return false;

        var actualDirection = candle.IsBullish ? Side.Long : Side.Short;
        if (actualDirection != expectedDirection) return false;

        // Body size: last-N-bars average is fine; body size is less time-of-day
        // dependent than volume and the opening spike doesn't distort it badly.
        var recent = ctx.History.TakeLast(_cfg.SignalLookback).ToList();
        var avgBody = recent.Average(c => c.BodySize);
        var bodyOk = candle.BodySize >= avgBody * _cfg.SignalBodyMultiplier;
        var ratioOk = candle.BodyRatio >= _cfg.MinBodyRatio;

        // Volume: per-minute-of-day historical average. A 14:35 bar is compared
        // to other 14:35 bars, not to the opening spike at 13:30.
        bool volumeOk;
        decimal? rvolReference = null;
        if (_cfg.MinVolumeMultiplier <= 0m)
        {
            volumeOk = true;
        }
        else
        {
            var slot = candle.Timestamp.UtcDateTime.Hour * 60
                       + candle.Timestamp.UtcDateTime.Minute;
            if (!_volumeHistoryByMinute.TryGetValue(slot, out var slotHistory)
                || slotHistory.Count < 3)
            {
                // Not enough same-minute history yet — fall back to "pass".
                // Three observations is enough that one outlier won't dominate.
                volumeOk = true;
            }
            else
            {
                // Exclude the current observation (just pushed) from its own
                // reference average. We want "is this bar high relative to
                // PAST same-minute bars?"
                var prior = slotHistory.Take(slotHistory.Count - 1);
                var avgSlotVol = prior.Average();
                rvolReference = avgSlotVol * _cfg.MinVolumeMultiplier;
                volumeOk = (decimal)candle.Volume >= rvolReference;
            }
        }

        if (DebugLog && !(bodyOk && ratioOk && volumeOk))
        {
            var volDesc = rvolReference is null
                ? "(warmup)"
                : $"vs {rvolReference:F0}";
            Console.WriteLine(
                $"  [{candle.Timestamp:HH:mm}] rejection {expectedDirection} rejected: " +
                $"body {candle.BodySize:F2} vs {avgBody * _cfg.SignalBodyMultiplier:F2} ({(bodyOk ? "✓" : "✗")}), " +
                $"ratio {candle.BodyRatio:F2} vs {_cfg.MinBodyRatio} ({(ratioOk ? "✓" : "✗")}), " +
                $"vol {candle.Volume} {volDesc} ({(volumeOk ? "✓" : "✗")})");
        }

        return bodyOk && ratioOk && volumeOk;
    }

    private void UpdateMinuteOfDayVolumeHistory(Candle candle)
    {
        var slot = candle.Timestamp.UtcDateTime.Hour * 60
                   + candle.Timestamp.UtcDateTime.Minute;
        if (!_volumeHistoryByMinute.TryGetValue(slot, out var q))
        {
            q = new Queue<decimal>();
            _volumeHistoryByMinute[slot] = q;
        }
        q.Enqueue(candle.Volume);
        // Cap per-slot history at HtfMaPeriod sessions' worth of observations.
        while (q.Count > _cfg.HtfMaPeriod)
        {
            q.Dequeue();
        }
    }

    private bool HtfPermits(Side side)
    {
        if (_yesterdayClose is null || _yesterdayMaAtClose is null)
        {
            // Warmup: not enough daily history. Skip — be conservative.
            return false;
        }

        var bullishRegime = _yesterdayClose.Value > _yesterdayMaAtClose.Value;
        return side == Side.Long ? bullishRegime : !bullishRegime;
    }

    // ─── 15-min aggregation + ATR ───────────────────────────────────────

    private void UpdateFifteenMinAggregation(Candle fiveMinCandle)
    {
        _currentFifteenMinSlice.Add(fiveMinCandle);
        var barsPerSlice = _cfg.OpeningRangeMinutes
            / Math.Max(1, (int)fiveMinCandle.Timeframe.ToTimeSpan().TotalMinutes);
        if (_currentFifteenMinSlice.Count < barsPerSlice) return;

        // Completed a 15-min slice. Compute its true range and push to rolling buffer.
        var high = _currentFifteenMinSlice.Max(c => c.High);
        var low = _currentFifteenMinSlice.Min(c => c.Low);
        var close = _currentFifteenMinSlice[^1].Close;

        var trueRange = _previous15MinClose is null
            ? high - low
            : Math.Max(high - low,
                Math.Max(
                    Math.Abs(high - _previous15MinClose.Value),
                    Math.Abs(low - _previous15MinClose.Value)));

        _recent15MinTrueRanges.Enqueue(trueRange);
        while (_recent15MinTrueRanges.Count > _cfg.AtrPeriod)
        {
            _recent15MinTrueRanges.Dequeue();
        }
        _previous15MinClose = close;
        _currentFifteenMinSlice.Clear();
    }

    private decimal? CurrentFifteenMinAtr()
    {
        if (_recent15MinTrueRanges.Count < _cfg.AtrPeriod) return null;
        return _recent15MinTrueRanges.Average();
    }

    // ─── Pyramid (near-identical to OneCandleStrategy) ──────────────────

    private PyramidSignal? TryPyramid(IStrategyContext ctx, Position pos)
    {
        if (_trancheCountThisPosition >= _cfg.MaxTranches) return null;
        if (_initialRisk <= 0m) return null;

        var candle = ctx.CurrentCandle;
        var isLong = pos.Side == Side.Long;
        var extreme = isLong ? ctx.HighSinceEntry : ctx.LowSinceEntry;
        if (extreme is null) return null;

        var madeNewExtreme = isLong
            ? candle.Close >= extreme.Value && candle.High >= extreme.Value
            : candle.Close <= extreme.Value && candle.Low <= extreme.Value;
        if (!madeNewExtreme) return null;

        var referencePrice = _lastAddPrice ?? pos.AverageEntry;
        var movement = (candle.Close - referencePrice) * pos.Side.Sign();

        var requiredR = _lastAddPrice is null
            ? _cfg.MinRMultipleForFirstAdd
            : _cfg.MinRMultipleBetweenAdds;
        if (movement < requiredR * _initialRisk) return null;

        var baseSize = pos.Tranches[0].Quantity;
        var addQty = Math.Max(1, (int)Math.Floor(baseSize * _cfg.TrancheSizeMultiplier));

        var newStop = ComputeLockInStop(pos, addQty, candle.Close);
        _trancheCountThisPosition++;
        _lastAddPrice = candle.Close;

        return new PyramidSignal(
            Timestamp: candle.Timestamp,
            Symbol: _symbol,
            TriggerPrice: candle.Close,
            NewStopLoss: newStop,
            Quantity: addQty,
            Reason: $"ORM pyramid #{_trancheCountThisPosition - 1}");
    }

    private decimal ComputeLockInStop(Position pos, int addQty, decimal currentPrice)
    {
        var futureTranches = pos.Tranches
            .Append(new Tranche(DateTimeOffset.MinValue, currentPrice, addQty, "proj"))
            .ToList();
        var totalQty = futureTranches.Sum(t => t.Quantity);
        var weightedEntry = futureTranches.Sum(t => t.EntryPrice * t.Quantity) / totalQty;
        var targetLockIn = _cfg.LockInRMultipleOnAdd * _initialRisk;
        var candidateStop = weightedEntry + pos.Side.Sign() * targetLockIn;

        return pos.Side == Side.Long
            ? Math.Max(pos.StopLoss, candidateStop)
            : Math.Min(pos.StopLoss, candidateStop);
    }

    // ─── Introspection helpers for testing/UI ───────────────────────────

    /// <summary>Snapshot of strategy state exposed for diagnostics/UI rendering.</summary>
    public StrategySnapshot GetSnapshot() => new(
        Phase: _phase,
        RectangleTop: _phase is Phase.RectangleActive or Phase.AwaitingRejection ? _rectangleTop : (decimal?)null,
        RectangleBottom: _phase is Phase.RectangleActive or Phase.AwaitingRejection ? _rectangleBottom : (decimal?)null,
        RectangleExpiresAt: _phase is Phase.RectangleActive or Phase.AwaitingRejection ? _rectangleExpiresAt : (DateTimeOffset?)null,
        BreakoutSide: _breakoutSide,
        CurrentFifteenMinAtr: CurrentFifteenMinAtr(),
        YesterdayClose: _yesterdayClose,
        YesterdayMa: _yesterdayMaAtClose);

    public enum Phase
    {
        /// <summary>Day hasn't produced a session-open bar yet.</summary>
        AggregatingOpeningRange,

        /// <summary>Opening range drawn; watching for a break.</summary>
        RectangleActive,

        /// <summary>Price broke out; watching for a rejection signal.</summary>
        AwaitingRejection,

        /// <summary>Setup either fired or was abandoned. No more attempts today.</summary>
        Done,
    }

    public sealed record StrategySnapshot(
        Phase Phase,
        decimal? RectangleTop,
        decimal? RectangleBottom,
        DateTimeOffset? RectangleExpiresAt,
        Side? BreakoutSide,
        decimal? CurrentFifteenMinAtr,
        decimal? YesterdayClose,
        decimal? YesterdayMa);
}
