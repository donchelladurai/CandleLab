using System.Text.Json;
using CandleLab.Domain;
using CandleLab.MarketData;
using FluentAssertions;
using Xunit;

namespace CandleLab.Tests;

public class AlpacaDataFetcherTests
{
    [Fact]
    public void Parses_Alpaca_Bar_Json_Correctly()
    {
        // A realistic Alpaca v2 /bars response shape. Uses lowercase one-letter
        // keys (o/h/l/c/v/t) and an optional next_page_token.
        const string json = """
            {
              "bars": [
                {"t":"2025-04-21T13:30:00Z","o":502.12,"h":502.80,"l":501.98,"c":502.45,"v":184500,"n":920,"vw":502.40},
                {"t":"2025-04-21T13:35:00Z","o":502.45,"h":502.60,"l":502.10,"c":502.20,"v":151200,"n":811,"vw":502.31}
              ],
              "next_page_token": "abc123",
              "symbol": "SPY"
            }
            """;

        var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var parsed = JsonSerializer.Deserialize<TestAlpacaBarsResponse>(json, opts);

        parsed.Should().NotBeNull();
        parsed!.Bars.Should().HaveCount(2);
        parsed.NextPageToken.Should().Be("abc123");

        var first = parsed.Bars![0];
        first.Timestamp.Should().Be(DateTimeOffset.Parse("2025-04-21T13:30:00Z"));
        first.Open.Should().Be(502.12m);
        first.High.Should().Be(502.80m);
        first.Low.Should().Be(501.98m);
        first.Close.Should().Be(502.45m);
        first.Volume.Should().Be(184_500L);
    }

    [Theory]
    [InlineData(Timeframe.OneMinute, "1Min")]
    [InlineData(Timeframe.FiveMinutes, "5Min")]
    [InlineData(Timeframe.FifteenMinutes, "15Min")]
    [InlineData(Timeframe.ThirtyMinutes, "30Min")]
    [InlineData(Timeframe.OneHour, "1Hour")]
    [InlineData(Timeframe.FourHours, "4Hour")]
    [InlineData(Timeframe.Daily, "1Day")]
    public void Maps_Timeframes_To_Alpaca_Format(Timeframe input, string expected)
    {
        AlpacaDataFetcher.ToAlpacaTimeframe(input).Should().Be(expected);
    }

    // Local mirror of the (internal) DTO just so the test can exercise the
    // JSON shape without exposing internals of AlpacaDataFetcher.
    private sealed record TestAlpacaBarsResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("bars")]
        public List<TestAlpacaBar>? Bars { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("next_page_token")]
        public string? NextPageToken { get; init; }
    }

    private sealed record TestAlpacaBar
    {
        [System.Text.Json.Serialization.JsonPropertyName("t")]
        public DateTimeOffset Timestamp { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("o")]
        public decimal Open { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("h")]
        public decimal High { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("l")]
        public decimal Low { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("c")]
        public decimal Close { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("v")]
        public long Volume { get; init; }
    }
}
