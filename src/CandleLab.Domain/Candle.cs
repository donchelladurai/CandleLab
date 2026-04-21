namespace CandleLab.Domain;

/// <summary>
/// A single OHLCV bar. Immutable by design.
/// Prices are decimals to preserve precision; volume is long for large intraday bars.
/// </summary>
public sealed record Candle(
    DateTimeOffset Timestamp,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume,
    Timeframe Timeframe)
{
    public decimal BodySize => Math.Abs(Close - Open);
    public decimal Range => High - Low;
    public decimal UpperWick => High - Math.Max(Open, Close);
    public decimal LowerWick => Math.Min(Open, Close) - Low;
    public bool IsBullish => Close > Open;
    public bool IsBearish => Close < Open;
    public bool IsDoji => BodySize == 0m;

    /// <summary>
    /// Body as a proportion of the candle's total range.
    /// Returns 1.0 for a marubozu, ~0 for a doji.
    /// </summary>
    public decimal BodyRatio => Range == 0m ? 0m : BodySize / Range;
}

public enum Timeframe
{
    OneMinute,
    FiveMinutes,
    FifteenMinutes,
    ThirtyMinutes,
    OneHour,
    FourHours,
    Daily,
}

public static class TimeframeExtensions
{
    public static TimeSpan ToTimeSpan(this Timeframe tf) => tf switch
    {
        Timeframe.OneMinute => TimeSpan.FromMinutes(1),
        Timeframe.FiveMinutes => TimeSpan.FromMinutes(5),
        Timeframe.FifteenMinutes => TimeSpan.FromMinutes(15),
        Timeframe.ThirtyMinutes => TimeSpan.FromMinutes(30),
        Timeframe.OneHour => TimeSpan.FromHours(1),
        Timeframe.FourHours => TimeSpan.FromHours(4),
        Timeframe.Daily => TimeSpan.FromDays(1),
        _ => throw new ArgumentOutOfRangeException(nameof(tf)),
    };
}

public enum Side
{
    Long,
    Short,
}

public static class SideExtensions
{
    public static int Sign(this Side side) => side == Side.Long ? 1 : -1;
    public static Side Opposite(this Side side) => side == Side.Long ? Side.Short : Side.Long;
}
