using CandleLab.Domain;

namespace CandleLab.Execution;

/// <summary>
/// Simulates order execution against historical candles.
///
/// FILL MODEL — important for realism:
///   • Entry stop-orders fill at the trigger price + spread (long) or - spread (short)
///     if the candle's range contains the trigger. We assume worst-case fill at trigger.
///   • Stop-loss hits check intra-bar range (high/low), not just close. If a bar's
///     low ≤ stop (for longs), we treat it as a hit at stop - slippage.
///   • Order of events within a bar: we check stop-loss BEFORE new entries on the
///     same bar. This is a conservative simplification — in reality you'd need
///     tick data to know the true sequence.
///   • No partial fills. Signals are all-or-nothing.
///
/// Pending entry triggers are stored and checked against each new bar's range
/// until filled or until the strategy emits a new entry.
/// </summary>
public sealed class BacktestExecutor : IExecutor
{
    private readonly ExecutionCosts _costs;
    private decimal _cash;
    private Position? _position;
    private EntrySignal? _pendingEntry;
    private decimal _highSinceEntry;
    private decimal _lowSinceEntry;
    private int _totalTrades;

    public BacktestExecutor(decimal startingCash, ExecutionCosts costs)
    {
        _cash = startingCash;
        _costs = costs;
    }

    public ExecutorSnapshot Snapshot => new(
        Cash: _cash,
        Equity: _cash + (_position?.UnrealisedPnL(_lastReferencePrice) ?? 0m),
        OpenPosition: _position,
        HighSinceEntry: _highSinceEntry,
        LowSinceEntry: _lowSinceEntry,
        TotalTrades: _totalTrades);

    private decimal _lastReferencePrice;

    public IReadOnlyList<ClosedTrade> ProcessBar(Candle candle, IReadOnlyList<Signal> signals)
    {
        _lastReferencePrice = candle.Close;
        var closed = new List<ClosedTrade>();

        // 0. Expire any stale pending entry before doing anything else with it.
        //    If the strategy set an ExpiresAt and this bar is at or after that
        //    instant, treat the order as cancelled — no fill, even if the range
        //    would have covered the trigger.
        if (_pendingEntry is { ExpiresAt: { } expiry } && candle.Timestamp >= expiry)
        {
            _pendingEntry = null;
        }

        // 1. If we have an open position, check if stop-loss (or take-profit) hit
        //    intra-bar. Conservative: check stop first.
        if (_position is { } pos)
        {
            _highSinceEntry = Math.Max(_highSinceEntry, candle.High);
            _lowSinceEntry = Math.Min(_lowSinceEntry, candle.Low);

            var stopHit = pos.Side == Side.Long
                ? candle.Low <= pos.StopLoss
                : candle.High >= pos.StopLoss;

            if (stopHit)
            {
                var fill = pos.StopLoss - pos.Side.Sign() * _costs.StopSlippage;
                var trade = ClosePosition(pos, fill, candle.Timestamp, "Stop-loss hit");
                closed.Add(trade);
                _position = null;
                // Don't process further signals this bar if we just got stopped out —
                // strategy's view of the world was based on the prior bar.
            }
            else if (pos.TakeProfit is { } tp)
            {
                var tpHit = pos.Side == Side.Long
                    ? candle.High >= tp
                    : candle.Low <= tp;
                if (tpHit)
                {
                    var trade = ClosePosition(pos, tp, candle.Timestamp, "Take-profit hit");
                    closed.Add(trade);
                    _position = null;
                }
            }
        }

        // 2. Check any pending entry trigger (stop-order).
        if (_position is null && _pendingEntry is { } entry)
        {
            var triggered = entry.Side == Side.Long
                ? candle.High >= entry.TriggerPrice
                : candle.Low <= entry.TriggerPrice;

            if (triggered)
            {
                OpenPosition(entry, candle.Timestamp);
                _pendingEntry = null;
            }
        }

        // 3. Process this bar's signals.
        foreach (var signal in signals)
        {
            switch (signal)
            {
                case EntrySignal e when _position is null:
                    // Store as pending — will trigger on the NEXT bar's range.
                    // This prevents look-ahead: strategy decided on bar N's close,
                    // the order goes live for bar N+1.
                    _pendingEntry = e;
                    break;

                case PyramidSignal p when _position is not null:
                    ApplyPyramid(p, candle);
                    break;

                case AdjustStopSignal a when _position is not null:
                    _position = _position.WithStopLoss(a.NewStopLoss);
                    break;

                case ExitSignal when _position is not null:
                    var fill = candle.Close - _position.Side.Sign() * _costs.SpreadPerSide;
                    closed.Add(ClosePosition(_position, fill, candle.Timestamp, "Manual exit"));
                    _position = null;
                    break;
            }
        }

        return closed;
    }

    private void OpenPosition(EntrySignal e, DateTimeOffset timestamp)
    {
        // Worst-case fill: trigger price worsened by spread.
        var fillPrice = e.TriggerPrice + e.Side.Sign() * _costs.SpreadPerSide;
        var commission = _costs.CommissionPerContractPerSide * e.Quantity;
        _cash -= commission;

        _position = new Position
        {
            Symbol = e.Symbol,
            Side = e.Side,
            Tranches = [new Tranche(timestamp, fillPrice, e.Quantity, e.Reason)],
            StopLoss = e.StopLoss,
            TakeProfit = e.TakeProfit,
        };

        _highSinceEntry = fillPrice;
        _lowSinceEntry = fillPrice;
    }

    private void ApplyPyramid(PyramidSignal p, Candle candle)
    {
        if (_position is null) return;
        var fillPrice = p.TriggerPrice + _position.Side.Sign() * _costs.SpreadPerSide;
        var commission = _costs.CommissionPerContractPerSide * p.Quantity;
        _cash -= commission;

        _position = _position.AddTranche(
            new Tranche(candle.Timestamp, fillPrice, p.Quantity, p.Reason),
            p.NewStopLoss);
    }

    private ClosedTrade ClosePosition(
        Position pos, decimal fillPrice, DateTimeOffset closedAt, string reason)
    {
        var totalQty = pos.TotalQuantity;
        var grossPnL = pos.Tranches.Sum(t =>
            (fillPrice - t.EntryPrice) * t.Quantity * pos.Side.Sign());

        var exitCommission = _costs.CommissionPerContractPerSide * totalQty;
        var netPnL = grossPnL - exitCommission;

        _cash += netPnL;
        _totalTrades++;

        return new ClosedTrade(
            Symbol: pos.Symbol,
            Side: pos.Side,
            OpenedAt: pos.OpenedAt,
            ClosedAt: closedAt,
            AverageEntry: pos.AverageEntry,
            ExitPrice: fillPrice,
            Quantity: totalQty,
            TrancheCount: pos.Tranches.Count,
            GrossPnL: grossPnL,
            Commission: (_costs.CommissionPerContractPerSide * totalQty * 2),
            NetPnL: netPnL,
            ExitReason: reason);
    }
}
