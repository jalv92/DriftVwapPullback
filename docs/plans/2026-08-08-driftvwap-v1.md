# DriftVwapPullback V1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the closed spec twice — a NinjaTrader 8 strategy and a PropSim plugin — prove they trade identically, and produce the pre-registered measurement run.

**Architecture:** One closed spec (`docs/specs/2026-08-07-driftvwap-design.md`) drives two independent implementations. The PropSim side is a sandboxed numpy plugin whose `entries()` emits candidate entries with stop and target prices; the engine resolves fills over real ticks. The NinjaScript side runs a 5-minute primary series with a 15-minute secondary series added via `AddDataSeries`. Both dump a JSONL trade list, and `research/compare_mirror.py` joins the two and returns PASS/FAIL. No backtest number is read until that gate passes.

**Tech Stack:** Python 3.12 + numpy (PropSim plugin, research scripts, stdlib `json` only for JSONL); C# / NinjaScript for NinjaTrader 8; `nt8c` for out-of-editor compilation.

## Global Constraints

- **The spec is normative and closed.** Every rule cited below as `R1`–`R7`, `§5`, `§6` refers to `docs/specs/2026-08-07-driftvwap-design.md`. If code and spec disagree, the spec wins; changing the spec is a separate commit with its own justification.
- **Parameter names are identical on both sides** and the list is closed (§5.1). NinjaScript property names in PascalCase, plugin keys in snake_case, one-to-one. An extra dial on one side makes the two runs different experiments.
- **Instrument NQ**, tick 0.25, 4 ticks per point, RTH `[09:30:00, 16:00:00)` America/New_York.
- **Defaults, both sides:** `drift_lookback_bars=4`, `drift_min_pct=0.10`, `stop_points=80`, `target_points_long=40`, `target_points_short=50`, `trade_start_hhmm=1030`, `trade_stop_hhmm=1530`, `flatten_hhmm=1555`, `contracts=1`, `max_trades_per_day=0`, `max_losses_per_day=0`.
- **No parameter is tuned in V1.** No sweep, no optimisation, no "try 0.15 and see".
- **The PropSim plugin runs in a sandbox** (`PropSim/plugins.py`): no imports except `math` and `numpy`; `np`, `tp`, `Strategy` and `Param` are injected; no dunder *attribute* access; no `open`/`eval`/`exec`/`getattr`; no `global`/`nonlocal`. Bare `__name__` is allowed, which is how the file runs its own selfchecks standalone.
- **Timestamp convention differs between the two systems and must be normalised at the join, not in the strategy code.** PropSim stamps a bar with the timestamp of its **first tick** (`tape.build_bars`: `t=ts[starts]`, i.e. the bar's OPEN). NinjaTrader stamps a minute bar at its **CLOSE**. A PropSim 5-minute bar at `t` is the NinjaTrader bar at `t + 300s`. Getting this wrong shifts every trade by one bar and the mirror will report a total mismatch that looks like a logic bug.
- **Commit after every task.** Conventional commits, English, `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`.

---

## File Structure

| File | Repo | Responsibility |
|---|---|---|
| `engine.py` (modify) | PropSim | Wall-clock flatten in `resolve()`. Neutral default, no other strategy affected |
| `propsim/drift_vwap_pullback.py` | DriftVwapPullback | The plugin: 15m aggregation, session VWAP, drift state, arming, triggers, `entries()`. Carries its own selfchecks |
| `ninjascript/DriftVwapPullbackStrategy.cs` | DriftVwapPullback | The strategy: same rules, orders, chart drawing, JSONL dump |
| `research/dump_trades.py` | DriftVwapPullback | Runs the plugin over a tape and writes the PropSim-side JSONL |
| `research/compare_mirror.py` | DriftVwapPullback | The gate: joins both JSONL files, returns PASS/FAIL |
| `research/report.py` | DriftVwapPullback | The pre-registered measurements (§9) |
| `docs/validation.md` | DriftVwapPullback | What has actually been run, and its result |

---

### Task 1: PropSim engine — wall-clock flatten

R5 requires flattening at 15:55 ET. The engine currently closes an unresolved trade only at the session end (16:00), so a trade entered after ~15:30 and still open would exit five minutes late and at a different price than NinjaTrader's. `entries()` cannot express this — it returns entry, stop and target, and never sees the exit path.

This is the same neutral-default extension pattern already used for `contracts`, `day_target` and `be_offset_ticks`: `p.get(...)` with a default that reproduces today's behaviour exactly, so every existing strategy is unaffected.

**Files:**
- Modify: `../PropSim/engine.py` (the `resolve()` exit-bound computation, around the `session_end` / `stop_end` block near line 1058)
- Modify: `../PropSim/engine.py` (the `--selfcheck` block)

**Interfaces:**
- Produces: `resolve(..., flatten_hhmm=0)` — an integer `HHMM` in ET; `0` means off. When set, a trade's exit index is additionally bounded by the first tick of its session whose `sec_of_day >= flatten_hhmm`, and the exit reason becomes `"flatten"`.

- [ ] **Step 1: Read the exit-bound code before touching it**

Read `engine.py` from the definition of `resolve()` through the block computing `session_end`, `stop_end`, `exit_i`, `exit_px` and `why`. Note the exact local names — the snippets below assume `session_end` and `why` as they appear today, and must be adapted if they differ.

- [ ] **Step 2: Write the failing selfcheck**

Add to the `--selfcheck` block in `engine.py`, following the numbering already there (this is check `10g`):

```python
# 10g wall-clock flatten bounds the exit and is OFF by default.
# A synthetic session: entry at 10:00, stop and target both far away, so the
# only thing that can close the trade is a time bound.
_t0 = tp.day_index_epoch_for_test() if False else None   # no helper needed
tape_f = _synthetic_session(open_px=20000.0, drift=0.0)  # see Step 3
tr_off = resolve(tape_f, entry_i=_idx_at(tape_f, 10 * 3600),
                 direc=1, stop=19000.0, target=21000.0)
tr_on = resolve(tape_f, entry_i=_idx_at(tape_f, 10 * 3600),
                direc=1, stop=19000.0, target=21000.0, flatten_hhmm=1555)
assert tr_off.why == "close", f"default must be unchanged, got {tr_off.why}"
assert tr_on.why == "flatten", f"flatten must bound the exit, got {tr_on.why}"
assert tp.sec_of_day(tr_on.exit_ts) == 15 * 3600 + 55 * 60, (
    f"flatten exited at {tp.sec_of_day(tr_on.exit_ts)}, want 57300")
assert tr_on.exit_ts < tr_off.exit_ts, "flatten must exit strictly earlier"
```

`_synthetic_session` and `_idx_at` are small local helpers built from the fixture style already used by the neighbouring checks; write them alongside if the file has no equivalent. If `resolve()`'s signature or `Trade` field names differ from the guesses above (`why`, `exit_ts`), adapt the assertions to the real names — do not rename engine fields to match this plan.

- [ ] **Step 3: Run the selfcheck to verify it fails**

Run: `cd ../PropSim && python3 engine.py --selfcheck`
Expected: FAIL on `10g`, with `TypeError: resolve() got an unexpected keyword argument 'flatten_hhmm'`.

- [ ] **Step 4: Implement the bound**

In `resolve()`, accept the parameter and fold it into the existing exit bound:

```python
def resolve(..., flatten_hhmm: int = 0):
    ...
    # existing: session_end = int(np.searchsorted(day, day[i0], "right"))
    if flatten_hhmm:
        cut_s = (flatten_hhmm // 100) * 3600 + (flatten_hhmm % 100) * 60
        # First tick of THIS session at or after the cut. Bounded by session_end
        # so a cut past 16:00 cannot reach into the next day.
        sod = tp.sec_of_day(ts)
        flat_end = int(np.searchsorted(sod[i0:session_end], cut_s, "left")) + i0
        if flat_end < session_end:
            session_end = flat_end
            flattened = True
```

and carry `flattened` into the reason so `why` becomes `"flatten"` rather than `"close"` when the flatten bound is the binding one. Thread the parameter through `backtest()` the same way `timeout_min` is threaded (`p.get("flatten_hhmm", 0)`).

**The searchsorted must run on the current session's slice, not the whole tape.** `sec_of_day` is periodic; searching globally finds the first 15:55 of the *dataset*, which for any trade after day one is in the past and would close every trade at its own entry tick.

- [ ] **Step 5: Run the full engine selfcheck**

Run: `cd ../PropSim && python3 engine.py --selfcheck`
Expected: PASS, including every pre-existing check. Checks `10a`–`10f` and the ORB timeframe-invariance check must be untouched — if any of them moved, the default is not neutral and the change is wrong.

- [ ] **Step 6: Commit (in the PropSim repo)**

```bash
cd ../PropSim
git add engine.py
git commit -m "feat(engine): wall-clock flatten bound in resolve(), default off

R5 of the DriftVwapPullback spec flattens at 15:55 ET; the engine could only
close an unresolved trade at the 16:00 session end, which is a different exit
price than NinjaTrader's and would fail a mirror gate on every late trade.

Default 0 reproduces today's behaviour exactly, so no existing strategy sees a
change -- the same neutral-default pattern as contracts and day_target. The
searchsorted runs on the trade's own session slice: sec_of_day is periodic, and
searching the whole tape would find a 15:55 in the past and close every trade
at its entry tick.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: Plugin — 15-minute aggregation, session VWAP, drift state

**Files:**
- Create: `propsim/drift_vwap_pullback.py`

**Interfaces:**
- Produces:
  - `_regime(bars) -> dict` with keys `key` (int64 15m bucket id), `c`, `vwap`, `state` (int8, +1/-1/0), `n_in_session` (int32), all length = number of 15m bars.
  - `_bucket15(t) -> np.ndarray` — the int64 bucket id `day_index(t) * 96 + sec_of_day(t) // 900`.
  - Module constants `TICK = 0.25`, `PTS = 4` (ticks per point), `RTH_START_S = 34200`.

- [ ] **Step 1: Write the failing selfchecks**

Create the file with the sandbox-compatible preamble and the first two selfchecks:

```python
#!/usr/bin/env python3
"""DriftVwapPullback core + PropSim plugin.

Spec: docs/specs/2026-08-07-driftvwap-design.md. The parameter list is CLOSED
and mirrors ninjascript/DriftVwapPullbackStrategy.cs one-to-one.

The sandbox injects `np`, `tp`, `Strategy` and `Param` instead of letting a
plugin import them. numpy is imported anyway (the injected binding grants
nothing an explicit import does not) so this file can run its own selfchecks
standalone. Stand-ins below make the class degrade to a plain object outside
the sandbox; NameError rather than a try/except import, because plugins.py's
AST check rejects an import inside a try exactly like a bare one.
"""
import numpy as np

try:
    Strategy
except NameError:                        # pragma: no cover - sandbox stand-in
    class Strategy:
        pass

    class Param:
        def __init__(self, default, lo, hi, desc, fixed=False):
            self.default, self.lo, self.hi = default, lo, hi
            self.desc, self.fixed = desc, fixed

TICK = 0.25
PTS = 4                                  # NQ ticks per index point
RTH_START_S = 9 * 3600 + 30 * 60         # 34200
BUCKETS_PER_DAY_15 = 96                  # 86400 / 900


def _selfcheck_regime():
    # Two sessions of synthetic 5-minute bars, 78 per session (09:30..16:00).
    bars = _fake_bars(sessions=2, drift_pts_per_bar=4.0)
    r = _regime(bars)
    assert len(r["key"]) == 2 * 26, f"26 fifteen-minute bars per session, got {len(r['key'])}"
    # A monotonically rising session: once five bars exist, the state is LONG.
    first = r["n_in_session"] < 4
    assert (r["state"][first] == 0).all(), "state must be FLAT before the 5th bar"
    assert (r["state"][~first] == 1).all(), "a rising session must read LONG"


def _selfcheck_vwap_resets_per_session():
    bars = _fake_bars(sessions=2, drift_pts_per_bar=4.0)
    r = _regime(bars)
    starts = np.flatnonzero(r["n_in_session"] == 0)
    assert len(starts) == 2, "two session starts"
    for s in starts:
        # The first bar's VWAP is its own typical price, to the cent.
        assert abs(r["vwap"][s] - r["typ"][s]) < 1e-9, (
            "VWAP must restart at each session, not carry over")


def _selfcheck_lookback_never_crosses_sessions():
    # A session that FALLS hard after a session that ROSE hard. If the lookback
    # walked into the previous session, the first bars of session 2 would read
    # LONG off yesterday's closes. The RTH tape is contiguous across days, so
    # this is the trap the spec calls out in R2.
    bars = _fake_bars(sessions=2, drift_pts_per_bar=4.0, flip_second=True)
    r = _regime(bars)
    s2 = np.flatnonzero(r["n_in_session"] == 0)[1]
    assert (r["state"][s2:s2 + 4] == 0).all(), (
        "the first four 15m bars of a session must be FLAT: no completed "
        "one-hour lookback exists inside the session yet")
    assert r["state"][s2 + 4] == -1, "the falling session must read SHORT once it can"
```

- [ ] **Step 2: Run to verify they fail**

Run: `python3 propsim/drift_vwap_pullback.py`
Expected: FAIL with `NameError: name '_fake_bars' is not defined`.

- [ ] **Step 3: Implement `_fake_bars`, `_bucket15` and `_regime`**

```python
def _fake_bars(sessions=1, drift_pts_per_bar=0.0, flip_second=False, day0=20000,
               pullback_every=0, flat_window=None):
    """Synthetic 5-minute RTH bars: 78 per session, 09:30..15:55 inclusive.

    `pullback_every=N` makes every Nth bar a counter-direction candle without
    reversing the trend (N=5 nets +3 points per 5 bars), which is what gives
    the long trigger something to fire on.

    `flat_window=(lo_s, hi_s)` holds price flat across that seconds-of-day
    span, which zeroes the one-hour rate of change and drives the drift state
    to FLAT — the break a re-arm test needs.

    THE DRIFT MAGNITUDE IS LOAD-BEARING, not decoration. R2 needs more than
    0.10% over one hour, and one hour is twelve 5-minute bars. At 20000, that
    is more than 20 points per hour, so more than ~1.67 points per bar — and
    `pullback_every=5` cuts the net rate by a further 40%. The checks use 4.0,
    which nets ~2.4 points per bar with pullbacks on (~0.14% per hour) and 4.0
    with them off (~0.24%). At 1.0 the fixture yields 0.06% per hour, every
    state reads FLAT, and every state assertion fails — or worse, an assertion
    written as "at most one entry" passes on zero entries and tests nothing.
    If you change this number, redo the arithmetic.
    """
    t, o, h, l, c, v, start = [], [], [], [], [], [], []
    px = 20000.0
    tick_i = 0
    for d in range(sessions):
        step = drift_pts_per_bar * (-1.0 if (flip_second and d == 1) else 1.0)
        for k in range(78):
            sod = RTH_START_S + k * 300
            ts = (day0 + d) * 86400 * tp.TPS + sod * tp.TPS
            if flat_window is not None and flat_window[0] <= sod < flat_window[1]:
                delta = 0.0
            elif pullback_every and (k % pullback_every) == pullback_every - 1:
                delta = -step
            else:
                delta = step
            nxt = px + delta
            t.append(ts); o.append(px); c.append(nxt)
            h.append(max(px, nxt) + 1.0); l.append(min(px, nxt) - 1.0)
            v.append(100.0); start.append(tick_i)
            tick_i += 1
            px = nxt
    n = len(t)
    return dict(t=np.array(t, np.int64), o=np.array(o), h=np.array(h),
                l=np.array(l), c=np.array(c), v=np.array(v),
                start=np.arange(n, dtype=np.int64),
                end=np.arange(1, n + 1, dtype=np.int64),
                n=np.ones(n, np.int64), delta=np.zeros(n))


def _bucket15(t):
    """The 15-minute bucket id of each timestamp: unique, ordered, day-aware."""
    return (tp.day_index(t).astype(np.int64) * BUCKETS_PER_DAY_15
            + (tp.sec_of_day(t) // 900).astype(np.int64))


def _regime(bars):
    """Aggregate 5m bars into 15m bars, then session VWAP and drift state."""
    t5 = bars["t"]
    key5 = _bucket15(t5)
    starts = np.concatenate(([0], np.flatnonzero(np.diff(key5)) + 1))
    ends = np.concatenate((starts[1:], [len(t5)]))

    t15 = t5[starts]
    h15 = np.maximum.reduceat(bars["h"], starts)
    l15 = np.minimum.reduceat(bars["l"], starts)
    c15 = bars["c"][ends - 1]
    v15 = np.add.reduceat(bars["v"], starts)
    typ = (h15 + l15 + c15) / 3.0

    day = tp.day_index(t15)
    new_day = np.empty(len(t15), bool)
    new_day[0] = True
    new_day[1:] = day[1:] != day[:-1]

    vwap = np.zeros(len(t15))
    n_in = np.zeros(len(t15), np.int32)
    acc_pv = acc_v = 0.0
    k = 0
    for i in range(len(t15)):
        if new_day[i]:
            acc_pv = acc_v = 0.0
            k = 0
        acc_pv += typ[i] * v15[i]
        acc_v += v15[i]
        vwap[i] = acc_pv / max(acc_v, 1.0)
        n_in[i] = k
        k += 1
    return dict(key=key5[starts], t=t15, c=c15, typ=typ, vwap=vwap,
                n_in_session=n_in, state=np.zeros(len(t15), np.int8))
```

Then the state, computed with the lookback confined to the session:

```python
def _drift_state(r, lookback, min_pct):
    """R2. FLAT until `lookback` bars have CLOSED inside the current session."""
    c, vwap, n_in = r["c"], r["vwap"], r["n_in_session"]
    st = np.zeros(len(c), np.int8)
    if len(c) <= lookback:
        return st
    # c[i-lookback] is only meaningful when it belongs to the same session,
    # which n_in_session >= lookback guarantees without any day arithmetic.
    ok = n_in >= lookback
    prev = np.empty(len(c))
    prev[:] = np.nan
    prev[lookback:] = c[:-lookback]
    roc = np.where(ok, c / np.where(np.isnan(prev), 1.0, prev) - 1.0, 0.0)
    rising = np.zeros(len(c), bool)
    rising[1:] = vwap[1:] > vwap[:-1]
    falling = np.zeros(len(c), bool)
    falling[1:] = vwap[1:] < vwap[:-1]
    thr = min_pct / 100.0
    st[ok & (c > vwap) & rising & (roc >= thr)] = 1
    st[ok & (c < vwap) & falling & (roc <= -thr)] = -1
    return st
```

Wire `_regime` to fill `state` by calling `_drift_state` with the defaults, and add the `__main__` block:

```python
if __name__ == "__main__":
    _selfcheck_regime()
    _selfcheck_vwap_resets_per_session()
    _selfcheck_lookback_never_crosses_sessions()
    print("selfcheck OK: regime, VWAP session reset, session-confined lookback")
```

Note `_regime` needs `tp` outside the sandbox too. Add a minimal stand-in next to the `Strategy` stand-in that implements `TPS`, `day_index` and `sec_of_day` over int64 .NET ticks, so the file runs standalone.

- [ ] **Step 4: Run to verify they pass**

Run: `python3 propsim/drift_vwap_pullback.py`
Expected: `selfcheck OK: regime, VWAP session reset, session-confined lookback`

- [ ] **Step 5: Commit**

```bash
git add propsim/drift_vwap_pullback.py
git commit -m "feat(propsim): 15m aggregation, session VWAP and drift state

R1 and R2 of the spec. The lookback is confined to the session by
n_in_session >= lookback rather than by day arithmetic: the RTH tape is
contiguous across days, so a naive c[i-4] at 09:45 reaches yesterday's 15:15
bar and measures a one-hour move across the overnight gap. A selfcheck builds
a falling session after a rising one and fails if that leaks.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: Plugin — arming, trigger, and `entries()`

**Files:**
- Modify: `propsim/drift_vwap_pullback.py`

**Interfaces:**
- Consumes: `_regime`, `_drift_state`, `_bucket15` from Task 2.
- Produces: `class DriftVwapPullback(Strategy)` with `name = "drift_vwap_pullback"`, and `entries(bars, tape, p) -> (entry_tick_idx int64, direction int8, stop float64, target float64)`.

- [ ] **Step 1: Write the failing selfchecks**

```python
def _selfcheck_state_applies_at_coincident_close():
    # The 15m bar closing at 10:45 and the 5m bar closing at 10:45 close at the
    # same instant. The spec (R4) says the trigger at T uses the state of the
    # last 15m bar closing AT OR BEFORE T, so that bar counts. This is the
    # single rule the NinjaScript side must match and the one most likely to
    # drift, so it is pinned here in the mirror's own vocabulary.
    bars = _fake_bars(sessions=1, drift_pts_per_bar=4.0)
    key_done = _last_completed_key(bars)
    # 5m bar index 15 opens 10:45 and closes 10:50 -> group closing 10:45.
    # 5m bar index 14 opens 10:40, closes 10:45 -> the 10:45 group counts.
    assert key_done[14] == key_done[15], (
        "the 5m bar closing at 10:45 must already see the 10:45 15m bar")
    assert key_done[13] < key_done[14], "the bar before it must see one fewer"


def _selfcheck_arming_requires_a_transition():
    # An uninterrupted drift produces exactly ONE trade (R3): the arm is
    # consumed by the FIRST counter-direction candle and never re-arms,
    # because re-arming needs the 15m state to leave LONG and come back.
    #
    # `pullback_every` is load-bearing. Without it a monotonically rising
    # session has no red candle at all, the long trigger can never fire, and
    # an assertion of "at most one entry" would pass on zero entries -- true
    # for the wrong reason, and blind to an arming bug that fires on every
    # red candle. The session must CONTAIN pullbacks for this to test arming.
    bars = _fake_bars(sessions=1, drift_pts_per_bar=4.0, pullback_every=5)
    s = DriftVwapPullback()
    idx, direc, stop, target = s.entries(bars, _fake_tape(bars), _defaults())
    assert len(idx) == 1, (
        f"an unbroken drift must produce exactly one entry, got {len(idx)}; "
        f"more than one means arming re-fires without a state transition")
    assert direc[0] == 1, "a rising session must go long"


def _selfcheck_arming_rearms_after_a_break():
    # The mirror of the check above: when the state DOES leave LONG and come
    # back, a second trade is allowed. Together the two pin R3 from both
    # sides -- one alone is satisfiable by a strategy that never re-arms.
    bars = _fake_bars(sessions=1, drift_pts_per_bar=4.0, pullback_every=5,
                      flat_window=(11 * 3600, 12 * 3600))
    s = DriftVwapPullback()
    idx, _, _, _ = s.entries(bars, _fake_tape(bars), _defaults())
    assert len(idx) == 2, (
        f"a drift that breaks and returns must arm twice, got {len(idx)}")


def _selfcheck_stop_and_target_sides():
    # `pullback_every` and the non-empty assertion are BOTH load-bearing. Without
    # the first there is no red candle, the long trigger never fires, `idx` comes
    # back empty, and the loop below asserts nothing while reporting success —
    # a stop placed on the WRONG SIDE of its entry sails straight through. That
    # is the bug PropSim's own plugins.py calls "free money that does not exist".
    # No loop in this file may be reachable with an empty array.
    bars = _fake_bars(sessions=1, drift_pts_per_bar=4.0, pullback_every=5)
    s = DriftVwapPullback()
    idx, direc, stop, target = s.entries(bars, _fake_tape(bars), _defaults())
    assert len(idx) > 0, "fixture produced no entries; this check would be vacuous"
    for i in range(len(idx)):
        fill = _fake_tape(bars)["px"][idx[i]]
        if direc[i] > 0:
            assert stop[i] < fill < target[i], "long: stop below, target above"
            assert abs(fill - stop[i] - 80.0) < 1e-9
            assert abs(target[i] - fill - 40.0) < 1e-9
        else:
            assert target[i] < fill < stop[i], "short: stop above, target below"
            assert abs(stop[i] - fill - 80.0) < 1e-9
            assert abs(fill - target[i] - 50.0) < 1e-9


def _selfcheck_short_side():
    # Every other fixture rises, so armed_dir == -1, the short stop/target math
    # and target_points_short are exercised by NOTHING without this. A sign error
    # confined to the short branch would otherwise pass the whole suite.
    bars = _fake_bars(sessions=1, drift_pts_per_bar=-4.0, pullback_every=5)
    s = DriftVwapPullback()
    idx, direc, stop, target = s.entries(bars, _fake_tape(bars), _defaults())
    assert len(idx) > 0, "fixture produced no entries; this check would be vacuous"
    tape = _fake_tape(bars)
    for i in range(len(idx)):
        fill = tape["px"][idx[i]]
        assert direc[i] == -1, "a falling session must go short"
        assert target[i] < fill < stop[i], "short: stop above, target below"
        assert abs(stop[i] - fill - 80.0) < 1e-9
        assert abs(fill - target[i] - 50.0) < 1e-9


def _selfcheck_time_window():
    bars = _fake_bars(sessions=1, drift_pts_per_bar=4.0, pullback_every=5)
    s = DriftVwapPullback()
    idx, direc, _, _ = s.entries(bars, _fake_tape(bars), _defaults())
    assert len(idx) > 0, "fixture produced no entries; this check would be vacuous"
    for i in idx:
        sod = tp.sec_of_day(bars["t"][i])
        assert 10 * 3600 + 30 * 60 <= sod <= 15 * 3600 + 30 * 60, (
            f"entry at {sod}s is outside the R5 window")
```

Fix the deliberate call-signature typo in `_selfcheck_stop_and_target_sides` when writing it — `s.entries(bars, _fake_tape(bars), _defaults())` positionally. Add `_fake_tape(bars)` returning `dict(ts=bars["t"], px=bars["o"], vol=bars["v"], side=np.zeros(len(bars["t"]), np.int8))` so one tick stands for one bar, and `_defaults()` returning the params dict from `PARAMS_DEFAULT`.

- [ ] **Step 2: Run to verify they fail**

Run: `python3 propsim/drift_vwap_pullback.py`
Expected: FAIL with `NameError: name '_last_completed_key' is not defined`.

- [ ] **Step 3: Implement**

```python
def _last_completed_key(bars):
    """The 15m bucket whose bar has CLOSED by the close of each 5m bar.

    Slot arithmetic, not positional: a 5-minute slot with no ticks produces no
    bar, and counting positions would silently misalign the aggregate. A 5m bar
    is the 3rd of its 15m group exactly when (sec_of_day - 09:30) // 300 % 3 == 2,
    and that bar closes at the same instant as the group -- so the group counts
    (R4: "at or before T").
    """
    t = bars["t"]
    key = _bucket15(t)
    pos = ((tp.sec_of_day(t) - RTH_START_S) // 300) % 3
    return np.where(pos == 2, key, key - 1)


PARAMS_DEFAULT = dict(
    drift_lookback_bars=4, drift_min_pct=0.10,
    stop_points=80, target_points_long=40, target_points_short=50,
    trade_start_hhmm=1030, trade_stop_hhmm=1530, flatten_hhmm=1555,
    contracts=1, max_trades_per_day=0, max_losses_per_day=0,
)


class DriftVwapPullback(Strategy):
    name, label = "drift_vwap_pullback", "Drift VWAP pullback (Conti)"
    uses_ticks = True
    full_session = False

    params = {
        "drift_lookback_bars": Param(4, 1, 24, "15m bars in the rate-of-change window"),
        "drift_min_pct": Param(0.10, 0.0, 2.0, "rate-of-change threshold, percent"),
        "stop_points": Param(80, 1, 400, "stop distance, index points"),
        "target_points_long": Param(40, 1, 400, "long target, index points"),
        "target_points_short": Param(50, 1, 400, "short target, index points"),
        "trade_start_hhmm": Param(1030, 0, 2359, "no entries before, ET HHMM", fixed=True),
        "trade_stop_hhmm": Param(1530, 0, 2359, "no entries after, ET HHMM", fixed=True),
        "flatten_hhmm": Param(1555, 0, 2359, "flatten all, ET HHMM", fixed=True),
        "contracts": Param(1, 1, 100, "position size, contracts", fixed=True),
        "max_trades_per_day": Param(0, 0, 20, "0 = off, see spec 5.3", fixed=True),
        "max_losses_per_day": Param(0, 0, 20, "0 = off, see spec 5.3", fixed=True),
    }

    def risk_ticks(self, p):
        return float(p["stop_points"]) * PTS

    def entries(self, bars, tape, p):
        t = bars["t"]
        if len(t) < 6:
            return _empty()
        # 5.2: the timeframe is a property of the RUN. A bar-counting strategy
        # on the wrong timeframe is a different strategy (measured on this
        # engine: MA-cross is -$10,575 at 1m and +$300 at 5m on the same ticks).
        gaps = np.diff(tp.sec_of_day(t))
        within = gaps[gaps > 0]
        if len(within) and int(np.median(within)) != 300:
            raise ValueError("drift_vwap_pullback requires 5-minute bars "
                             f"(tf_secs=300); got ~{int(np.median(within))}s")

        r = _regime(bars)
        r["state"] = _drift_state(r, int(p["drift_lookback_bars"]),
                                  float(p["drift_min_pct"]))
        state_of = dict(zip(r["key"].tolist(), r["state"].tolist()))

        done = _last_completed_key(bars)
        sod_close = tp.sec_of_day(t) + 300
        lo = _hhmm_s(int(p["trade_start_hhmm"]))
        hi = _hhmm_s(int(p["trade_stop_hhmm"]))
        day = tp.day_index(t)
        o, c = bars["o"], bars["c"]

        out_i, out_d = [], []
        prev_state, armed, armed_dir, last_key = 0, False, 0, None
        for i in range(len(t) - 1):
            k = int(done[i])
            if k != last_key:
                st = int(state_of.get(k, 0))
                if st != prev_state:
                    armed = st != 0
                    armed_dir = st
                prev_state = st
                last_key = k
            if not armed:
                continue
            if not (lo <= sod_close[i] <= hi):
                continue
            fired = (armed_dir == 1 and c[i] < o[i]) or (armed_dir == -1 and c[i] > o[i])
            if not fired:
                continue
            if day[i + 1] != day[i]:            # never carry a signal overnight
                continue
            out_i.append(int(bars["start"][i + 1]))
            out_d.append(armed_dir)
            armed = False

        if not out_i:
            return _empty()
        entry_tick = np.array(out_i, np.int64)
        direc = np.array(out_d, np.int8)
        fill = tape["px"][entry_tick].astype(np.float64)
        stop = fill - direc * float(p["stop_points"])
        tgt_pts = np.where(direc > 0, float(p["target_points_long"]),
                           float(p["target_points_short"]))
        target = fill + direc * tgt_pts
        return entry_tick, direc, stop, target
```

Add the two small helpers `_hhmm_s(hhmm)` returning `(hhmm // 100) * 3600 + (hhmm % 100) * 60`, and `_empty()` returning `(np.array([], np.int64), np.array([], np.int8), np.array([]), np.array([]))` — the plugin cannot import the engine's, so it carries its own with the identical shape.

Register the four new selfchecks in `__main__`.

- [ ] **Step 4: Run to verify they pass**

Run: `python3 propsim/drift_vwap_pullback.py`
Expected: all seven selfchecks pass.

- [ ] **Step 5: Commit**

```bash
git add propsim/drift_vwap_pullback.py
git commit -m "feat(propsim): arming state machine, trigger and entries()

R3, R4, R5 and R6. Arming needs a state TRANSITION, so an uninterrupted drift
produces one trade rather than one per bar -- pinned by a selfcheck on a
monotonic session.

_last_completed_key does slot arithmetic rather than positional: a 5-minute
slot with no ticks produces no bar and positional grouping would misalign the
aggregate. The 5m bar closing at 10:45 already sees the 15m bar closing at
10:45 -- both are complete at that instant, which is R4's 'at or before T' and
is the rule most likely to drift on the NinjaScript side.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: Plugin — sandbox validation and a real smoke run

**Files:**
- No new files. Installs `propsim/drift_vwap_pullback.py` to `~/.prop-sim/strategies/`.

**Interfaces:**
- Consumes: the plugin from Task 3.
- Produces: a validated, installed plugin registered as `drift_vwap_pullback`.

- [ ] **Step 1: Run the sandbox validator**

Run: `cd ../PropSim && python3 plugins.py --check ../DriftVwapPullback/propsim/drift_vwap_pullback.py`
Expected: PASS. If it rejects an import or a dunder attribute, fix the plugin — do not weaken the validator.

- [ ] **Step 2: Install and confirm registration**

```bash
mkdir -p ~/.prop-sim/strategies
cp propsim/drift_vwap_pullback.py ~/.prop-sim/strategies/
cd ../PropSim && python3 plugins.py
```
Expected: `drift_vwap_pullback` listed as installed and valid.

- [ ] **Step 3: Smoke backtest on one real contract**

Run: `cd ../PropSim && python3 engine.py --strategy drift_vwap_pullback --tf 5m --contract 'NQ 09-26'`
(Adapt the flags to `engine.py --help`; the point is one real contract at 5 minutes.)

Expected: it completes and reports a non-zero trade count. **A zero-trade result is a failure, not a finding** — check first that the tape is RTH-filtered and that `full_session = False` is being honoured.

- [ ] **Step 4: Sanity-check the trade count against the source**

The source reports ~3.2 trades per session. This run is uncapped, so it should land in the same order of magnitude. Record the number; do not act on it yet — §9.2 is the real control and it runs on the ALL tape in Task 11.

- [ ] **Step 5: Commit**

```bash
git commit --allow-empty -m "chore(propsim): plugin validates and runs on real ticks

plugins.py --check passes; a smoke backtest on one contract produces trades.
No numbers read yet -- the mirror gate has not run.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: NinjaScript — skeleton, parameters, timeframe guard, 15m series

**Files:**
- Create: `ninjascript/DriftVwapPullbackStrategy.cs`

**Interfaces:**
- Produces: `class DriftVwapPullbackStrategy : Strategy` in namespace `NinjaTrader.NinjaScript.Strategies`, with the eleven public properties named in Global Constraints (PascalCase: `DriftLookbackBars`, `DriftMinPct`, `StopPoints`, `TargetPointsLong`, `TargetPointsShort`, `TradeStartHHMM`, `TradeStopHHMM`, `FlattenHHMM`, `Contracts`, `MaxTradesPerDay`, `MaxLossesPerDay`).

- [ ] **Step 1: Write the skeleton**

```csharp
protected override void OnStateChange()
{
    if (State == State.SetDefaults)
    {
        Name                        = "DriftVwapPullbackStrategy";
        Calculate                   = Calculate.OnBarClose;
        EntriesPerDirection         = 1;
        EntryHandling               = EntryHandling.AllEntries;
        IsExitOnSessionCloseStrategy= false;   // R5 flattens at 15:55 ourselves
        BarsRequiredToTrade         = 20;
        DriftLookbackBars = 4;   DriftMinPct = 0.10;
        StopPoints = 80;         TargetPointsLong = 40;  TargetPointsShort = 50;
        TradeStartHHMM = 1030;   TradeStopHHMM = 1530;   FlattenHHMM = 1555;
        Contracts = 1;           MaxTradesPerDay = 0;    MaxLossesPerDay = 0;
    }
    else if (State == State.Configure)
    {
        AddDataSeries(BarsPeriodType.Minute, 15);   // BarsInProgress == 1
    }
    else if (State == State.DataLoaded)
    {
        // Spec 5.2: the timeframe is a property of the RUN, not of the code.
        if (BarsPeriods[0].BarsPeriodType != BarsPeriodType.Minute
            || BarsPeriods[0].Value != 5)
        {
            Log("DriftVwapPullback requires a 5-minute primary series; got "
                + BarsPeriods[0].ToString(), LogLevel.Error);
            SetState(State.Finalized);
        }
    }
}
```

- [ ] **Step 2: Compile and verify the guard is present**

Run: `nt8c build` against a staged Custom folder containing the file.
Expected: compiles clean. If `nt8c` reports errors, check them against `[[nt8c-cross-file-namespace-trap]]` — this project has four known false positives on that CLI.

- [ ] **Step 3: Verify the guard fires**

Apply the strategy to a 1-minute NQ chart in NinjaTrader.
Expected: the log carries the "requires a 5-minute primary series" error and the strategy does not arm.

- [ ] **Step 4: Commit**

```bash
git add ninjascript/DriftVwapPullbackStrategy.cs
git commit -m "feat(nt8): strategy skeleton, closed parameter list, 5m timeframe guard

The eleven properties are the plugin's params one-to-one (spec 5.1). The
DataLoaded guard refuses any primary series that is not 5-minute: measured on
this workspace's engine, the same rules return -\$10,575 at 1m and +\$300 at 5m
on identical ticks, so a silently mis-applied timeframe is a different strategy.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 6: NinjaScript — VWAP and drift state, independent of series processing order

**Files:**
- Modify: `ninjascript/DriftVwapPullbackStrategy.cs`

**Interfaces:**
- Consumes: the skeleton from Task 5.
- Produces: `int DriftStateAt(DateTime asOf)` returning `+1`, `-1` or `0`, and `double SessionVwap15(int barsAgo)`.

- [ ] **Step 1: Implement the 15-minute accumulator on `BarsInProgress == 1`**

Maintain `cumPV` and `cumV` as fields, reset when `Times[1][0].Date` changes or the bar's time-of-day is the session's first. Store each closed 15m bar's `close`, `vwap` and its within-session index in `List<>` fields, keyed by close time.

```csharp
if (BarsInProgress == 1)
{
    if (Bars.IsFirstBarOfSession) { cumPV = 0; cumV = 0; barsInSession15 = 0; }
    double typ = (Highs[1][0] + Lows[1][0] + Closes[1][0]) / 3.0;
    cumPV += typ * Volumes[1][0];
    cumV  += Volumes[1][0];
    reg15.Add(new Reg {
        CloseTime = Times[1][0],                      // NT8 stamps at the CLOSE
        Close     = Closes[1][0],
        Vwap      = cumPV / Math.Max(cumV, 1.0),
        NInSession= barsInSession15++
    });
    return;                                            // no trading logic here
}
```

- [ ] **Step 2: Implement the ordering-independent lookup**

**Do not read `Closes[1][0]` from `BarsInProgress == 0`.** At 10:45 a 5-minute and a 15-minute bar close at the same instant, and whether the secondary series has already been processed decides whether `Closes[1][0]` is the 10:45 bar or the 10:30 one. That is a coin flip between two different strategies, and it is not documented in the pages this project has to hand.

Instead, select by timestamp, which is deterministic and is exactly R4:

```csharp
// The last 15m bar that CLOSED at or before `asOf`. R4's "at or before":
// the 15m bar closing at 10:45 counts for the 5m bar closing at 10:45,
// because both are complete at that instant.
private int LastCompletedIndex(DateTime asOf)
{
    for (int i = reg15.Count - 1; i >= 0; i--)
        if (reg15[i].CloseTime <= asOf) return i;
    return -1;
}
```

Then `DriftStateAt` applies R2 on that index, returning `0` when `NInSession < DriftLookbackBars` or when fewer than `DriftLookbackBars + 1` bars exist inside the session.

- [ ] **Step 3: Verify against the plugin on one session**

Print the drift state at every 5-minute close for one session to the output window; run the plugin over the same session and print its `state_of[done[i]]`. Compare by eye.
Expected: identical sequences, including that the first fifteen minutes after 10:30 are FLAT.

- [ ] **Step 4: Commit**

```bash
git add ninjascript/DriftVwapPullbackStrategy.cs
git commit -m "feat(nt8): 15m session VWAP and drift state, selected by timestamp

The state is looked up by close time rather than read from Closes[1][0]. At
10:45 a 5m and a 15m bar close together, and whether the secondary series has
been processed yet decides which 15m bar Closes[1][0] returns -- a coin flip
between two different strategies. Selecting the last bar closing at or before
the moment in question is R4 exactly, and is independent of processing order.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 7: NinjaScript — arming, trigger, brackets, flatten, drawing

**Files:**
- Modify: `ninjascript/DriftVwapPullbackStrategy.cs`

**Interfaces:**
- Consumes: `DriftStateAt` from Task 6.
- Produces: entries via `EnterLong`/`EnterShort` with `SetStopLoss`/`SetProfitTarget` brackets, and chart drawing of the VWAP, drift state and trigger bars.

- [ ] **Step 1: Implement the state machine on `BarsInProgress == 0`**

Mirror the plugin's loop exactly: track `prevState`, `armed`, `armedDir` and `lastIdx`; re-evaluate only when `LastCompletedIndex(Time[0])` changes; arm on a transition; fire on the first counter-direction candle inside the R5 window; disarm on firing.

- [ ] **Step 2: Submit the bracket before the entry**

```csharp
SetStopLoss(CalculationMode.Ticks, StopPoints * 4);
SetProfitTarget(CalculationMode.Ticks,
                (armedDir > 0 ? TargetPointsLong : TargetPointsShort) * 4);
if (armedDir > 0) EnterLong(Contracts, "DVP_L"); else EnterShort(Contracts, "DVP_S");
```

`SetStopLoss`/`SetProfitTarget` must be called **before** the entry method on the same pass, or the bracket attaches to the following trade. This is the OCO-less bracket bug already recorded against `[[tbstrategy-gate-project]]` — do not repeat it.

- [ ] **Step 3: Implement the 15:55 flatten**

At every `BarsInProgress == 0` close, if `ToTime(Time[0]) >= FlattenHHMM * 100` and the position is not flat, exit at market with a distinct signal name so the JSONL dump can label the reason `flatten`.

- [ ] **Step 4: Draw**

Plot the 15-minute VWAP as a line on the 5-minute chart; draw a green dot above / red dot below each 15-minute close carrying the drift state (matching the source's green and red crosses); mark each trigger bar with an arrow. Gate all drawing behind a `ShowDrawings` bool — **and exclude that bool from the parameter list**, following `LatigoBreak`, which documents chart furniture as one of the three NinjaScript properties with deliberately no plugin counterpart.

- [ ] **Step 5: Verify on Playback**

Run one session in Market Replay. Confirm: entries land at the open of the bar after a counter-direction candle; no entry before 10:30 or after 15:30; any open position is flat by 15:55.

- [ ] **Step 6: Commit**

```bash
git add ninjascript/DriftVwapPullbackStrategy.cs
git commit -m "feat(nt8): arming, trigger, brackets, 15:55 flatten and chart drawing

The state machine mirrors the plugin's loop line for line. SetStopLoss and
SetProfitTarget are called before the entry on the same pass; called after, the
bracket attaches to the NEXT trade, which is the bug already on record against
TBStrategy. ShowDrawings is deliberately not in the parameter list -- chart
furniture has nothing to simulate on the PropSim side.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 8: NinjaScript — JSONL trade dump

**Files:**
- Modify: `ninjascript/DriftVwapPullbackStrategy.cs`

**Interfaces:**
- Produces: one JSON object per line at `%USERPROFILE%\Documents\DriftVwap\nt8_trades.jsonl`, schema:
  `{"entry_ts": <long .NET ticks>, "exit_ts": <long>, "dir": 1|-1, "entry_px": <double>, "exit_px": <double>, "reason": "stop"|"target"|"flatten", "qty": <int>}`

- [ ] **Step 1: Write one line per closed trade in `OnExecutionUpdate`**

Emit only when the execution closes a position (`Position.MarketPosition == MarketPosition.Flat` after the update). Derive `reason` from the exit order's signal name.

- [ ] **Step 2: Emit .NET ticks as integers, not as formatted dates**

`entry_ts` and `exit_ts` are `DateTime.Ticks` — raw `long`. They exceed 2^53, so any consumer that parses them as a float loses precision. The comparison script uses Python's stdlib `json`, whose ints are arbitrary-precision, for exactly this reason.

- [ ] **Step 3: Verify the file**

Run one Playback session, then:
```bash
wc -l nt8_trades.jsonl && head -2 nt8_trades.jsonl
```
Expected: one line per trade shown in the NinjaTrader Trades tab, same count.

- [ ] **Step 4: Commit**

```bash
git add ninjascript/DriftVwapPullbackStrategy.cs
git commit -m "feat(nt8): JSONL trade dump for the mirror gate

Timestamps are raw DateTime.Ticks as integers. They exceed 2**53, so a consumer
that parses them as floats rounds them silently; the comparison script uses
Python's arbitrary-precision ints for that reason.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 9: `research/dump_trades.py` — the PropSim-side trade list

**Files:**
- Create: `research/dump_trades.py`

**Interfaces:**
- Consumes: the installed plugin, `PropSim/engine.py`.
- Produces: `propsim_trades.jsonl`, **the identical schema to Task 8**, with `entry_ts`/`exit_ts` converted to .NET ticks and shifted to NinjaTrader's close-stamping convention.

- [ ] **Step 1: Write the failing test**

```python
def test_timestamps_are_nt8_close_stamped():
    # PropSim stamps a bar at its first tick (OPEN); NinjaTrader stamps a
    # minute bar at its CLOSE. A trade whose entry tick is the first tick of
    # the 10:45 5-minute bar is, in NinjaTrader's vocabulary, an entry at
    # 10:50. Not normalising here shifts every trade by one bar and the mirror
    # reports a total mismatch that reads like a logic bug.
    rows = dump(contract="NQ 09-26", start="2026-06-01", end="2026-06-02")
    for r in rows:
        assert r["entry_ts"] % (10_000_000 * 300) == 0, (
            "entry timestamps must land on 5-minute boundaries in .NET ticks")
```

- [ ] **Step 2: Run to verify it fails**

Run: `python3 -m pytest research/test_dump_trades.py -v`
Expected: FAIL — `dump` not defined.

- [ ] **Step 3: Implement**

Run `engine.backtest(contract, "drift_vwap_pullback", tf_secs=300, ...)`, take the resolved trade list, and for each trade write the schema from Task 8. Convert PropSim's epoch-seconds/`.ncd` timestamps to .NET ticks (`(s + 62_135_596_800) * 10_000_000`) and add one bar period to entry timestamps that are bar-open stamps.

- [ ] **Step 4: Run to verify it passes**

Run: `python3 -m pytest research/test_dump_trades.py -v`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add research/dump_trades.py research/test_dump_trades.py
git commit -m "feat(research): PropSim-side trade dump in the NT8 JSONL schema

PropSim stamps a bar at its first tick, NinjaTrader at its close. Normalising
here rather than in either strategy keeps both implementations reading their
own platform's convention, and a test pins the 5-minute boundary.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 10: `research/compare_mirror.py` — the gate

**Files:**
- Create: `research/compare_mirror.py`

**Interfaces:**
- Consumes: `nt8_trades.jsonl` and `propsim_trades.jsonl` (identical schema).
- Produces: exit code 0 on PASS, 1 on FAIL, and a per-trade table of matches and discrepancies.

- [ ] **Step 1: Write the failing tests**

```python
def test_int_roundtrip():
    # .NET tick ints exceed 2**53. Python's json round-trips them exactly;
    # this is the proof, not a formality.
    big = 638_540_123_456_789_012
    assert json.loads(json.dumps(big)) == big
    assert isinstance(json.loads(json.dumps(big)), int)


def test_identical_lists_pass():
    rows = [{"entry_ts": 638_540_000_000_000_000, "exit_ts": 638_540_000_100_000_000,
             "dir": 1, "entry_px": 20000.0, "exit_px": 20040.0,
             "reason": "target", "qty": 1}]
    assert verdict(rows, list(rows))["pass"] is True


def test_one_bar_shift_fails_loudly():
    # The exact failure a missed open/close stamping convention produces.
    a = [{"entry_ts": 638_540_000_000_000_000, "exit_ts": 638_540_000_100_000_000,
          "dir": 1, "entry_px": 20000.0, "exit_px": 20040.0,
          "reason": "target", "qty": 1}]
    b = [dict(a[0], entry_ts=a[0]["entry_ts"] + 3_000_000_000)]   # +5 minutes
    v = verdict(a, b)
    assert v["pass"] is False
    assert v["unmatched_a"] == 1 and v["unmatched_b"] == 1


def test_price_mismatch_fails():
    a = [{"entry_ts": 638_540_000_000_000_000, "exit_ts": 638_540_000_100_000_000,
          "dir": 1, "entry_px": 20000.0, "exit_px": 20040.0,
          "reason": "target", "qty": 1}]
    b = [dict(a[0], exit_px=20040.5)]      # two ticks
    assert verdict(a, b)["pass"] is False
```

- [ ] **Step 2: Run to verify they fail**

Run: `python3 -m pytest research/test_compare_mirror.py -v`
Expected: FAIL — `verdict` not defined.

- [ ] **Step 3: Implement `verdict`**

Join on `entry_ts` exactly (both sides are normalised to 5-minute boundaries, so there is no tolerance to tune). For each matched pair require identical `dir`, `entry_px` and `exit_px` within one tick (0.25), and identical `reason`. Report counts of matched, mismatched, and unmatched-on-each-side. **PASS requires zero unmatched and zero mismatched**; anything less is reported as a table and returns FAIL.

Use stdlib `json` only.

- [ ] **Step 4: Run to verify they pass**

Run: `python3 -m pytest research/test_compare_mirror.py -v`
Expected: 4 passed.

- [ ] **Step 5: Commit**

```bash
git add research/compare_mirror.py research/test_compare_mirror.py
git commit -m "feat(research): the mirror-fidelity gate

PASS requires zero unmatched and zero mismatched trades. A test reproduces the
exact failure a missed open/close stamping convention produces -- every trade
shifted five minutes, both sides fully unmatched -- so that failure names
itself instead of reading like a logic bug.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 11: Run the gate, then the pre-registered measurement

**Files:**
- Create: `research/report.py`
- Create: `docs/validation.md`

**Interfaces:**
- Consumes: everything above.
- Produces: `docs/validation.md` — what was run, on what data, and what came out.

- [ ] **Step 1: Run the gate on a shared span**

Pick 10 consecutive RTH sessions available to both NinjaTrader Market Replay and the PropSim ALL tape. Run the strategy in Playback over them, run `dump_trades.py` over the same dates, then:

Run: `python3 research/compare_mirror.py nt8_trades.jsonl propsim_trades.jsonl`
Expected: PASS. **If it fails, stop.** Fix the divergence and re-run. Do not proceed to Step 3 — a number produced by two implementations that disagree is a number about neither of them.

- [ ] **Step 2: Record the gate result**

Write `docs/validation.md` with the span, the trade counts on both sides, and the verdict. Record it even if it took several attempts, and say what diverged.

- [ ] **Step 3: Run the measurement, uncapped, on the full ALL tape**

Run the plugin over 2025-08-01 → 2026-08-04 (275 sessions), `tf_secs=300`, tick-true fills, all caps off.

- [ ] **Step 4: Implement `research/report.py` and produce §9's four outputs**

1. **The losing-streak distribution** — histogram of consecutive-loser run lengths, plus the maximum. This is what sets the daily loss cap.
2. **The fidelity controls** — trades per session against the source's 3.2, win rate against 64 %, average win and loss in points against 43.3 and 65.0.
3. **The gate table** — average trade net, profit factor, expectancy in R, win rate vs breakeven, max drawdown, Sharpe, Sortino, equity R², positive months, worst losing streak, commissions as a share of gross, sample size. Plus the daily t-statistic.
4. **Trades by hour of day** — to test §10's prediction that they concentrate between 10:30 and 12:00.

Also report the capped variants derived as post-hoc filters (§5.3): first 4 trades per day, and stop after the 2nd loser of the day.

- [ ] **Step 5: Interpret the fidelity controls BEFORE the profit numbers**

If trades per session, win rate and the average win/loss ratio all land near the source's, the transcription is correct and the profit numbers mean what they say. **If they do not, the transcription is wrong** — re-read §6 of the spec, decide which ambiguity was misread, and treat that as a spec revision rather than adjusting code until the numbers improve.

- [ ] **Step 6: Commit**

```bash
git add research/report.py docs/validation.md
git commit -m "feat(research): pre-registered measurement run and validation record

Mirror gate result, the losing-streak distribution that sets the daily cap, the
fidelity controls against the source's reported behaviour, and the gate table.
The capped variants are post-hoc filters on the same run, not re-simulations.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Self-review

**Spec coverage.** R1 → Task 2. R2 → Task 2 (plugin) and Task 6 (NinjaScript). R3, R4 → Task 3 and Task 7. R5 → Task 1 (the flatten bound), Task 3 and Task 7. R6 → Task 3 and Task 7. R7 → no task: the PropSim engine already drops entries landing while a trade is open, and NinjaTrader's `EntriesPerDirection = 1` is native. §5.1 (closed list) → Tasks 3 and 5. §5.2 (timeframe guard) → Tasks 3 and 5. §5.3 (caps off, post-hoc filters) → Tasks 3, 5 and 11. §7 (architecture) → the file table. §8 (mirror gate) → Tasks 9, 10, 11. §9 (pre-registered measurements) → Task 11. §6's seven ambiguity resolutions are all realised in Tasks 2, 3, 6 and 7.

**One spec item gained a task the spec did not anticipate:** R5's 15:55 flatten cannot be expressed through `entries()`, and the engine only closed at the 16:00 session end. §5.3's claim that no engine change is needed is true of the *loss cap* and false of the *flatten*. Task 1 covers it and the spec should be amended to say so.

**Type consistency.** `_regime` returns `key`/`t`/`c`/`typ`/`vwap`/`n_in_session`/`state` and every consumer uses those names. `_last_completed_key` returns bucket ids from `_bucket15`, and `state_of` is keyed by the same ids. The JSONL schema is defined once in Task 8 and referenced by Tasks 9 and 10. `DriftStateAt` returns the same `+1/-1/0` as the plugin's `state`.

**Known gaps, stated rather than hidden.** Task 1's snippets guess at `resolve()`'s local names (`session_end`, `why`, `exit_ts`); Step 1 exists to read them first and adapt. Task 4's `engine.py` CLI flags are approximate and Step 3 says to check `--help`. Neither is a placeholder — the work is specified, only the exact identifiers are to be read off the file.
