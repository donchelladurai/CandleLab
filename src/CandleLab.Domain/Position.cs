namespace CandleLab.Domain;

/// <summary>
/// A single entry into a position. A pyramid is a Position holding multiple Tranches.
/// </summary>
public sealed record Tranche(
    DateTimeOffset EntryTime,
    decimal EntryPrice,
    int Quantity,
    string Reason);

/// <summary>
/// An open position. Immutable — state transitions produce new instances.
/// Represents the full pyramid (base + any additions).
/// </summary>
public sealed record Position
{
    public required string Symbol { get; init; }
    public required Side Side { get; init; }
    public required IReadOnlyList<Tranche> Tranches { get; init; }
    public required decimal StopLoss { get; init; }
    public decimal? TakeProfit { get; init; }

    public int TotalQuantity => Tranches.Sum(t => t.Quantity);

    /// <summary>Weighted-average entry across all tranches.</summary>
    public decimal AverageEntry =>
        Tranches.Sum(t => t.EntryPrice * t.Quantity) / TotalQuantity;

    public DateTimeOffset OpenedAt => Tranches[0].EntryTime;

    /// <summary>Current unrealised P&L for a given reference price (e.g. last close).</summary>
    public decimal UnrealisedPnL(decimal referencePrice) =>
        Tranches.Sum(t => (referencePrice - t.EntryPrice) * t.Quantity * Side.Sign());

    /// <summary>P&L if stopped out right now.</summary>
    public decimal PnLIfStopped =>
        Tranches.Sum(t => (StopLoss - t.EntryPrice) * t.Quantity * Side.Sign());

    public Position AddTranche(Tranche tranche, decimal newStopLoss) =>
        this with
        {
            Tranches = [.. Tranches, tranche],
            StopLoss = newStopLoss,
        };

    public Position WithStopLoss(decimal stopLoss) =>
        this with { StopLoss = stopLoss };
}

/// <summary>
/// A closed position — what ends up in the trade journal.
/// </summary>
public sealed record ClosedTrade(
    string Symbol,
    Side Side,
    DateTimeOffset OpenedAt,
    DateTimeOffset ClosedAt,
    decimal AverageEntry,
    decimal ExitPrice,
    int Quantity,
    int TrancheCount,
    decimal GrossPnL,
    decimal Commission,
    decimal NetPnL,
    string ExitReason)
{
    public TimeSpan Duration => ClosedAt - OpenedAt;
    public decimal ReturnPerContract => NetPnL / Quantity;
    public bool IsWin => NetPnL > 0;
}
