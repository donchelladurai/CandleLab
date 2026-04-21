using CandleLab.Domain;

namespace CandleLab.Strategies;

/// <summary>
/// One Candle scalping strategy with Method 1 pyramiding (add on new highs/lows).
///
/// ENTRY:
///   1. Identify a "signal candle": body size &gt;= N x recent-average body,
///      body ratio &gt;= threshold, volume &gt;= M x recent-average volume.
///   2. Emit a stop-order at the signal candle's high (long) / low (short)
///      with an expiry of EntryTriggerTimeoutBars × bar duration.
///   3. Stop-loss at the opposite end of the signal candle.
///   4. The executor fills when a later bar's range crosses the trigger,
///      or cancels the order at expiry. The strategy does not duplicate
///      that check — doing so was the source of a look-ahead bug in v0.
///
/// PYRAMID (Method 1 — breakout of new high/low):
///   - Allowed only when price has moved at least MinRMultipleForFirstAdd in favour.
///   - Trigger: current candle's close makes a new extreme since position open.
///   - Each add uses TrancheSizeMultiplier × the base size.
///   - On each add, raise the stop to lock in at least LockInRMultipleOnAdd
///     of profit on the combined position.
///   - Max total tranches capped by MaxTranches.
///
/// EXIT:
///   - Stop-loss hit (handled by executor, not this strategy).
///   - Optional reversal signal (not in v1 — stop-out is the only exit).
///
/// STATE MANAGEMENT:
///   The strategy tracks pyramid state (initial risk, tranche count, last-add price)
///   but deliberately does NOT track "pending entry" state for fills — that belongs
///   to the executor. The strategy detects fills by observing the
///   <see cref="IStrategyContext.OpenPosition"/> transition from null to non-null
///   and reconstructs initial risk from the filled position itself. This keeps
///   strategy state in sync with what actually happened rather than what the
///   strategy predicted would happen.
/// </summary>
public sealed class OneCandleStrategy : IStrategy
{
    private readonly OneCandleStrategyConfig _cfg;
    private readonly string _symbol;

    // Identifier for the currently open position. When this differs from
    // ctx.OpenPosition.OpenedAt, we've just observed a fresh fill and should
    // re-initialise pyramid state from the position itself.
    private DateTimeOffset? _currentPositionOpenedAt;

    // Count of tranches in the current position (1 = base entry only).
    private int _trancheCountThisPosition;

    // Initial stop distance in price units. Used as the "R" for pyramid triggers.
    // Reconstructed from the position on first observation rather than guessed
    // up-front — this way the strategy is never out of sync with the executor.
    private decimal _initialRisk;

    // Price at which the last pyramid add fired (so we require an *additional*
    // MinRMultipleBetweenAdds of favourable movement before the next one).
    private decimal? _lastAddPrice;

    public OneCandleStrategy(string symbol, OneCandleStrategyConfig cfg)
    {
        _symbol = symbol;
        _cfg = cfg;
    }

    public string Name => "OneCandle+Pyramid(M1)";

    public IEnumerable<Signal> OnCandle(IStrategyContext ctx)
    {
        var candle = ctx.CurrentCandle;

        if (ctx.OpenPosition is { } pos)
        {
            // First bar we're observing this position? Initialise pyramid state.
            if (_currentPositionOpenedAt != pos.OpenedAt)
            {
                _currentPositionOpenedAt = pos.OpenedAt;
                _initialRisk = Math.Abs(pos.Tranches[0].EntryPrice - pos.StopLoss);
                _trancheCountThisPosition = 1;
                _lastAddPrice = null;
            }

            var pyramid = TryPyramid(ctx, pos);
            if (pyramid is not null)
            {
                yield return pyramid;
            }

            // No new entries while in a position.
            yield break;
        }

        // No open position — reset any lingering per-position state from a just-closed trade.
        if (_currentPositionOpenedAt.HasValue)
        {
            _currentPositionOpenedAt = null;
            _trancheCountThisPosition = 0;
            _initialRisk = 0m;
            _lastAddPrice = null;
        }

        // Look for a signal candle. If found, emit the entry immediately with
        // an expiry matching the timeout. The executor holds it as a pending
        // stop-order and fills on a later bar's range, or cancels on expiry.
        var setup = TryIdentifySignalCandle(ctx);
        if (setup is null)
        {
            yield break;
        }

        var stopDistance = Math.Abs(setup.TriggerPrice - setup.StopLoss);
        if (stopDistance == 0m) yield break;

        var riskBudget = ctx.AccountEquity * _cfg.RiskPerTrade;
        var quantity = (int)Math.Floor(riskBudget / stopDistance);
        if (quantity < 1) yield break;

        var expiresAt = candle.Timestamp
            + candle.Timeframe.ToTimeSpan() * _cfg.EntryTriggerTimeoutBars;

        yield return new EntrySignal(
            Timestamp: candle.Timestamp,
            Symbol: _symbol,
            Side: setup.Side,
            TriggerPrice: setup.TriggerPrice,
            StopLoss: setup.StopLoss,
            TakeProfit: null,
            Quantity: quantity,
            Reason: $"OneCandle signal @ {candle.Timestamp:u}")
        {
            ExpiresAt = expiresAt,
        };
    }

    private SignalCandleInfo? TryIdentifySignalCandle(IStrategyContext ctx)
    {
        if (ctx.History.Count < _cfg.LookbackForAverage)
        {
            return null; // not enough history yet
        }

        var candle = ctx.CurrentCandle;
        var recent = ctx.History.TakeLast(_cfg.LookbackForAverage).ToList();
        var avgBody = recent.Average(c => c.BodySize);
        var avgVolume = recent.Average(c => (decimal)c.Volume);

        if (candle.BodySize < avgBody * _cfg.SignalBodyMultiplier) return null;
        if (candle.BodyRatio < _cfg.MinBodyRatio) return null;
        if (_cfg.MinVolumeMultiplier > 0
            && (decimal)candle.Volume < avgVolume * _cfg.MinVolumeMultiplier) return null;
        if (candle.IsDoji) return null;

        var side = candle.IsBullish ? Side.Long : Side.Short;
        var trigger = side == Side.Long ? candle.High : candle.Low;
        var stop = side == Side.Long ? candle.Low : candle.High;

        return new SignalCandleInfo(side, trigger, stop);
    }

    private PyramidSignal? TryPyramid(IStrategyContext ctx, Position pos)
    {
        if (_trancheCountThisPosition >= _cfg.MaxTranches) return null;
        if (_initialRisk <= 0m) return null;

        var candle = ctx.CurrentCandle;

        // For longs we need a new high since entry; for shorts a new low.
        var isLong = pos.Side == Side.Long;
        var extreme = isLong ? ctx.HighSinceEntry : ctx.LowSinceEntry;
        if (extreme is null) return null;

        var madeNewExtreme = isLong
            ? candle.Close >= extreme.Value && candle.High >= extreme.Value
            : candle.Close <= extreme.Value && candle.Low <= extreme.Value;
        if (!madeNewExtreme) return null;

        // How much favourable movement from the reference price?
        var referencePrice = _lastAddPrice ?? pos.AverageEntry;
        var movement = (candle.Close - referencePrice) * pos.Side.Sign();

        var requiredR = _lastAddPrice is null
            ? _cfg.MinRMultipleForFirstAdd
            : _cfg.MinRMultipleBetweenAdds;

        if (movement < requiredR * _initialRisk) return null;

        // Size of this tranche.
        var baseSize = pos.Tranches[0].Quantity;
        var addQty = Math.Max(1, (int)Math.Floor(baseSize * _cfg.TrancheSizeMultiplier));

        // New stop: protect an R-multiple of profit on the combined position.
        var newStop = ComputeLockInStop(pos, addQty, candle.Close);

        _trancheCountThisPosition++;
        _lastAddPrice = candle.Close;

        return new PyramidSignal(
            Timestamp: candle.Timestamp,
            Symbol: _symbol,
            TriggerPrice: candle.Close,
            NewStopLoss: newStop,
            Quantity: addQty,
            Reason: $"Pyramid #{_trancheCountThisPosition - 1} on new extreme");
    }

    private decimal ComputeLockInStop(Position pos, int addQty, decimal currentPrice)
    {
        // Treat the soon-to-exist position: existing tranches + new one at currentPrice.
        // We want: sum((stop - entry) * qty * sign) >= lockInR * initialRisk * totalQty
        // Solve for stop.
        var futureTranches = pos.Tranches
            .Append(new Tranche(DateTimeOffset.MinValue, currentPrice, addQty, "proj"))
            .ToList();
        var totalQty = futureTranches.Sum(t => t.Quantity);
        var weightedEntry = futureTranches.Sum(t => t.EntryPrice * t.Quantity) / totalQty;

        var targetLockIn = _cfg.LockInRMultipleOnAdd * _initialRisk;
        // stop = avgEntry + sign * targetLockIn
        var candidateStop = weightedEntry + pos.Side.Sign() * targetLockIn;

        // Never loosen the stop — always take the tighter of current vs candidate.
        return pos.Side == Side.Long
            ? Math.Max(pos.StopLoss, candidateStop)
            : Math.Min(pos.StopLoss, candidateStop);
    }

    private sealed record SignalCandleInfo(Side Side, decimal TriggerPrice, decimal StopLoss);
}
