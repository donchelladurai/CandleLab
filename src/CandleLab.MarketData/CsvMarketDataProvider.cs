using System.Globalization;
using System.Runtime.CompilerServices;
using CandleLab.Domain;

namespace CandleLab.MarketData;

/// <summary>
/// Reads standard OHLCV CSV files. Expected format (with header):
///
///   timestamp,open,high,low,close,volume
///   2024-01-02T14:30:00+00:00,4720.50,4722.00,4719.80,4721.30,185320
///
/// Timestamps must be ISO 8601 with offset. Decimals use invariant culture.
/// Files are assumed to be pre-sorted ascending by timestamp.
/// </summary>
public sealed class CsvMarketDataProvider : IMarketDataProvider
{
    private readonly string _rootPath;
    private readonly Timeframe _fileTimeframe;

    public CsvMarketDataProvider(string rootPath, Timeframe fileTimeframe)
    {
        _rootPath = rootPath ?? throw new ArgumentNullException(nameof(rootPath));
        _fileTimeframe = fileTimeframe;
    }

    public async IAsyncEnumerable<Candle> StreamCandlesAsync(
        string symbol,
        Timeframe timeframe,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (timeframe != _fileTimeframe)
        {
            throw new InvalidOperationException(
                $"This provider is configured for {_fileTimeframe} but was asked for {timeframe}. " +
                "Downsampling is not supported in v1 — use a file that matches the requested timeframe.");
        }

        var path = Path.Combine(_rootPath, $"{symbol}_{timeframe}.csv");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"CSV not found: {path}");
        }

        using var reader = new StreamReader(path);
        var header = await reader.ReadLineAsync(cancellationToken);
        if (header is null)
        {
            yield break;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) yield break;                       // EOF
            if (string.IsNullOrWhiteSpace(line)) continue;

            var candle = ParseLine(line, timeframe);

            if (from.HasValue && candle.Timestamp < from.Value) continue;
            if (to.HasValue && candle.Timestamp > to.Value) yield break;

            yield return candle;
        }
    }

    private static Candle ParseLine(string line, Timeframe tf)
    {
        var parts = line.Split(',');
        if (parts.Length < 6)
        {
            throw new FormatException($"Bad CSV row: '{line}'");
        }

        var ts = DateTimeOffset.Parse(parts[0], CultureInfo.InvariantCulture);
        var open = decimal.Parse(parts[1], CultureInfo.InvariantCulture);
        var high = decimal.Parse(parts[2], CultureInfo.InvariantCulture);
        var low = decimal.Parse(parts[3], CultureInfo.InvariantCulture);
        var close = decimal.Parse(parts[4], CultureInfo.InvariantCulture);
        var volume = long.Parse(parts[5], CultureInfo.InvariantCulture);

        return new Candle(ts, open, high, low, close, volume, tf);
    }
}
