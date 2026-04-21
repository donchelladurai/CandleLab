using System.Text;
using System.Text.Json;
using CandleLab.Domain;

namespace CandleLab.Backtesting;

/// <summary>
/// Generates a single self-contained HTML that embeds multiple backtest
/// results side-by-side, with commentary sections and a switcher UI. Used
/// for publishing a write-up that compares several strategy configurations
/// on the same page.
///
/// The commentary is expected to be filled in by the author — this class
/// generates placeholders. Tables (4-run grid, pyramid-tranche split) are
/// computed from the runs automatically.
/// </summary>
public static class AnalysisReportWriter
{
    public static async Task WriteAsync(
        IReadOnlyList<(string Label, BacktestResult Result)> runs,
        AnalysisMeta meta,
        string outputPath)
    {
        if (runs.Count == 0)
            throw new ArgumentException("At least one run is required.", nameof(runs));

        // Build per-run JSON payloads using the single-run writer's shared logic
        // so the embedded chart renders identically to the per-run reports.
        var runPayloads = runs.Select(r => new
        {
            label = r.Label,
            payload = HtmlReportWriter.BuildPayloadForRun(r.Result),
            summary = BuildRunSummary(r.Result),
        }).ToList();

        var tranche = BuildTrancheSplit(runs);

        var docPayload = new
        {
            meta = new
            {
                title = meta.Title,
                subtitle = meta.Subtitle,
                author = meta.Author,
                year = meta.Year,
                githubUrl = meta.GithubUrl,
            },
            runs = runPayloads,
            trancheSplit = tranche,
        };

        var json = JsonSerializer.Serialize(docPayload, HtmlReportWriter.SharedJsonOpts);
        var template = ReadTemplate();
        var html = template.Replace("__PAYLOAD_JSON__", json)
                           .Replace("__TITLE__", meta.Title);
        await File.WriteAllTextAsync(outputPath, html, Encoding.UTF8);
    }

    private static object BuildRunSummary(BacktestResult r) => new
    {
        strategyName = r.StrategyName,
        symbol = r.Symbol,
        totalTrades = r.Metrics.TotalTrades,
        winRate = r.Metrics.WinRate * 100m,
        netReturn = r.TotalReturn,
        netReturnPercent = r.TotalReturnPercent,
        profitFactor = r.Metrics.ProfitFactor,
        expectancy = r.Metrics.ExpectancyPerTrade,
        maxDdPct = r.Metrics.MaxDrawdownPercent,
        sharpe = r.Metrics.SharpeRatio,
        // Side breakdown.
        longTrades = r.Trades.Count(t => t.Side == Side.Long),
        longWins = r.Trades.Count(t => t.Side == Side.Long && t.IsWin),
        longNet = r.Trades.Where(t => t.Side == Side.Long).Sum(t => t.NetPnL),
        shortTrades = r.Trades.Count(t => t.Side == Side.Short),
        shortWins = r.Trades.Count(t => t.Side == Side.Short && t.IsWin),
        shortNet = r.Trades.Where(t => t.Side == Side.Short).Sum(t => t.NetPnL),
    };

    private static object BuildTrancheSplit(IReadOnlyList<(string Label, BacktestResult Result)> runs)
    {
        // For each run, split trades by tranche count bucket (1 vs 2+).
        return runs.Select(r => new
        {
            label = r.Label,
            oneTranche = new
            {
                count = r.Result.Trades.Count(t => t.TrancheCount == 1),
                wins = r.Result.Trades.Count(t => t.TrancheCount == 1 && t.IsWin),
                net = r.Result.Trades.Where(t => t.TrancheCount == 1).Sum(t => t.NetPnL),
            },
            multiTranche = new
            {
                count = r.Result.Trades.Count(t => t.TrancheCount > 1),
                wins = r.Result.Trades.Count(t => t.TrancheCount > 1 && t.IsWin),
                net = r.Result.Trades.Where(t => t.TrancheCount > 1).Sum(t => t.NetPnL),
            },
        }).ToList();
    }

    private static string ReadTemplate()
    {
        var asmDir = Path.GetDirectoryName(typeof(AnalysisReportWriter).Assembly.Location)!;
        var templatePath = Path.Combine(asmDir, "AnalysisTemplate.html");
        if (File.Exists(templatePath)) return File.ReadAllText(templatePath);

        var search = asmDir;
        for (var i = 0; i < 6 && search is not null; i++)
        {
            var candidate = Path.Combine(search, "src", "CandleLab.Backtesting", "AnalysisTemplate.html");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            search = Path.GetDirectoryName(search);
        }

        throw new FileNotFoundException("AnalysisTemplate.html not found alongside assembly or in source tree.");
    }
}

/// <summary>
/// Metadata for the top of the analysis report. Title and subtitle appear
/// in the masthead; author and year go into the copyright footer.
/// </summary>
public sealed record AnalysisMeta(
    string Title,
    string Subtitle,
    string Author,
    int Year,
    string GithubUrl);
