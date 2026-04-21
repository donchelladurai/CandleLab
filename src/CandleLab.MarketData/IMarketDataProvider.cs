using CandleLab.Domain;

namespace CandleLab.MarketData;

/// <summary>
/// Abstraction over historical (backtest) and live (forward test / production) market data.
/// Same interface for both — implementations differ.
/// </summary>
public interface IMarketDataProvider
{
    /// <summary>
    /// Streams candles in chronological order. For backtests this reads from storage;
    /// for live this awaits new bars from the broker.
    /// </summary>
    IAsyncEnumerable<Candle> StreamCandlesAsync(
        string symbol,
        Timeframe timeframe,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        CancellationToken cancellationToken = default);
}
