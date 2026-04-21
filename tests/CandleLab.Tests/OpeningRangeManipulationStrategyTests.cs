using CandleLab.Domain;
using CandleLab.Strategies;
using FluentAssertions;
using Xunit;

namespace CandleLab.Tests;

/// <summary>
/// Tests drive the strategy through full bar sequences so day-change,
/// 15-min aggregation, and ATR warmup paths all run. Uses short lookbacks
/// (AtrPeriod=2, SignalLookback=3) to keep tests tractable.
///
/// Opening-range geometry used throughout:
///   FeedManipulationOpeningRange produces three 5-min bars that aggregate
///   to a 15-min candle of O=100, C=102, H=106, L=96 — rectangle [96, 106]
///   with upper and lower wicks of 4 each. Well above the 25%-of-ATR
///   threshold (ATR=5.5 after warmup, threshold=1.375).
/// </summary>
public class OpeningRangeManipulationStrategyTests
{
    private static OpeningRangeManipulationStrategyConfig BaseConfig() => new()
    {
        OpeningRangeMinutes = 15,
        OpeningRangeValidForMinutes = 90,
        MinWickRatioOfAtr = 0.25m,
        AtrPeriod = 2,
        RequireConfirmationCandle = false,
        SignalBodyMultiplier = 1.5m,
        MinBodyRatio = 0.6m,
        MinVolumeMultiplier = 0m, // disable volume filter in unit tests
        SignalLookback = 3,
        EntryTriggerTimeoutBars = 3,
        UseHigherTimeframeFilter = false,
        RiskPerTrade = 0.01m,
        MaxTranches = 3,
        MinRMultipleForFirstAdd = 1.0m,
        TrancheSizeMultiplier = 0.6m,
        LockInRMultipleOnAdd = 0.5m,
    };

    [Fact]
    public void Detects_Manipulation_Candle_And_Draws_Rectangle_Active()
    {
        var strategy = new OpeningRangeManipulationStrategy("SPY", BaseConfig());
        var harness = new Harness(strategy);

        // Two days of prior 15-min slices to warm up ATR.
        harness.FeedBoring15MinSlice(DayStart(0), closeAt: 100m);
        harness.FeedBoring15MinSlice(DayStart(0, minute: 15), closeAt: 100m);

        harness.FeedManipulationOpeningRange(DayStart(1));

        var snap = strategy.GetSnapshot();
        snap.Phase.Should().Be(OpeningRangeManipulationStrategy.Phase.RectangleActive);
        snap.RectangleTop.Should().Be(106m);
        snap.RectangleBottom.Should().Be(96m);
    }

    [Fact]
    public void Rejects_Opening_Range_With_Insufficient_Wicks()
    {
        var strategy = new OpeningRangeManipulationStrategy("SPY", BaseConfig());
        var harness = new Harness(strategy);

        harness.FeedBoring15MinSlice(DayStart(0), closeAt: 100m);
        harness.FeedBoring15MinSlice(DayStart(0, minute: 15), closeAt: 100m);

        harness.FeedSmallWickOpeningRange(DayStart(1));

        var snap = strategy.GetSnapshot();
        snap.Phase.Should().Be(OpeningRangeManipulationStrategy.Phase.Done);
        snap.RectangleTop.Should().BeNull();
    }

    [Fact]
    public void Upward_Break_Then_Bearish_Rejection_Emits_Short_Entry()
    {
        var strategy = new OpeningRangeManipulationStrategy("SPY", BaseConfig());
        var harness = new Harness(strategy);

        harness.FeedBoring15MinSlice(DayStart(0), closeAt: 100m);
        harness.FeedBoring15MinSlice(DayStart(0, minute: 15), closeAt: 100m);

        harness.FeedManipulationOpeningRange(DayStart(1));
        // Rectangle [96, 106]. Now feed modest history bars inside the rectangle
        // so SignalLookback has data, then break up, then reject bearish.

        harness.FeedBar(DayStart(1, minute: 15), open: 102m, high: 103m, low: 101.5m, close: 102.5m);
        harness.FeedBar(DayStart(1, minute: 20), open: 102.5m, high: 103m, low: 102m, close: 102.8m);
        harness.FeedBar(DayStart(1, minute: 25), open: 102.8m, high: 103.5m, low: 102.5m, close: 103m);

        // Upward breakout: closes above 106.
        harness.FeedBar(DayStart(1, minute: 30), open: 103m, high: 107m, low: 102.8m, close: 106.5m);
        strategy.GetSnapshot().Phase.Should().Be(OpeningRangeManipulationStrategy.Phase.AwaitingRejection);
        strategy.GetSnapshot().BreakoutSide.Should().Be(Side.Long);

        // Bearish rejection: big red candle, wick above rectangle top.
        var signals = harness.FeedBarAndCollect(
            DayStart(1, minute: 35),
            open: 106.5m, high: 107m, low: 102m, close: 102.5m);

        signals.Should().ContainSingle(s => s is EntrySignal);
        var entry = (EntrySignal)signals.Single();
        entry.Side.Should().Be(Side.Short);
        entry.TriggerPrice.Should().Be(102m);
        entry.StopLoss.Should().Be(107m);
    }

    [Fact]
    public void Downward_Break_Then_Bullish_Rejection_Emits_Long_Entry()
    {
        var strategy = new OpeningRangeManipulationStrategy("SPY", BaseConfig());
        var harness = new Harness(strategy);

        harness.FeedBoring15MinSlice(DayStart(0), closeAt: 100m);
        harness.FeedBoring15MinSlice(DayStart(0, minute: 15), closeAt: 100m);

        harness.FeedManipulationOpeningRange(DayStart(1));

        harness.FeedBar(DayStart(1, minute: 15), open: 102m, high: 102.5m, low: 101m, close: 101.5m);
        harness.FeedBar(DayStart(1, minute: 20), open: 101.5m, high: 102m, low: 101m, close: 101.7m);
        harness.FeedBar(DayStart(1, minute: 25), open: 101.7m, high: 102m, low: 101m, close: 101.3m);

        // Downward breakout: closes below 96.
        harness.FeedBar(DayStart(1, minute: 30), open: 101.3m, high: 101.5m, low: 95m, close: 95.5m);
        strategy.GetSnapshot().BreakoutSide.Should().Be(Side.Short);

        // Bullish rejection: big green candle sweeping below the rectangle.
        var signals = harness.FeedBarAndCollect(
            DayStart(1, minute: 35),
            open: 95.5m, high: 100m, low: 95m, close: 99.5m);

        signals.Should().ContainSingle(s => s is EntrySignal);
        var entry = (EntrySignal)signals.Single();
        entry.Side.Should().Be(Side.Long);
        entry.TriggerPrice.Should().Be(100m);
        entry.StopLoss.Should().Be(95m);
    }

    [Fact]
    public void Rectangle_Expires_After_Valid_Minutes()
    {
        var cfg = BaseConfig() with { OpeningRangeValidForMinutes = 30 };
        var strategy = new OpeningRangeManipulationStrategy("SPY", cfg);
        var harness = new Harness(strategy);

        harness.FeedBoring15MinSlice(DayStart(0), closeAt: 100m);
        harness.FeedBoring15MinSlice(DayStart(0, minute: 15), closeAt: 100m);

        harness.FeedManipulationOpeningRange(DayStart(1));
        // Rectangle drawn at minute 0; valid for 30 min → expires at minute 30.

        // Bars staying inside rectangle until and beyond expiry.
        for (var m = 15; m <= 35; m += 5)
        {
            harness.FeedBar(DayStart(1, minute: m), open: 102m, high: 103m, low: 101m, close: 102m);
        }

        strategy.GetSnapshot().Phase.Should().Be(OpeningRangeManipulationStrategy.Phase.Done);
    }

    [Fact]
    public void Htf_Filter_Blocks_Short_Entry_When_Regime_Is_Bullish()
    {
        var cfg = BaseConfig() with
        {
            UseHigherTimeframeFilter = true,
            HtfMaPeriod = 2,
        };
        var strategy = new OpeningRangeManipulationStrategy("SPY", cfg);
        var harness = new Harness(strategy);

        // Arrange daily closes: day -1 @ 100, day 0 @ 102.
        // At day 1, MA (last 2) = 101, yesterday's close (day 0) = 102 → bullish.
        // We also need 2 15-min slices for ATR warmup. Pack both into day -1 & 0.
        harness.FeedBoring15MinSlice(DayStart(-1), closeAt: 100m);
        harness.FeedBoring15MinSlice(DayStart(-1, minute: 15), closeAt: 100m);
        harness.FeedBoring15MinSlice(DayStart(0), closeAt: 102m);
        harness.FeedBoring15MinSlice(DayStart(0, minute: 15), closeAt: 102m);

        harness.FeedManipulationOpeningRange(DayStart(1));

        harness.FeedBar(DayStart(1, minute: 15), open: 102m, high: 103m, low: 101.5m, close: 102.5m);
        harness.FeedBar(DayStart(1, minute: 20), open: 102.5m, high: 103m, low: 102m, close: 102.8m);
        harness.FeedBar(DayStart(1, minute: 25), open: 102.8m, high: 103.5m, low: 102.5m, close: 103m);

        harness.FeedBar(DayStart(1, minute: 30), open: 103m, high: 107m, low: 102.8m, close: 106.5m);

        var signals = harness.FeedBarAndCollect(
            DayStart(1, minute: 35),
            open: 106.5m, high: 107m, low: 102m, close: 102.5m);

        signals.OfType<EntrySignal>().Should().BeEmpty(
            "HTF regime bullish; short entry must be blocked");
    }

    [Fact]
    public void Confirmation_Candle_When_Enabled_Requires_Second_Candle()
    {
        var cfg = BaseConfig() with { RequireConfirmationCandle = true };
        var strategy = new OpeningRangeManipulationStrategy("SPY", cfg);
        var harness = new Harness(strategy);

        harness.FeedBoring15MinSlice(DayStart(0), closeAt: 100m);
        harness.FeedBoring15MinSlice(DayStart(0, minute: 15), closeAt: 100m);

        harness.FeedManipulationOpeningRange(DayStart(1));

        harness.FeedBar(DayStart(1, minute: 15), open: 102m, high: 103m, low: 101.5m, close: 102.5m);
        harness.FeedBar(DayStart(1, minute: 20), open: 102.5m, high: 103m, low: 102m, close: 102.8m);
        harness.FeedBar(DayStart(1, minute: 25), open: 102.8m, high: 103.5m, low: 102.5m, close: 103m);

        harness.FeedBar(DayStart(1, minute: 30), open: 103m, high: 107m, low: 102.8m, close: 106.5m);

        // First bearish rejection candle: stored as pending, no entry emitted.
        var first = harness.FeedBarAndCollect(
            DayStart(1, minute: 35),
            open: 106.5m, high: 107m, low: 102m, close: 102.5m);
        first.OfType<EntrySignal>().Should().BeEmpty();

        // Second bearish candle confirms → entry uses N-1 (first rejection) levels.
        var second = harness.FeedBarAndCollect(
            DayStart(1, minute: 40),
            open: 102.5m, high: 103m, low: 98m, close: 98.5m);
        second.Should().ContainSingle(s => s is EntrySignal);
        var entry = (EntrySignal)second.Single();
        entry.TriggerPrice.Should().Be(102m, "trigger anchored to first rejection's low");
        entry.StopLoss.Should().Be(107m, "stop anchored to first rejection's high");
    }

    [Fact]
    public void State_Resets_Between_Days()
    {
        var strategy = new OpeningRangeManipulationStrategy("SPY", BaseConfig());
        var harness = new Harness(strategy);

        harness.FeedBoring15MinSlice(DayStart(0), closeAt: 100m);
        harness.FeedBoring15MinSlice(DayStart(0, minute: 15), closeAt: 100m);

        harness.FeedSmallWickOpeningRange(DayStart(1));
        strategy.GetSnapshot().Phase.Should().Be(OpeningRangeManipulationStrategy.Phase.Done);

        harness.FeedManipulationOpeningRange(DayStart(2));
        strategy.GetSnapshot().Phase.Should().Be(
            OpeningRangeManipulationStrategy.Phase.RectangleActive,
            "day-1 Done state must not leak into day 2");
    }

    // ─── Test helpers ───────────────────────────────────────────────────

    private static DateTimeOffset DayStart(int dayOffset, int minute = 0) =>
        DateTimeOffset.Parse("2024-01-02T14:30:00+00:00")
            .AddDays(dayOffset)
            .AddMinutes(minute);

    private sealed class Harness
    {
        private readonly OpeningRangeManipulationStrategy _strategy;
        private readonly List<Candle> _history = new();

        public Harness(OpeningRangeManipulationStrategy strategy)
        {
            _strategy = strategy;
        }

        public void FeedBar(
            DateTimeOffset time,
            decimal open, decimal high, decimal low, decimal close,
            long volume = 1000)
        {
            FeedBarAndCollect(time, open, high, low, close, volume);
        }

        public IReadOnlyList<Signal> FeedBarAndCollect(
            DateTimeOffset time,
            decimal open, decimal high, decimal low, decimal close,
            long volume = 1000)
        {
            var candle = new Candle(time, open, high, low, close, volume, Timeframe.FiveMinutes);
            var ctx = new Ctx(candle, _history.ToArray());
            var signals = _strategy.OnCandle(ctx).ToList();
            _history.Add(candle);
            return signals;
        }

        /// <summary>
        /// Feeds three 5-min bars that aggregate to a boring 15-min slice with
        /// range 1.0 centred on closeAt.
        /// </summary>
        public void FeedBoring15MinSlice(DateTimeOffset start, decimal closeAt = 100m)
        {
            var open = closeAt - 0.25m;
            var high = closeAt + 0.25m;
            var low = closeAt - 0.75m;
            FeedBar(start, open, high, low, closeAt);
            FeedBar(start.AddMinutes(5), open, high, low, closeAt);
            FeedBar(start.AddMinutes(10), open, high, low, closeAt);
        }

        /// <summary>
        /// Three 5-min bars that aggregate to O=100, C=102, H=106, L=96.
        /// Upper wick 4, lower wick 4, body 2 — comfortably passes the
        /// 25%-of-ATR filter once ATR is warm.
        /// </summary>
        public void FeedManipulationOpeningRange(DateTimeOffset start)
        {
            FeedBar(start,                 open: 100m, high: 106m, low: 99m,   close: 104m);
            FeedBar(start.AddMinutes(5),   open: 104m, high: 104.5m, low: 96m, close: 98m);
            FeedBar(start.AddMinutes(10),  open: 98m,  high: 102.5m, low: 97.5m, close: 102m);
        }

        /// <summary>Aggregates to a candle with tiny wicks — fails the filter.</summary>
        public void FeedSmallWickOpeningRange(DateTimeOffset start)
        {
            FeedBar(start,                 open: 100m,    high: 100.1m, low: 99.95m, close: 100.05m);
            FeedBar(start.AddMinutes(5),   open: 100.05m, high: 101m,   low: 100m,   close: 100.95m);
            FeedBar(start.AddMinutes(10),  open: 100.95m, high: 102m,   low: 100.95m, close: 101.9m);
        }

        private sealed class Ctx : IStrategyContext
        {
            public Ctx(Candle current, IReadOnlyList<Candle> history)
            {
                CurrentCandle = current;
                History = history;
            }

            public Candle CurrentCandle { get; }
            public IReadOnlyList<Candle> History { get; }
            public Position? OpenPosition => null;
            public decimal? HighSinceEntry => null;
            public decimal? LowSinceEntry => null;
            public decimal AccountEquity => 10_000m;
        }
    }
}
