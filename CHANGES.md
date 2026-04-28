# CHANGES — v0.8

**Spec correction. All backtest results produced by v0.1 through v0.7 are
invalid and should not be cited.**

## What was wrong

From v0.1 onwards the opening-range "manipulation candle" filter was
implemented as:

> both the upper AND lower wicks of the opening 15-min candle must be
> at least 25% of the 14-period **15-minute** ATR

The correct specification is:

> the total size (high − low) of the opening 15-min candle must be at
> least 25% of the 14-period **daily** ATR

Three differences, all material:
1. **Total candle size, not wicks.** Significance is judged by the
   candle's overall range, not by whether both extremes are touched.
2. **Daily ATR, not 15-min ATR.** The reference volatility is the
   instrument's typical daily range, two orders of magnitude larger
   than the 15-min range.
3. **Single threshold, not dual.** The old filter required *both*
   wicks to independently pass; the corrected filter has one test.

The combined effect: the v0.1-v0.7 filter was dramatically more
restrictive than the spec called for, rejecting most opening candles
that should have qualified. All subsequent analysis (the 4-run grid
showing negative expectancy, the 0%/100% pyramid-tranche finding, the
"ICT doesn't work on SPY/QQQ" conclusion) was drawn from a strategy
that wasn't implementing the specified rules.

This was my fault — an implementation error in the original translation
of the spec to code that was never caught because the tests verified
internal consistency rather than spec conformance.

## What's been fixed

**Config changes in `OpeningRangeManipulationStrategyConfig`:**
- Removed: `MinWickRatioOfAtr`
- Added: `MinCandleSizeRatioOfDailyAtr` (default 0.25)
- Added: `DailyAtrPeriod` (default 14)
- Existing: `AtrPeriod` retained for downstream signal-candle body
  normalisation only

**Strategy changes:**
- New daily OHLC tracker — `_todayHigh`, `_todayLow`,
  `_previousDailyClose` — updated each bar
- New daily true-range queue, finalised on day rollover
- New `CurrentDailyAtr()` helper alongside the existing 15-min one
- Filter in `CheckOpeningRangeComplete` now tests
  `(top - bottom) >= DailyAtr × MinCandleSizeRatioOfDailyAtr`
- Debug log format updated

**CLI changes:**
- `wickratio=` → `sizeratio=`
- All launch profiles updated

**Tests:**
- `MinCandleSizeRatioOfDailyAtr = 0.25m` in base config
- `DailyAtrPeriod = 2` in base config (fast warmup for tests)
- New `Harness.FeedWarmupDaysBefore(testDay)` helper that feeds two
  prior days of bars to warm the daily-ATR queue
- Old per-test 15-min warmup calls removed and replaced
- Fixture `FeedSmallWickOpeningRange` renamed to `FeedSmallOpeningRange`
  (total range 2.05, below the 2.5 threshold at the test's warmed
  daily ATR of 10.0)
- Test `Rejects_Opening_Range_With_Insufficient_Wicks` renamed to
  `Rejects_Opening_Range_Below_Daily_ATR_Threshold`
- All 26 tests pass

## Also shipped

The chart UX fix from the patched-by-hand analysis.html (default
session = first session with a rectangle or trade; "Show all sessions"
toggle) was pushed back into the template so future `analyse` runs
benefit. Previously the file defaulted to the first calendar day,
which was almost always empty and gave readers the impression the
charts had no overlay data.

`text-align: justify` on prose paragraphs also ported into the
template.

## What to do next

1. Extract the zip, rebuild
2. Delete the old `out_*` folders and the old `analysis.html`
3. Run the "★ Build analysis.html" launch profile again
4. **Look at the new numbers before editing anything.** The trade
   counts will be different — almost certainly higher. Win rates,
   expectancy, and the pyramid-tranche split may also look different.
   Those new numbers are the first honest test of the strategy.

## About the previous analysis.html

The prose in the analysis.html you were preparing to publish was
written against v0.7 numbers that no longer hold. **Do not publish
that file.** If you still want to publish a write-up, it has to be
rewritten against the v0.8 results and include an honest section on
the spec correction itself — the story of "I implemented this, got
a negative result, discovered my implementation was wrong, corrected
it, and here's what came out of the correct version" is more
credible than quietly replacing the numbers.

## Tests

26/26 pass.

## What this release doesn't address

- The earlier inherited issues (avg_entry precision, exit-reason
  labelling, stop-line placeholder, ending-equity mismatch)
  remain open
