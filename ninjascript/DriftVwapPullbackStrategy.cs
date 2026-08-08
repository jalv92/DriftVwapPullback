// DriftVwapPullbackStrategy -- NinjaScript mirror of propsim/drift_vwap_pullback.py.
// NQ, RTH only (09:30-16:00 ET). Execution series 5-minute, regime series 15-minute.
//
// THE MIRROR IS THE CONTRACT. docs/specs/2026-08-07-driftvwap-design.md is the
// closed spec; propsim/drift_vwap_pullback.py is its reviewed, mutation-tested
// implementation. Every rule below is transcribed from that file and where the
// two disagree THAT FILE WINS.
//
// LAB STATUS -- FULL v1 (plan tasks 5-8). Parameter list, 5-minute timeframe
// guard, 15-minute session VWAP + drift state (R1/R2), the R3/R4 arm+trigger
// state machine, R5's 15:55 flatten, bracket orders, chart drawing and the
// JSONL trade dump for the mirror gate are all here. UpdateArmState() carries
// a line-by-line correspondence comment against the plugin's entries() loop
// -- read it before touching the state machine.
//
// R4 -- THE COINCIDENT-CLOSE TRAP, AND WHY THIS FILE DOES NOT DEPEND ON
// SERIES PROCESSING ORDER AT ALL. At 10:45, 11:00, ... a 5-minute bar and a
// 15-minute bar close at the same instant. Two designs were tried here:
//
//   1. (REJECTED) Read Closes[1][0] from the primary branch. A coin flip on
//      whether NinjaTrader has dispatched the secondary series' own
//      OnBarUpdate for that timestamp yet.
//   2. (REJECTED) Append each closed 15m bar to `_reg15` from the
//      BarsInProgress == 1 branch itself, then select by timestamp. This
//      still silently depends on dispatch order: PullbackZoneStrategy.cs's
//      header (lines ~63-71) documents that NT8 processes the PRIMARY series
//      first on a shared timestamp, so at the coincident bar `_reg15` would
//      not yet hold the 15m entry the primary branch needs -- a one-bar lag
//      at every 15-minute boundary, silent, and the SAME family of bug this
//      file exists to avoid.
//
// What this file actually does: `_reg15` is populated ONLY from the PRIMARY
// (BarsInProgress == 0) branch, via FoldClosedRegimeBars(), which reads
// BarsArray[Regime15Idx] by ABSOLUTE INDEX -- not through the secondary
// series' own OnBarUpdate dispatch or its `Closes[1][0]`-style processing
// pointer at all. This removes the dependency instead of guessing which way
// it resolves. Two things make this safe:
//
//   - NO LOOKAHEAD: b15.Count spans the WHOLE loaded series, future bars
//     included (NT8 preloads historical data for every added series ahead of
//     the primary's own dispatch position). FoldClosedRegimeBars' `if
//     (b15.GetTime(j) > Time[0]) break;` is the ONLY thing standing between
//     that and reading a bar that has not closed yet, and it must never be
//     weakened. Same guard, same reasoning, as
//     PullbackZoneStrategy.FoldClosedZoneBars.
//   - NO LAG: because the fold runs unconditionally on every primary-branch
//     call (not "if the secondary already fired"), the coincident 15m bar at
//     10:45 is folded in during the SAME OnBarUpdate call that evaluates the
//     10:45 5-minute bar, regardless of whether NinjaTrader has separately
//     dispatched BarsInProgress == 1 for it yet or ever will before this
//     read. This file never reads that series' own event at all.
//
// DriftStateAt/LastCompletedIndex still select by comparing timestamps ("the
// last 15m bar closing at or before asOf") -- that part of the design was
// correct before and is unchanged; only how `_reg15` gets populated changed.
//
// PERCENT VS FRACTION. DriftMinPct is a PERCENT (default 0.10 means "0.10%"),
// while the rate of change computed below is a FRACTION (close/prevClose - 1,
// e.g. 0.001 for +0.1%). DriftStateAt divides DriftMinPct by 100 before
// comparing -- see propsim/drift_vwap_pullback.py's `thr = min_pct / 100.0`.
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class DriftVwapPullbackStrategy : Strategy
    {
        private const int Regime15Idx = 1;
        private const int PTS = 4;              // NQ ticks per index point -- plugin's own PTS
        private const int DriftDotOffsetTicks = 40;   // cosmetic only, not a shared param

        // Fix round 1: propsim/drift_vwap_pullback.py's entries() hard-fails
        // (raises ValueError) on any bar outside RTH [09:30:00, 16:00:00) --
        // see CheckRth for why this file's bound is written the other way
        // (open-exclusive, close-inclusive) despite checking the same clock.
        private const int RthOpenHHMMSS = 93000;    // 09:30:00
        private const int RthCloseHHMMSS = 160000;  // 16:00:00

        private const string SigLong = "DVP_L";
        private const string SigShort = "DVP_S";
        private const string SigFlatten = "DVP_Flatten";

        // One closed 15m bar: its own close timestamp, close price, session
        // VWAP as of that close, and its 0-based position within the current
        // RTH session. Appended in order, never removed -- LastCompletedIndex
        // and DriftStateAt both rely on positional lookups (`i - N`) being
        // safe exactly when NInSession says so (see DriftStateAt).
        private sealed class Reg15
        {
            public DateTime CloseTime;
            public double Close;
            public double Vwap;
            public int NInSession;
        }

        private readonly List<Reg15> _reg15 = new List<Reg15>();
        private double _cumPv, _cumV;
        private int _barsInSession15;
        private int _regDone = -1;      // last BarsArray[Regime15Idx] index folded into _reg15

        // R3/R4 arm/trigger state -- mirrors entries()'s prev_state/armed/
        // armed_dir/last_key exactly; see UpdateArmState(). Nullable so the
        // very first bar (idx possibly -1, "no 15m bar closed yet") still
        // compares unequal and forces one evaluation, the same way Python's
        // `last_key = None` does.
        private int? _lastRegimeIdx;
        private int _prevState;
        private bool _armed;
        private int _armedDir;

        // One open trade's entry side, captured at the entry fill and
        // consumed at the exit fill that leaves the strategy flat. See
        // OnExecutionUpdate for why this doesn't yet handle a same-day
        // reversal cleanly (task-78 report).
        private struct OpenTrade
        {
            public long EntryTicks;
            public double EntryPx;
            public int Dir;
            public int Qty;
        }
        private OpenTrade? _openTrade;

        private static readonly object _jsonlLock = new object();
        private string _jsonlPath;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "DriftVwapPullbackStrategy";
                Description = "Mirror of propsim/drift_vwap_pullback.py -- 15m VWAP drift + 5m pullback trigger (Conti). See docs/specs/2026-08-07-driftvwap-design.md.";
                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = false;   // R5 flattens at 15:55 ourselves (task 7)
                BarsRequiredToTrade = 20;

                // PARAMS_DEFAULT, one property per plugin param -- the closed
                // list (spec 5.1). Bounds below are transcribed from the
                // plugin's own Param(default, lo, hi, ...) declarations, the
                // same single source of truth on both sides.
                DriftLookbackBars = 4;
                DriftMinPct = 0.10;
                StopPoints = 80;
                TargetPointsLong = 40;
                TargetPointsShort = 50;
                TradeStartHHMM = 1030;
                TradeStopHHMM = 1530;
                FlattenHHMM = 1555;
                Contracts = 1;
                MaxTradesPerDay = 0;
                MaxLossesPerDay = 0;

                ShowDrawings = true;   // C#-only, not a shared param -- see its Display attribute
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Minute, 15);   // BarsInProgress == 1
            }
            else if (State == State.DataLoaded)
            {
                // A reused instance (e.g. IsInstantiatedOnEachOptimizationIteration
                // == false on some future config) would otherwise carry the
                // PREVIOUS run's accumulated 15m history into this one -- field
                // initializers run once at construction, not per State chain.
                _reg15.Clear();
                _cumPv = 0; _cumV = 0; _barsInSession15 = 0;
                _regDone = -1;
                _lastRegimeIdx = null; _prevState = 0; _armed = false; _armedDir = 0;
                _openTrade = null;

                // Spec 5.2: the timeframe is a property of the RUN, not of the code.
                if (BarsPeriods[0].BarsPeriodType != BarsPeriodType.Minute
                    || BarsPeriods[0].Value != 5)
                {
                    Log(Name + ": requires a 5-minute primary series; got "
                        + BarsPeriods[0].ToString(), Cbi.LogLevel.Error);
                    // Fix round 2: State.Terminated, not State.Finalized -- see
                    // CheckRth's comment for the evidence. setstate.md is explicit
                    // ("Setting State to State.Terminated is meant as a way to
                    // abort the strategy as it is running") and its own example is
                    // this exact SetState-then-return shape.
                    SetState(State.Terminated);
                    return;
                }
            }
        }

        protected override void OnBarUpdate()
        {
            // BarsInProgress == Regime15Idx does NOTHING -- deliberately. The
            // 15m regime is folded from THIS (primary) branch instead; see
            // the file header and FoldClosedRegimeBars.
            if (BarsInProgress != 0 || CurrentBar < 0)
                return;

            if (!CheckRth(Time[0], "primary (5m)"))
                return;

            int before = _reg15.Count;
            // Fix round 2 (CRITICAL): FoldClosedRegimeBars() used to be void,
            // so its own internal `return` on a failed regime-side CheckRth
            // only exited that method -- this caller fell straight through
            // into the draw loop, the flatten check, UpdateArmState and
            // potentially a live EnterLong/EnterShort, on the very bar the
            // strategy just declared fatal. The accumulator itself stayed
            // clean (FoldRegimeBar never ran for the bad bar), but an order
            // could still be submitted on top of a declared-unusable bar.
            if (!FoldClosedRegimeBars())
                return;
            for (int i = before; i < _reg15.Count; i++)
                DrawRegimeBar(i);

            // R5 -- flatten takes priority over everything below it. A
            // position must never survive past FlattenHHMM regardless of
            // what the arm/trigger state machine would otherwise do on this
            // same bar, so this returns rather than falling through to it.
            // The plugin has no flatten concept to mirror here (spec 5.3 /
            // task-7 brief step 3) -- this half is NT8-only.
            if (Position.MarketPosition != MarketPosition.Flat && ToTime(Time[0]) >= FlattenHHMM * 100)
            {
                if (Position.MarketPosition == MarketPosition.Long)
                    ExitLong(SigFlatten, SigLong);
                else
                    ExitShort(SigFlatten, SigShort);
                return;
            }

            UpdateArmState();

            if (!_armed)
                return;

            // R4 -- the trade window, on the bar's own CLOSE (Time[0] IS the
            // close under NT8's bar stamping -- see the file header), exactly
            // like the plugin's sod_close[i] = sod[i] + 300.
            int sodClose = ToTime(Time[0]);
            if (sodClose < TradeStartHHMM * 100 || sodClose > TradeStopHHMM * 100)
                return;

            bool fired = (_armedDir > 0 && Close[0] < Open[0]) || (_armedDir < 0 && Close[0] > Open[0]);
            if (!fired)
                return;

            // Bracket BEFORE the entry, same pass -- called after, the
            // bracket attaches to the NEXT trade and this one runs naked
            // (the TBStrategy bug already on record in this workspace).
            SetStopLoss(CalculationMode.Ticks, StopPoints * PTS);
            SetProfitTarget(CalculationMode.Ticks,
                (_armedDir > 0 ? TargetPointsLong : TargetPointsShort) * PTS);
            if (_armedDir > 0)
                EnterLong(Contracts, SigLong);
            else
                EnterShort(Contracts, SigShort);

            if (ShowDrawings && ChartControl != null)
            {
                if (_armedDir > 0)
                    Draw.ArrowUp(this, "DVP_Trig_" + CurrentBar, false, 0, Low[0] - 4 * TickSize, Brushes.Lime);
                else
                    Draw.ArrowDown(this, "DVP_Trig_" + CurrentBar, false, 0, High[0] + 4 * TickSize, Brushes.Red);
            }

            _armed = false;   // the arm is consumed by this firing -- see UpdateArmState
        }

        // R3/R4's state machine -- a line-by-line port of the plugin's
        // entries() loop body:
        //
        //   k = int(done[i])                        -> idx = LastCompletedIndex(Time[0])
        //   if k != last_key:                        -> if (idx != _lastRegimeIdx)
        //       st = int(state_of.get(k, 0))          ->     st = DriftStateAt(Time[0])
        //       if st != prev_state:                  ->     if (st != _prevState)
        //           armed = st != 0                    ->         _armed = st != 0
        //           armed_dir = st                      ->         _armedDir = st
        //       prev_state = st                        ->     _prevState = st
        //       last_key = k                            ->     _lastRegimeIdx = idx
        //
        // `last_key = None` in Python always compares unequal on the first
        // bar, forcing one evaluation even while idx == -1 ("no 15m bar
        // closed yet"); `_lastRegimeIdx` being nullable reproduces exactly
        // that, rather than colliding with -1 as a legitimate sentinel.
        //
        // What the plugin's loop ALSO has that this method deliberately does
        // NOT reproduce: `if day[i + 1] != day[i]: continue` (never carry a
        // signal into a fill on the next calendar day). That guard exists
        // because Python resolves the entry's fill by indexing the literal
        // NEXT bar in an array, which can be tomorrow's bar if today's array
        // has run out; NT8 instead submits a live market order that fills on
        // whatever the next tick turns out to be, so there is no equivalent
        // "index into tomorrow" failure mode to guard against, and building
        // one would need to look ahead into a bar that has not formed yet --
        // the same lookahead this file's header goes out of its way to
        // avoid elsewhere. Under the default TradeStopHHMM (1530), the next
        // 5m bar is always well inside the same RTH session regardless, so
        // this is believed to be a no-op difference; flagged here rather
        // than silently assumed away, per the task-78 report.
        private void UpdateArmState()
        {
            int idx = LastCompletedIndex(Time[0]);
            if (_lastRegimeIdx == null || idx != _lastRegimeIdx.Value)
            {
                int st = DriftStateAt(Time[0]);
                if (st != _prevState)
                {
                    _armed = st != 0;
                    _armedDir = st;
                }
                _prevState = st;
                _lastRegimeIdx = idx;
            }
        }

        // Folds every 15m bar that has closed at or before the current 5m
        // bar's close, exactly once, in order -- by ABSOLUTE INDEX into
        // BarsArray[Regime15Idx], never via that series' own OnBarUpdate
        // event. See the file header for why this removes the series-
        // processing-order dependency entirely instead of merely guessing it.
        //
        // b15.Count spans the WHOLE loaded series, future bars included --
        // the `> Time[0]` break is the ONLY thing standing between this loop
        // and lookahead. IT MUST NEVER BE WEAKENED. Same guard, same
        // reasoning, as PullbackZoneStrategy.FoldClosedZoneBars.
        //
        // Returns false when a regime bar fails CheckRth (the strategy has
        // already requested termination) so the caller can abort THIS bar's
        // OnBarUpdate instead of falling through to the draw loop, the
        // flatten check and the trigger with a live order -- see the caller.
        private bool FoldClosedRegimeBars()
        {
            Bars b15 = BarsArray[Regime15Idx];
            for (int j = _regDone + 1; j < b15.Count; j++)
            {
                if (b15.GetTime(j) > Time[0])
                    break;                                 // not closed yet
                if (!CheckRth(b15.GetTime(j), "regime (15m)"))
                    return false;                          // never fold an out-of-RTH bar
                FoldRegimeBar(b15, j);
                _regDone = j;
            }
            return true;
        }

        // Fix round 1 -- the NT8 counterpart of entries()'s RTH hard-fail
        // (file header, propsim/drift_vwap_pullback.py: "raise ValueError ...
        // requires an RTH-only tape"). The plugin's check is PER-BAR, over
        // the bars["t"] array actually being processed, not a session-
        // template inspection done once up front -- a declared template is a
        // proxy for what the data contains, not a guarantee of it (a partial
        // holiday, a mislabeled template). This mirrors that exactly: every
        // bar this file ever reads from either series is checked at the
        // moment it is about to be used, and the strategy refuses to run
        // (Log at Error + SetState(State.Terminated), same pattern as the
        // 5-minute timeframe guard in DataLoaded) rather than degrade into a
        // silently wrong VWAP -- never a warning that lets the bar through.
        //
        // Fix round 2: State.Terminated, not State.Finalized. Reflecting the
        // real NinjaTrader.Core.dll this machine compiles against confirms
        // Finalized(9) is a genuine enum member one past Terminated(8), but
        // it appears nowhere in NinjaTrader's public developer docs (state.md,
        // onstatechange.md, understanding_the_lifecycle_of.md all describe
        // only Terminated as the shutdown/cleanup state). setstate.md is
        // explicit and unambiguous: "Setting State to State.Terminated is
        // meant as a way to abort the strategy as it is running," with a
        // worked example matching this exact shape (SetState then return in
        // OnBarUpdate). Finalized was carried over from the plan's own Task 5
        // snippet into both this guard and the pre-existing timeframe guard;
        // both now use Terminated.
        //
        // Bound is (RthOpenHHMMSS, RthCloseHHMMSS] -- open-EXCLUSIVE,
        // close-INCLUSIVE -- which is the mirror of, not identical to,
        // Python's [RTH_START_S, RTH_END_S) over bar OPENS. NT8 stamps a bar
        // at its CLOSE (file header, R4 section), so the last legitimate RTH
        // bar of every session -- 15:55-16:00 on the primary, 15:45-16:00 on
        // the regime series -- closes at EXACTLY 16:00:00. A close-exclusive
        // bound copied verbatim from Python's open-exclusive-at-the-other-end
        // check would reject that bar as "outside RTH" and false-fail on
        // ordinary, correct data. The two checks agree on every instant a
        // bar could actually occupy; only the boundary arithmetic differs to
        // account for which edge of the bar each side's timestamp names.
        private bool CheckRth(DateTime t, string series)
        {
            int hhmmss = ToTime(t);
            if (hhmmss > RthOpenHHMMSS && hhmmss <= RthCloseHHMMSS)
                return true;

            Log(Name + ": " + series + " bar closed at "
                + t.ToString("yyyy-MM-dd HH:mm:ss")
                + ", outside RTH (09:30:00, 16:00:00] -- refusing to run. "
                + "FIX: set the chart's Trading Hours template to 'US Equities RTH' "
                + "(09:30-16:00 Eastern). NOT 'CME US Index Futures RTH' -- that one "
                + "is declared in CENTRAL time and runs 08:30-16:00 CT = 09:30-17:00 ET, "
                + "so it emits bars past 16:00 ET and trips this same guard. "
                + "propsim/drift_vwap_pullback.py hard-fails on out-of-RTH bars too "
                + "(entries() raises ValueError), because the session VWAP anchors to "
                + "the RTH open and a template that spans the overnight makes it wrong.",
                Cbi.LogLevel.Error);
            SetState(State.Terminated);
            return false;
        }

        // R1 -- session VWAP over 15-minute bars, bar-typical formulation,
        // reset at the first 15m bar of the RTH session. IsFirstBarOfSessionByIndex
        // reads the session boundary from the index itself -- the same
        // absolute-index discipline as the fold loop that calls this.
        private void FoldRegimeBar(Bars b15, int j)
        {
            if (b15.IsFirstBarOfSessionByIndex(j))
            {
                _cumPv = 0; _cumV = 0; _barsInSession15 = 0;
            }

            double typ = (b15.GetHigh(j) + b15.GetLow(j) + b15.GetClose(j)) / 3.0;
            double vol = b15.GetVolume(j);
            _cumPv += typ * vol;
            _cumV += vol;

            _reg15.Add(new Reg15
            {
                CloseTime = b15.GetTime(j),               // NT8 stamps a bar at its CLOSE
                Close = b15.GetClose(j),
                Vwap = _cumPv / Math.Max(_cumV, 1.0),
                NInSession = _barsInSession15++
            });
        }

        // R4's ordering rule: the last 15m bar that CLOSED at or before `asOf`.
        // Selecting by timestamp (rather than by "the last _reg15 entry") is
        // what makes DriftStateAt itself order-agnostic; FoldClosedRegimeBars
        // above is what makes the POPULATION of _reg15 order-agnostic too --
        // see the file header, both matter.
        private int LastCompletedIndex(DateTime asOf)
        {
            for (int i = _reg15.Count - 1; i >= 0; i--)
                if (_reg15[i].CloseTime <= asOf)
                    return i;
            return -1;
        }

        // The session VWAP `barsAgo` completed 15m bars back from the most
        // recently closed one (0 = the last closed bar). For inspection/
        // drawing (task 7); not used by DriftStateAt, which addresses _reg15
        // by its own resolved index instead of by "the last one".
        public double SessionVwap15(int barsAgo)
        {
            int idx = _reg15.Count - 1 - barsAgo;
            return idx >= 0 ? _reg15[idx].Vwap : 0.0;
        }

        // R2, evaluated at the 15m bar that closed at or before `asOf`.
        // +1 LONG, -1 SHORT, 0 FLAT. Comparisons are strict; a tie is FLAT.
        //
        // FLAT until `DriftLookbackBars` bars have closed WITHIN the current
        // session (NInSession >= DriftLookbackBars). This also makes every
        // positional lookback below safe without any day/session arithmetic:
        // NInSession only resets to 0 at a session's first bar and increments
        // by exactly 1 per bar after that, so if bar i's NInSession is k, bars
        // i-k..i are all in the same session -- and DriftLookbackBars' Range
        // floor of 1 guarantees k >= 1 here, so i-1 is always in range and in
        // session too.
        public int DriftStateAt(DateTime asOf)
        {
            int i = LastCompletedIndex(asOf);
            if (i < 0 || _reg15[i].NInSession < DriftLookbackBars)
                return 0;

            Reg15 cur = _reg15[i];
            Reg15 prevVwapBar = _reg15[i - 1];
            Reg15 prevRocBar = _reg15[i - DriftLookbackBars];

            double roc = cur.Close / prevRocBar.Close - 1.0;
            double thr = DriftMinPct / 100.0;             // percent param, fraction roc -- see header

            if (cur.Close > cur.Vwap && cur.Vwap > prevVwapBar.Vwap && roc >= thr)
                return 1;
            if (cur.Close < cur.Vwap && cur.Vwap < prevVwapBar.Vwap && roc <= -thr)
                return -1;
            return 0;
        }

        // Task 7 step 4 -- chart furniture only, nothing here feeds the
        // state machine. Called once per newly-folded 15m bar (see the
        // `before`/`_reg15.Count` loop in OnBarUpdate), never per 5m bar, so
        // each VWAP segment and drift dot is drawn exactly once under a
        // unique index-based tag and never revisited.
        private void DrawRegimeBar(int i)
        {
            if (!ShowDrawings || ChartControl == null)
                return;

            Reg15 cur = _reg15[i];
            if (i > 0)
            {
                Reg15 prev = _reg15[i - 1];
                Draw.Line(this, "DVP_Vwap_" + i, false, prev.CloseTime, prev.Vwap,
                    cur.CloseTime, cur.Vwap, Brushes.DodgerBlue, DashStyleHelper.Solid, 2);
            }

            // DriftStateAt(cur.CloseTime) resolves back to index i itself --
            // cur IS the last 15m bar closed at or before its own close --
            // so this reuses the shared R2 formula instead of recomputing it.
            int st = DriftStateAt(cur.CloseTime);
            double off = DriftDotOffsetTicks * TickSize;
            if (st > 0)
                Draw.Dot(this, "DVP_Drift_" + i, false, cur.CloseTime, cur.Close + off, Brushes.Lime);
            else if (st < 0)
                Draw.Dot(this, "DVP_Drift_" + i, false, cur.CloseTime, cur.Close - off, Brushes.Red);
        }

        // Task 8 -- one JSONL trade row per closed position, for the mirror
        // gate. Two passes: the entry fill seeds _openTrade; the exit fill
        // that leaves the strategy flat consumes it and writes the row.
        //
        // KNOWN GAP, not silently assumed away: if a same-day opposite-
        // direction re-arm fires (UpdateArmState can arm a fresh direction
        // the instant the 15m state flips straight from +1 to -1 with no
        // intervening FLAT bar) WHILE the previous trade's bracket has not
        // yet resolved, NT8's managed order handling nets the reversal and
        // the exact Order.Name NT8 assigns to the implicit close of the old
        // leg is not something this file can verify without running
        // NinjaTrader. `_openTrade == null` gates the entry branch so a
        // reversal cannot clobber the still-open trade's fields, and the
        // "manual" fallback below catches whatever the closing leg's order
        // is named instead of mis-labeling it stop/target/flatten. See the
        // task-78 report's hand-off checklist for how to check this on a
        // real Playback run.
        protected override void OnExecutionUpdate(Execution execution, string executionId,
            double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution == null || execution.Order == null)
                return;
            string n = execution.Order.Name;

            if ((n == SigLong || n == SigShort) && _openTrade == null)
            {
                _openTrade = new OpenTrade
                {
                    EntryTicks = time.Ticks,
                    EntryPx = price,
                    Dir = n == SigLong ? 1 : -1,
                    Qty = execution.Order.Quantity
                };
                return;
            }

            if (_openTrade != null && Position.MarketPosition == MarketPosition.Flat)
            {
                // SetStopLoss/SetProfitTarget's own default signal names --
                // see their reference docs ("Stop loss" / "Profit target").
                // Not a guess: this is what the two-arg overloads the brief
                // specifies actually generate.
                string reason = n == "Stop loss" ? "stop"
                              : n == "Profit target" ? "target"
                              : n == SigFlatten ? "flatten"
                              : "manual";
                WriteTrade(_openTrade.Value, time.Ticks, price, reason);
                _openTrade = null;
            }
        }

        private static string Px(double v)
        {
            return v.ToString("0.######", CultureInfo.InvariantCulture);
        }

        // entry_ts/exit_ts are raw .NET ticks as JSON INTEGERS, not
        // formatted dates -- they exceed 2^53 so a float-parsing consumer
        // would silently round them (task 8 step 2). Path/lock/try-catch
        // pattern copied from PullbackZoneStrategy's corpus writer: logging
        // must never break trading.
        private void WriteTrade(OpenTrade t, long exitTicks, double exitPx, string reason)
        {
            StringBuilder sb = new StringBuilder(160);
            sb.Append("{\"entry_ts\":").Append(t.EntryTicks.ToString(CultureInfo.InvariantCulture))
              .Append(",\"exit_ts\":").Append(exitTicks.ToString(CultureInfo.InvariantCulture))
              .Append(",\"dir\":").Append(t.Dir.ToString(CultureInfo.InvariantCulture))
              .Append(",\"entry_px\":").Append(Px(t.EntryPx))
              .Append(",\"exit_px\":").Append(Px(exitPx))
              .Append(",\"reason\":\"").Append(reason).Append("\"")
              .Append(",\"qty\":").Append(t.Qty.ToString(CultureInfo.InvariantCulture))
              .Append("}");
            try
            {
                if (_jsonlPath == null)
                    _jsonlPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                        "DriftVwap", "nt8_trades.jsonl");
                lock (_jsonlLock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_jsonlPath));
                    File.AppendAllText(_jsonlPath, sb.ToString() + Environment.NewLine);
                }
            }
            catch { }
        }

        #region Properties
        [NinjaScriptProperty, Range(1, 24)]
        [Display(Name = "Drift lookback (15m bars)", Description = "15m bars in the rate-of-change window. Frozen spec value: 4 (one hour).", GroupName = "01. Regime", Order = 0)]
        public int DriftLookbackBars { get; set; }

        [NinjaScriptProperty, Range(0.0, 2.0)]
        [Display(Name = "Drift min (%)", Description = "Rate-of-change threshold over the lookback window, in PERCENT (0.10 = 0.10%). Frozen spec value: 0.10.", GroupName = "01. Regime", Order = 1)]
        public double DriftMinPct { get; set; }

        [NinjaScriptProperty, Range(1, 400)]
        [Display(Name = "Stop (points)", Description = "Stop distance, index points. Frozen spec value: 80.", GroupName = "02. Risk", Order = 0)]
        public double StopPoints { get; set; }

        [NinjaScriptProperty, Range(1, 400)]
        [Display(Name = "Target long (points)", Description = "Long target, index points. Frozen spec value: 40.", GroupName = "02. Risk", Order = 1)]
        public double TargetPointsLong { get; set; }

        [NinjaScriptProperty, Range(1, 400)]
        [Display(Name = "Target short (points)", Description = "Short target, index points. Frozen spec value: 50 -- the asymmetric target is transcribed faithfully, not corrected (spec R6).", GroupName = "02. Risk", Order = 2)]
        public double TargetPointsShort { get; set; }

        [NinjaScriptProperty, Range(0, 2359)]
        [Display(Name = "Trade start (ET, HHMM)", Description = "No entries before this ET clock time. Frozen spec value: 1030.", GroupName = "03. Session", Order = 0)]
        public int TradeStartHHMM { get; set; }

        [NinjaScriptProperty, Range(0, 2359)]
        [Display(Name = "Trade stop (ET, HHMM)", Description = "No entries after this ET clock time. Frozen spec value: 1530.", GroupName = "03. Session", Order = 1)]
        public int TradeStopHHMM { get; set; }

        [NinjaScriptProperty, Range(0, 2359)]
        [Display(Name = "Flatten (ET, HHMM)", Description = "Flatten all open positions at this ET clock time. Frozen spec value: 1555.", GroupName = "03. Session", Order = 2)]
        public int FlattenHHMM { get; set; }

        [NinjaScriptProperty, Range(1, 100)]
        [Display(Name = "Contracts", GroupName = "04. Sizing", Order = 0)]
        public int Contracts { get; set; }

        [NinjaScriptProperty, Range(0, 20)]
        [Display(Name = "Max trades/day", Description = "0 = off. Applied as a post-hoc filter, not simulated live (spec 5.3).", GroupName = "04. Sizing", Order = 1)]
        public int MaxTradesPerDay { get; set; }

        [NinjaScriptProperty, Range(0, 20)]
        [Display(Name = "Max losses/day", Description = "0 = off. Applied as a post-hoc filter, not simulated live (spec 5.3).", GroupName = "04. Sizing", Order = 2)]
        public int MaxLossesPerDay { get; set; }

        // The one C#-only parameter (task 7 brief item 5) -- chart furniture
        // has nothing to simulate on the PropSim side, following
        // LatigoBreak's ShowDrawings. Deliberately NOT mirrored in
        // propsim/drift_vwap_pullback.py.
        [NinjaScriptProperty]
        [Display(Name = "Show drawings", GroupName = "05. Visuals", Order = 0)]
        public bool ShowDrawings { get; set; }
        #endregion
    }
}
