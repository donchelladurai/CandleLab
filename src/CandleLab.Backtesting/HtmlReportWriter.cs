using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CandleLab.Domain;

namespace CandleLab.Backtesting;

/// <summary>
/// Generates a self-contained HTML file with an interactive day-by-day chart
/// of the backtest. SVG is hand-rolled in JS (no external charting library);
/// the only external need is the browser itself.
///
/// Data embedded as JSON. The file weighs roughly (bars × ~100 bytes) — a
/// 30-day / 5-min backtest is ~300KB uncompressed, nothing to worry about.
/// </summary>
public static class HtmlReportWriter
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task WriteAsync(BacktestResult result, string outputPath)
    {
        var payload = BuildPayload(result);
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        var html = BuildHtml(result, json);
        await File.WriteAllTextAsync(outputPath, html, Encoding.UTF8);
    }

    /// <summary>Shared JSON options used by both single-run and multi-run writers.</summary>
    internal static JsonSerializerOptions SharedJsonOpts => JsonOpts;

    /// <summary>Build the per-run payload used by the single-run and multi-run writers.</summary>
    internal static object BuildPayloadForRun(BacktestResult r) => BuildPayload(r);

    private static object BuildPayload(BacktestResult r)
    {
        // Session = one UTC calendar day of bars. The ORM strategy's day boundary
        // is UTC, so grouping here aligns with its state machine.
        var sessions = r.Bars
            .Select((bar, idx) => (bar, idx))
            .GroupBy(b => DateOnly.FromDateTime(b.bar.Timestamp.UtcDateTime))
            .OrderBy(g => g.Key)
            .Select(g => BuildSession(g.Key, g.ToList(), r))
            .ToList();

        return new
        {
            strategyName = r.StrategyName,
            symbol = r.Symbol,
            startTime = r.StartTime,
            endTime = r.EndTime,
            startingCapital = r.StartingCapital,
            endingEquity = r.EndingEquity,
            totalReturnPercent = r.TotalReturnPercent,
            metrics = new
            {
                totalTrades = r.Metrics.TotalTrades,
                winRate = r.Metrics.WinRate * 100m,
                profitFactor = r.Metrics.ProfitFactor,
                sharpe = r.Metrics.SharpeRatio,
                maxDrawdownPercent = r.Metrics.MaxDrawdownPercent,
            },
            sessions,
        };
    }

    private static object BuildSession(
        DateOnly day,
        List<(Candle bar, int idx)> bars,
        BacktestResult r)
    {
        var barData = bars.Select(b => new
        {
            t = b.bar.Timestamp,
            o = b.bar.Open,
            h = b.bar.High,
            l = b.bar.Low,
            c = b.bar.Close,
            v = b.bar.Volume,
        }).ToList();

        var dayStart = bars.First().bar.Timestamp;
        var dayEnd = bars.Last().bar.Timestamp;

        // Trades overlapping this session (entered during or before, closed after start).
        var trades = r.Trades
            .Where(t => t.OpenedAt <= dayEnd && t.ClosedAt >= dayStart)
            .Select(t => new
            {
                side = t.Side.ToString(),
                openedAt = t.OpenedAt,
                closedAt = t.ClosedAt,
                entryPrice = t.AverageEntry,
                exitPrice = t.ExitPrice,
                netPnl = t.NetPnL,
                exitReason = t.ExitReason,
                isWin = t.IsWin,
            })
            .ToList();

        // Derive overlay events from strategy snapshots via phase diffing.
        var overlays = DeriveOverlays(bars, r.StrategySnapshots);

        return new
        {
            day = day.ToString("yyyy-MM-dd"),
            bars = barData,
            trades,
            overlays,
        };
    }

    /// <summary>
    /// Walks the per-bar snapshots for this session and extracts overlay events:
    /// rectangle draw + expiry, breakout bar, rejection bar. Works by detecting
    /// phase transitions in the snapshot object. If the strategy doesn't expose
    /// snapshots (snapshots all null), returns an empty overlay object and the
    /// UI falls back to trades-only rendering.
    /// </summary>
    private static object DeriveOverlays(
        List<(Candle bar, int idx)> sessionBars,
        IReadOnlyList<object?> allSnapshots)
    {
        string? prevPhase = null;
        object? rectangle = null;
        object? breakout = null;
        object? rejection = null;

        foreach (var (bar, globalIdx) in sessionBars)
        {
            if (globalIdx >= allSnapshots.Count) break;
            var snap = allSnapshots[globalIdx];
            if (snap is null) continue;

            // Reflection-free: the snapshot is the StrategySnapshot record whose
            // properties are camel-cased by the serializer. We re-serialise to a
            // JsonElement once per bar, which is more robust than reflection and
            // means we don't have to reference the Strategies project's record
            // types from here.
            var elem = JsonSerializer.SerializeToElement(snap, JsonOpts);
            var phase = elem.TryGetProperty("phase", out var p) ? p.GetString() : null;

            // Rectangle appears the moment phase first reports RectangleActive.
            if (rectangle is null
                && phase == "RectangleActive"
                && elem.TryGetProperty("rectangleTop", out var top) && top.ValueKind == JsonValueKind.Number
                && elem.TryGetProperty("rectangleBottom", out var bot) && bot.ValueKind == JsonValueKind.Number
                && elem.TryGetProperty("rectangleExpiresAt", out var expires) && expires.ValueKind == JsonValueKind.String)
            {
                // Anchor the rectangle visually at the session's first bar, not at
                // the bar where aggregation completed. The opening range conceptually
                // exists from the open (14:30 UTC) — drawing it from 14:40 would
                // clip the 15-min bars that formed it.
                rectangle = new
                {
                    top = top.GetDecimal(),
                    bottom = bot.GetDecimal(),
                    start = sessionBars[0].bar.Timestamp,
                    expiresAt = DateTimeOffset.Parse(expires.GetString()!),
                };
            }

            // Breakout: phase transitions RectangleActive -> AwaitingRejection.
            if (breakout is null
                && prevPhase == "RectangleActive"
                && phase == "AwaitingRejection"
                && elem.TryGetProperty("breakoutSide", out var side) && side.ValueKind == JsonValueKind.String)
            {
                breakout = new
                {
                    time = bar.Timestamp,
                    side = side.GetString(),
                    close = bar.Close,
                };
            }

            // Rejection: phase transitions AwaitingRejection -> Done while that day
            // also produced a trade. Detecting "AwaitingRejection -> Done AND entry
            // emitted this bar" is tricky from snapshots alone — we use the heuristic
            // that if phase went Done and a trade opened within the same minute,
            // this is the rejection bar.
            if (rejection is null
                && prevPhase == "AwaitingRejection"
                && phase == "Done")
            {
                rejection = new
                {
                    time = bar.Timestamp,
                };
            }

            prevPhase = phase;
        }

        return new { rectangle, breakout, rejection };
    }

    private static string BuildHtml(BacktestResult r, string json)
    {
        // Keep HTML + CSS + JS all inline so the file is self-contained and
        // survives being copied/shared/emailed. No external fetches.
        var template = ReadEmbeddedTemplate();
        return template
            .Replace("__TITLE__", $"CandleLab: {r.StrategyName} on {r.Symbol}")
            .Replace("__PAYLOAD_JSON__", json);
    }

    private static string ReadEmbeddedTemplate()
    {
        // Template is a separate file checked into the repo alongside this .cs —
        // easier to edit with HTML tooling than hand-embedded in a C# string.
        var asmDir = Path.GetDirectoryName(typeof(HtmlReportWriter).Assembly.Location)!;
        var templatePath = Path.Combine(asmDir, "ReportTemplate.html");
        if (File.Exists(templatePath)) return File.ReadAllText(templatePath);

        // Fallback: search up from the assembly location for the source file.
        // Only hit during development; in a published app the template is
        // copied alongside the DLL by the .csproj.
        var search = asmDir;
        for (var i = 0; i < 6 && search is not null; i++)
        {
            var candidate = Path.Combine(search, "src", "CandleLab.Backtesting", "ReportTemplate.html");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            search = Path.GetDirectoryName(search);
        }

        throw new FileNotFoundException("ReportTemplate.html not found alongside assembly or in source tree.");
    }
}
