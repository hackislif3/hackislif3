// DrTrader - Level 2 order book bookkeeping and time snapshots for the DOM heatmap.
// REALTIME ONLY: NinjaTrader does not provide historical market depth on charts (Tick Replay replays trades,
// not the order book). Playback of data recorded with "Record live data for playback" does replay depth.
using System;
using System.Collections.Generic;

namespace NinjaTrader.NinjaScript.AddOns.DrTrader
{
    public enum DepthOperation { Add = 0, Update = 1, Remove = 2 }

    /// <summary>
    /// Position-based order book, mirroring how NinjaTrader delivers depth (OnMarketDepth: Add / Update / Remove
    /// at a ladder position). Position 0 is the best bid / best ask.
    /// </summary>
    public sealed class DepthBook
    {
        private readonly List<long> bidTicks = new List<long>();
        private readonly List<long> bidSizes = new List<long>();
        private readonly List<long> askTicks = new List<long>();
        private readonly List<long> askSizes = new List<long>();
        public int MaxLevels = 50;
        public long Version;

        public int BidCount { get { return bidTicks.Count; } }
        public int AskCount { get { return askTicks.Count; } }
        public long BidTick(int i) { return bidTicks[i]; }
        public long BidSize(int i) { return bidSizes[i]; }
        public long AskTick(int i) { return askTicks[i]; }
        public long AskSize(int i) { return askSizes[i]; }

        public void Clear()
        {
            bidTicks.Clear(); bidSizes.Clear(); askTicks.Clear(); askSizes.Clear();
            Version++;
        }

        public void Apply(bool isBid, DepthOperation op, int position, long tick, long size)
        {
            if (position < 0) return;
            List<long> ticks = isBid ? bidTicks : askTicks;
            List<long> sizes = isBid ? bidSizes : askSizes;
            switch (op)
            {
                case DepthOperation.Add:
                    if (position <= ticks.Count) { ticks.Insert(position, tick); sizes.Insert(position, size); }
                    else { ticks.Add(tick); sizes.Add(size); }
                    if (ticks.Count > MaxLevels) { ticks.RemoveAt(ticks.Count - 1); sizes.RemoveAt(sizes.Count - 1); }
                    break;
                case DepthOperation.Update:
                    if (position < ticks.Count) { ticks[position] = tick; sizes[position] = size; }
                    else if (position == ticks.Count && ticks.Count < MaxLevels) { ticks.Add(tick); sizes.Add(size); }
                    break;
                default:
                    if (position < ticks.Count) { ticks.RemoveAt(position); sizes.RemoveAt(position); }
                    break;
            }
            Version++;
        }

        public long MaxSize
        {
            get
            {
                long m = 0;
                for (int i = 0; i < bidSizes.Count; i++) if (bidSizes[i] > m) m = bidSizes[i];
                for (int i = 0; i < askSizes.Count; i++) if (askSizes[i] > m) m = askSizes[i];
                return m;
            }
        }

        /// <summary>Copy of the current book. Side: 1 = bid, 2 = ask. Levels with size 0 are skipped.</summary>
        public HeatmapColumn Snapshot(int bar, DateTime time, long lastTradeTick)
        {
            int n = 0;
            for (int i = 0; i < bidSizes.Count; i++) if (bidSizes[i] > 0) n++;
            for (int i = 0; i < askSizes.Count; i++) if (askSizes[i] > 0) n++;
            HeatmapColumn c = new HeatmapColumn();
            c.Bar = bar; c.Time = time; c.LastTick = lastTradeTick;
            c.Ticks = new long[n]; c.Sizes = new long[n]; c.Side = new byte[n];
            int k = 0;
            for (int i = 0; i < bidSizes.Count; i++) if (bidSizes[i] > 0) { c.Ticks[k] = bidTicks[i]; c.Sizes[k] = bidSizes[i]; c.Side[k] = 1; k++; }
            for (int i = 0; i < askSizes.Count; i++) if (askSizes[i] > 0) { c.Ticks[k] = askTicks[i]; c.Sizes[k] = askSizes[i]; c.Side[k] = 2; k++; }
            return c;
        }
    }

    /// <summary>One point in time of the book (resting limit orders, NOT executed volume).</summary>
    public sealed class HeatmapColumn
    {
        public int Bar;
        public DateTime Time;
        public long LastTick;      // last traded price at the snapshot (long.MinValue = unknown)
        public long[] Ticks;
        public long[] Sizes;
        public byte[] Side;        // 1 = bid, 2 = ask
    }

    /// <summary>Bounded, time-ordered list of book snapshots with bar lookup and an adaptive colour scale.</summary>
    public sealed class HeatmapStore
    {
        private readonly List<HeatmapColumn> cols = new List<HeatmapColumn>();
        public int MaxColumns = 60000;

        public int Count { get { return cols.Count; } }
        public HeatmapColumn this[int i] { get { return cols[i]; } }

        public void Add(HeatmapColumn c)
        {
            if (c == null) return;
            cols.Add(c);
            // trim in chunks so adding stays cheap
            if (cols.Count > MaxColumns + MaxColumns / 10) cols.RemoveRange(0, cols.Count - MaxColumns);
        }

        public void Clear() { cols.Clear(); }

        /// <summary>Index of the first snapshot whose bar is >= bar (Count if none).</summary>
        public int FirstIndexAtOrAfter(int bar)
        {
            int lo = 0, hi = cols.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (cols[mid].Bar < bar) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        /// <summary>
        /// Size at the given percentile among snapshots of the bar range (sampled to at most ~maxSamples values).
        /// Used as the "hot" end of the colour scale so one huge order does not wash out everything else.
        /// </summary>
        public double ScaleFor(int fromBar, int toBar, double percentile, int maxSamples)
        {
            int a = FirstIndexAtOrAfter(fromBar), b = FirstIndexAtOrAfter(toBar + 1);
            if (b <= a) return 0;
            long cells = 0;
            for (int i = a; i < b; i++) cells += cols[i].Sizes.Length;
            int stride = (int)Math.Max(1, cells / Math.Max(100, maxSamples));
            List<long> vals = new List<long>();
            long k = 0;
            for (int i = a; i < b; i++)
            {
                long[] s = cols[i].Sizes;
                for (int j = 0; j < s.Length; j++, k++) if (k % stride == 0) vals.Add(s[j]);
            }
            if (vals.Count == 0) return 0;
            vals.Sort();
            int idx = (int)Math.Round((vals.Count - 1) * Math.Max(0, Math.Min(100, percentile)) / 100.0);
            return Math.Max(1, vals[idx]);
        }
    }
}
