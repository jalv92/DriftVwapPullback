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
// v2 (2026-08-10) -- THREE NT8-ONLY ADDITIONS, OUTSIDE THE MIRROR. Ported
// from LatigoBreakStrategy.cs, which already paid for every gotcha marked
// below as such:
//
//   1. HAND-DRAGGABLE BRACKETS. SetStopLoss/SetProfitTarget are GONE. The
//      bracket is now a pair of live-until-cancelled Exit orders (DVP_Stop /
//      DVP_Target) submitted from OnExecutionUpdate on the entry fill and
//      never re-asserted, so a stop or target dragged by hand in Chart
//      Trader STAYS where you put it. Not a style choice: the engine
//      re-asserts Set* prices (which is exactly what reverts a hand-drag),
//      AND the managed approach ignores an Exit* order outright while a Set*
//      order is active for the position -- managed_approach.md, "Methods
//      that generate orders to exit a position will be ignored if ... an
//      order submitted by a set method is active". The two mechanisms cannot
//      coexist; the Set* calls had to go, not merely be supplemented.
//      OnOrderUpdate adopts hand-moves into _stopPx/_targetPx so the
//      breakeven guards below stay honest about where the stop really is.
//   2. BREAKEVEN at a percent of the entry->target run (default 75%),
//      one-shot, evaluated on every tick.
//   3. DAILY RISK GOVERNOR -- a profit target and a loss limit in DOLLARS,
//      measured by default over the WHOLE ACCOUNT (so a limit is reached by
//      the combined day of every robot on that account, not just this one),
//      checked on every tick, flattening the position and locking out
//      entries for the rest of the session on a breach.
//
// WHY THE MIRROR SURVIVES ALL THREE: each is inert at its default
// (UseBreakeven false, both dollar limits 0), the same convention
// MaxTradesPerDay/MaxLossesPerDay already follow, so a default run still
// produces exactly the trade list propsim/drift_vwap_pullback.py produces.
// Turn any of them on and the run is NO LONGER comparable to a default
// PropSim dump -- none of the three exists on the plugin side, and the
// strategy says so in the log at startup.
//
// WHY 2 AND 3 RUN ON THE TICK SERIES. This strategy is Calculate.OnBarClose
// on 5-minute bars; a breakeven or a loss limit that could only fire every
// five minutes is not a risk control. Both therefore run from the
// BarsInProgress == FillTickIdx branch of OnBarUpdate -- the 1-tick series
// that already exists for fill resolution. Nothing about the 5-minute
// decision path changed: the primary branch is byte-for-byte the v1 logic,
// still under OnBarClose, and the tick branch never touches the arm/trigger
// state machine, _reg15, or the VWAP accumulator.
//
// R5's 15:55 flatten is unaffected by the bracket change: it is a MARKET
// order, and the ignore-rules above explicitly do not apply to those ("These
// rules do not apply to market orders, such as ExitLong() or ExitShort()").
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

        // Fix round 5: NOT OrderFillResolution.High -- see the SetDefaults
        // comment for why. v2: this series is no longer read-only furniture
        // for NT8's fill engine -- OnBarUpdate now has a real
        // BarsInProgress == FillTickIdx branch, which is where the breakeven
        // and the daily risk governor live (file header, "WHY 2 AND 3 RUN ON
        // THE TICK SERIES"). It still supplies nothing to the 5-minute
        // decision path: no bar of it is ever folded, compared or stored.
        private const int FillTickIdx = 2;

        // PTS (4, NQ ticks per index point) lived here to turn the points-based
        // risk parameters into the tick counts SetStopLoss/SetProfitTarget
        // wanted. v2 prices the bracket in price units directly, so nothing
        // needs the conversion any more.
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
        private const string SigStop = "DVP_Stop";       // v2 -- was SetStopLoss' "Stop loss"
        private const string SigTarget = "DVP_Target";   // v2 -- was SetProfitTarget's "Profit target"

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

        // Fix round 7 -- MaxTradesPerDay/MaxLossesPerDay were UI decoration
        // until now (Javier set both, the strategy ignored both). Reset each
        // RTH session on Bars.IsFirstBarOfSession -- the same session-
        // boundary primitive FoldRegimeBar already uses
        // (IsFirstBarOfSessionByIndex) for the 15m series; no second notion
        // of "day". Counted unconditionally (see OnBarUpdate/OnExecutionUpdate);
        // only the CAP CHECK is gated on > 0, so a 0 default can never block
        // anything regardless of what these hold.
        private int _tradesToday;
        private int _lossesToday;

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
            public double EntryCommission;   // fix round 9 -- net-of-commission loss test
        }
        private OpenTrade? _openTrade;

        // --- v2 bracket state -------------------------------------------------
        // _stopPx/_targetPx track the LIVE order prices, not merely what was
        // submitted: OnOrderUpdate writes a hand-drag back into them, so the
        // breakeven's "never move the stop backwards" guard compares against
        // where the stop actually is. _targetPx == 0 means "no target working"
        // (never priced, or deliberately cancelled by hand -- the breakeven
        // must not resurrect one the user removed on purpose).
        private double _stopPx, _targetPx;
        private bool _beApplied;

        // Live bracket references. Any Cancelled event whose Order is NOT the
        // current reference is an echo of one of our OWN cancel-replaces (the
        // breakeven re-price, a partial-fill resize) -- never a hand-cancel.
        // Latigo's paid lesson: order NAMES cannot tell the two apart, only
        // reference identity can, and the refs must be nulled BEFORE each
        // re-submit so in-stack echoes miss. Genuine cancels are judged one
        // second later (CheckBracketCancels) so an OCO cancel racing the
        // closing fill doesn't read as a hand-cancel either.
        private Order _stopOrder, _targetOrder;
        private DateTime _stopCancelAt = DateTime.MinValue, _targetCancelAt = DateTime.MinValue;

        // In-flight order flags, set BEFORE the Enter*/Exit* call that creates
        // the order -- the workspace's standing rule from the NT8 order-event
        // race (a fill event can arrive inside the submitting call's own stack).
        private bool _entryPending, _flattenPending;

        // --- v2 daily risk governor -------------------------------------------
        private double _dayStartRealized;
        private bool _dailyLockout;
        private DateTime _acctSessionDay = DateTime.MinValue;

        // Account-wide mode. The dollars that decide a breach come from
        // Account.Get(...), which is the WHOLE account -- every other robot's
        // realized and unrealized P&L included. That is the part Javier asked
        // for and it needs no cooperation from those robots. This static
        // registry is only how MULTIPLE INSTANCES OF THIS CLASS agree on one
        // day baseline and propagate a breach to each other; re-read under
        // lock on every tick, never cached, so a wipe-and-recreate cannot
        // split the group.
        private sealed class AcctDayGov
        {
            public DateTime Day;
            public double Baseline;
            public volatile bool Breached;
        }
        private static readonly object _acctGovLock = new object();
        private static readonly Dictionary<string, AcctDayGov> _acctGov = new Dictionary<string, AcctDayGov>();

        private static readonly object _jsonlLock = new object();
        private string _jsonlPath;
        private bool _jsonlDisabled;   // set true only if this run's own file creation fails

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

                // v2, all NT8-only. Defaults chosen so the mirror gate is
                // unaffected: the breakeven is OFF (same as LatigoBreak's own
                // default) and both dollar limits are 0 = off, the convention
                // MaxTradesPerDay/MaxLossesPerDay already use. BreakevenPercent
                // is 75 -- Javier's asked-for value, live the moment the
                // checkbox above it is ticked.
                UseBreakeven = false;
                BreakevenPercent = 75;
                BreakevenOffsetTicks = 0;

                DailyProfitTargetUSD = 0;
                DailyLossLimitUSD = 0;
                UseAccountDailyPnL = true;   // account total, not this strategy's own P&L

                ShowDrawings = true;   // C#-only, not a shared param -- see its Display attribute

                // Fix round 5 -- historical fill granularity, matching the
                // PropSim engine's real-tick walk instead of NT8's default
                // (Standard: fills resolved against the PRIMARY 5-minute bar,
                // which can silently hide a stop AND a target inside the same
                // bar and let the two sides disagree on which hit first).
                //
                // NOT OrderFillResolution.High: this strategy is multi-series
                // (AddDataSeries(Minute, 15) below), and this workspace has a
                // real, previously-hit NT8 runtime error on record for that
                // exact combination -- HFT_NT8/decisions/ADR-004b: loading a
                // multi-series strategy with OrderFillResolution.High throws
                // "'High' Order Fill Resolution is only available for
                // single-series strategies. For multi-series strategies,
                // please program directly into your strategy the more
                // granular resolution you would like to simulate order fills
                // with." ADR-004b's own resolution is exactly what's below:
                // Standard (already the default; written explicitly for the
                // same reason ADR-004b does) + OrderFillResolutionType/Value
                // pointed at a 1-tick series the strategy adds itself via
                // AddDataSeries -- NT8 uses that series for fill refinement
                // whether High or a manually-added series supplies it; the
                // granularity is identical either way (ADR-004b section 3).
                OrderFillResolution = OrderFillResolution.Standard;
                OrderFillResolutionType = BarsPeriodType.Tick;
                OrderFillResolutionValue = 1;
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Minute, 15);   // BarsInProgress == Regime15Idx (1)

                // Fix round 5: the 1-tick fill-resolution series, ADDED AFTER
                // the 15-minute one above so it becomes index FillTickIdx (2)
                // and Regime15Idx (1) is UNCHANGED -- index order follows
                // AddDataSeries call order, so reordering these two lines
                // would silently repoint every BarsArray[Regime15Idx] read in
                // this file at the wrong series. Never read directly; exists
                // only for OrderFillResolutionType/Value above to consume.
                AddDataSeries(BarsPeriodType.Tick, 1);      // BarsInProgress == FillTickIdx (2)
            }
            else if (State == State.DataLoaded)
            {
                // Fix round 6: the timeframe guard runs FIRST, before anything
                // else in this branch -- including the JSONL path/file setup
                // below. A wrong-timeframe chart must never touch the JSONL
                // file for a run that is about to abort; before this reorder
                // the file setup ran first, so attaching to e.g. a 1-minute
                // chart with a good file already on disk truncated it and
                // THEN aborted, destroying a real run's output for a run that
                // never started. Spec 5.2: the timeframe is a property of the
                // RUN, not of the code.
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

                // A reused instance (e.g. IsInstantiatedOnEachOptimizationIteration
                // == false on some future config) would otherwise carry the
                // PREVIOUS run's accumulated 15m history into this one -- field
                // initializers run once at construction, not per State chain.
                _reg15.Clear();
                _cumPv = 0; _cumV = 0; _barsInSession15 = 0;
                _regDone = -1;
                _lastRegimeIdx = null; _prevState = 0; _armed = false; _armedDir = 0;
                _openTrade = null;
                _tradesToday = 0; _lossesToday = 0;   // fix round 7 -- also self-resets on the
                                                       // first bar of every session (OnBarUpdate);
                                                       // zeroed here too for a fresh run, same as
                                                       // every other per-run field above.

                // v2 state, same reasoning as the block above.
                _stopPx = 0; _targetPx = 0; _beApplied = false;
                _stopOrder = null; _targetOrder = null;
                _stopCancelAt = DateTime.MinValue; _targetCancelAt = DateTime.MinValue;
                _entryPending = false; _flattenPending = false;
                _dailyLockout = false;
                _acctSessionDay = DateTime.MinValue;
                _dayStartRealized = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;

                // A Playback rewind replays a day this account has already
                // "lived", so any breach the discarded pass broadcast is stale.
                // Drop this account's registry entry and let the first session
                // boundary of the new pass re-baseline it.
                if (Account != null)
                    lock (_acctGovLock) _acctGov.Remove(Account.Name);

                // Fix round 7/8 -- once, here (never per bar): a capped run's
                // trade list is a SUBSET of what the plugin would produce
                // (entries() never caps), so it is not directly comparable to
                // a default PropSim dump unless the same filter is applied
                // there too. LogLevel.Warning, not Alert: this fires on every
                // run where the caps are set and only confirms a value the
                // user just typed into the parameter grid -- a routine
                // acknowledgment, not an unexpected condition. The modal
                // Alert produces is reserved for the guards that actually
                // abort the run (the timeframe guard, CheckRth); spending it
                // on a routine confirmation trains the user to dismiss
                // dialogs from this strategy without reading them.
                //
                // v2 folds the three new NT8-only knobs into the SAME notice
                // rather than adding a second one: they break comparability for
                // exactly the same reason (the plugin has no concept of any of
                // them), so one line naming whichever are active is what the
                // user needs, and one dismissable notice stays readable.
                if (MaxTradesPerDay > 0 || MaxLossesPerDay > 0 || UseBreakeven
                    || DailyProfitTargetUSD > 0 || DailyLossLimitUSD > 0)
                    Log(Name + ": NT8-only controls ACTIVE this run (MaxTradesPerDay="
                        + MaxTradesPerDay + ", MaxLossesPerDay=" + MaxLossesPerDay
                        + ", UseBreakeven=" + UseBreakeven
                        + ", DailyProfitTargetUSD=" + DailyProfitTargetUSD
                        + ", DailyLossLimitUSD=" + DailyLossLimitUSD
                        + ") -- NOT directly comparable to a default PropSim dump; "
                        + "the plugin implements none of them.", Cbi.LogLevel.Warning);

                // Fix round 3: resolve the JSONL path ONCE here (WriteTrade no
                // longer builds it). Fix round 6: the path is now PER-RUN,
                // not fixed -- two instances sharing one path (Javier runs the
                // Strategy Analyzer mirror gate while keeping a Playback chart
                // loaded for other checks, on the team lead's own recommended
                // workflow) used to mean whichever instance's DataLoaded ran
                // second truncated out whatever the other had already written
                // THIS run, which a fixed path could never avoid short of
                // detecting the collision. A per-run filename makes the
                // collision impossible instead: nothing to truncate, nothing
                // for a sibling instance to step on. Instrument + a
                // to-the-second timestamp, not a hash -- readable at a glance
                // in the folder, and DataLoaded firing exactly once per run
                // (onstatechange.md: "called only once after all data series
                // have been loaded") means this timestamp is read exactly
                // once too, so it names this run and only this run.
                string sym = Instrument?.MasterInstrument?.Name ?? "UNKNOWN";
                _jsonlPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "DriftVwap",
                    "nt8_trades_" + sym + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".jsonl");
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_jsonlPath));
                    File.WriteAllText(_jsonlPath, string.Empty);   // create: fresh path, nothing to truncate
                    _jsonlDisabled = false;
                }
                catch (Exception ex)
                {
                    // Can't guarantee this run gets a usable file. Disable the
                    // dump for the whole run instead of guessing; the Log
                    // entry says why so a missing file isn't a mystery.
                    Log(Name + ": could not create " + _jsonlPath + " (" + ex.Message
                        + ") -- JSONL trade dump disabled for this run.", Cbi.LogLevel.Error);
                    _jsonlDisabled = true;
                }
            }
            else if (State == State.Realtime)
            {
                // Account-wide measurement only exists here (see CurrentAccountDay).
                // Print it so the mode in force is never a guess: the same
                // parameter grid means two different things in the Analyzer and
                // on a live/Playback chart.
                if ((DailyProfitTargetUSD > 0 || DailyLossLimitUSD > 0) && UseAccountDailyPnL)
                    Print(Name + ": daily limits watching the WHOLE ACCOUNT ("
                        + (Account != null ? Account.Name : "?")
                        + ") -- every robot's P&L on it counts toward the "
                        + DailyProfitTargetUSD + " / -" + DailyLossLimitUSD + " USD limits.");
            }
        }

        protected override void OnBarUpdate()
        {
            // v2 -- the tick branch: risk controls that must not wait for a
            // 5-minute bar to close. Deliberately touches NOTHING the mirror
            // depends on (no _reg15, no VWAP accumulator, no arm/trigger
            // state); it only reads price and manages an already-open trade.
            // No CheckRth here on purpose: the guard exists to protect the
            // VWAP from out-of-session bars, and terminating the strategy over
            // a stray tick while a position is open would be strictly worse
            // than managing it.
            if (BarsInProgress == FillTickIdx)
            {
                if (CurrentBars[FillTickIdx] < 0)
                    return;
                CheckRiskGovernor();
                if (Position.MarketPosition != MarketPosition.Flat)
                {
                    CheckBracketCancels(Times[FillTickIdx][0]);
                    ManagePosition(Closes[FillTickIdx][0]);
                }
                return;
            }

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
                // v2 routes through the shared FlattenNow() the governor also
                // uses -- same market order, same DVP_Flatten signal name, but
                // with the in-flight flag that stops a second submit on the
                // next bar while the first is still working. An empty
                // fromEntrySignal exits every entry, which for a strategy with
                // EntriesPerDirection = 1 is the same set of contracts the old
                // per-signal call closed.
                FlattenNow();
                return;
            }

            // Fix round 7 -- per-session reset, BEFORE the arm-gated early
            // return below, so a session that never arms still clears the
            // PREVIOUS session's counts (an early return here would leave a
            // stale non-zero count sitting in front of a session that hasn't
            // taken a single trade yet). Bars.IsFirstBarOfSession is the
            // same session-boundary primitive FoldRegimeBar already uses on
            // the 15m series -- no second notion of "day".
            if (Bars.IsFirstBarOfSession)
            {
                _tradesToday = 0;
                _lossesToday = 0;

                // v2 -- the governor's day rolls on the SAME boundary, not a
                // second notion of "day". This fires on the first 5m bar to
                // CLOSE in the session (09:35), so a lockout from yesterday
                // survives the session's first five minutes. That is inert by
                // construction: no position can exist across the boundary
                // (R5 flattens at 15:55) and no entry is possible before
                // TradeStartHHMM, which is 10:30 by default and can never be
                // earlier than the 09:35 reset would allow anyway.
                _dailyLockout = false;
                _dayStartRealized = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
                _acctSessionDay = Time[0].Date;
                if (UseAccountDailyPnL)
                    CurrentAccountDay();   // prime this day's shared baseline
            }

            UpdateArmState();

            if (!_armed)
                return;

            // Fix round 7 -- Javier's own report: neither cap did anything
            // before this. 0 means off (both defaults, unchanged): the
            // condition is structurally false whenever the parameter is 0,
            // regardless of what the counters hold, so the default path
            // cannot be capped by construction, not by a value happening to
            // stay below a threshold.
            if (MaxTradesPerDay > 0 && _tradesToday >= MaxTradesPerDay)
                return;
            if (MaxLossesPerDay > 0 && _lossesToday >= MaxLossesPerDay)
                return;

            // v2 -- the dollar governor blocks entries the same way the count
            // caps do. Placed AFTER UpdateArmState deliberately: the arm/
            // trigger state machine must keep tracking the 15m regime while
            // locked out, or it would resume the next session mid-sequence
            // with a stale _prevState.
            if (_dailyLockout)
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

            // v2 -- the SetStopLoss/SetProfitTarget pair that used to sit here
            // is GONE, not moved: while a Set* order is active the managed
            // approach ignores Exit* orders outright, so the two bracket
            // mechanisms cannot coexist (file header, item 1). The bracket is
            // now priced off the ACTUAL fill and submitted from
            // OnExecutionUpdate -- which also removes the ordering hazard the
            // old comment here warned about (the TBStrategy naked-trade bug):
            // there is no longer a "call it before the entry or the next trade
            // gets it" rule to get wrong, because the bracket cannot be
            // submitted before the fill it is attached to exists.
            _entryPending = true;   // BEFORE Enter* -- NT8 order-event race
            if (_armedDir > 0)
                EnterLong(Contracts, SigLong);
            else
                EnterShort(Contracts, SigShort);
            _tradesToday++;   // fix round 7 -- counts the entry taken, not the eventual fill

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

            if (n == SigLong || n == SigShort)
            {
                // Any order state that leaves filled contracts on the books
                // counts as being in the trade -- a full fill, a partial fill,
                // or a cancel after a partial.
                if (execution.Order.OrderState != OrderState.Filled
                    && execution.Order.OrderState != OrderState.PartFilled
                    && !(execution.Order.OrderState == OrderState.Cancelled
                         && execution.Order.Filled > 0))
                    return;

                _entryPending = false;

                if (_openTrade == null)
                    _openTrade = new OpenTrade
                    {
                        EntryTicks = time.Ticks,
                        EntryPx = price,
                        Dir = n == SigLong ? 1 : -1,
                        Qty = execution.Order.Quantity,
                        EntryCommission = execution.Commission   // fix round 9
                    };

                // v2 -- the governor breached while this entry was in flight.
                // Close it on the fill event itself rather than waiting for
                // the next tick, and give it no brackets: the position must
                // not outlive the breach by even one tick.
                if (_dailyLockout)
                {
                    FlattenNow();   // _entryPending was just cleared, so this goes through
                    return;
                }

                SubmitBrackets(execution.Order);   // prices on the first fill, resizes on later ones
                return;
            }

            // v2 -- bracket bookkeeping is keyed on being FLAT, deliberately
            // NOT on _openTrade being non-null like the JSONL row below it.
            // The two answer different questions, and tying the bracket reset
            // to the trade record would leave _stopPx holding the PREVIOUS
            // trade's price on any path where the record is missing -- and
            // SubmitBrackets treats a non-zero _stopPx as "already priced",
            // so the next trade would silently inherit a stop from the last
            // one. Clearing the in-flight flatten flag here matters for the
            // same reason: stuck true, it would veto every future flatten,
            // including the governor's.
            if (Position.MarketPosition == MarketPosition.Flat)
            {
                _stopPx = 0; _targetPx = 0;
                _beApplied = false;
                _stopOrder = null; _targetOrder = null;
                _stopCancelAt = DateTime.MinValue; _targetCancelAt = DateTime.MinValue;
                _flattenPending = false;
            }

            if (_openTrade != null && Position.MarketPosition == MarketPosition.Flat)
            {
                // v2 -- these are OUR signal names now, not SetStopLoss/
                // SetProfitTarget's generated "Stop loss" / "Profit target".
                // Leaving the old strings here would have silently relabelled
                // every stop and target in the JSONL as "manual" and taken the
                // mirror gate's exit-reason comparison down with it.
                string reason = n == SigStop ? "stop"
                              : n == SigTarget ? "target"
                              : n == SigFlatten ? "flatten"
                              : "manual";

                // Fix round 9 -- a "loss" is NET of commission, strict: gross
                // price P&L converted to dollars, minus BOTH legs' commission,
                // compared to zero; a tie is NOT a loss, same strictness
                // convention DriftStateAt already uses ("comparisons are
                // strict; a tie is FLAT"). Superseded the fix-round-7 gross
                // (pre-commission) test after the risk audit found the gap
                // that reasoning missed: the 80pt stop / 40-50pt targets make
                // gross vs net essentially never disagree on THOSE three
                // exits, but the 15:55 flatten is bounded by none of that
                // geometry and can close within a tick or two of entry --
                // exactly where commission decides win vs. small net loss.
                // Misclassifying a marginal net loss as not-a-loss makes
                // MaxLossesPerDay fire LATER than it should -- fails open in
                // the one direction that costs money -- so net is the
                // correct test, not gross with a documented blind spot.
                //
                // PointValue null-checked (this call site is OnExecutionUpdate,
                // not OnBarUpdate -- pointvalue.md's own "no null check needed"
                // exemption is scoped to OnBarUpdate only) and falls back to
                // 0.0 as a ponytail: if MasterInstrument were ever
                // unavailable here (should not happen -- an execution only
                // fires for an instrument this strategy is actively trading),
                // grossPnl collapses to 0 and netPnl is always <= -commission,
                // i.e. every trade misclassifies as a loss -- the same fail-
                // open-toward-caution direction as the reasoning above, not a
                // silent hole in the other direction.
                double pointValue = Instrument?.MasterInstrument?.PointValue ?? 0.0;
                double grossPnl = (price - _openTrade.Value.EntryPx) * _openTrade.Value.Dir
                    * pointValue * _openTrade.Value.Qty;
                double netPnl = grossPnl - _openTrade.Value.EntryCommission - execution.Commission;
                if (netPnl < 0)
                    _lossesToday++;

                WriteTrade(_openTrade.Value, time.Ticks, price, reason);
                _openTrade = null;
            }
        }

        // v2 -- the bracket, as a pair of live-until-cancelled Exit orders
        // priced off the ACTUAL fill. Submitted here rather than at signal
        // time and never re-asserted afterwards, which is precisely what lets
        // a hand-drag in Chart Trader survive (see the file header).
        //
        // Called on every entry execution: the first one prices the bracket
        // (_stopPx == 0), later partial fills only resize it, so a partially
        // filled entry is never left with a bracket for the wrong quantity.
        private void SubmitBrackets(Order entry)
        {
            int qty = entry.Filled;
            if (qty <= 0)
                return;

            // Direction from the ORDER's own name, not from _armedDir: the
            // 15m state machine can re-arm the other way while this trade is
            // still open, and the bracket must belong to the trade that is
            // actually on the books.
            int dir = entry.Name == SigLong ? 1 : -1;

            if (_stopPx == 0)
            {
                double basePx = Position.AveragePrice > 0 ? Position.AveragePrice : entry.AverageFillPrice;
                double target = dir > 0 ? TargetPointsLong : TargetPointsShort;
                // StopPoints/TargetPoints are INDEX POINTS and NQ/MNQ quote in
                // points, so these are direct price offsets -- the same
                // distances the old SetStopLoss/SetProfitTarget pair expressed
                // as (points x 4) ticks.
                _stopPx = Instrument.MasterInstrument.RoundToTickSize(basePx - dir * StopPoints);
                _targetPx = Instrument.MasterInstrument.RoundToTickSize(basePx + dir * target);
            }

            // Null the refs BEFORE submitting: a cancel-replace echoes the OLD
            // orders' Cancelled events, sometimes in this very call stack, and
            // they must not match the current references.
            _stopOrder = null; _targetOrder = null;
            if (dir > 0)
            {
                _stopOrder = ExitLongStopMarket(0, true, qty, _stopPx, SigStop, SigLong);
                _targetOrder = ExitLongLimit(0, true, qty, _targetPx, SigTarget, SigLong);
            }
            else
            {
                _stopOrder = ExitShortStopMarket(0, true, qty, _stopPx, SigStop, SigShort);
                _targetOrder = ExitShortLimit(0, true, qty, _targetPx, SigTarget, SigShort);
            }
        }

        // Deferred hand-cancel detector. A bracket's Cancelled event only means
        // "the user removed it" if the position is STILL open a second later:
        // our own cancel-replaces never reach here (reference check in
        // OnOrderUpdate) and the OCO cancel that races a closing fill is wiped
        // by the flat bookkeeping above before this timer expires.
        private void CheckBracketCancels(DateTime t)
        {
            if (_stopCancelAt != DateTime.MinValue && (t - _stopCancelAt).TotalSeconds >= 1)
            {
                _stopCancelAt = DateTime.MinValue;
                Print(Name + ": " + SigStop + " cancelled by hand -- the position is no longer protected on that side.");
            }
            if (_targetCancelAt != DateTime.MinValue && (t - _targetCancelAt).TotalSeconds >= 1)
            {
                _targetCancelAt = DateTime.MinValue;
                _targetPx = 0;   // honour it: the breakeven must not resurrect a target removed on purpose
                Print(Name + ": " + SigTarget + " cancelled by hand -- take profit removed.");
            }
        }

        // v2 breakeven -- one shot, on price covering BreakevenPercent of the
        // entry->target run. Runs per tick from the FillTickIdx branch.
        //
        // The run is measured against the LIVE target (_targetPx, which
        // OnOrderUpdate keeps in sync with a hand-drag), so dragging the target
        // further out moves the breakeven trigger with it rather than leaving
        // it anchored to a price that no longer exists.
        private void ManagePosition(double px)
        {
            if (!UseBreakeven || _beApplied || _targetPx == 0 || _stopPx == 0)
                return;

            int dir = Position.MarketPosition == MarketPosition.Long ? 1 : -1;
            double entry = Position.AveragePrice;
            double run = (_targetPx - entry) * dir;
            if (run < TickSize)
                return;
            double covered = (px - entry) * dir;
            if (covered / run * 100.0 < BreakevenPercent)
                return;

            double bePx = Instrument.MasterInstrument.RoundToTickSize(
                entry + dir * BreakevenOffsetTicks * TickSize);
            // Never move the stop backwards, and never to or past the working
            // target -- a large offset against a short run would otherwise
            // invert the bracket.
            if (dir > 0 ? (bePx <= _stopPx || bePx >= _targetPx)
                        : (bePx >= _stopPx || bePx <= _targetPx))
                return;

            // Trackers BEFORE the submits (Playback can call OnOrderUpdate
            // synchronously in this stack, and a stale tracker makes the
            // breakeven's own echo print as "moved by hand"). The stop change
            // is a cancel-replace under OCO, which ALSO kills the target leg,
            // so the target is re-submitted in the same pass -- Latigo lost a
            // live take-profit to exactly this and the fix is not optional.
            _stopPx = bePx;
            _beApplied = true;
            _stopOrder = null; _targetOrder = null;
            if (dir > 0)
            {
                _stopOrder = ExitLongStopMarket(0, true, Position.Quantity, bePx, SigStop, SigLong);
                _targetOrder = ExitLongLimit(0, true, Position.Quantity, _targetPx, SigTarget, SigLong);
            }
            else
            {
                _stopOrder = ExitShortStopMarket(0, true, Position.Quantity, bePx, SigStop, SigShort);
                _targetOrder = ExitShortLimit(0, true, Position.Quantity, _targetPx, SigTarget, SigShort);
            }
            Print(Name + ": breakeven armed at " + Px(bePx) + " (" + BreakevenPercent + "% of the run covered).");
        }

        // --- v2 daily risk governor ------------------------------------------

        // Fetch-or-create this account's shared day entry. Null means shared
        // mode cannot operate, and the caller falls back to this instance's own
        // P&L.
        //
        // WHAT WINDOW THE BASELINE DEFINES, STATED EXACTLY. Baseline is taken
        // at the first 5-minute close of the RTH session (09:35 ET), so the
        // governor measures the account's P&L FROM THE RTH OPEN ONWARD -- every
        // robot's trades, every instrument, but only from 09:30. P&L another
        // strategy booked in the overnight session (a LatigoBreak 18:00/20:00
        // window, say) is NOT counted against these limits.
        //
        // This is deliberate, not an oversight. Reading the account's own daily
        // figure raw would capture the overnight too, but it depends on the
        // broker resetting AccountItem.RealizedProfitLoss at their daily
        // rollover -- unverified here, and wrong in the expensive direction if
        // the connection reports a lifetime figure instead (an instant, silent
        // lockout on the first tick). Subtracting a baseline this file took
        // itself is correct on every connection, including Playback and Sim101.
        // Anchoring to the broker's own start-of-day (AccountItem
        // .SodLiquidatingValue vs NetLiquidation) is the upgrade path if the
        // overnight ever needs to count; it needs a connection that actually
        // populates SodLiquidatingValue, which sim accounts often do not.
        //
        // HISTORICAL RUNS NEVER PARTICIPATE. A Strategy Analyzer run has no
        // meaningful account-wide P&L to read, and this registry is static --
        // shared across every instance in the NT8 process. Without this guard,
        // running the mirror gate in the Analyzer while a live or Playback
        // chart holds the same strategy would let that chart's breach lock out
        // the Analyzer run, silently truncating it. This file already carries
        // one scar from exactly that pair of concurrent runs (the per-run JSONL
        // filename, fix round 6); this is the same hazard, pre-empted.
        private AcctDayGov CurrentAccountDay()
        {
            if (State != State.Realtime || Account == null || _acctSessionDay == DateTime.MinValue)
                return null;
            lock (_acctGovLock)
            {
                AcctDayGov g;
                _acctGov.TryGetValue(Account.Name, out g);
                if (g != null && g.Day > _acctSessionDay)
                    return null;
                if (g == null || g.Day < _acctSessionDay)
                {
                    g = new AcctDayGov
                    {
                        Day = _acctSessionDay,
                        Baseline = Account.Get(AccountItem.RealizedProfitLoss, Currency.UsDollar),
                        Breached = false,
                    };
                    _acctGov[Account.Name] = g;
                }
                return g;
            }
        }

        private void CheckRiskGovernor()
        {
            if (_dailyLockout)
            {
                // One Exit call is not guaranteed to fill -- retry until flat.
                // FlattenNow() no-ops while one is already working.
                if (Position.MarketPosition != MarketPosition.Flat)
                    FlattenNow();
                return;
            }

            AcctDayGov gov = UseAccountDailyPnL ? CurrentAccountDay() : null;
            if (gov != null && gov.Breached)   // honour the broadcast even with this instance's own limits off
            {
                Lockout("account-wide breach broadcast received");
                return;
            }
            if (DailyProfitTargetUSD <= 0 && DailyLossLimitUSD <= 0)
                return;

            double dayPnL;
            bool sharedMode = gov != null;
            if (sharedMode)
            {
                // THE WHOLE ACCOUNT, which is the point: these two figures
                // include every position and every closed trade on the account
                // today, whoever put them there -- another strategy, another
                // instrument, or a manual trade.
                double realized = Account.Get(AccountItem.RealizedProfitLoss, Currency.UsDollar) - gov.Baseline;
                double unrealized = Account.Get(AccountItem.UnrealizedProfitLoss, Currency.UsDollar);
                dayPnL = realized + unrealized;
            }
            else
            {
                double realized = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit - _dayStartRealized;
                double unrealized = Position.MarketPosition != MarketPosition.Flat
                    ? Position.GetUnrealizedProfitLoss(PerformanceUnit.Currency)
                    : 0.0;
                dayPnL = realized + unrealized;
            }

            bool hitTarget = DailyProfitTargetUSD > 0 && dayPnL >= DailyProfitTargetUSD;
            bool hitLoss = DailyLossLimitUSD > 0 && dayPnL <= -DailyLossLimitUSD;
            if (!hitTarget && !hitLoss)
                return;

            if (sharedMode)
                gov.Breached = true;   // broadcast to the other instances on this account
            Lockout(string.Format(CultureInfo.InvariantCulture, "daily {0} hit ({1:F2} USD{2})",
                hitTarget ? "profit target" : "loss limit", dayPnL, sharedMode ? ", account-wide" : ""));
        }

        private void Lockout(string reason)
        {
            _dailyLockout = true;
            Print(Name + ": " + reason + " -- no more entries until the next session.");
            if (Position.MarketPosition != MarketPosition.Flat)
                FlattenNow();
        }

        // Market exit of everything. Empty fromEntrySignal on purpose: it
        // attaches the exit to ALL entries. The two-argument overload is also
        // deliberate -- ExitLong(string) alone takes a fromEntrySignal, not a
        // signal name, a bug this workspace has already paid for once.
        //
        // The in-flight guard lives HERE rather than at each call site: R5's
        // 15:55 flatten fires on every 5-minute bar until the position is
        // actually gone, and the governor retries on every tick, so both would
        // otherwise pile duplicate market orders on top of one already working.
        // An order that dies without filling clears the flag in OnOrderUpdate,
        // which is what lets the next call through to retry.
        private void FlattenNow()
        {
            if (_flattenPending || _entryPending)
                return;
            _flattenPending = true;   // BEFORE Exit* -- NT8 order-event race
            if (Position.MarketPosition == MarketPosition.Long)
                ExitLong(SigFlatten, "");
            else if (Position.MarketPosition == MarketPosition.Short)
                ExitShort(SigFlatten, "");
            else
                _flattenPending = false;
        }

        // v2 -- adopt hand-moved bracket prices so the breakeven's guards
        // compare against where the stop and target REALLY are, and notice a
        // bracket the user cancelled outright.
        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice,
            int quantity, int filled, double averageFillPrice, OrderState orderState,
            DateTime time, ErrorCode error, string comment)
        {
            if (order == null)
                return;

            if (order.Name == SigFlatten
                && (orderState == OrderState.Rejected || orderState == OrderState.Cancelled))
            {
                _flattenPending = false;   // let the governor retry on the next tick
                return;
            }

            if (order.Name == SigStop || order.Name == SigTarget)
            {
                // Events for orders that are not the CURRENT references are
                // echoes of our own cancel-replaces (breakeven, partial-fill
                // resize) -- ignored wholesale. Names cannot distinguish them;
                // only reference identity can.
                if (!ReferenceEquals(order, _stopOrder) && !ReferenceEquals(order, _targetOrder))
                    return;

                if (orderState == OrderState.Working || orderState == OrderState.Accepted)
                {
                    double p = order.Name == SigStop ? stopPrice : limitPrice;
                    double tracked = order.Name == SigStop ? _stopPx : _targetPx;
                    if (p > 0 && Math.Abs(p - tracked) >= TickSize * 0.5)
                    {
                        Print(Name + ": " + order.Name + " moved by hand to " + Px(p) + " -- adopted.");
                        if (order.Name == SigStop) _stopPx = p; else _targetPx = p;
                    }
                }
                else if (orderState == OrderState.Cancelled
                         && Position.MarketPosition != MarketPosition.Flat
                         && !_flattenPending && !_dailyLockout)
                {
                    // Judged a second later in CheckBracketCancels.
                    if (order.Name == SigStop) _stopCancelAt = time;
                    else _targetCancelAt = time;
                }
                return;
            }

            // An entry that died without ever filling has to release the
            // in-flight flag, or the governor would refuse to flatten for the
            // rest of the session believing an entry is still on its way.
            if (order.Name != SigLong && order.Name != SigShort)
                return;
            if (!_entryPending
                || (orderState != OrderState.Rejected && orderState != OrderState.Cancelled))
                return;
            _entryPending = false;
            if (filled == 0)
                Print(Name + ": entry " + order.Name + " " + orderState + " unfilled.");
        }

        private static string Px(double v)
        {
            return v.ToString("0.######", CultureInfo.InvariantCulture);
        }

        // entry_ts/exit_ts are raw .NET ticks as JSON INTEGERS, not
        // formatted dates -- they exceed 2^53 so a float-parsing consumer
        // would silently round them (task 8 step 2). Path/lock/try-catch
        // pattern copied from PullbackZoneStrategy's corpus writer: logging
        // must never break trading. Path is resolved once, at DataLoaded --
        // not here -- so there is exactly one place that builds it, and it's
        // per-run unique (fix rounds 3 and 6); _jsonlDisabled is set there
        // too, if that run's own file creation failed.
        private void WriteTrade(OpenTrade t, long exitTicks, double exitPx, string reason)
        {
            if (_jsonlDisabled)
                return;

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
                lock (_jsonlLock)
                {
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
        [Display(Name = "Max trades/day", Description = "0 = off. Blocks new entries once this many have been taken in the current RTH session; resets every session.", GroupName = "04. Sizing", Order = 1)]
        public int MaxTradesPerDay { get; set; }

        [NinjaScriptProperty, Range(0, 20)]
        [Display(Name = "Max losses/day", Description = "0 = off. Blocks new entries once this many losing trades (net of commission) have closed in the current RTH session, total not consecutive; resets every session.", GroupName = "04. Sizing", Order = 2)]
        public int MaxLossesPerDay { get; set; }

        // --- v2, NT8-only (like ShowDrawings below): deliberately NOT mirrored
        // in propsim/drift_vwap_pullback.py, and inert at these defaults so a
        // default run still matches a default plugin dump. See the file header.

        [NinjaScriptProperty]
        [Display(Name = "Use breakeven", Description = "Move the stop to entry (+offset) once price covers a % of the entry->target run. One-shot; when it triggers it overrides a hand-dragged stop ONCE. OFF keeps this run comparable to a default PropSim dump.", GroupName = "05. Breakeven", Order = 0)]
        public bool UseBreakeven { get; set; }

        [NinjaScriptProperty, Range(1, 100)]
        [Display(Name = "Breakeven trigger (% of run)", Description = "Percent of the entry->target distance price must cover before the stop moves to breakeven. Default 75.", GroupName = "05. Breakeven", Order = 1)]
        public int BreakevenPercent { get; set; }

        [NinjaScriptProperty, Range(-100, 100)]
        [Display(Name = "Breakeven offset (ticks)", Description = "Breakeven stop = entry +/- this many ticks (positive locks in a little, covering commissions).", GroupName = "05. Breakeven", Order = 2)]
        public int BreakevenOffsetTicks { get; set; }

        [NinjaScriptProperty, Range(0, 1000000)]
        [Display(Name = "Daily profit target (USD)", Description = "0 = off. Stop looking for trades (and flatten anything open) once the day's P&L reaches +this many dollars. Resets every session.", GroupName = "06. Daily limits", Order = 0)]
        public double DailyProfitTargetUSD { get; set; }

        [NinjaScriptProperty, Range(0, 1000000)]
        [Display(Name = "Daily loss limit (USD)", Description = "0 = off. Stop looking for trades (and flatten anything open) once the day's P&L reaches -this many dollars. Resets every session.", GroupName = "06. Daily limits", Order = 1)]
        public double DailyLossLimitUSD { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Limits watch the whole account", Description = "ON (default): the two limits above measure the ACCOUNT's combined P&L since the 09:30 RTH open -- every other robot on the same account counts toward them, whether or not this strategy took the trade. Overnight P&L booked before 09:30 is NOT counted. OFF: only this strategy instance's own P&L. Live/Playback only; a Strategy Analyzer run always falls back to own-P&L.", GroupName = "06. Daily limits", Order = 2)]
        public bool UseAccountDailyPnL { get; set; }

        // The one C#-only parameter (task 7 brief item 5) -- chart furniture
        // has nothing to simulate on the PropSim side, following
        // LatigoBreak's ShowDrawings. Deliberately NOT mirrored in
        // propsim/drift_vwap_pullback.py.
        [NinjaScriptProperty]
        [Display(Name = "Show drawings", GroupName = "07. Visuals", Order = 0)]
        public bool ShowDrawings { get; set; }
        #endregion
    }
}
