// DriftVwapPullbackStrategy -- NinjaScript mirror of propsim/drift_vwap_pullback.py.
// NQ, RTH only (09:30-16:00 ET). Execution series 5-minute, regime series 15-minute.
//
// THE MIRROR IS THE CONTRACT. docs/specs/2026-08-07-driftvwap-design.md is the
// closed spec; propsim/drift_vwap_pullback.py is its reviewed, mutation-tested
// implementation. Every rule below is transcribed from that file and where the
// two disagree THAT FILE WINS.
//
// LAB STATUS -- SKELETON + REGIME ONLY (plan tasks 5+6). This file has the
// closed parameter list, the 5-minute timeframe guard, and the 15-minute
// session VWAP + drift state (R1/R2). It has NO entries, NO exits, NO orders
// and NO drawing -- that is task 7. OnBarUpdate's primary-series branch
// currently only prints the drift state for manual verification against the
// plugin.
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
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class DriftVwapPullbackStrategy : Strategy
    {
        private const int Regime15Idx = 1;

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

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "DriftVwapPullbackStrategy";
                Description = "Mirror of propsim/drift_vwap_pullback.py -- 15m VWAP drift + 5m pullback trigger (Conti). SKELETON: no regime/entries yet (plan tasks 6-7). See docs/specs/2026-08-07-driftvwap-design.md.";
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

                // Spec 5.2: the timeframe is a property of the RUN, not of the code.
                if (BarsPeriods[0].BarsPeriodType != BarsPeriodType.Minute
                    || BarsPeriods[0].Value != 5)
                {
                    Log(Name + ": requires a 5-minute primary series; got "
                        + BarsPeriods[0].ToString(), Cbi.LogLevel.Error);
                    SetState(State.Finalized);
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

            FoldClosedRegimeBars();

            // Diagnostic only (task 6, GUI hand-off step 3): prints the drift
            // state and session VWAP at every 5m close so it can be compared,
            // bar for bar, against the plugin's state_of[done[i]] on the same
            // session. Task 7 replaces this with the R3 arming state machine
            // and the R4 trigger -- this line prints, it does not act.
            Print(Name + ": " + Time[0].ToString("yyyy-MM-dd HH:mm")
                + " drift=" + DriftStateAt(Time[0])
                + " vwap15=" + SessionVwap15(0).ToString("F2"));
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
        private void FoldClosedRegimeBars()
        {
            Bars b15 = BarsArray[Regime15Idx];
            for (int j = _regDone + 1; j < b15.Count; j++)
            {
                if (b15.GetTime(j) > Time[0])
                    break;                                 // not closed yet
                FoldRegimeBar(b15, j);
                _regDone = j;
            }
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
        #endregion
    }
}
