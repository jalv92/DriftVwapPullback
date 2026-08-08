#!/usr/bin/env python3
"""Dump DriftVwapPullback trades from the PropSim tape into the NT8 JSONL
schema (WriteTrade, ninjascript/DriftVwapPullbackStrategy.cs:582-606), so a
later task can join the two implementations' trades and prove they agree.

    python3 research/dump_trades.py --contract "NQ 09-26" \\
        --start 2026-06-11 --end 2026-08-05 --out propsim_trades.jsonl

PropSim's tick timestamps (tape.py's `ts`) ARE ALREADY .NET ticks -- parsed
straight out of NinjaTrader's own .ncd files (ncd_parse.read_ticks) -- so no
epoch arithmetic runs here; tp.to_datetime(net_ticks) == datetime(1,1,1) +
timedelta(microseconds=net_ticks/10) confirms it, and a live sample decodes to
real 2026 ET session times. What DOES need fixing is the bar-stamping
convention: PropSim bars a signal's entry at the first REAL tick after a
5-minute boundary (jittered by however long until the next print -- observed
8ms to 13.2s on real data), while NinjaTrader's bar-based backtester has no
tick granularity and stamps every fill inside a bar with that bar's own
Time[0], which is its CLOSE. See _nt8_entry_ts.
"""
import argparse
import json
import sys
from pathlib import Path

# Sibling PropSim repo + this project's own propsim/ dir, same pattern as
# PullbackZone/research/dump_episodes.py.
_PROPSIM = Path(__file__).resolve().parents[2] / "PropSim"
sys.path.insert(0, str(_PROPSIM))
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "propsim"))

import engine           # noqa: E402  (path must be set up first)
import tape as tp        # noqa: E402
import drift_vwap_pullback as dvp    # noqa: E402

# engine.LIBRARY is a fixed dict of built-in strategies; installed user
# plugins are normally registered into it by plugins.register_all() at app
# startup. `python3 engine.py --strategy X` run as __main__ ends up with a
# SECOND copy of the module whose LIBRARY never saw that registration --
# KeyError for any installed plugin, a pre-existing PropSim bug, not ours to
# fix. Registering the class directly, as plugins.py's own smoke_test() does,
# sidesteps it: this script imports `engine` normally, never as __main__.
engine.LIBRARY[dvp.DriftVwapPullback.name] = dvp.DriftVwapPullback

TPS = tp.TPS   # .NET ticks per second == the C# side's DateTime.Ticks unit


def _nt8_entry_ts(raw_net_ticks: int, bar_ticks: int) -> int:
    """PropSim's entry tick -> NinjaTrader's close-stamped equivalent.

    Floor to the 5-minute boundary the tick actually belongs to (undoing the
    print-time jitter), then step one bar period forward: NT8 fills this
    trade on "next bar's open", but a bar-based backtest has no tick clock of
    its own and stamps that fill with the bar's Time[0] -- its CLOSE, one
    period past the open. Adding the period to the raw (unfloored) tick would
    still miss the boundary by the same jitter it started with -- checked
    against real trades before trusting this.
    """
    boundary = (raw_net_ticks // bar_ticks) * bar_ticks
    return boundary + bar_ticks


def dump(contract, start=None, end=None, tf_secs=300, params=None, slippage_ticks=None):
    """Run drift_vwap_pullback over PropSim's tape; return NT8-schema dicts.

    `slippage_ticks=None` (the default) keeps PropSim's own pessimistic fill
    model (engine.Costs' default, 2 ticks each way) -- this is what the
    measurement run must use, and it stays the default so nobody gets an
    accidentally-optimistic number from this function. Pass 0.0 ONLY for the
    mirror-fidelity comparison against NinjaTrader, which fills at the real
    price: that gate checks whether the two sides make the same DECISIONS,
    and slippage is a cost model, not a decision, so it has to come out to
    compare like for like. The caller states the value explicitly either way
    -- it is never picked for them.
    """
    costs = None if slippage_ticks is None else engine.Costs(slippage_ticks=slippage_ticks)
    trades, meta = engine.backtest(contract, dvp.DriftVwapPullback.name, tf_secs,
                                    start=start, end=end, params=params, costs=costs)
    bar_ticks = tf_secs * TPS
    qty = int(meta["params"]["contracts"])
    rows = []
    for t in trades:
        entry_raw = tp.to_net(t.entry_time)
        exit_raw = tp.to_net(t.exit_time)      # exit ticks are real fills mid-bar,
        rows.append({                          # not bar-open stamps -- left as-is.
            "entry_ts": _nt8_entry_ts(entry_raw, bar_ticks),
            "exit_ts": exit_raw,
            "dir": int(t.direction),
            "entry_px": float(t.entry_price),
            "exit_px": float(t.exit_price),
            "reason": t.reason,
            "qty": qty,
        })
    return rows


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                  formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--contract", default="NQ 09-26")
    ap.add_argument("--start")
    ap.add_argument("--end")
    ap.add_argument("--tf-secs", type=int, default=300)
    ap.add_argument("--slippage-ticks", type=float, default=None,
                     help="default: PropSim's own pessimistic 2.0 (engine.Costs). "
                          "Pass 0 for the mirror-fidelity comparison against NT8.")
    ap.add_argument("--out", required=True)
    a = ap.parse_args()
    rows = dump(a.contract, a.start, a.end, a.tf_secs, slippage_ticks=a.slippage_ticks)
    with open(a.out, "w") as f:
        for r in rows:
            f.write(json.dumps(r, separators=(",", ":")) + "\n")
    effective = a.slippage_ticks if a.slippage_ticks is not None else engine.Costs.slippage_ticks
    print(f"wrote {len(rows)} trades to {a.out} (slippage_ticks={effective})")


if __name__ == "__main__":
    main()
