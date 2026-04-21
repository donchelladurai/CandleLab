# CHANGES — v0.7

Publishing-ready artifact. Everything is about making the analysis
shareable.

## New: `analyse` CLI verb

```
dotnet run --project src/CandleLab.Runner -- analyse \
    symbols=SPY_iex,QQQ_iex \
    modes=reversal,continuation \
    out=./analysis.html
```

Runs the full grid of (symbol × mode) backtests in sequence and writes
a single self-contained HTML file. The file contains:

- Masthead with title, subtitle, author, GitHub link
- Three prose sections with placeholder DRAFT text: *What this is*,
  *The claim*, *How I tested it*
- Headline numbers table (trades, win %, net P&L, expectancy, PF, max DD)
- Side-breakdown table (longs vs shorts within each run)
- Pyramid-tranche split table with a "Key finding" callout explaining
  why the 0%/100% split is mechanically deterministic
- Interactive run switcher — tabs that swap between embedded per-run
  charts, reusing the same hand-rolled SVG renderer as the single-run
  report
- Footer with CC BY 4.0 for writing, MIT for code, GitHub link

All in one HTML file, roughly 1–1.5 MB depending on trade counts.
Portable via email, Slack, Discord, etc. without zipping.

### Workflow

1. Fetch data (`fetch` verb) — or reuse existing CSVs
2. `analyse` verb → `analysis.html`
3. Open in a browser. DRAFT placeholders are visually highlighted
   (orange sidebar, italic). Edit the HTML directly to fill them in —
   no rebuild required
4. Share

### Defaults

`analyse` without arguments runs:
- Symbols: `SPY_iex,QQQ_iex`
- Modes: `reversal,continuation`
- Filters: the same RELAXED config used in v0.6 launch profiles
  (`wickratio=0.10 bodyratio=0.45 bodymult=1.2 volmult=0`, HTF off)
- Costs: SPY-sized defaults (`spread=0.005 slippage=0.02`)

Override via CLI args if you want a different grid.

## New: `AnalysisReportWriter` + `AnalysisTemplate.html`

Separate from the per-run `HtmlReportWriter`. The two writers share
per-run payload logic (`HtmlReportWriter.BuildPayloadForRun` is now
`internal` so the analysis writer can reuse it) so the embedded chart
in the analysis is identical to the stand-alone per-run reports.

Template is CSS-styled with a narrow reading column for prose and a
wider column for tables and the interactive chart. Dark mode only.

## New: `LICENSE` file at repo root

MIT licence for code. The HTML footer separately claims CC BY 4.0 for
the written content. Kept as two licences because they serve different
purposes: CC BY is better for prose (credit + adapt), MIT is the
convention for code.

## Fixed: win-rate percentage display

The `HtmlReportWriter` (single-run report) was passing win rate as
0.42 (fraction) but formatting it as `42.0%` in the template — which
actually rendered as `0.4%` in the stats strip. Pre-existing bug from
v0.3, just never noticed because we rarely looked at the per-run
report's stats header.

`r.Metrics.WinRate` is now multiplied by 100 at the JSON boundary in
both writers. The analyse command's log output also displays the
correct percentage.

## New launch profile

★ **Build analysis.html (the publishable artifact)** — top of the
dropdown, one click to produce the HTML.

## Tests

26/26 pass. No new tests specifically for the analysis writer —
it's effectively a presentation layer around the existing payload
builder, and the integration test is running the command end-to-end.

## What this release doesn't address

- The 1-tranche loss / 2+ tranche win asymmetry described in the
  analysis is exposed in the tranche table but not *charted*. A
  two-line equity curve ("1-tranche trades only" vs "2+ tranche only")
  would make the finding visceral instead of just tabular. Deferred
  to v0.8 if publishing feedback suggests it's needed.
- Per-bar stop-line visualisation is still a placeholder toggle
- Exit-reason labelling still shows "Stop-loss hit" for trailing-stop
  wins

## What to do next

1. Extract the zip over your existing project
2. Pick the "★ Build analysis.html" profile, F5
3. Open `analysis.html` in a browser
4. Edit the DRAFT sections directly in the HTML file (search for
   `class="placeholder"`). No rebuild needed — save and refresh
5. When the prose is ready, publish: GitHub repo with the HTML as
   a release artifact, or host it somewhere (analysis.html is a single
   file, trivially hostable on GitHub Pages, Netlify, your own site)
