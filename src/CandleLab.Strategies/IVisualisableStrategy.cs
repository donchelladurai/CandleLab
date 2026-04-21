namespace CandleLab.Strategies;

/// <summary>
/// Optional interface for strategies that want to expose their internal state
/// to the backtest visualiser. The engine captures GetSnapshot() after each
/// bar and the HTML report writer derives overlay events from the sequence.
///
/// The returned object must be JSON-serialisable. Records with primitive
/// properties work well; opaque CLR types will not.
/// </summary>
public interface IVisualisableStrategy
{
    object? GetSnapshot();
}
