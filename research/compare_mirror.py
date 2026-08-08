#!/usr/bin/env python3
"""The mirror-fidelity gate: join NT8's and PropSim's trade JSONL exports on
entry_ts and require them to agree exactly, or fail loudly.

    python3 research/compare_mirror.py nt8_trades.jsonl propsim_trades.jsonl

Both files are the schema WriteTrade emits (ninjascript/DriftVwapPullbackStrategy.cs:582-606),
one JSON object per line:
    {"entry_ts":<ticks>,"exit_ts":<ticks>,"dir":1|-1,"entry_px":<num>,
     "exit_px":<num>,"reason":"stop"|"target"|"flatten","qty":<int>}

Join key is entry_ts, exact -- both sides are normalised to 5-minute
boundaries upstream (dump_trades.py's _nt8_entry_ts), so there is nothing to
round or tune here. A matched pair still needs identical dir, identical
reason, and entry_px/exit_px agreeing within one tick (0.25) to count as
agreeing; anything else is a mismatch, not a near-miss. PASS requires zero
unmatched on either side and zero mismatches -- this is deliberate: a gate
that can be talked into agreeing on "close enough" is not a gate. See the
brief for why no tolerance knob exists here, ever.

Exit code 0 on PASS, 1 on FAIL.
"""
import json
import sys

ONE_TICK = 0.25


def _load(path):
    rows = []
    with open(path) as f:
        for line in f:
            line = line.strip()
            if line:
                rows.append(json.loads(line))
    return rows


def verdict(rows_a, rows_b):
    """Compare two lists of trade dicts, joined on entry_ts. Returns a dict
    with pass/fail, counts, and the matched/mismatched/unmatched rows for
    reporting."""
    by_ts_a = {}
    for r in rows_a:
        by_ts_a.setdefault(r["entry_ts"], []).append(r)
    by_ts_b = {}
    for r in rows_b:
        by_ts_b.setdefault(r["entry_ts"], []).append(r)

    matched = []
    mismatched = []
    for ts in sorted(set(by_ts_a) & set(by_ts_b)):
        pair_a = by_ts_a.pop(ts)
        pair_b = by_ts_b.pop(ts)
        # entry_ts is the join key and is normalised upstream to be unique
        # per side; more than one row per side at the same tick is a data
        # problem this gate should surface, not silently pair off.
        if len(pair_a) != 1 or len(pair_b) != 1:
            mismatched.append((pair_a, pair_b, "duplicate entry_ts on one or both sides"))
            continue
        a, b = pair_a[0], pair_b[0]
        diffs = []
        if a["dir"] != b["dir"]:
            diffs.append(f"dir {a['dir']} != {b['dir']}")
        if a["reason"] != b["reason"]:
            diffs.append(f"reason {a['reason']!r} != {b['reason']!r}")
        if abs(a["entry_px"] - b["entry_px"]) > ONE_TICK:
            diffs.append(f"entry_px {a['entry_px']} != {b['entry_px']}")
        if abs(a["exit_px"] - b["exit_px"]) > ONE_TICK:
            diffs.append(f"exit_px {a['exit_px']} != {b['exit_px']}")
        if diffs:
            mismatched.append((a, b, "; ".join(diffs)))
        else:
            matched.append((a, b))

    unmatched_a = list(by_ts_a.values())
    unmatched_b = list(by_ts_b.values())
    unmatched_a = [r for group in unmatched_a for r in group]
    unmatched_b = [r for group in unmatched_b for r in group]

    return {
        "pass": not mismatched and not unmatched_a and not unmatched_b,
        "matched": len(matched),
        "mismatched": mismatched,
        "unmatched_a": len(unmatched_a),
        "unmatched_b": len(unmatched_b),
        "unmatched_a_rows": unmatched_a,
        "unmatched_b_rows": unmatched_b,
    }


def _report(v, label_a, label_b):
    print(f"matched: {v['matched']}")
    print(f"mismatched: {len(v['mismatched'])}")
    print(f"unmatched ({label_a}): {v['unmatched_a']}")
    print(f"unmatched ({label_b}): {v['unmatched_b']}")

    if v["mismatched"]:
        print("\n-- mismatched pairs --")
        for a, b, why in v["mismatched"]:
            print(f"  entry_ts={a.get('entry_ts') if isinstance(a, dict) else '?'}: {why}")
            print(f"    {label_a}: {a}")
            print(f"    {label_b}: {b}")

    if v["unmatched_a_rows"]:
        print(f"\n-- unmatched on {label_a} --")
        for r in v["unmatched_a_rows"]:
            print(f"  {r}")

    if v["unmatched_b_rows"]:
        print(f"\n-- unmatched on {label_b} --")
        for r in v["unmatched_b_rows"]:
            print(f"  {r}")

    print(f"\nVERDICT: {'PASS' if v['pass'] else 'FAIL'}")


def main():
    if len(sys.argv) != 3:
        print(f"usage: {sys.argv[0]} nt8_trades.jsonl propsim_trades.jsonl", file=sys.stderr)
        return 2
    path_a, path_b = sys.argv[1], sys.argv[2]
    rows_a = _load(path_a)
    rows_b = _load(path_b)
    v = verdict(rows_a, rows_b)
    _report(v, path_a, path_b)
    return 0 if v["pass"] else 1


if __name__ == "__main__":
    sys.exit(main())
