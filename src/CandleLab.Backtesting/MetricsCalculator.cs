using CandleLab.Domain;

namespace CandleLab.Backtesting;

public static class MetricsCalculator
{
    public static BacktestMetrics Compute(
        IReadOnlyList<ClosedTrade> trades,
        IReadOnlyList<EquityPoint> equityCurve,
        decimal startingCapital)
    {
        if (trades.Count == 0)
        {
            return Empty();
        }

        var wins = trades.Where(t => t.IsWin).ToList();
        var losses = trades.Where(t => !t.IsWin).ToList();

        var avgWin = wins.Count > 0 ? wins.Average(t => t.NetPnL) : 0m;
        var avgLoss = losses.Count > 0 ? Math.Abs(losses.Average(t => t.NetPnL)) : 0m;

        var grossProfit = wins.Sum(t => t.NetPnL);
        var grossLoss = Math.Abs(losses.Sum(t => t.NetPnL));

        var profitFactor = grossLoss == 0m
            ? (grossProfit > 0m ? decimal.MaxValue : 0m)
            : grossProfit / grossLoss;

        var winRate = (decimal)wins.Count / trades.Count;
        var expectancy = winRate * avgWin - (1m - winRate) * avgLoss;

        var (mdd, mddPct) = ComputeMaxDrawdown(equityCurve, startingCapital);
        var sharpe = ComputeSharpe(equityCurve);

        var avgDuration = TimeSpan.FromSeconds(trades.Average(t => t.Duration.TotalSeconds));
        var avgTranches = (decimal)trades.Average(t => t.TrancheCount);

        return new BacktestMetrics
        {
            TotalTrades = trades.Count,
            WinningTrades = wins.Count,
            LosingTrades = losses.Count,
            WinRate = winRate,
            AverageWin = avgWin,
            AverageLoss = avgLoss,
            ProfitFactor = profitFactor,
            ExpectancyPerTrade = expectancy,
            MaxDrawdown = mdd,
            MaxDrawdownPercent = mddPct,
            SharpeRatio = sharpe,
            AverageTradeDuration = avgDuration,
            AveragePyramidTranches = avgTranches,
        };
    }

    private static (decimal Amount, decimal Percent) ComputeMaxDrawdown(
        IReadOnlyList<EquityPoint> curve, decimal startingCapital)
    {
        if (curve.Count == 0) return (0m, 0m);

        var peak = startingCapital;
        var maxDd = 0m;
        var maxDdPct = 0m;

        foreach (var point in curve)
        {
            if (point.Equity > peak) peak = point.Equity;
            var dd = peak - point.Equity;
            if (dd > maxDd)
            {
                maxDd = dd;
                maxDdPct = peak == 0m ? 0m : dd / peak * 100m;
            }
        }

        return (maxDd, maxDdPct);
    }

    private static decimal ComputeSharpe(IReadOnlyList<EquityPoint> curve)
    {
        // Simple Sharpe from bar-to-bar returns, annualised assuming 252 trading days
        // with ~78 five-minute bars per day. Rough but useful for comparison.
        // Assumes zero risk-free rate.
        if (curve.Count < 2) return 0m;

        var returns = new List<double>(curve.Count - 1);
        for (var i = 1; i < curve.Count; i++)
        {
            var prev = (double)curve[i - 1].Equity;
            if (prev == 0) continue;
            var r = ((double)curve[i].Equity - prev) / prev;
            returns.Add(r);
        }

        if (returns.Count == 0) return 0m;

        var mean = returns.Average();
        var variance = returns.Sum(r => (r - mean) * (r - mean)) / returns.Count;
        var stdDev = Math.Sqrt(variance);
        if (stdDev == 0) return 0m;

        // Annualisation factor: sqrt(bars per year). 252 * 78 ≈ 19,656 5-min bars.
        const double barsPerYear = 252 * 78;
        var annualised = (mean / stdDev) * Math.Sqrt(barsPerYear);
        return (decimal)Math.Round(annualised, 3);
    }

    private static BacktestMetrics Empty() => new()
    {
        TotalTrades = 0,
        WinningTrades = 0,
        LosingTrades = 0,
        WinRate = 0m,
        AverageWin = 0m,
        AverageLoss = 0m,
        ProfitFactor = 0m,
        ExpectancyPerTrade = 0m,
        MaxDrawdown = 0m,
        MaxDrawdownPercent = 0m,
        SharpeRatio = 0m,
        AverageTradeDuration = TimeSpan.Zero,
        AveragePyramidTranches = 0m,
    };
}
