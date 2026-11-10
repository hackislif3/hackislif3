// GOF - absorption candidates, CVD divergence and (realtime-only) depth replenishment observation.
using System;
using System.Collections.Generic;

namespace NinjaTrader.NinjaScript.AddOns.DrTrader
{
    public sealed class AbsorptionSettings
    {
        public double MinAggressionRatio = 1.8;   // side volume / time-of-day baseline side volume
        public double MinDeltaPct = 0.15;         // |delta| / volume, in the aggressive direction
        public double MaxProgressAtr = 0.30;      // net close-to-close progress in the aggressive direction, in ATRs
        public double MinCloseLocation = 0.50;    // close location in the bar range away from the aggression
        public int LevelTicks = 3;                // ticks at the bar extreme inspected for concentration
        public double MinLevelShare = 0.30;       // aggressive volume at the extreme / bar aggressive volume
        public int ConfirmBars = 3;               // bars allowed for confirmation
        public int InvalidationTicks = 2;         // trading beyond the level by this many ticks invalidates
        public bool RequireRetest = false;
        public int RetestTicks = 3;
        public int MaxCandidates = 2000;
    }

    /// <summary>
    /// An absorption CANDIDATE observed from executed trades: heavy aggression that failed to move price.
    /// It is NOT a detection of an iceberg/hidden order; executed prints alone cannot prove resting size.
    /// State transitions are visible and logged; a candidate is never silently deleted.
    /// </summary>
    public sealed class AbsorptionCandidate
    {
        public int Bar;
        public DateTime Time;
        public int Direction;           // +1 = bullish candidate (selling absorbed at lows), -1 = bearish
        public long LevelTick;          // defended level (bar low for bullish, bar high for bearish)
        public double LevelPrice;
        public double CandidateHigh, CandidateLow, CandidateClose;
        public double AggressionRatio, DeltaPct, ProgressAtr, CloseLocation, LevelShare;
        public long AggressiveVolume;
        public long LargeVolume;
        public bool RepeatedAtLevel;
        public bool Replenished;        // realtime-only depth evidence (if enabled)
        public AbsorptionState State = AbsorptionState.Pending;
        public int ResolvedBar = -1;
        public bool Retested;
        public double Strength;         // 0..1 transparent composite of the metrics above
    }

    public sealed class AbsorptionEngine
    {
        public AbsorptionSettings S = new AbsorptionSettings();
        public readonly List<AbsorptionCandidate> Candidates = new List<AbsorptionCandidate>();
        private readonly List<AbsorptionCandidate> pending = new List<AbsorptionCandidate>();

        /// <summary>
        /// Evaluate bar r (already finalized). First resolves pending candidates using this bar, then tests
        /// this bar for a new candidate. Returns candidates confirmed on this bar.
        /// </summary>
        public void OnBar(BarRecord r, BarFootprint f, BarRecord prev, double tickSize, List<RepeatedExecutionEvent> repeatsThisBar,
                          List<AbsorptionCandidate> created, List<AbsorptionCandidate> confirmed, List<AbsorptionCandidate> invalidated)
        {
            // ---- resolve pending
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                AbsorptionCandidate c = pending[i];
                if (r.Bar <= c.Bar) continue;
                double tol = S.InvalidationTicks * tickSize;
                bool broke = c.Direction > 0 ? r.Low < c.LevelPrice - tol : r.High > c.LevelPrice + tol;
                if (broke)
                {
                    c.State = AbsorptionState.Invalidated; c.ResolvedBar = r.Bar;
                    pending.RemoveAt(i);
                    if (invalidated != null) invalidated.Add(c);
                    continue;
                }
                double rt = S.RetestTicks * tickSize;
                if (c.Direction > 0 && r.Low <= c.LevelPrice + rt) c.Retested = true;
                if (c.Direction < 0 && r.High >= c.LevelPrice - rt) c.Retested = true;

                bool confirm = c.Direction > 0 ? r.Close > c.CandidateHigh : r.Close < c.CandidateLow;
                if (confirm && (!S.RequireRetest || c.Retested))
                {
                    c.State = AbsorptionState.Confirmed; c.ResolvedBar = r.Bar;
                    pending.RemoveAt(i);
                    if (confirmed != null) confirmed.Add(c);
                    continue;
                }
                if (r.Bar - c.Bar >= S.ConfirmBars)
                {
                    c.State = AbsorptionState.Expired; c.ResolvedBar = r.Bar;
                    pending.RemoveAt(i);
                }
            }

            // ---- new candidate on this bar
            if (f == null || !f.Ladder.HasData || r.Volume <= 0 || prev == null || double.IsNaN(r.AtrTicks) || r.AtrTicks <= 0) return;
            double range = r.High - r.Low;
            double closeLocUp = range > 0 ? (r.Close - r.Low) / range : 0.5;
            double progress = (r.Close - prev.Close) / tickSize / r.AtrTicks;   // + up, - down

            // bullish: sellers aggressive, price did not follow down, sell volume concentrated at the low
            if (!double.IsNaN(r.SellRatio) && r.SellRatio >= S.MinAggressionRatio && r.DeltaPct <= -S.MinDeltaPct &&
                -progress <= S.MaxProgressAtr && closeLocUp >= S.MinCloseLocation)
            {
                long atLevel = 0;
                for (int k = 0; k < S.LevelTicks; k++) atLevel += f.Ladder.SellAt(f.Ladder.MinTick + k);
                double share = f.Sell > 0 ? (double)atLevel / f.Sell : 0;
                if (share >= S.MinLevelShare)
                    Create(r, f, +1, f.Ladder.MinTick, r.Low, r.SellRatio, r.DeltaPct, progress, closeLocUp, share, f.Sell, f.LargeSellVolume, repeatsThisBar, created);
            }
            // bearish: buyers aggressive, price could not advance, buy volume concentrated at the high
            if (!double.IsNaN(r.BuyRatio) && r.BuyRatio >= S.MinAggressionRatio && r.DeltaPct >= S.MinDeltaPct &&
                progress <= S.MaxProgressAtr && (1 - closeLocUp) >= S.MinCloseLocation)
            {
                long atLevel = 0;
                for (int k = 0; k < S.LevelTicks; k++) atLevel += f.Ladder.BuyAt(f.Ladder.MaxTick - k);
                double share = f.Buy > 0 ? (double)atLevel / f.Buy : 0;
                if (share >= S.MinLevelShare)
                    Create(r, f, -1, f.Ladder.MaxTick, r.High, r.BuyRatio, r.DeltaPct, progress, 1 - closeLocUp, share, f.Buy, f.LargeBuyVolume, repeatsThisBar, created);
            }
        }

        private void Create(BarRecord r, BarFootprint f, int dir, long levelTick, double levelPrice, double aggr, double dpct,
                            double progress, double closeLoc, double share, long aggVol, long largeVol,
                            List<RepeatedExecutionEvent> repeats, List<AbsorptionCandidate> created)
        {
            AbsorptionCandidate c = new AbsorptionCandidate();
            c.Bar = r.Bar; c.Time = r.Time; c.Direction = dir; c.LevelTick = levelTick; c.LevelPrice = levelPrice;
            c.CandidateHigh = r.High; c.CandidateLow = r.Low; c.CandidateClose = r.Close;
            c.AggressionRatio = aggr; c.DeltaPct = dpct; c.ProgressAtr = progress; c.CloseLocation = closeLoc; c.LevelShare = share;
            c.AggressiveVolume = aggVol; c.LargeVolume = largeVol;
            if (repeats != null)
                for (int i = 0; i < repeats.Count; i++)
                {
                    RepeatedExecutionEvent e = repeats[i];
                    // repeated execution by the ABSORBED side near the defended level
                    if (e.Side == -dir && Math.Abs(e.Tick - levelTick) <= S.LevelTicks) c.RepeatedAtLevel = true;
                }
            c.Strength = (PriceMath.Clamp01((aggr - 1.0) / 2.0) + PriceMath.Clamp01(share / 0.6) +
                          PriceMath.Clamp01(closeLoc) + PriceMath.Clamp01(1.0 - Math.Abs(progress) / Math.Max(0.01, S.MaxProgressAtr)) +
                          (c.RepeatedAtLevel ? 1.0 : 0.0)) / 5.0;
            Candidates.Add(c);
            if (Candidates.Count > S.MaxCandidates) Candidates.RemoveAt(0);
            pending.Add(c);
            if (created != null) created.Add(c);
        }

        /// <summary>Marks pending candidates at a level as replenished (called by the depth monitor in realtime).</summary>
        public void MarkReplenished(long tick, int absorbedSide)
        {
            for (int i = 0; i < pending.Count; i++)
                if (pending[i].Direction == -absorbedSide && Math.Abs(pending[i].LevelTick - tick) <= S.LevelTicks)
                    pending[i].Replenished = true;
        }
    }

    public sealed class DivergenceSettings
    {
        public int PivotStrength = 3;      // bars on each side; divergence is CONFIRMED PivotStrength bars after the pivot
        public int MaxPivotDistance = 60;  // bars between the two pivots compared
        public int MinPriceTicks = 1;      // new high/low must exceed the previous pivot by this many ticks
        public double MinCvdDiff = 0;      // CVD must fail by more than this
        public bool RequireSameCvdPeriod = true;
    }

    public sealed class DivergenceEvent
    {
        public int Direction;      // +1 bullish (price LL, CVD HL), -1 bearish (price HH, CVD LH)
        public int PivotBar;
        public int PrevPivotBar;
        public int ConfirmBar;
        public DateTime ConfirmTime;
        public double Price, PrevPrice;
        public long Cvd, PrevCvd;
    }

    /// <summary>
    /// Price/CVD divergence on confirmed swing pivots. A pivot at bar p is only known at bar p+S, so the event
    /// is stamped at the confirmation bar - it is never back-dated (no repainting).
    /// </summary>
    public sealed class DivergenceEngine
    {
        public DivergenceSettings S = new DivergenceSettings();
        public readonly List<DivergenceEvent> Events = new List<DivergenceEvent>();
        private readonly List<int> pivotHighs = new List<int>();
        private readonly List<int> pivotLows = new List<int>();
        public int LastBullBar = -1000000, LastBearBar = -1000000;

        public void OnBar(BarHistory h, int bar, double tickSize, List<DivergenceEvent> created)
        {
            int st = Math.Max(1, S.PivotStrength);
            int p = bar - st;
            BarRecord pr = h.Get(p);
            if (pr == null) return;
            bool isHigh = true, isLow = true;
            for (int k = 1; k <= st; k++)
            {
                BarRecord a = h.Get(p - k), b = h.Get(p + k);
                if (a == null || b == null) return;
                if (a.High >= pr.High || b.High > pr.High) isHigh = false;
                if (a.Low <= pr.Low || b.Low < pr.Low) isLow = false;
            }
            BarRecord cb = h.Get(bar);
            if (isHigh)
            {
                // compare with the most recent pivot high only
                BarRecord q = pivotHighs.Count > 0 ? h.Get(pivotHighs[pivotHighs.Count - 1]) : null;
                if (q != null && p - q.Bar <= S.MaxPivotDistance && (!S.RequireSameCvdPeriod || q.CvdResetId == pr.CvdResetId) &&
                    pr.High >= q.High + S.MinPriceTicks * tickSize - tickSize * 0.01 && pr.CvdHigh < q.CvdHigh - S.MinCvdDiff)
                {
                    Emit(-1, pr, q, cb, created);
                    LastBearBar = bar;
                }
                pivotHighs.Add(p);
                if (pivotHighs.Count > 50) pivotHighs.RemoveAt(0);
            }
            if (isLow)
            {
                BarRecord q = pivotLows.Count > 0 ? h.Get(pivotLows[pivotLows.Count - 1]) : null;
                if (q != null && p - q.Bar <= S.MaxPivotDistance && (!S.RequireSameCvdPeriod || q.CvdResetId == pr.CvdResetId) &&
                    pr.Low <= q.Low - S.MinPriceTicks * tickSize + tickSize * 0.01 && pr.CvdLow > q.CvdLow + S.MinCvdDiff)
                {
                    Emit(+1, pr, q, cb, created);
                    LastBullBar = bar;
                }
                pivotLows.Add(p);
                if (pivotLows.Count > 50) pivotLows.RemoveAt(0);
            }
        }

        private void Emit(int dir, BarRecord pr, BarRecord q, BarRecord cb, List<DivergenceEvent> created)
        {
            DivergenceEvent e = new DivergenceEvent();
            e.Direction = dir; e.PivotBar = pr.Bar; e.PrevPivotBar = q.Bar;
            e.ConfirmBar = cb != null ? cb.Bar : pr.Bar; e.ConfirmTime = cb != null ? cb.Time : pr.Time;
            e.Price = dir < 0 ? pr.High : pr.Low; e.PrevPrice = dir < 0 ? q.High : q.Low;
            e.Cvd = dir < 0 ? pr.CvdHigh : pr.CvdLow; e.PrevCvd = dir < 0 ? q.CvdHigh : q.CvdLow;
            Events.Add(e);
            if (Events.Count > 2000) Events.RemoveAt(0);
            if (created != null) created.Add(e);
        }
    }

    /// <summary>
    /// REALTIME-ONLY Level-2 observation. NinjaTrader does not replay historical market depth on charts, so this
    /// evidence does not exist on historical bars. "Replenishment" = executed volume at a price on one side
    /// exceeded the largest displayed size seen at that price while the price held. That is consistent with
    /// hidden/iceberg or refreshed liquidity but does not prove it (orders can be added by many participants).
    /// </summary>
    public sealed class DepthReplenishmentMonitor
    {
        public double MinRatio = 2.0;
        public long MinTraded = 50;
        public int WindowSeconds = 60;

        private sealed class Cell
        {
            public DateTime Start;
            public long MaxDisplayed;
            public long Traded;
            public bool Emitted;
        }
        private readonly Dictionary<long, Cell> bid = new Dictionary<long, Cell>();
        private readonly Dictionary<long, Cell> ask = new Dictionary<long, Cell>();

        public void OnDepth(bool isBid, long tick, long size, DateTime time)
        {
            Dictionary<long, Cell> d = isBid ? bid : ask;
            Cell c = GetCell(d, tick, time);
            if (size > c.MaxDisplayed) c.MaxDisplayed = size;
        }

        /// <summary>Trade at the bid (sell aggressor) consumes bid liquidity, and vice versa. Returns true once per window.</summary>
        public bool OnTrade(ref TradePrint t)
        {
            if (t.Side == AggressorSide.Unknown) return false;
            Dictionary<long, Cell> d = t.Side == AggressorSide.Sell ? bid : ask;
            Cell c = GetCell(d, t.Tick, t.Time);
            c.Traded += t.Volume;
            if (!c.Emitted && c.MaxDisplayed > 0 && c.Traded >= MinTraded && c.Traded >= MinRatio * c.MaxDisplayed)
            {
                c.Emitted = true;
                return true;
            }
            return false;
        }

        private Cell GetCell(Dictionary<long, Cell> d, long tick, DateTime time)
        {
            Cell c;
            if (!d.TryGetValue(tick, out c) || (time - c.Start).TotalSeconds > WindowSeconds)
            {
                c = new Cell();
                c.Start = time;
                d[tick] = c;
                if (d.Count > 400) Purge(d, time);
            }
            return c;
        }

        private void Purge(Dictionary<long, Cell> d, DateTime now)
        {
            List<long> dead = new List<long>();
            foreach (KeyValuePair<long, Cell> kv in d) if ((now - kv.Value.Start).TotalSeconds > WindowSeconds) dead.Add(kv.Key);
            for (int i = 0; i < dead.Count; i++) d.Remove(dead[i]);
        }
    }
}
