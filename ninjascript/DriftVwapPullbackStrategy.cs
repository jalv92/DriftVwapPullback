// DriftVwapPullbackStrategy -- NinjaScript mirror of propsim/drift_vwap_pullback.py.
// NQ, RTH only (09:30-16:00 ET). Execution series 5-minute, regime series 15-minute.
//
// THE MIRROR IS THE CONTRACT. docs/specs/2026-08-07-driftvwap-design.md is the
// closed spec; propsim/drift_vwap_pullback.py is its reviewed, mutation-tested
// implementation. Every rule below is transcribed from that file and where the
// two disagree THAT FILE WINS.
//
// LAB STATUS -- SKELETON ONLY (plan task 5). This file has the closed
// parameter list and the 5-minute timeframe guard. No regime computation, no
// entries, no exits, no orders and no drawing yet -- those are tasks 6 and 7.
#region Using declarations
using System;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class DriftVwapPullbackStrategy : Strategy
    {
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
