import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from dump_trades import dump, tp   # noqa: E402


def test_timestamps_are_nt8_close_stamped():
    # PropSim stamps a bar at its first tick (OPEN); NinjaTrader stamps a
    # minute bar at its CLOSE. A trade whose entry tick is the first tick of
    # the 10:45 5-minute bar is, in NinjaTrader's vocabulary, an entry at
    # 10:50. Not normalising here shifts every trade by one bar and the mirror
    # reports a total mismatch that reads like a logic bug.
    #
    # Task brief's example window (2026-06-01..06-02) predates this machine's
    # cached tape, which starts 2026-06-11 (confirmed via
    # PropSim.tape.available_range("NQ 09-26")) -- engine.backtest() raises
    # SystemExit("no ticks in that range") on it. 06-11..06-16 is real,
    # cached, and non-vacuous (11 trades).
    rows = dump(contract="NQ 09-26", start="2026-06-11", end="2026-06-16")
    assert rows, "fixture window produced no trades -- this check would pass vacuously"
    for r in rows:
        assert r["entry_ts"] % (10_000_000 * 300) == 0, (
            "entry timestamps must land on 5-minute boundaries in .NET ticks")


def test_timestamps_decode_to_a_plausible_year():
    # The grid test above is blind to a wrong epoch base: a bad offset of the
    # shape (ts + seconds) * TPS is, by construction, still a multiple of the
    # 5-minute grid -- confirmed against a real entry_ts before writing this.
    # A wrong epoch instead throws the decoded date miles outside any real
    # trading year (or overflows Python's datetime past year 9999 outright),
    # which the grid check can't see and this one exists to catch.
    rows = dump(contract="NQ 09-26", start="2026-06-11", end="2026-06-16")
    assert rows, "fixture window produced no trades -- this check would pass vacuously"
    for r in rows:
        for key in ("entry_ts", "exit_ts"):
            try:
                year = tp.to_datetime(r[key]).year
            except (OverflowError, ValueError) as e:
                raise AssertionError(f"{key}={r[key]} does not decode to a date at all: {e}")
            assert 2020 <= year <= 2035, (
                f"{key}={r[key]} decodes to year {year}, outside a plausible trading window")
