namespace CandleLab.Domain;

/// <summary>
/// A signal emitted by a strategy. Strategies are pure: they emit signals;
/// the executor decides how to realise them.
/// </summary>
public abstract record Signal(DateTimeOffset Timestamp, string Symbol)
{
    public string CorrelationId { get; init; } = Guid.NewGuid().ToString("N");
}

/// <summary>
/// Open a new position. Includes stop-loss and optional take-profit.
/// TriggerPrice is a stop-limit; the order fills only when price crosses it.
/// </summary>
public sealed record EntrySignal(
    DateTimeOffset Timestamp,
    string Symbol,
    Side Side,
    decimal TriggerPrice,
    decimal StopLoss,
    decimal? TakeProfit,
    int Quantity,
    string Reason)
    : Signal(Timestamp, Symbol)
{
    /// <summary>
    /// Optional expiry. When set, the order is cancelled at or after this time
    /// without filling — equivalent to a broker "day" or "GTD" time-in-force.
    /// Null = good-till-cancelled (stays pending until filled or superseded).
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>
/// Add to an existing winning position (pyramiding).
/// NewStopLoss will replace the stop on the whole position.
/// </summary>
public sealed record PyramidSignal(
    DateTimeOffset Timestamp,
    string Symbol,
    decimal TriggerPrice,
    decimal NewStopLoss,
    int Quantity,
    string Reason)
    : Signal(Timestamp, Symbol);

/// <summary>
/// Trail the stop-loss on an open position without adding size.
/// </summary>
public sealed record AdjustStopSignal(
    DateTimeOffset Timestamp,
    string Symbol,
    decimal NewStopLoss,
    string Reason)
    : Signal(Timestamp, Symbol);

/// <summary>
/// Close the entire position at market (next bar open or intra-bar at current).
/// </summary>
public sealed record ExitSignal(
    DateTimeOffset Timestamp,
    string Symbol,
    string Reason)
    : Signal(Timestamp, Symbol);
