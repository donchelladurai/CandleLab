# CandleLab

A .NET backtesting framework for candlestick trading strategies, built to dispassionately validate claims about what works in the markets before risking real money.

## Why this exists

There's no shortage of trading strategies pitched online — YouTube videos, Discord servers, TikTok reels, paid courses. Almost all arrive with confident claims and impressive-looking charts. Almost none arrive with a rigorous backtest on real data.

CandleLab exists because I wanted to answer a simple question: do these strategies actually work when you test them honestly? Not in the sense of "find a configuration where the backtest looks nice" — that's easy, and it's how people end up losing real money. In the sense of: take the strategy as specified, code it exactly, run it against a meaningful amount of real data, and see what happens.

The first strategy I put through this process — a popular ICT "opening-range manipulation candle" setup — did not produce positive expectancy on SPY or QQQ over the last 12 months, in either directional interpretation, with or without relaxed filters. That finding is documented in the generated `analysis.html` report. The code, the data pipeline, and the methodology are all in this repo so you can reproduce it, argue with it, or test your own variants.

## What's here

- **.NET 10 backtesting engine.** Strategy-pluggable, broker-agnostic, deterministic execution. Strategies emit signals; the engine simulates fills with configurable spread and slippage. Session-clipped to US regular hours by default.
- **Two strategy implementations.** A generic one-candle detector (baseline) and the opening-range manipulation strategy. Both configurable via CLI args. New strategies are one `IStrategy` class away.
- **Alpaca historical data fetcher.** Downloads 5-minute bars (IEX or SIP feed) to local CSVs with pagination, rate-limit backoff, and retry. Credentials live in a gitignored `alpaca.json` file.
- **HTML visualiser.** Every backtest produces a self-contained HTML report with day-by-day candlestick charts, overlaid strategy events (opening range, breakout, rejection, entry, exit), toggleable layers, and hover tooltips. Runs offline, shareable as a single file.
- **Publishable analysis artifact.** A separate writer that embeds multiple runs' data into one HTML with commentary sections, cross-run comparison tables, and an interactive chart switcher. This is the file you get when you publish a finding.
- **Unit tests.** 26 passing, covering strategy state machines, pyramid logic, volume filters, session boundaries, and Alpaca JSON parsing.

## Quick start

### Prerequisites

- .NET 10 SDK
- An Alpaca paper-trading account ([free to create](https://app.alpaca.markets/signup))

### Set up credentials

Copy `alpaca.json.example` to `alpaca.json` in the repo root and paste in your paper-trading `keyId` and `secretKey`. The file is gitignored so your keys stay local.

### Fetch some data

```bash
dotnet run --project src/CandleLab.Runner -- fetch \
    symbols=SPY,QQQ \
    feeds=iex \
    overwrite=true
```

This writes a year of 5-minute bars to `data/SPY_iex_FiveMinutes.csv` and `data/QQQ_iex_FiveMinutes.csv`. Takes about 15 seconds.

### Run a backtest

```bash
dotnet run --project src/CandleLab.Runner -- \
    symbol=SPY_iex \
    strategy=openingrange \
    mode=reversal \
    nohtf=true \
    out=./out_spy_reversal
```

This produces three files in `out_spy_reversal/`: `trades.csv`, `equity.csv`, and a `report.html` you can open in any browser.

### Generate the publishable analysis

```bash
dotnet run --project src/CandleLab.Runner -- analyse \
    symbols=SPY_iex,QQQ_iex \
    modes=reversal,continuation \
    out=./analysis.html
```

This runs the full 2×2 grid and produces one self-contained HTML containing all four runs' charts, the comparison tables, and commentary sections for you to fill in.

## Running in Visual Studio

The `launchSettings.json` includes preset profiles for the common workflows. Pick one from the green Run button's dropdown and hit F5. The ★-marked profile at the top builds the analysis HTML directly.

## Architecture

The project is split into six .NET projects with a deliberate dependency hierarchy:

```
Domain           (no dependencies — Candle, Signal, Position, etc.)
  ↑
  ├── MarketData     (CSV provider + Alpaca fetcher)
  ├── Strategies     (IStrategy + the two implementations)
  └── Execution      (IExecutor + BacktestExecutor)
       ↑
       └── Backtesting  (engine, metrics, HTML writers)
            ↑
            └── Runner  (CLI)
```

Two principles drive the design:

1. **No look-ahead bias.** The `IStrategyContext` given to a strategy contains only candles strictly before the current one. Entry signals are queued and filled on the *next* bar's range, never the bar that produced them. Stops can be hit intra-bar. This matches what a real broker would do.

2. **Broker-agnostic execution.** `IExecutor` is the abstraction between strategy logic and order handling. `BacktestExecutor` simulates fills with realistic costs. A hypothetical future `AlpacaPaperExecutor` or `IGExecutor` would slot in behind the same interface without touching strategy code.

## The first finding

The opening-range manipulation strategy, as commonly described in ICT content, works like this:

1. Wait 15 minutes after market open
2. Check if the opening candle has "manipulation" wicks (both wicks at least 25% of ATR)
3. Draw a rectangle at the opening candle's high/low, valid for ~90 minutes
4. Wait for price to break outside the rectangle
5. Enter on a strong signal candle in the (reversal: opposite / continuation: same) direction
6. Stop at the signal candle's opposite extreme
7. Pyramid on continuation

Tested on 12 months of SPY and QQQ 5-minute data (April 2025 to April 2026, via Alpaca IEX), with realistic costs (half-cent spread, 2-cent stop slippage) and 1% risk per trade, across both direction interpretations and with relaxed filters to maximise trade count:

| Configuration | Trades | Win % | Net P&L | Expectancy |
|---|---|---|---|---|
| SPY reversal | 105 | 41.9% | −$2,343 | −$22 |
| SPY continuation | 106 | 38.7% | −$2,869 | −$27 |
| QQQ reversal | 84 | 46.4% | −$1,273 | −$15 |
| QQQ continuation | 108 | 48.1% | −$1,464 | −$14 |

All four lose money. The losses are roughly symmetric between longs and shorts, which rules out a simple trend-filter fix.

The pyramid mechanics are worth looking at separately. Every trade with a single tranche (position opened, never added to) lost. Every trade with two or more tranches (pyramided after +1R profit, lock-in stop in place) won. This holds across all four configurations. The pyramid isn't creating edge — it's revealing that roughly 40-48% of trades make it past the first add threshold, and the other 52-60% stop out before they can, which is the actual coin-flip underneath the strategy.

Open `analysis.html` after running the analyse command to see the full breakdown with interactive charts.

## Scope and limits of this finding

I tested one setup, on two instruments, over one year, at one timeframe, with one set of execution-cost assumptions. That's not a verdict on all ICT material, all opening-range strategies, all timeframes, or all market conditions. It's a verdict on this specific configuration on this specific data.

If you think the strategy works and mine doesn't, fork the repo and change what you think I got wrong. The whole point of publishing the code is that the claim is now falsifiable by anyone willing to run it.

## What I'd welcome

- Bug reports on the backtesting engine itself
- New strategy implementations, especially ones backed by published research
- Improvements to execution-cost modelling
- Feedback on the visualiser

What I'm less interested in: "you didn't use the real ICT timeframe" or similar unfalsifiable objections unless they come with a specific, testable correction.

## Licence

Code is MIT — do what you want with it, just keep the copyright notice.

Written content (the README, the analysis commentary) is [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) — attribute me and you can adapt.

## Contact

Don Chelladurai — [GitHub](https://github.com/donchelladurai)

Not financial advice. This is personal research published for educational purposes. Anything in this repo that resembles trading guidance is coincidental, and you should never risk capital on a strategy you haven't independently validated.
