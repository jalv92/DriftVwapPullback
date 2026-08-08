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
BUCKETS_PER_DAY_15 = 96                  # 86400 / 900
DRIFT_LOOKBACK_BARS = 4                  # R2 default: 15m bars in the 1h lookback
DRIFT_MIN_PCT = 0.10                     # R2 default: minimum 1h move, percent


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
    r = dict(key=key5[starts], t=t15, c=c15, typ=typ, vwap=vwap, n_in_session=n_in)
    r["state"] = _drift_state(r, DRIFT_LOOKBACK_BARS, DRIFT_MIN_PCT)
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


if __name__ == "__main__":
    _selfcheck_regime()
    _selfcheck_vwap_resets_per_session()
    _selfcheck_lookback_never_crosses_sessions()
    print("selfcheck OK: regime, VWAP session reset, session-confined lookback")
