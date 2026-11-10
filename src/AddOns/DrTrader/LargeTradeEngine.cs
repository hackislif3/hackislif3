// GOF - Large trade detection: print aggregation, manual/adaptive thresholds, clusters, repeated execution.
using System;
using System.Collections.Generic;

namespace NinjaTrader.NinjaScript.AddOns.DrTrader
{
    public sealed class LargeTradeSettings
    {
        public LargeTradeThresholdMode Mode = LargeTradeThresholdMode.MaxOfManualAndAdaptive;
        public int ManualThreshold = 20;
        public double Percentile = 99.0;            // adaptive: percentile of aggregated execution sizes
        public int MinSamples = 500;                // samples needed before the adaptive value is trusted
        public int BucketMinutes = 30;              // time-of-day bucket (exchange local time)
        public double DailyDecay = 0.85;            // histogram weights multiplied at each new trading day
        public double RvolExponent = 0.25;          // threshold *= clamp(RVOL^exp, 0.75, 1.5); 0 = off
        public int AggregationMs = 5;               // same-side prints within N ms -> one aggregated execution; <0 = off
        public int ClusterWindowMs = 3000;
        public int ClusterMaxRangeTicks = 10;
        public int ClusterMinCount = 2;
        public int RepeatWindowSeconds = 60;
        public int RepeatMinVolume = 100;
        public double RepeatThresholdMultiple = 4.0;// repeat min volume = max(RepeatMinVolume, multiple * large threshold)
        public int RepeatMinPrints = 10;
        public int MaxEvents = 5000;
    }

    public sealed class LargeTradeEvent
    {
        public DateTime Time;
        public DateTime EndTime;
        public int Bar;
        public int Side;
        public long Volume;
        public int Prints;
        public long FirstTick;
        public long LastTick;
        public double Vwap;
        public double Threshold;
        public ThresholdSource ThresholdSource;
        public bool Estimated;      // at least one print classified by tick rule
    }

    public sealed class LargeTradeCluster
    {
        public DateTime StartTime, EndTime;
        public int StartBar, EndBar;
        public int Side;
        public int Count;
        public long Volume;
        public long LowTick, HighTick;
    }

    public sealed class RepeatedExecutionEvent
    {
        public DateTime Time;
        public DateTime WindowStart;
        public int Bar;
        public long Tick;
        public int Side;
        public long Volume;
        public int Prints;
    }

    /// <summary>Histogram of execution sizes with exact percentile and exponential daily decay.</summary>
    public sealed class SizeHistogram
    {
        private const int MaxBin = 2000;
        private readonly double[] bins = new double[MaxBin + 1];
        private double total;
        private int rawSamples;
        private int sinceRecompute = int.MaxValue / 2;
        private double cachedPct = double.NaN;
        private double cachedValue = double.NaN;

        public int RawSamples { get { return rawSamples; } }
        public double Weight { get { return total; } }

        public void Add(long size)
        {
            int b = (int)Math.Min(MaxBin, Math.Max(1, size));
            bins[b] += 1.0;
            total += 1.0;
            rawSamples++;
            sinceRecompute++;
        }

        public void Decay(double f)
        {
            for (int i = 0; i < bins.Length; i++) bins[i] *= f;
            total *= f;
            sinceRecompute = int.MaxValue / 2;
        }

        public double Percentile(double pct)
        {
            if (total <= 0) return double.NaN;
            if (pct == cachedPct && sinceRecompute < 100) return cachedValue;
            double target = total * PriceMath.Clamp(pct, 0, 100) / 100.0;
            double acc = 0;
            double value = MaxBin;
            for (int i = 1; i <= MaxBin; i++)
            {
                acc += bins[i];
                if (acc >= target) { value = i; break; }
            }
            cachedPct = pct;
            cachedValue = value;
            sinceRecompute = 0;
            return value;
        }
    }

    public sealed class LargeTradeEngine
    {
        public LargeTradeSettings S = new LargeTradeSettings();
        public readonly List<LargeTradeEvent> Events = new List<LargeTradeEvent>();
        public readonly List<LargeTradeCluster> Clusters = new List<LargeTradeCluster>();
        public readonly List<RepeatedExecutionEvent> Repeats = new List<RepeatedExecutionEvent>();
        public int ConsecutiveSide;
        public int ConsecutiveCount;
        public double LastThreshold = double.NaN;
        public ThresholdSource LastThresholdSource = ThresholdSource.Manual;

        private SizeHistogram[] buckets;
        private readonly SizeHistogram global = new SizeHistogram();

        // aggregation state
        private bool aggActive;
        private int aggSide;
        private int aggBar;
        private DateTime aggStart, aggLast;
        private long aggVol;
        private int aggPrints;
        private long aggFirstTick, aggLastTick;
        private double aggNotional;
        private bool aggEstimated;
        private int aggBucket;
        private double aggRvol = double.NaN;

        // cluster state
        private LargeTradeCluster open;

        // repeated execution state
        private sealed class RepeatCell
        {
            public DateTime Start;
            public long BuyVol, SellVol;
            public int BuyPrints, SellPrints;
            public bool BuyEmitted, SellEmitted;
        }
        private readonly Dictionary<long, RepeatCell> cells = new Dictionary<long, RepeatCell>();

        public LargeTradeEngine() { Init(); }

        public void Init()
        {
            int n = (24 * 60) / Math.Max(1, S.BucketMinutes) + 1;
            buckets = new SizeHistogram[n];
            for (int i = 0; i < n; i++) buckets[i] = new SizeHistogram();
        }

        public void OnNewTradingDay()
        {
            for (int i = 0; i < buckets.Length; i++) buckets[i].Decay(S.DailyDecay);
            global.Decay(S.DailyDecay);
        }

        public int BucketOf(DateTime exchangeLocal)
        {
            int b = (int)(exchangeLocal.TimeOfDay.TotalMinutes / Math.Max(1, S.BucketMinutes));
            return Math.Min(buckets.Length - 1, Math.Max(0, b));
        }

        /// <summary>Threshold for an execution in the given bucket using ONLY previously seen executions.</summary>
        public double CurrentThreshold(int bucket, double rvol, out ThresholdSource src)
        {
            double manual = Math.Max(1, S.ManualThreshold);
            if (S.Mode == LargeTradeThresholdMode.Manual) { src = ThresholdSource.Manual; return manual; }

            double adaptive = double.NaN;
            SizeHistogram h = buckets[Math.Min(buckets.Length - 1, Math.Max(0, bucket))];
            if (h.RawSamples >= S.MinSamples) adaptive = h.Percentile(S.Percentile);
            else if (global.RawSamples >= S.MinSamples) adaptive = global.Percentile(S.Percentile);

            if (double.IsNaN(adaptive)) { src = ThresholdSource.ManualWarmup; return manual; }
            if (S.RvolExponent > 0 && !double.IsNaN(rvol) && rvol > 0)
                adaptive *= PriceMath.Clamp(Math.Pow(rvol, S.RvolExponent), 0.75, 1.5);
            adaptive = Math.Max(2, Math.Ceiling(adaptive));
            src = ThresholdSource.Adaptive;
            if (S.Mode == LargeTradeThresholdMode.MaxOfManualAndAdaptive) return Math.Max(manual, adaptive);
            return adaptive;
        }

        /// <summary>Feed every classified print. Returns completed large trades through the output lists.</summary>
        public void OnPrint(ref TradePrint t, int bar, int bucket, double rvol, BarFootprint fp,
                            List<LargeTradeEvent> newLarge, List<LargeTradeCluster> newClusters, List<RepeatedExecutionEvent> newRepeats)
        {
            int side = (int)t.Side;
            // cluster timeout is evaluated on print timestamps (deterministic historical == realtime)
            if (open != null && (t.Time - open.EndTime).TotalMilliseconds > S.ClusterWindowMs) CloseCluster(newClusters);

            if (side != 0)
            {
                bool extend = aggActive && S.AggregationMs >= 0 && side == aggSide && bar == aggBar &&
                              (t.Time - aggLast).TotalMilliseconds <= S.AggregationMs &&
                              (side > 0 ? t.Tick >= aggLastTick : t.Tick <= aggLastTick);
                if (!extend)
                {
                    FinalizeAggregate(fp, newLarge, newClusters);
                    aggActive = true;
                    aggSide = side;
                    aggBar = bar;
                    aggStart = t.Time;
                    aggVol = 0;
                    aggPrints = 0;
                    aggFirstTick = t.Tick;
                    aggNotional = 0;
                    aggEstimated = false;
                    aggBucket = bucket;
                    aggRvol = rvol;
                }
                aggLast = t.Time;
                aggVol += t.Volume;
                aggPrints++;
                aggLastTick = t.Tick;
                aggNotional += (double)t.Tick * t.Volume;
                if (t.Source == ClassificationSource.TickRule) aggEstimated = true;
                if (S.AggregationMs < 0) FinalizeAggregate(fp, newLarge, newClusters);

                UpdateRepeat(ref t, bar, side, newRepeats);
            }
        }

        /// <summary>Call at bar finalization so an aggregate never spans two bars.</summary>
        public void FlushBar(BarFootprint fp, List<LargeTradeEvent> newLarge, List<LargeTradeCluster> newClusters)
        {
            FinalizeAggregate(fp, newLarge, newClusters);
            // purge stale repeat cells
            if (cells.Count > 512)
            {
                List<long> dead = new List<long>();
                DateTime newest = DateTime.MinValue;
                foreach (KeyValuePair<long, RepeatCell> kv in cells) if (kv.Value.Start > newest) newest = kv.Value.Start;
                foreach (KeyValuePair<long, RepeatCell> kv in cells)
                    if ((newest - kv.Value.Start).TotalSeconds > 2 * S.RepeatWindowSeconds) dead.Add(kv.Key);
                for (int i = 0; i < dead.Count; i++) cells.Remove(dead[i]);
            }
        }

        private void FinalizeAggregate(BarFootprint fp, List<LargeTradeEvent> newLarge, List<LargeTradeCluster> newClusters)
        {
            if (!aggActive) return;
            aggActive = false;
            ThresholdSource src;
            double thr = CurrentThreshold(aggBucket, aggRvol, out src);
            LastThreshold = thr;
            LastThresholdSource = src;

            if (aggVol >= thr)
            {
                LargeTradeEvent e = new LargeTradeEvent();
                e.Time = aggStart; e.EndTime = aggLast; e.Bar = aggBar; e.Side = aggSide; e.Volume = aggVol;
                e.Prints = aggPrints; e.FirstTick = aggFirstTick; e.LastTick = aggLastTick;
                e.Vwap = aggNotional / Math.Max(1, aggVol);
                e.Threshold = thr; e.ThresholdSource = src; e.Estimated = aggEstimated;
                Events.Add(e);
                if (Events.Count > S.MaxEvents) Events.RemoveAt(0);
                if (newLarge != null) newLarge.Add(e);
                if (fp != null && fp.Bar == aggBar)
                {
                    if (aggSide > 0) { fp.LargeBuyVolume += aggVol; fp.LargeBuyCount++; }
                    else { fp.LargeSellVolume += aggVol; fp.LargeSellCount++; }
                }
                if (aggSide == ConsecutiveSide) ConsecutiveCount++;
                else { ConsecutiveSide = aggSide; ConsecutiveCount = 1; }
                AddToCluster(e, newClusters);
            }
            // learn AFTER evaluating (strictly past-only threshold)
            buckets[aggBucket].Add(aggVol);
            global.Add(aggVol);
        }

        private void AddToCluster(LargeTradeEvent e, List<LargeTradeCluster> newClusters)
        {
            if (open != null)
            {
                long lo = Math.Min(open.LowTick, Math.Min(e.FirstTick, e.LastTick));
                long hi = Math.Max(open.HighTick, Math.Max(e.FirstTick, e.LastTick));
                bool fits = e.Side == open.Side && (e.Time - open.EndTime).TotalMilliseconds <= S.ClusterWindowMs &&
                            hi - lo <= S.ClusterMaxRangeTicks;
                if (fits)
                {
                    open.Count++; open.Volume += e.Volume; open.EndTime = e.EndTime; open.EndBar = e.Bar;
                    open.LowTick = lo; open.HighTick = hi;
                    return;
                }
                CloseCluster(newClusters);
            }
            open = new LargeTradeCluster();
            open.StartTime = e.Time; open.EndTime = e.EndTime; open.StartBar = e.Bar; open.EndBar = e.Bar;
            open.Side = e.Side; open.Count = 1; open.Volume = e.Volume;
            open.LowTick = Math.Min(e.FirstTick, e.LastTick); open.HighTick = Math.Max(e.FirstTick, e.LastTick);
        }

        private void CloseCluster(List<LargeTradeCluster> newClusters)
        {
            if (open == null) return;
            if (open.Count >= S.ClusterMinCount)
            {
                Clusters.Add(open);
                if (Clusters.Count > S.MaxEvents) Clusters.RemoveAt(0);
                if (newClusters != null) newClusters.Add(open);
            }
            open = null;
        }

        private void UpdateRepeat(ref TradePrint t, int bar, int side, List<RepeatedExecutionEvent> newRepeats)
        {
            RepeatCell c;
            if (!cells.TryGetValue(t.Tick, out c) || (t.Time - c.Start).TotalSeconds > S.RepeatWindowSeconds)
            {
                c = new RepeatCell();
                c.Start = t.Time;
                cells[t.Tick] = c;
            }
            double thr = double.IsNaN(LastThreshold) ? S.ManualThreshold : LastThreshold;
            long minVol = (long)Math.Max(S.RepeatMinVolume, S.RepeatThresholdMultiple * thr);
            if (side > 0)
            {
                c.BuyVol += t.Volume; c.BuyPrints++;
                if (!c.BuyEmitted && c.BuyVol >= minVol && c.BuyPrints >= S.RepeatMinPrints)
                { c.BuyEmitted = true; EmitRepeat(t, bar, +1, c.BuyVol, c.BuyPrints, c.Start, newRepeats); }
            }
            else
            {
                c.SellVol += t.Volume; c.SellPrints++;
                if (!c.SellEmitted && c.SellVol >= minVol && c.SellPrints >= S.RepeatMinPrints)
                { c.SellEmitted = true; EmitRepeat(t, bar, -1, c.SellVol, c.SellPrints, c.Start, newRepeats); }
            }
        }

        private void EmitRepeat(TradePrint t, int bar, int side, long vol, int prints, DateTime start, List<RepeatedExecutionEvent> newRepeats)
        {
            RepeatedExecutionEvent r = new RepeatedExecutionEvent();
            r.Time = t.Time; r.WindowStart = start; r.Bar = bar; r.Tick = t.Tick; r.Side = side; r.Volume = vol; r.Prints = prints;
            Repeats.Add(r);
            if (Repeats.Count > S.MaxEvents) Repeats.RemoveAt(0);
            if (newRepeats != null) newRepeats.Add(r);
        }
    }
}
