using CandleLab.Domain;
using CandleLab.Strategies;
using FluentAssertions;
using Xunit;

namespace CandleLab.Tests;

public class OneCandleStrategyTests
{
    [Fact]
    public void Does_Not_Emit_Signal_Before_Lookback_Fills()
    {
        var strategy = new OneCandleStrategy("SPX", new OneCandleStrategyConfig
        {
            LookbackForAverage = 20,
        });

        var ctx = new TestContext(
            current: Candle(bullish: true, bodySize: 10m),
            history: Enumerable.Range(0, 10).Select(_ => Candle(bullish: true, bodySize: 1m)).ToList());

        var signals = strategy.OnCandle(ctx).ToList();

        signals.Should().BeEmpty("history has fewer than LookbackForAverage candles");
    }

    [Fact]
    public void Emits_Entry_Signal_At_Close_Of_Signal_Candle()
    {
        // Regression: in v0 the strategy delayed emission by one bar and gated
        // it on the NEXT bar's high/low breaching the trigger — which caused
        // look-ahead AND duplicated the executor's job. The contract is now:
        // signal candle identified → emit stop-order NOW with an expiry.
        // Fills happen in the executor on subsequent bars.
        var strategy = new OneCandleStrategy("SPX", new OneCandleStrategyConfig
        {
            LookbackForAverage = 20,
            SignalBodyMultiplier = 1.5m,
            MinBodyRatio = 0.6m,
            MinVolumeMultiplier = 1.2m,
            EntryTriggerTimeoutBars = 3,
            RiskPerTrade = 0.01m,
        });

        var t0 = DateTimeOffset.Parse("2024-01-02T14:30:00+00:00");
        var history = Enumerable.Range(0, 20)
            .Select(i => Candle(bullish: true, bodySize: 0.5m, volume: 100,
                time: t0 + TimeSpan.FromMinutes(5 * i)))
            .ToList();

        var signalCandleTime = t0 + TimeSpan.FromMinutes(5 * 20);
        // Marubozu: open = low, close = high — body 1.0 on range 1.0 gives ratio 1.0,
        // and 1.0 comfortably exceeds 1.5 × avg-body (0.5).
        var signalCandle = Candle(
            bullish: true, volume: 200,
            open: 100m, close: 101m, high: 101m, low: 100m,
            time: signalCandleTime);

        var ctx = new TestContext(
            current: signalCandle,
            history: history,
            accountEquity: 10_000m);

        var signals = strategy.OnCandle(ctx).ToList();

        signals.Should().ContainSingle(s => s is EntrySignal,
            "signal candle identification emits the order immediately");

        var entry = (EntrySignal)signals.Single();
        entry.Side.Should().Be(Side.Long);
        entry.TriggerPrice.Should().Be(101m, "signal candle's high");
        entry.StopLoss.Should().Be(100m, "signal candle's low");
        entry.ExpiresAt.Should().Be(
            signalCandleTime + TimeSpan.FromMinutes(5) * 3,
            "expiry = signal-candle timestamp + timeframe × EntryTriggerTimeoutBars");
    }

    [Fact]
    public void No_New_Entries_While_Position_Is_Open()
    {
        var strategy = new OneCandleStrategy("SPX", new OneCandleStrategyConfig());
        var history = Enumerable.Range(0, 20).Select(_ => Candle(bullish: true, bodySize: 1m)).ToList();

        var existingPos = new Position
        {
            Symbol = "SPX",
            Side = Side.Long,
            Tranches = [new Tranche(DateTimeOffset.UtcNow, 100m, 1, "test")],
            StopLoss = 99m,
        };

        var ctx = new TestContext(
            current: Candle(bullish: true, bodySize: 5m, volume: 500),
            history: history,
            openPosition: existingPos,
            highSinceEntry: 100.5m);

        var signals = strategy.OnCandle(ctx).ToList();
        signals.OfType<EntrySignal>().Should().BeEmpty();
    }

    [Fact]
    public void Pyramid_Fires_Once_Position_Has_Moved_One_R_Favourably()
    {
        // Regression: in v0 the strategy cleared _initialRisk on the bar between
        // EntrySignal emission and observed OpenPosition (because OpenPosition
        // is null at that point from the strategy's perspective), so pyramids
        // never fired in any backtest. The strategy now reconstructs _initialRisk
        // from the position itself on first observation.
        var strategy = new OneCandleStrategy("SPX", new OneCandleStrategyConfig
        {
            LookbackForAverage = 20,
            MinRMultipleForFirstAdd = 1.0m,
            MaxTranches = 3,
            TrancheSizeMultiplier = 0.6m,
            LockInRMultipleOnAdd = 0.5m,
        });

        var history = Enumerable.Range(0, 20).Select(_ => Candle(bullish: true, bodySize: 1m)).ToList();

        // Position opened at 101, stop at 100 → R = 1.0.
        var openedAt = DateTimeOffset.Parse("2024-01-02T15:00:00+00:00");
        var pos = new Position
        {
            Symbol = "SPX",
            Side = Side.Long,
            Tranches = [new Tranche(openedAt, 101m, 10, "base")],
            StopLoss = 100m,
        };

        // Bar 1: first observation of the position. Price up 0.3 — not enough.
        var ctxFirst = new TestContext(
            current: Candle(bullish: true, bodySize: 0.2m,
                open: 101.1m, close: 101.3m, high: 101.4m, low: 101m,
                time: openedAt + TimeSpan.FromMinutes(5)),
            history: history,
            openPosition: pos,
            highSinceEntry: 101.4m,
            lowSinceEntry: 101m);

        var firstSignals = strategy.OnCandle(ctxFirst).ToList();
        firstSignals.OfType<PyramidSignal>().Should().BeEmpty(
            "moved only 0.3 of required 1R");

        // Bar 2: closes at 102.5 = 1.5R above entry AND makes a new high.
        var ctxPyramid = new TestContext(
            current: Candle(bullish: true, bodySize: 1.2m,
                open: 101.3m, close: 102.5m, high: 102.6m, low: 101.3m,
                time: openedAt + TimeSpan.FromMinutes(10)),
            history: history,
            openPosition: pos,
            highSinceEntry: 101.4m, // prior-bar extreme; current bar breaks it
            lowSinceEntry: 101m);

        var signals = strategy.OnCandle(ctxPyramid).ToList();
        signals.Should().ContainSingle(s => s is PyramidSignal,
            "price has moved >= 1R since entry and made a new high");

        var pyramid = (PyramidSignal)signals.Single();
        pyramid.Quantity.Should().Be(6, "base 10 × TrancheSizeMultiplier 0.6 = 6");
        pyramid.NewStopLoss.Should().BeGreaterThan(pos.StopLoss,
            "lock-in stop must be tighter than the original on the long side");
    }

    [Fact]
    public void Resets_State_Between_Positions()
    {
        // If a position closes and a new signal candle appears, the strategy
        // must start fresh — no lingering tranche count or initial risk.
        var strategy = new OneCandleStrategy("SPX", new OneCandleStrategyConfig
        {
            LookbackForAverage = 20,
            SignalBodyMultiplier = 1.5m,
            MinBodyRatio = 0.6m,
            MinVolumeMultiplier = 0m,
            EntryTriggerTimeoutBars = 3,
        });

        var t0 = DateTimeOffset.Parse("2024-01-02T14:30:00+00:00");
        var history = Enumerable.Range(0, 20)
            .Select(i => Candle(bullish: true, bodySize: 0.5m, volume: 100,
                time: t0 + TimeSpan.FromMinutes(5 * i)))
            .ToList();

        // First, simulate having observed an open position.
        var pos = new Position
        {
            Symbol = "SPX",
            Side = Side.Long,
            Tranches = [new Tranche(t0, 101m, 10, "base")],
            StopLoss = 100m,
        };
        strategy.OnCandle(new TestContext(
            current: Candle(bullish: true, bodySize: 0.1m,
                time: t0 + TimeSpan.FromMinutes(100)),
            history: history,
            openPosition: pos,
            highSinceEntry: 101.1m)).ToList();

        // Position gone — new signal candle should produce a clean entry.
        var sigTime = t0 + TimeSpan.FromMinutes(200);
        var signalCandle = Candle(
            bullish: true, volume: 200,
            open: 200m, close: 201m, high: 201m, low: 200m,
            time: sigTime);

        var signals = strategy.OnCandle(new TestContext(
            current: signalCandle,
            history: history,
            accountEquity: 10_000m)).ToList();

        signals.Should().ContainSingle(s => s is EntrySignal);
        var entry = (EntrySignal)signals.Single();
        entry.TriggerPrice.Should().Be(201m, "fresh setup on new signal candle");
    }

    // Helpers
    private static Candle Candle(
        bool bullish = true,
        decimal bodySize = 1m,
        decimal? open = null,
        decimal? close = null,
        decimal? high = null,
        decimal? low = null,
        long volume = 100,
        DateTimeOffset? time = null)
    {
        open ??= 100m;
        close ??= bullish ? open.Value + bodySize : open.Value - bodySize;
        high ??= Math.Max(open.Value, close.Value) + 0.05m;
        low ??= Math.Min(open.Value, close.Value) - 0.05m;
        return new Candle(
            time ?? DateTimeOffset.UtcNow,
            open.Value, high.Value, low.Value, close.Value, volume, Timeframe.FiveMinutes);
    }

    private sealed class TestContext(
        Candle current,
        IReadOnlyList<Candle> history,
        Position? openPosition = null,
        decimal? highSinceEntry = null,
        decimal? lowSinceEntry = null,
        decimal accountEquity = 10_000m) : IStrategyContext
    {
        public Candle CurrentCandle { get; } = current;
        public IReadOnlyList<Candle> History { get; } = history;
        public Position? OpenPosition { get; } = openPosition;
        public decimal? HighSinceEntry { get; } = highSinceEntry;
        public decimal? LowSinceEntry { get; } = lowSinceEntry;
        public decimal AccountEquity { get; } = accountEquity;
    }
}
