using System.Globalization;
using System.Text;

namespace CandleLab.Backtesting;

public static class ReportWriter
{
    public static string FormatSummary(BacktestResult r)
    {
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        var m = r.Metrics;

        sb.AppendLine("═══════════════════════════════════════════════════════════════");
        sb.AppendLine($"  BACKTEST RESULT  ·  {r.StrategyName}");
        sb.AppendLine("═══════════════════════════════════════════════════════════════");
        sb.AppendLine($"  Symbol           : {r.Symbol}");
        sb.AppendLine($"  Period           : {r.StartTime:u}  →  {r.EndTime:u}");
        sb.AppendLine($"  Duration         : {(r.EndTime - r.StartTime).TotalDays:F1} days");
        sb.AppendLine();
        sb.AppendLine($"  Starting capital : {r.StartingCapital.ToString("N2", inv)}");
        sb.AppendLine($"  Ending equity    : {r.EndingEquity.ToString("N2", inv)}");
        sb.AppendLine($"  Total return     : {r.TotalReturn.ToString("N2", inv)} ({r.TotalReturnPercent.ToString("N2", inv)}%)");
        sb.AppendLine();
        sb.AppendLine("  ─── Trade statistics ─────────────────────────────────────────");
        sb.AppendLine($"  Total trades     : {m.TotalTrades}");
        sb.AppendLine($"  Wins / Losses    : {m.WinningTrades} / {m.LosingTrades}");
        sb.AppendLine($"  Win rate         : {(m.WinRate * 100m).ToString("N1", inv)}%");
        sb.AppendLine($"  Avg win          : {m.AverageWin.ToString("N2", inv)}");
        sb.AppendLine($"  Avg loss         : {m.AverageLoss.ToString("N2", inv)}");
        sb.AppendLine($"  Profit factor    : {FormatProfitFactor(m.ProfitFactor)}");
        sb.AppendLine($"  Expectancy/trade : {m.ExpectancyPerTrade.ToString("N2", inv)}");
        sb.AppendLine($"  Avg duration     : {m.AverageTradeDuration}");
        sb.AppendLine($"  Avg tranches     : {m.AveragePyramidTranches.ToString("N2", inv)}");
        sb.AppendLine();
        sb.AppendLine("  ─── Risk metrics ────────────────────────────────────────────");
        sb.AppendLine($"  Max drawdown     : {m.MaxDrawdown.ToString("N2", inv)} ({m.MaxDrawdownPercent.ToString("N2", inv)}%)");
        sb.AppendLine($"  Sharpe (annual)  : {m.SharpeRatio.ToString("N3", inv)}");
        sb.AppendLine("═══════════════════════════════════════════════════════════════");

        return sb.ToString();
    }

    public static async Task WriteTradeJournalAsync(
        BacktestResult r, string path, CancellationToken ct = default)
    {
        var inv = CultureInfo.InvariantCulture;
        await using var writer = new StreamWriter(path);
        await writer.WriteLineAsync(
            "opened_at,closed_at,symbol,side,avg_entry,exit_price,quantity,tranches,gross_pnl,commission,net_pnl,exit_reason");

        foreach (var t in r.Trades)
        {
            ct.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(string.Create(inv,
                $"{t.OpenedAt:O},{t.ClosedAt:O},{t.Symbol},{t.Side},{t.AverageEntry},{t.ExitPrice},{t.Quantity},{t.TrancheCount},{t.GrossPnL},{t.Commission},{t.NetPnL},\"{t.ExitReason}\""));
        }
    }

    public static async Task WriteEquityCurveAsync(
        BacktestResult r, string path, CancellationToken ct = default)
    {
        var inv = CultureInfo.InvariantCulture;
        await using var writer = new StreamWriter(path);
        await writer.WriteLineAsync("timestamp,equity,cash");
        foreach (var p in r.EquityCurve)
        {
            ct.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(string.Create(inv, $"{p.Timestamp:O},{p.Equity},{p.Cash}"));
        }
    }

    private static string FormatProfitFactor(decimal pf) =>
        pf == decimal.MaxValue ? "∞" : pf.ToString("N2", CultureInfo.InvariantCulture);
}
