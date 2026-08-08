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

try:
    tp
except NameError:                        # pragma: no cover - sandbox stand-in
    class _Tp:
        """Standalone stand-in for PropSim's injected tape helpers. Mirrors
        plugins.py's _TapeHelpers over int64 .NET ticks (tape.py)."""
        TPS = 10_000_000                 # .NET ticks per second
        _NET_EPOCH_S = 62135596800       # seconds from 0001-01-01 to 1970-01-01

        @staticmethod
        def day_index(ts):
            return (ts // _Tp.TPS - _Tp._NET_EPOCH_S) // 86400

        @staticmethod
        def sec_of_day(ts):
            return (ts // _Tp.TPS - _Tp._NET_EPOCH_S) % 86400

    tp = _Tp

TICK = 0.25
PTS = 4                                  # NQ ticks per index point
RTH_START_S = 9 * 3600 + 30 * 60         # 34200
RTH_END_S = 16 * 3600                    # 57600, exclusive -- RTH close
BUCKETS_PER_DAY_15 = 96                  # 86400 / 900

# The one place every param default lives. `_regime`'s own default arguments
# and the class's `params` dict both read from here instead of repeating the
# literals -- two sources of truth for one default is how they drift apart.
PARAMS_DEFAULT = dict(
    drift_lookback_bars=4, drift_min_pct=0.10,
    stop_points=80, target_points_long=40, target_points_short=50,
    trade_start_hhmm=1030, trade_stop_hhmm=1530, flatten_hhmm=1555,
    contracts=1, max_trades_per_day=0, max_losses_per_day=0,
)


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


def _regime(bars, lookback=PARAMS_DEFAULT["drift_lookback_bars"],
            min_pct=PARAMS_DEFAULT["drift_min_pct"]):
    """Aggregate 5m bars into 15m bars, then session VWAP and drift state.

    `lookback`/`min_pct` default to PARAMS_DEFAULT so callers that don't care
    (the regime selfchecks) get the same numbers a live run would use, and
    `entries()` passes the params actually in force -- one computation, not a
    default computed here and a second one redone on top of it.
    """
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
    r = dict(key=key5[starts], t=t15, c=c15, typ=typ, vwap=vwap, n_in_session=n_in)
    r["state"] = _drift_state(r, lookback, min_pct)
    return r


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


def _hhmm_s(hhmm):
    return (hhmm // 100) * 3600 + (hhmm % 100) * 60


def _empty():
    return (np.array([], np.int64), np.array([], np.int8),
            np.array([]), np.array([]))


def _fake_tape(bars):
    return dict(ts=bars["t"], px=bars["o"], vol=bars["v"],
                side=np.zeros(len(bars["t"]), np.int8))


def _defaults():
    return dict(PARAMS_DEFAULT)


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
    p = _defaults()

    # Prove the fixture actually drives the state to FLAT inside the window,
    # not just that entries() happens to return 2 -- a weak fixture must fail
    # loudly here rather than pass the count assertion for the wrong reason
    # (already caught once in this plan: a vacuous selfcheck).
    r = _regime(bars, int(p["drift_lookback_bars"]), float(p["drift_min_pct"]))
    sod15 = tp.sec_of_day(r["t"])
    in_window = (sod15 >= 11 * 3600) & (sod15 < 12 * 3600)
    assert (r["state"][in_window] == 0).any(), (
        "flat_window=(11:00,12:00) must drive at least one 15m bar's state "
        "to FLAT, or the re-arm check below is vacuous")

    s = DriftVwapPullback()
    idx, _, _, _ = s.entries(bars, _fake_tape(bars), p)
    assert len(idx) == 2, (
        f"a drift that breaks and returns must arm twice, got {len(idx)}")


def _selfcheck_stop_and_target_sides():
    bars = _fake_bars(sessions=1, drift_pts_per_bar=4.0)
    s = DriftVwapPullback()
    idx, direc, stop, target = s.entries(bars, _fake_tape(bars), _defaults())
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


def _selfcheck_time_window():
    bars = _fake_bars(sessions=1, drift_pts_per_bar=4.0)
    s = DriftVwapPullback()
    idx, direc, _, _ = s.entries(bars, _fake_tape(bars), _defaults())
    for i in idx:
        sod = tp.sec_of_day(bars["t"][i])
        assert 10 * 3600 + 30 * 60 <= sod <= 15 * 3600 + 30 * 60, (
            f"entry at {sod}s is outside the R5 window")


class DriftVwapPullback(Strategy):
    name, label = "drift_vwap_pullback", "Drift VWAP pullback (Conti)"
    uses_ticks = True
    full_session = False

    params = {
        "drift_lookback_bars": Param(PARAMS_DEFAULT["drift_lookback_bars"], 1, 24,
                                      "15m bars in the rate-of-change window"),
        "drift_min_pct": Param(PARAMS_DEFAULT["drift_min_pct"], 0.0, 2.0,
                                "rate-of-change threshold, percent"),
        "stop_points": Param(PARAMS_DEFAULT["stop_points"], 1, 400,
                              "stop distance, index points"),
        "target_points_long": Param(PARAMS_DEFAULT["target_points_long"], 1, 400,
                                     "long target, index points"),
        "target_points_short": Param(PARAMS_DEFAULT["target_points_short"], 1, 400,
                                      "short target, index points"),
        "trade_start_hhmm": Param(PARAMS_DEFAULT["trade_start_hhmm"], 0, 2359,
                                   "no entries before, ET HHMM", fixed=True),
        "trade_stop_hhmm": Param(PARAMS_DEFAULT["trade_stop_hhmm"], 0, 2359,
                                  "no entries after, ET HHMM", fixed=True),
        "flatten_hhmm": Param(PARAMS_DEFAULT["flatten_hhmm"], 0, 2359,
                               "flatten all, ET HHMM", fixed=True),
        "contracts": Param(PARAMS_DEFAULT["contracts"], 1, 100,
                            "position size, contracts", fixed=True),
        "max_trades_per_day": Param(PARAMS_DEFAULT["max_trades_per_day"], 0, 20,
                                     "0 = off, see spec 5.3", fixed=True),
        "max_losses_per_day": Param(PARAMS_DEFAULT["max_losses_per_day"], 0, 20,
                                     "0 = off, see spec 5.3", fixed=True),
    }

    def risk_ticks(self, p):
        return float(p["stop_points"]) * PTS

    def entries(self, bars, tape, p):
        t = bars["t"]
        if len(t) < 6:
            return _empty()

        # The session VWAP resets on the calendar-day boundary (_regime, via
        # tp.day_index), which equals the SESSION boundary only because the
        # tape is RTH-filtered -- a property nothing else here enforces.
        # full_session=False makes the engine serve an RTH tape by convention;
        # this makes it a checked precondition instead, so a bar that walked
        # outside [09:30, 16:00) fails loudly here rather than producing a
        # VWAP that silently accumulates across a day boundary.
        sod = tp.sec_of_day(t)
        outside = (sod < RTH_START_S) | (sod >= RTH_END_S)
        if outside.any():
            bad = int(sod[outside][0])
            raise ValueError(
                "drift_vwap_pullback requires an RTH-only tape "
                f"[09:30:00, 16:00:00); {int(outside.sum())} bar(s) outside, "
                f"e.g. sec_of_day={bad}")

        # 5.2: the timeframe is a property of the RUN. A bar-counting strategy
        # on the wrong timeframe is a different strategy (measured on this
        # engine: MA-cross is -$10,575 at 1m and +$300 at 5m on the same ticks).
        gaps = np.diff(sod)
        within = gaps[gaps > 0]
        if len(within) and int(np.median(within)) != 300:
            raise ValueError("drift_vwap_pullback requires 5-minute bars "
                             f"(tf_secs=300); got ~{int(np.median(within))}s")

        r = _regime(bars, int(p["drift_lookback_bars"]), float(p["drift_min_pct"]))
        state_of = dict(zip(r["key"].tolist(), r["state"].tolist()))

        done = _last_completed_key(bars)
        sod_close = sod + 300
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


if __name__ == "__main__":
    _selfcheck_regime()
    _selfcheck_vwap_resets_per_session()
    _selfcheck_lookback_never_crosses_sessions()
    _selfcheck_state_applies_at_coincident_close()
    _selfcheck_arming_requires_a_transition()
    _selfcheck_arming_rearms_after_a_break()
    _selfcheck_stop_and_target_sides()
    _selfcheck_time_window()
    print("selfcheck OK: regime, VWAP session reset, session-confined lookback, "
          "coincident close, arming transition, arming re-arm, stop/target sides, "
          "time window")
