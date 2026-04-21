using CandleLab.Domain;
using CandleLab.Execution;
using FluentAssertions;
using Xunit;

namespace CandleLab.Tests;

public class BacktestExecutorTests
{
    private static readonly ExecutionCosts NoCosts = new()
    {
        SpreadPerSide = 0m,
        CommissionPerContractPerSide = 0m,
        StopSlippage = 0m,
    };

    [Fact]
    public void Entry_Signal_Triggers_On_Next_Bar_When_Price_Crosses_Trigger()
    {
        var ex = new BacktestExecutor(10000m, NoCosts);

        // Bar 1 — emit entry signal; position NOT opened same bar.
        var bar1 = MakeBar(open: 100m, high: 100.5m, low: 99.5m, close: 100m);
        ex.ProcessBar(bar1, [new EntrySignal(
            Timestamp: bar1.Timestamp,
            Symbol: "SPX",
            Side: Side.Long,
            TriggerPrice: 101m,
            StopLoss: 99m,
            TakeProfit: null,
            Quantity: 1,
            Reason: "test")]);

        ex.Snapshot.OpenPosition.Should().BeNull("entry is queued but position opens only when price crosses trigger on a later bar");

        // Bar 2 — range covers trigger; fill happens.
        var bar2 = MakeBar(open: 100.5m, high: 101.5m, low: 100m, close: 101.2m, time: bar1.Timestamp.AddMinutes(5));
        ex.ProcessBar(bar2, []);

        ex.Snapshot.OpenPosition.Should().NotBeNull();
        ex.Snapshot.OpenPosition!.Tranches.Should().HaveCount(1);
        ex.Snapshot.OpenPosition.Tranches[0].EntryPrice.Should().Be(101m);
    }

    [Fact]
    public void Stop_Loss_Hit_Closes_Position_And_Records_Loss()
    {
        var ex = new BacktestExecutor(10000m, NoCosts);

        // Open a position via entry.
        var bar1 = MakeBar(100m, 100.5m, 99.5m, 100m);
        ex.ProcessBar(bar1, [new EntrySignal(bar1.Timestamp, "SPX", Side.Long, 101m, 99m, null, 1, "test")]);
        var bar2 = MakeBar(100.5m, 101.5m, 100m, 101.2m, bar1.Timestamp.AddMinutes(5));
        ex.ProcessBar(bar2, []);

        ex.Snapshot.OpenPosition.Should().NotBeNull();

        // Bar 3 — price drops below stop-loss.
        var bar3 = MakeBar(101m, 101.2m, 98.5m, 99m, bar2.Timestamp.AddMinutes(5));
        var closed = ex.ProcessBar(bar3, []);

        closed.Should().HaveCount(1);
        closed[0].ExitReason.Should().Contain("Stop");
        closed[0].NetPnL.Should().Be(-2m); // entered 101, stopped 99, qty 1
        ex.Snapshot.OpenPosition.Should().BeNull();
    }

    [Fact]
    public void Pyramid_Signal_Adds_Tranche_And_Moves_Stop()
    {
        var ex = new BacktestExecutor(10000m, NoCosts);

        // Open.
        var bar1 = MakeBar(100m, 100.5m, 99.5m, 100m);
        ex.ProcessBar(bar1, [new EntrySignal(bar1.Timestamp, "SPX", Side.Long, 101m, 99m, null, 2, "test")]);
        var bar2 = MakeBar(100.5m, 101.5m, 100m, 101.2m, bar1.Timestamp.AddMinutes(5));
        ex.ProcessBar(bar2, []);

        // Add.
        var bar3 = MakeBar(101.2m, 102m, 101m, 101.8m, bar2.Timestamp.AddMinutes(5));
        ex.ProcessBar(bar3, [new PyramidSignal(bar3.Timestamp, "SPX", 101.8m, 101m, 1, "pyramid")]);

        ex.Snapshot.OpenPosition.Should().NotBeNull();
        ex.Snapshot.OpenPosition!.Tranches.Should().HaveCount(2);
        ex.Snapshot.OpenPosition.StopLoss.Should().Be(101m);
        ex.Snapshot.OpenPosition.TotalQuantity.Should().Be(3);
    }

    [Fact]
    public void Expired_Entry_Signal_Does_Not_Fill_Even_If_Range_Crosses_Trigger()
    {
        var ex = new BacktestExecutor(10000m, NoCosts);

        var t0 = DateTimeOffset.Parse("2024-01-02T10:00:00+00:00");
        var signal = new EntrySignal(
            Timestamp: t0,
            Symbol: "SPX",
            Side: Side.Long,
            TriggerPrice: 101m,
            StopLoss: 99m,
            TakeProfit: null,
            Quantity: 1,
            Reason: "test")
        {
            ExpiresAt = t0.AddMinutes(10), // expires BEFORE bar 3 below
        };

        // Bar 1 — queue the entry. No fill (bar's range doesn't cross).
        ex.ProcessBar(MakeBar(100m, 100.5m, 99.5m, 100m, t0), [signal]);
        ex.Snapshot.OpenPosition.Should().BeNull();

        // Bar 2 — still before expiry; range doesn't cross either.
        ex.ProcessBar(MakeBar(100m, 100.8m, 99.8m, 100.2m, t0.AddMinutes(5)), []);
        ex.Snapshot.OpenPosition.Should().BeNull();

        // Bar 3 — AT expiry (>= cancels). Range WOULD have crossed (high 102 >= 101)
        // but the order is dead. No fill.
        var closed = ex.ProcessBar(MakeBar(100.2m, 102m, 100m, 101.5m, t0.AddMinutes(10)), []);
        closed.Should().BeEmpty();
        ex.Snapshot.OpenPosition.Should().BeNull(
            "pending entry expired at bar 3's timestamp and must not fill");
    }

    [Fact]
    public void Entry_Signal_With_Null_Expiry_Is_Good_Till_Cancelled()
    {
        var ex = new BacktestExecutor(10000m, NoCosts);
        var t0 = DateTimeOffset.Parse("2024-01-02T10:00:00+00:00");

        var signal = new EntrySignal(t0, "SPX", Side.Long, 101m, 99m, null, 1, "test");
        signal.ExpiresAt.Should().BeNull();

        ex.ProcessBar(MakeBar(100m, 100.5m, 99.5m, 100m, t0), [signal]);
        // Many bars pass; still never expires.
        for (var i = 1; i < 20; i++)
        {
            ex.ProcessBar(MakeBar(100m, 100.8m, 99.8m, 100.2m, t0.AddMinutes(5 * i)), []);
        }
        ex.Snapshot.OpenPosition.Should().BeNull("never triggered but also never expired");

        // Now a bar that crosses — should fill.
        ex.ProcessBar(MakeBar(100.2m, 101.5m, 100m, 101.2m, t0.AddMinutes(5 * 20)), []);
        ex.Snapshot.OpenPosition.Should().NotBeNull();
    }

    private static Candle MakeBar(
        decimal open, decimal high, decimal low, decimal close,
        DateTimeOffset? time = null, long volume = 1000)
        => new(time ?? DateTimeOffset.UtcNow, open, high, low, close, volume, Timeframe.FiveMinutes);
}
