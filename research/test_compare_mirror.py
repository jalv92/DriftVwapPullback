import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from compare_mirror import verdict   # noqa: E402


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
