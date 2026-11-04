// GOF - per-bar footprint, bar history, cumulative delta and bar statistics.
using System;
using System.Collections.Generic;

namespace NinjaTrader.NinjaScript.AddOns.GoldOrderFlow
{
    /// <summary>Executed volume at price for ONE bar, split by aggressor side.</summary>
    public sealed class BarFootprint
    {
        public int Bar;
        public DateTime Time;
        public readonly PriceLadder Ladder = new PriceLadder(32);
        /// <summary>Number of executed prints (trades as reported by the feed) per price and aggressor side.
        /// A print count is NOT an order count: one aggressive order can fill as several prints, and resting
        /// (passive) orders are not visible in trade data.</summary>
        public readonly PriceLadder Trades = new PriceLadder(32);
        public int Prints;
        public long ExactVolume;
        public long InsideVolume;
        public long EstimatedVolume;
        public long MaxPrint;
        public long LargeBuyVolume;
        public long LargeSellVolume;
        public int LargeBuyCount;
        public int LargeSellCount;
        public long CvdOpen, CvdHigh, CvdLow, CvdClose;
        public bool HasCvd;

        public long Buy { get { return Ladder.TotalBuy; } }
        public long Sell { get { return Ladder.TotalSell; } }
        public long Volume { get { return Ladder.TotalVolume; } }
        public long Delta { get { return Ladder.Delta; } }
        public double ExactShare { get { long v = Volume; return v == 0 ? 0 : (double)ExactVolume / v; } }
        public double EstimatedShare { get { long v = Volume; return v == 0 ? 0 : (double)EstimatedVolume / v; } }
    }

    /// <summary>Keeps the most recent N bar footprints (memory bound).</summary>
    public sealed class FootprintStore
    {
        private readonly Dictionary<int, BarFootprint> map = new Dictionary<int, BarFootprint>();
        private readonly Queue<int> order = new Queue<int>();
        public int MaxBars = 3000;

        public BarFootprint GetOrCreate(int bar, DateTime time)
        {
            BarFootprint f;
            if (map.TryGetValue(bar, out f)) return f;
            f = new BarFootprint();
            f.Bar = bar;
            f.Time = time;
            map[bar] = f;
            order.Enqueue(bar);
            while (order.Count > Math.Max(10, MaxBars)) map.Remove(order.Dequeue());
            return f;
        }

        public BarFootprint Get(int bar)
        {
            BarFootprint f;
            return map.TryGetValue(bar, out f) ? f : null;
        }

        public int OldestBar { get { return order.Count > 0 ? order.Peek() : -1; } }
    }

    /// <summary>Finalized bar values used by the analytic engines. Values never change after finalization.</summary>
    public sealed class BarRecord
    {
        public int Bar;
        public DateTime Time;
        public double Open, High, Low, Close;
        public long Volume, Buy, Sell, Delta;
        public double DeltaPct;
        public long CvdOpen, CvdHigh, CvdLow, CvdClose;
        public int CvdResetId;
        public long SessionCvd, DayCvd, WeekCvd;
        public double AtrTicks;
        public double DayPoc = double.NaN, DayVah = double.NaN, DayVal = double.NaN, WeekPoc = double.NaN;
        public double ExactShare;
        public double VolumeRatio = double.NaN;   // volume / time-of-day baseline
        public double DeltaRatio = double.NaN;    // |delta| / time-of-day baseline of |delta|
        public double SellRatio = double.NaN;     // sell volume / baseline side volume
        public double BuyRatio = double.NaN;
        public int BuyStacks, SellStacks;
        public bool VolumeSpike, DeltaSpike;
        public int Exhaustion;                    // +1 = buying exhaustion at highs, -1 = selling exhaustion at lows
        public string Window = "-";
        public bool NewsBlackout;
        public long LargeBuyVolume, LargeSellVolume;
    }

    public sealed class BarHistory
    {
        private readonly List<BarRecord> list = new List<BarRecord>();
        private int firstBar = -1;
        public int MaxRecords = 20000;

        public void Add(BarRecord r)
        {
            if (list.Count > 0 && r.Bar != list[list.Count - 1].Bar + 1)
            {
                // non-contiguous (should not happen) -> restart history to keep indexing exact
                list.Clear();
                firstBar = -1;
            }
            if (list.Count == 0) firstBar = r.Bar;
            list.Add(r);
            if (list.Count > MaxRecords)
            {
                int remove = list.Count - MaxRecords;
                list.RemoveRange(0, remove);
                firstBar += remove;
            }
        }

        public BarRecord Get(int bar)
        {
            if (firstBar < 0) return null;
            int i = bar - firstBar;
            return (i >= 0 && i < list.Count) ? list[i] : null;
        }

        public BarRecord Last { get { return list.Count > 0 ? list[list.Count - 1] : null; } }
        public int Count { get { return list.Count; } }
        public int FirstBar { get { return firstBar; } }
        public int LastBar { get { return list.Count > 0 ? list[list.Count - 1].Bar : -1; } }
    }

    /// <summary>
    /// Delta = aggressive buy volume - aggressive sell volume. CVD is kept for every reset scope simultaneously
    /// (session / trading day / week / continuous); DisplayMode selects the one written to BarRecord.Cvd*.
    /// </summary>
    public sealed class DeltaEngine
    {
        public CvdResetMode DisplayMode = CvdResetMode.Session;
        public long SessionCvd;
        public long DayCvd;
        public long WeekCvd;
        public long ContinuousCvd;
        public int ResetId;
        public long SessionBuy, SessionSell, SessionVolume;

        public long DisplayCvd
        {
            get
            {
                switch (DisplayMode)
                {
                    case CvdResetMode.Session: return SessionCvd;
                    case CvdResetMode.TradingDay: return DayCvd;
                    case CvdResetMode.Week: return WeekCvd;
                    default: return ContinuousCvd;
                }
            }
        }

        public void OnSessionStart(bool newDay, bool newWeek)
        {
            SessionCvd = 0;
            SessionBuy = SessionSell = SessionVolume = 0;
            if (newDay) DayCvd = 0;
            if (newWeek) WeekCvd = 0;
            bool displayReset = DisplayMode == CvdResetMode.Session ||
                                (DisplayMode == CvdResetMode.TradingDay && newDay) ||
                                (DisplayMode == CvdResetMode.Week && newWeek);
            if (displayReset) ResetId++;
        }

        public void BeginBar(BarFootprint f)
        {
            long c = DisplayCvd;
            f.CvdOpen = f.CvdHigh = f.CvdLow = f.CvdClose = c;
            f.HasCvd = true;
        }

        public void OnTrade(ref TradePrint t, BarFootprint f)
        {
            SessionVolume += t.Volume;
            long d = 0;
            if (t.Side == AggressorSide.Buy) { d = t.Volume; SessionBuy += t.Volume; }
            else if (t.Side == AggressorSide.Sell) { d = -t.Volume; SessionSell += t.Volume; }
            if (d == 0) return;
            SessionCvd += d; DayCvd += d; WeekCvd += d; ContinuousCvd += d;
            if (f != null)
            {
                if (!f.HasCvd) BeginBar(f);
                long c = DisplayCvd;
                f.CvdClose = c;
                if (c > f.CvdHigh) f.CvdHigh = c;
                if (c < f.CvdLow) f.CvdLow = c;
            }
        }
    }

    /// <summary>
    /// Time-of-day baseline: exponentially weighted mean of a bar metric per time-of-day bucket, so Asia-session
    /// bars are compared with Asia-session bars and NY-open bars with NY-open bars. Only past bars are used:
    /// the ratio is computed BEFORE the current bar is added.
    /// </summary>
    public sealed class TimeOfDayBaseline
    {
        private readonly double[] mean;
        private readonly int[] count;
        private readonly int bucketMinutes;
        private readonly double alpha;
        public int MinSamples = 5;

        public TimeOfDayBaseline(int bucketMinutes, double alpha)
        {
            this.bucketMinutes = Math.Max(1, bucketMinutes);
            int n = (24 * 60) / this.bucketMinutes + 1;
            mean = new double[n];
            count = new int[n];
            this.alpha = PriceMath.Clamp(alpha, 0.01, 1.0);
        }

        public int Bucket(DateTime localTime) { return (int)(localTime.TimeOfDay.TotalMinutes / bucketMinutes); }

        public double Baseline(int bucket)
        {
            if (bucket < 0 || bucket >= mean.Length || count[bucket] < MinSamples) return double.NaN;
            return mean[bucket];
        }

        public void Add(int bucket, double value)
        {
            if (bucket < 0 || bucket >= mean.Length) return;
            if (count[bucket] == 0) mean[bucket] = value;
            else mean[bucket] += alpha * (value - mean[bucket]);
            count[bucket]++;
        }
    }

    public sealed class BarAnalyticsSettings
    {
        public int RollingBars = 50;
        public int TodBucketMinutes = 15;
        public double TodAlpha = 0.15;
        public double VolumeSpikeRatio = 2.5;
        public double DeltaSpikeRatio = 2.5;
        public int AtrPeriod = 14;
        public int ExhaustionLookback = 10;     // bar must make a new N-bar extreme
        public int ExhaustionTopTicks = 2;      // extreme ticks inspected
        public double ExhaustionMaxShare = 0.05;// aggressive volume at the extreme ticks / bar volume
        public double ExhaustionDeltaFade = 0.5;// |delta| must be < fade * mean |delta| of the previous 5 bars
        public double ImbalanceRatio = 3.0;     // 300 %
        public long ImbalanceMinVolume = 5;
        public int StackedCount = 3;
    }

    public sealed class StackedImbalanceZone
    {
        public int Bar;
        public int Side;        // +1 buy imbalance (ask side), -1 sell imbalance (bid side)
        public long LowTick;
        public long HighTick;
        public int Count;
    }

    /// <summary>Diagonal bid/ask imbalance, spikes, exhaustion and ATR. Everything uses data up to the bar only.</summary>
    public sealed class BarAnalytics
    {
        public BarAnalyticsSettings S = new BarAnalyticsSettings();
        private TimeOfDayBaseline volBase;
        private TimeOfDayBaseline deltaBase;
        private RollingWindow rollVol;
        private RollingWindow rollAbsDelta;
        private readonly Queue<long> recentAbsDelta = new Queue<long>();
        private double atr = double.NaN;
        private double prevClose = double.NaN;

        public BarAnalytics() { Init(); }

        public void Init()
        {
            volBase = new TimeOfDayBaseline(S.TodBucketMinutes, S.TodAlpha);
            deltaBase = new TimeOfDayBaseline(S.TodBucketMinutes, S.TodAlpha);
            rollVol = new RollingWindow(S.RollingBars);
            rollAbsDelta = new RollingWindow(S.RollingBars);
            recentAbsDelta.Clear();
            atr = double.NaN;
            prevClose = double.NaN;
        }

        public double AtrTicks { get { return atr; } }

        public void Analyze(BarRecord r, BarFootprint f, DateTime localTime, BarHistory history, double tickSize, List<StackedImbalanceZone> zonesOut)
        {
            // ---- ATR (Wilder) in ticks
            double tr = (r.High - r.Low) / tickSize;
            if (!double.IsNaN(prevClose))
                tr = Math.Max(tr, Math.Max(Math.Abs(r.High - prevClose), Math.Abs(r.Low - prevClose)) / tickSize);
            atr = double.IsNaN(atr) ? tr : atr + (tr - atr) / Math.Max(1, S.AtrPeriod);
            prevClose = r.Close;
            r.AtrTicks = atr;

            // ---- spikes vs time-of-day baseline (fallback: rolling window), computed BEFORE adding this bar
            int bucket = volBase.Bucket(localTime);
            double vb = volBase.Baseline(bucket);
            if (double.IsNaN(vb)) vb = rollVol.Count >= 10 ? rollVol.Mean : double.NaN;
            double db = deltaBase.Baseline(bucket);
            if (double.IsNaN(db)) db = rollAbsDelta.Count >= 10 ? rollAbsDelta.Mean : double.NaN;

            r.VolumeRatio = (!double.IsNaN(vb) && vb > 0) ? r.Volume / vb : double.NaN;
            r.DeltaRatio = (!double.IsNaN(db) && db > 0) ? Math.Abs(r.Delta) / db : double.NaN;
            r.BuyRatio = (!double.IsNaN(vb) && vb > 0) ? r.Buy / (vb * 0.5) : double.NaN;
            r.SellRatio = (!double.IsNaN(vb) && vb > 0) ? r.Sell / (vb * 0.5) : double.NaN;
            r.VolumeSpike = !double.IsNaN(r.VolumeRatio) && r.VolumeRatio >= S.VolumeSpikeRatio;
            r.DeltaSpike = !double.IsNaN(r.DeltaRatio) && r.DeltaRatio >= S.DeltaSpikeRatio;

            // ---- exhaustion
            r.Exhaustion = 0;
            if (f != null && f.Ladder.HasData && r.Volume > 0)
            {
                double meanRecent = 0;
                if (recentAbsDelta.Count > 0) { foreach (long v in recentAbsDelta) meanRecent += v; meanRecent /= recentAbsDelta.Count; }
                bool newHigh = true, newLow = true;
                for (int k = 1; k <= S.ExhaustionLookback; k++)
                {
                    BarRecord p = history.Get(r.Bar - k);
                    if (p == null) { newHigh = newLow = false; break; }
                    if (p.High >= r.High) newHigh = false;
                    if (p.Low <= r.Low) newLow = false;
                }
                bool fade = recentAbsDelta.Count >= 3 && Math.Abs(r.Delta) < S.ExhaustionDeltaFade * meanRecent;
                if (newHigh && fade)
                {
                    long top = 0;
                    for (int k = 0; k < S.ExhaustionTopTicks; k++) top += f.Ladder.BuyAt(f.Ladder.MaxTick - k);
                    if ((double)top / r.Volume <= S.ExhaustionMaxShare) r.Exhaustion = +1;
                }
                if (newLow && fade)
                {
                    long bot = 0;
                    for (int k = 0; k < S.ExhaustionTopTicks; k++) bot += f.Ladder.SellAt(f.Ladder.MinTick + k);
                    if ((double)bot / r.Volume <= S.ExhaustionMaxShare) r.Exhaustion = -1;
                }
            }

            // ---- diagonal imbalance + stacking
            r.BuyStacks = r.SellStacks = 0;
            if (f != null && f.Ladder.HasData) DetectImbalances(r, f, zonesOut);

            // ---- update baselines (after use)
            volBase.Add(bucket, r.Volume);
            deltaBase.Add(bucket, Math.Abs(r.Delta));
            rollVol.Add(r.Volume);
            rollAbsDelta.Add(Math.Abs(r.Delta));
            recentAbsDelta.Enqueue(Math.Abs(r.Delta));
            while (recentAbsDelta.Count > 5) recentAbsDelta.Dequeue();
        }

        /// <summary>
        /// Buy imbalance at P : askVol(P) >= ratio * bidVol(P - 1 tick)  and askVol(P) >= min volume.
        /// Sell imbalance at P: bidVol(P) >= ratio * askVol(P + 1 tick) and bidVol(P) >= min volume.
        /// Stacked = >= StackedCount consecutive imbalanced prices on the same side.
        /// </summary>
        private void DetectImbalances(BarRecord r, BarFootprint f, List<StackedImbalanceZone> zonesOut)
        {
            PriceLadder l = f.Ladder;
            int runBuy = 0, runSell = 0;
            long runBuyStart = 0, runSellStart = 0;
            for (long t = l.MinTick; t <= l.MaxTick + 1; t++)
            {
                bool bi = false, si = false;
                if (t <= l.MaxTick)
                {
                    long ask = l.BuyAt(t), bidBelow = l.SellAt(t - 1);
                    bi = ask >= S.ImbalanceMinVolume && ask >= S.ImbalanceRatio * bidBelow;
                    long bid = l.SellAt(t), askAbove = l.BuyAt(t + 1);
                    si = bid >= S.ImbalanceMinVolume && bid >= S.ImbalanceRatio * askAbove;
                }
                if (bi) { if (runBuy == 0) runBuyStart = t; runBuy++; }
                else
                {
                    if (runBuy >= S.StackedCount) { r.BuyStacks++; AddZone(zonesOut, r.Bar, +1, runBuyStart, t - 1, runBuy); }
                    runBuy = 0;
                }
                if (si) { if (runSell == 0) runSellStart = t; runSell++; }
                else
                {
                    if (runSell >= S.StackedCount) { r.SellStacks++; AddZone(zonesOut, r.Bar, -1, runSellStart, t - 1, runSell); }
                    runSell = 0;
                }
            }
        }

        private static void AddZone(List<StackedImbalanceZone> zones, int bar, int side, long lo, long hi, int count)
        {
            if (zones == null) return;
            StackedImbalanceZone z = new StackedImbalanceZone();
            z.Bar = bar; z.Side = side; z.LowTick = lo; z.HighTick = hi; z.Count = count;
            zones.Add(z);
        }
    }
}
