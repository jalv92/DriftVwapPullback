# DriftVwapPullback — design spec (V1)

**Status: CLOSED.** Both implementations derive from this document. Changing a
normative rule or a parameter default is a new spec revision and a new
pre-registered run, not a tweak.

Date: 2026-08-07 · Instrument: NQ · Author: Javier Lora

---

## 1. Provenance

The strategy is a replication of the "Drift VWAP Pullback" described on camera by
Matteo Conti (ex-market maker, Nordea Markets; CIO, SQR Capital) in
`https://www.youtube.com/watch?v=wm4A6qo0g3I` (2026, 44 min). There is no
published code. Every rule below is transcribed from his spoken description; §6
lists each point where his words were ambiguous and records which reading was
frozen and why.

His reported figures, for reference only — they are **not** targets and no
parameter here was chosen to reproduce them:

| | |
|---|---|
| Development window | in-sample 2020/21–2024, out-of-sample 2024 → 2026-08-02 |
| Trades | > 4,000 |
| Win rate | 64–65 % |
| Average win / loss | $866 / $1,300 (1 NQ, i.e. 43.3 / 65.0 points) |
| Derived profit factor | 1.18 |
| Derived expectancy | +$86.24 per trade |
| Derived breakeven win rate | 60.0 % |

He states explicitly that the strategy is unlikely to make money on private
capital and is designed to maximise the probability of passing a prop-firm
challenge.

## 2. Scope

**In scope for V1:** a faithful replication of the rules, implemented twice —
once in NinjaScript, once as a PropSim plugin — from this closed spec, plus a
mirror-fidelity gate and one pre-registered measurement run.

**Explicitly out of scope for V1:**

- Prop-firm rule scoring, P(pass), Monte Carlo. Deferred by decision.
- Any parameter search, sweep, or optimisation. Nothing here is tuned.
- The `max_trades_per_day` and `max_losses_per_day` guardrails as *simulated*
  rules — see §5.3. They exist as parameters, default off, and are applied as
  post-hoc filters instead.
- A standalone indicator. The NinjaScript strategy draws the VWAP, the drift
  state and the trigger bars, which covers the visual need with one file.

## 3. Instrument, session, sizing

| | |
|---|---|
| Instrument | NQ (E-mini Nasdaq-100). Tick 0.25, 1 point = 4 ticks = $20 |
| Session | RTH only, `[09:30:00, 16:00:00)` America/New_York |
| Execution series | 5-minute |
| Regime series | 15-minute |
| Size | 1 contract |

MNQ is the same rules at $2/point; V1 measures on NQ so that the point figures
compare directly against the source's.

Both 5- and 15-minute bars align to wall-clock slots (`second_of_day // tf`), so
09:30 is a boundary of both and a 15-minute bar is exactly three consecutive
5-minute bars. The PropSim side therefore builds the regime series by
aggregating the execution series **by slot index, not by array position** — a
5-minute slot with no ticks produces no bar, and positional grouping would
silently misalign the aggregate.

## 4. Normative rules

### R1 — Session VWAP

    VWAP[i] = Σ(typical[j] × volume[j]) / Σ(volume[j])   for j = anchor..i

over **15-minute bars**, where `typical = (high + low + close) / 3`, reset at the
first 15-minute bar of the RTH session (the 09:30 bar).

The bar-typical formulation is normative. It is *less* accurate than a
tick-accumulated VWAP and is chosen anyway, for one reason: it is the only
formulation both implementations can compute identically. NinjaTrader can only
accumulate a tick-exact VWAP under Tick Replay, and Tick Replay is mutually
exclusive with High Order Fill Resolution — choosing the exact VWAP would force
the NinjaTrader side into the configuration where its fills are not trustworthy.
The bar-typical VWAP deviates from the true VWAP by a few ticks intraday, but it
deviates *identically on both sides*, which is the property that matters.

### R2 — Drift state

Evaluated at the close of every 15-minute bar. `close₁₅[0]` is the bar just
closed; `close₁₅[4]` is four bars (one hour) earlier.

    LONG   ⟺  close₁₅[0] > VWAP[0]
          and  VWAP[0]    > VWAP[1]
          and  close₁₅[0] / close₁₅[4] − 1 ≥ +0.001

    SHORT  ⟺  close₁₅[0] < VWAP[0]
          and  VWAP[0]    < VWAP[1]
          and  close₁₅[0] / close₁₅[4] − 1 ≤ −0.001

    FLAT   ⟺  neither

Comparisons are strict; a tie is FLAT.

**The lookback must not cross the session boundary.** The state is FLAT until
five 15-minute bars have closed *within the current RTH session*. This is not
defensive padding: the tape is RTH-filtered and therefore contiguous across
days, so a naive `bars[i-4]` at 09:45 reaches yesterday's 15:15 bar and measures
a one-hour rate of change across the overnight gap. The same class of trap —
an index that silently walks into the previous session — has already cost this
workspace three separate defects in `PropSim/continuous.py`.

Counting from the 09:30 bar, the fifth close lands at 10:45. R5 opens the entry
window at 10:30, so **the first fifteen minutes of the tradeable window are FLAT
by construction**. That is a consequence of the rules as described, not a choice,
and it is expected to show up in §9.4's histogram.

### R3 — Arming

A state machine, evaluated at each 15-minute close, carrying `armed` (bool) and
`armed_dir`:

    if state ≠ previous_state:
        armed     = (state ≠ FLAT)
        armed_dir = state
    # if the state is unchanged, `armed` keeps whatever value it holds —
    # in particular it stays false after a trigger has consumed it.

Consequence, and it is intended: a drift that holds all afternoon without
interruption produces **one** trade. Re-arming requires the three conditions to
break and return.

### R4 — Trigger and entry

Evaluated at the close of every 5-minute bar, when `armed` and R5's window
allows and no position is open:

- `armed_dir == LONG` and `close₅ < open₅` → enter long
- `armed_dir == SHORT` and `close₅ > open₅` → enter short

Firing sets `armed = false`.

Entry is a **market order at the open of the next 5-minute bar**. The source is
explicit that the pullback bar need not reach or touch the VWAP.

**Ordering requirement (a lookahead trap).** The drift state a trigger reads at
time *T* must be the state as of the last 15-minute bar that closed **at or
before** *T*. At 10:45, 11:00, … a 5-minute and a 15-minute bar close together;
an implementation that reads a 15-minute bar still in formation is reading the
future. Both sides must derive the state from completed 15-minute bars only, and
`research/compare_mirror.py` is the check that catches a violation.

### R5 — Time windows

| | |
|---|---|
| No entries before | 10:30:00 ET (lets the VWAP form) |
| No entries after | 15:30:00 ET |
| Flatten all | 15:55:00 ET |

**The flatten needs an engine change, and §5.3's claim to the contrary is
wrong.** `entries()` returns an entry, a stop and a target and never sees the
exit path, so a wall-clock exit cannot live in the plugin. PropSim's engine
today closes an unresolved trade only at the 16:00 RTH session end — five
minutes late, at a different price than NinjaTrader's, which would fail the
mirror gate on every trade still open after 15:55. `resolve()` therefore gains a
`flatten_hhmm` bound with a neutral default of 0, the same pattern by which
`contracts`, `day_target` and `be_offset_ticks` entered without any other
strategy noticing. §5.3 remains correct about the *loss cap*, which genuinely
needs nothing.

### R6 — Exits

| | Long | Short |
|---|---|---|
| Stop | 80 points (320 ticks) | 80 points (320 ticks) |
| Target | 40 points (160 ticks) | 50 points (200 ticks) |

Plus the R5 time exit. The asymmetric target is transcribed faithfully and is
believed to be a fitting artefact — the described mechanism is symmetric and
offers no reason for shorts to earn a wider target. It is **not** corrected here;
correcting it would make this a different strategy than the one under test. It is
recorded as a hypothesis to examine after V1 reports.

### R7 — Structural

One position at a time. On the PropSim side this is the engine's own rule
(`engine.py`: `entries()` emits candidates, the engine drops those landing while
a trade is open); on the NinjaScript side it is native. No implementation work.

## 5. Parameters

### 5.1 The list is CLOSED

Parameter names are the NinjaScript property names in snake_case. Every dial here
exists on both sides and every dial on either side exists here. This is not
tidiness: an extra parameter on one side silently makes a PropSim run and a
Market Replay run two different experiments.

| Name | Default | Note |
|---|---|---|
| `drift_lookback_bars` | 4 | 15-minute bars in the rate-of-change window |
| `drift_min_pct` | 0.10 | percent over that window |
| `stop_points` | 80 | |
| `target_points_long` | 40 | |
| `target_points_short` | 50 | |
| `trade_start_hhmm` | 1030 | |
| `trade_stop_hhmm` | 1530 | |
| `flatten_hhmm` | 1555 | |
| `contracts` | 1 | |
| `max_trades_per_day` | **0 = off** | see §5.3 |
| `max_losses_per_day` | **0 = off** | see §5.3 |

The PropSim plugin declares `stop_ticks = 320` so the engine selfcheck bounds
stop-outs correctly, `uses_ticks = True`, and `full_session = False`.

### 5.2 The timeframe is a property of the run, not of the code

Measured on this workspace's own engine: a moving-average cross returns −$10,575
at 1-minute and +$300 at 5-minute **on the same ticks**. A strategy silently
running on the wrong timeframe is a different strategy.

Both implementations therefore **assert** their execution series is 5-minute and
refuse to run otherwise — NinjaScript by checking `BarsPeriod` at `DataLoaded`,
the plugin by checking the spacing of `bars["t"]` (PropSim's `tf_secs` defaults
to 300 but the caller may pass anything).

### 5.3 Why the two daily guardrails are off

The source specifies max 4 trades/day and max 2 losses/day. Both are disabled in
V1 and applied as **post-hoc filters on the resolved trade list** instead. Three
reasons:

1. **They are exact filters, not approximations.** No trade's outcome depends on
   whether earlier trades were taken: the trigger is dictated by price, not by
   P&L, and only one position is open at a time. "The first N of the day" and
   "stop after the 2nd loser" therefore only ever *remove* trades from the list.
   One uncapped run yields every capped variant with no re-simulation.
2. **A trade cap inside `entries()` would break the mirror.** `entries()` emits
   candidates; the engine then drops those overlapping an open position. A cap
   counted there counts *candidates*, while NinjaTrader counts *fills* — the two
   sides would trade different sets while believing they ran the same rule.
3. **The loss cap is the thing being measured.** Javier's decision for V1 is to
   observe the losing-streak distribution and set the cap from it, rather than
   inherit the source's number.

Avoiding a simulated loss cap also removes the need for a `day_max_losses`
governor in `PropSim/engine.py`, which would otherwise have to be added: the
existing daily governor is denominated in dollars, and a dollar threshold is not
the same rule as a count.

## 6. Ambiguities in the source, and how each was resolved

| # | Ambiguity | Resolution | Why |
|---|---|---|---|
| 1 | "Max two losses a day" — he says *consecutive* and *total* in one breath | Total, reset daily — then **disabled** in V1 and measured instead | Decision; the measured streak distribution should set it |
| 2 | What re-arms the trigger — he says "the first pullback" yet allows 4 trades/day | The 15-minute conditions must break and return (R3) | Decision. It is also the only reading that is path-independent, so both sides compute it identically. "Re-arm when the previous trade closes" would have inherited the NT8↔PropSim divergence that `LatigoBreak` documents |
| 3 | VWAP source — "a VWAP based on the 15-minute chart" | Bar typical price × bar volume (R1) | The only formulation reproducible on both sides without forcing NT8 into Tick Replay |
| 4 | The 1-hour rate of change | `close₁₅[0] / close₁₅[4] − 1` | His own words: "the increase over the past 4 15-minute bars, which is 1 hour" |
| 5 | "Red candle" | `close < open` on the 5-minute series | Literal |
| 6 | Entry timing | Market at the next 5-minute bar's open | His own words: "as the candle closes, you send a market order" |
| 7 | Must the pullback reach the VWAP? | No | His own words: "it doesn't matter how close it is to the VWAP" |

Ambiguities 1–3 were genuine coin flips; 4–7 have a single literal reading.

## 7. Architecture

Public repo `jalv92/DriftVwapPullback`, MIT, mirroring the PullbackZone layout.

| Path | What it is |
|---|---|
| `ninjascript/DriftVwapPullbackStrategy.cs` | One file. A Strategy, not an indicator — Market Replay needs a Strategy to produce trades, and a Strategy draws the VWAP, the drift state and the trigger bars just as an indicator would. 15-minute regime series via `AddDataSeries`; execution on the primary 5-minute series |
| `propsim/drift_vwap_pullback.py` | PropSim plugin. Reuses the per-session VWAP accumulator pattern from `vwap_revert` (`PropSim/engine.py`) |
| `research/compare_mirror.py` | The fidelity gate (§8) |
| `docs/specs/` | This document |

## 8. Mirror-fidelity gate

Before any backtest number from either side is believed, both implementations run
over the same span and must agree trade for trade: same entry bar, same
direction, same exit price. `LatigoBreak` closed this gate at 4 of 5 trades
identical to the cent; that is the bar.

**Until this gate passes, the backtest is not read.** A number produced by two
implementations that disagree is a number about neither of them.

## 9. What the first run measures — pre-registered

On PropSim's ALL continuous tape (275 sessions of real NQ ticks, 2025-08-01 →
2026-08-04), tick-true fills, uncapped:

1. **The losing-streak distribution** — the reason V1 exists. The daily loss cap
   is set from this, measured, not inherited.
2. **Fidelity controls, not profit controls.** Trades per day against his 3.2
   (4,000 trades over ~1,250 sessions), win rate against his 64 %, average
   win/loss against his 43.3 / 65.0 points. If these three agree, the transcribed
   rules are the rules he described. If they disagree, the transcription is wrong
   and must be re-read **before** any profit figure is interpreted.
3. The `strategy-profitability-gates` table, and the daily t-statistic.
4. Trades by hour of day — to falsify or confirm the suspicion in §10.

Note on the tape: it starts 2025-08-01, inside the window the source calls
out-of-sample. It is unseen by his *parameter fitting*, which is what matters for
overfitting, but it is not unseen by his reporting.

## 10. Known risks

- **The edge is 4 percentage points of win rate.** With a 0.67 payoff ratio the
  breakeven win rate is 60.0 % against his reported 64 %. Two ticks of slippage
  or a regime shift closes that gap. This is the single most fragile number in
  the strategy.
- **Profit factor 1.18 and expectancy 0.066R fail this workspace's own gates**
  (≥1.30 and ≥0.10R) on the source's own reported figures. V1 will say whether
  our tape agrees.
- **The VWAP flattens as the session runs.** Its denominator is cumulative
  volume, so by early afternoon R2's slope test discriminates on ever-smaller
  changes and approaches a coin flip. Prediction: trades concentrate between
  10:30 and 12:00. §9.4 tests it.
- **The mechanism story may be wrong even if the P&L is right.** He attributes
  the effect to VWAP-targeting execution algorithms clustering near the VWAP;
  but a VWAP algorithm slices against a *volume schedule* and benchmarks to VWAP,
  it does not wait for price to return to it. The empirical claim stands or falls
  independently of the story, and the story needs its own falsification test —
  the discipline that killed `break-retest` and `cbde`, both of which had
  positive P&L and inverted mechanism diagnostics.
- **This is the fifth strategy of the "pullback in a trend" family tested here**
  after Pullback (archived, PF 1.06), break-retest, trendline-retest and
  PullbackZone. The prior is negative. What makes it non-redundant is that the
  level is a VWAP rather than swing structure, and that its R is negative
  (target < stop), which inverts the funnel's paid-for lesson that NQ needs
  R ≥ 1.5.
