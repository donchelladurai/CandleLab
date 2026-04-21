using CandleLab.Domain;

namespace CandleLab.Strategies;

/// <summary>
/// Read-only view of what the strategy is allowed to see at decision time.
/// Passing this explicitly prevents look-ahead bias — strategies cannot peek at future data.
/// </summary>
public interface IStrategyContext
{
    /// <summary>The candle that has just closed.</summary>
    Candle CurrentCandle { get; }

    /// <summary>Candles strictly before the current one, most recent last.</summary>
    IReadOnlyList<Candle> History { get; }

    /// <summary>The currently open position, if any.</summary>
    Position? OpenPosition { get; }

    /// <summary>Highest high since the position was opened (for pyramid trigger).</summary>
    decimal? HighSinceEntry { get; }

    /// <summary>Lowest low since the position was opened (for pyramid trigger).</summary>
    decimal? LowSinceEntry { get; }

    /// <summary>Account equity at the start of the current bar.</summary>
    decimal AccountEquity { get; }
}

/// <summary>
/// Strategies are pure: they take context and emit signals.
/// Signals are turned into orders by the executor.
/// </summary>
public interface IStrategy
{
    string Name { get; }

    /// <summary>
    /// Called once per closed candle. May emit zero or more signals.
    /// Must not mutate anything on the context.
    /// </summary>
    IEnumerable<Signal> OnCandle(IStrategyContext context);
}
