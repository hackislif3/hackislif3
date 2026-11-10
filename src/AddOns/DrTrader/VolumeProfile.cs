// GOF - Volume Profile engine (volume at price from executed prints, never estimated from OHLC).
using System;
using System.Collections.Generic;

namespace NinjaTrader.NinjaScript.AddOns.DrTrader
{
    /// <summary>Dense, auto-growing volume-at-price ladder keyed by tick index.</summary>
    public sealed class PriceLadder
    {
        private long baseTick;
        private long[] buy;
        private long[] sell;
        private long[] neutral;
        private int capacity;
        private bool hasData;
        private long minTick;
        private long maxTick;
        private long totalBuy;
        private long totalSell;
        private long totalNeutral;
        private int prints;

        public PriceLadder() : this(32) { }

        public PriceLadder(int initialCapacity)
        {
            capacity = Math.Max(8, initialCapacity);
            buy = new long[capacity];
            sell = new long[capacity];
            neutral = new long[capacity];
        }

        public bool HasData { get { return hasData; } }
        public long MinTick { get { return minTick; } }
        public long MaxTick { get { return maxTick; } }
        public long TotalBuy { get { return totalBuy; } }
        public long TotalSell { get { return totalSell; } }
        public long TotalNeutral { get { return totalNeutral; } }
        public long TotalVolume { get { return totalBuy + totalSell + totalNeutral; } }
        public long Delta { get { return totalBuy - totalSell; } }
        public int Prints { get { return prints; } }

        public void Clear()
        {
            Array.Clear(buy, 0, capacity);
            Array.Clear(sell, 0, capacity);
            Array.Clear(neutral, 0, capacity);
            hasData = false;
            totalBuy = totalSell = totalNeutral = 0;
            prints = 0;
            minTick = maxTick = 0;
        }

        public void Add(long tick, long volume, AggressorSide side)
        {
            if (volume <= 0) return;
            AddRaw(tick, side == AggressorSide.Buy ? volume : 0, side == AggressorSide.Sell ? volume : 0,
                   side == AggressorSide.Unknown ? volume : 0);
            prints++;
        }

        public void AddLadder(PriceLadder other)
        {
            if (other == null || !other.hasData) return;
            for (long t = other.minTick; t <= other.maxTick; t++)
            {
                int i = (int)(t - other.baseTick);
                long b = other.buy[i], s = other.sell[i], n = other.neutral[i];
                if (b != 0 || s != 0 || n != 0) AddRaw(t, b, s, n);
            }
            prints += other.prints;
        }

        private void AddRaw(long tick, long b, long s, long n)
        {
            Ensure(tick);
            int i = (int)(tick - baseTick);
            buy[i] += b; sell[i] += s; neutral[i] += n;
            totalBuy += b; totalSell += s; totalNeutral += n;
            if (!hasData) { minTick = maxTick = tick; hasData = true; }
            else
            {
                if (tick < minTick) minTick = tick;
                if (tick > maxTick) maxTick = tick;
            }
        }

        private void Ensure(long tick)
        {
            if (!hasData)
            {
                baseTick = tick - capacity / 2;
                return;
            }
            if (tick >= baseTick && tick < baseTick + capacity) return;

            long newMin = Math.Min(tick, minTick);
            long newMax = Math.Max(tick, maxTick);
            long span = newMax - newMin + 1;
            int newCap = capacity;
            while (newCap < span * 2) newCap *= 2;
            long newBase = newMin - (newCap - span) / 2;

            long[] nb = new long[newCap], ns = new long[newCap], nn = new long[newCap];
            int srcStart = (int)(minTick - baseTick);
            int dstStart = (int)(minTick - newBase);
            int len = (int)(maxTick - minTick + 1);
            Array.Copy(buy, srcStart, nb, dstStart, len);
            Array.Copy(sell, srcStart, ns, dstStart, len);
            Array.Copy(neutral, srcStart, nn, dstStart, len);
            buy = nb; sell = ns; neutral = nn;
            capacity = newCap;
            baseTick = newBase;
        }

        private bool InRange(long tick) { return hasData && tick >= minTick && tick <= maxTick; }

        public long BuyAt(long tick) { return InRange(tick) ? buy[(int)(tick - baseTick)] : 0; }
        public long SellAt(long tick) { return InRange(tick) ? sell[(int)(tick - baseTick)] : 0; }
        public long NeutralAt(long tick) { return InRange(tick) ? neutral[(int)(tick - baseTick)] : 0; }
        public long VolumeAt(long tick)
        {
            if (!InRange(tick)) return 0;
            int i = (int)(tick - baseTick);
            return buy[i] + sell[i] + neutral[i];
        }
    }

    public sealed class ProfileSettings
    {
        public int RowTicks = 1;
        public double ValueAreaPercent = 70.0;
        public ValueAreaMethod VaMethod = ValueAreaMethod.TwoRowAuction;
        public int SmoothingRows = 5;      // odd number, centered moving average for node detection
        public int NodeRadiusRows = 6;     // a node must be the extreme within +/- radius rows
        public double HvnMinFraction = 0.50; // HVN smoothed volume >= fraction * max smoothed volume
        public double LvnMaxFraction = 0.30; // LVN smoothed volume <= fraction * max smoothed volume
        public double LvnProminence = 2.0;  // peaks on BOTH sides must be >= prominence * LVN volume
        public int NodeEdgeRows = 3;        // ignore profile tails for LVN detection

        public ProfileSettings Clone() { return (ProfileSettings)MemberwiseClone(); }
    }

    /// <summary>Immutable result of a profile computation.</summary>
    public sealed class ProfileStats
    {
        public bool IsValid;
        public int RowTicks;
        public long FirstRowTick;   // start tick of row 0 (lowest row)
        public int RowCount;
        public long[] RowVolume;
        public long[] RowBuy;
        public long[] RowSell;
        public long TotalVolume;
        public long TotalBuy;
        public long TotalSell;
        public long MaxRowVolume;
        public int PocRow = -1;
        public int VahRow = -1;
        public int ValRow = -1;
        public int[] HvnRows = new int[0];
        public int[] LvnRows = new int[0];

        public long RowStartTick(int row) { return FirstRowTick + (long)row * RowTicks; }
        public long RowEndTick(int row) { return RowStartTick(row) + RowTicks - 1; }
        public double RowCenterPrice(int row, double tickSize) { return (RowStartTick(row) + (RowTicks - 1) / 2.0) * tickSize; }
        public double RowLowEdge(int row, double tickSize) { return (RowStartTick(row) - 0.5) * tickSize; }
        public double RowHighEdge(int row, double tickSize) { return (RowEndTick(row) + 0.5) * tickSize; }

        public double PocPrice(double tickSize) { return PocRow < 0 ? double.NaN : RowCenterPrice(PocRow, tickSize); }
        public double VahPrice(double tickSize) { return VahRow < 0 ? double.NaN : (RowEndTick(VahRow)) * tickSize; }
        public double ValPrice(double tickSize) { return ValRow < 0 ? double.NaN : (RowStartTick(ValRow)) * tickSize; }

        public static readonly ProfileStats Empty = new ProfileStats();
    }

    public static class ProfileCalculator
    {
        /// <summary>
        /// POC  = row with the highest traded volume. Tie -> the row closest to the middle of the profile range,
        ///        then the lower row (deterministic).
        /// VA   = rows around the POC until >= ValueAreaPercent of total volume, expanding toward the side with the
        ///        larger volume of the next two rows (CBOT two-row auction method) or next single row.
        ///        Equal sides expand both ways.
        /// HVN/LVN = local extremes of a smoothed profile (see ProfileSettings).
        /// </summary>
        public static ProfileStats Compute(PriceLadder ladder, ProfileSettings s)
        {
            ProfileStats r = new ProfileStats();
            if (ladder == null || !ladder.HasData || ladder.TotalVolume <= 0) return r;

            int rowTicks = Math.Max(1, s.RowTicks);
            long firstRow = PriceMath.FloorDiv(ladder.MinTick, rowTicks);
            long lastRow = PriceMath.FloorDiv(ladder.MaxTick, rowTicks);
            int n = (int)(lastRow - firstRow + 1);

            r.RowTicks = rowTicks;
            r.FirstRowTick = firstRow * rowTicks;
            r.RowCount = n;
            r.RowVolume = new long[n];
            r.RowBuy = new long[n];
            r.RowSell = new long[n];

            for (long t = ladder.MinTick; t <= ladder.MaxTick; t++)
            {
                int row = (int)(PriceMath.FloorDiv(t, rowTicks) - firstRow);
                long b = ladder.BuyAt(t), sl = ladder.SellAt(t), nt = ladder.NeutralAt(t);
                r.RowBuy[row] += b;
                r.RowSell[row] += sl;
                r.RowVolume[row] += b + sl + nt;
            }
            for (int i = 0; i < n; i++)
            {
                r.TotalVolume += r.RowVolume[i];
                r.TotalBuy += r.RowBuy[i];
                r.TotalSell += r.RowSell[i];
            }

            // ---- POC
            double center = (n - 1) / 2.0;
            int poc = 0;
            long best = -1;
            for (int i = 0; i < n; i++)
            {
                long v = r.RowVolume[i];
                if (v > best || (v == best && Math.Abs(i - center) < Math.Abs(poc - center)))
                {
                    best = v;
                    poc = i;
                }
            }
            r.PocRow = poc;
            r.MaxRowVolume = best;

            // ---- Value area
            double pct = PriceMath.Clamp(s.ValueAreaPercent, 1.0, 100.0) / 100.0;
            long target = (long)Math.Ceiling(r.TotalVolume * pct);
            int hi = poc, lo = poc;
            long acc = r.RowVolume[poc];
            bool two = s.VaMethod == ValueAreaMethod.TwoRowAuction;
            while (acc < target && (lo > 0 || hi < n - 1))
            {
                long upSum = -1, dnSum = -1;
                int upRows = 0, dnRows = 0;
                if (hi + 1 < n)
                {
                    upSum = r.RowVolume[hi + 1]; upRows = 1;
                    if (two && hi + 2 < n) { upSum += r.RowVolume[hi + 2]; upRows = 2; }
                }
                if (lo - 1 >= 0)
                {
                    dnSum = r.RowVolume[lo - 1]; dnRows = 1;
                    if (two && lo - 2 >= 0) { dnSum += r.RowVolume[lo - 2]; dnRows = 2; }
                }
                if (upSum > dnSum) { hi += upRows; acc += upSum; }
                else if (dnSum > upSum) { lo -= dnRows; acc += dnSum; }
                else
                {
                    if (upRows > 0) { hi += upRows; acc += upSum; }
                    if (dnRows > 0) { lo -= dnRows; acc += dnSum; }
                }
            }
            r.VahRow = hi;
            r.ValRow = lo;

            ComputeNodes(r, s);
            r.IsValid = true;
            return r;
        }

        private static void ComputeNodes(ProfileStats r, ProfileSettings s)
        {
            int n = r.RowCount;
            if (n < 5) return;
            int w = Math.Max(1, s.SmoothingRows | 1);
            int half = w / 2;
            double[] sm = new double[n];
            for (int i = 0; i < n; i++)
            {
                double sum = 0; int c = 0;
                for (int j = i - half; j <= i + half; j++)
                {
                    if (j < 0 || j >= n) continue;
                    sum += r.RowVolume[j]; c++;
                }
                sm[i] = c > 0 ? sum / c : 0;
            }
            double maxS = 0;
            for (int i = 0; i < n; i++) if (sm[i] > maxS) maxS = sm[i];
            if (maxS <= 0) return;

            int rad = Math.Max(1, s.NodeRadiusRows);
            List<int> hvn = new List<int>();
            List<int> lvn = new List<int>();
            for (int i = 0; i < n; i++)
            {
                bool isMax = true, isMin = true;
                for (int j = i - rad; j <= i + rad; j++)
                {
                    if (j < 0 || j >= n || j == i) continue;
                    if (j < i) { if (sm[j] >= sm[i]) isMax = false; if (sm[j] <= sm[i]) isMin = false; }
                    else { if (sm[j] > sm[i]) isMax = false; if (sm[j] < sm[i]) isMin = false; }
                }
                if (isMax && sm[i] >= s.HvnMinFraction * maxS) hvn.Add(i);
                if (isMin && i >= s.NodeEdgeRows && i < n - s.NodeEdgeRows && sm[i] <= s.LvnMaxFraction * maxS)
                {
                    double leftPeak = 0, rightPeak = 0;
                    for (int j = 0; j < i; j++) if (sm[j] > leftPeak) leftPeak = sm[j];
                    for (int j = i + 1; j < n; j++) if (sm[j] > rightPeak) rightPeak = sm[j];
                    double floor = Math.Max(sm[i], 1e-9) * s.LvnProminence;
                    if (leftPeak >= floor && rightPeak >= floor) lvn.Add(i);
                }
            }
            r.HvnRows = hvn.ToArray();
            r.LvnRows = lvn.ToArray();
        }
    }

    /// <summary>A profile over a period (session/day/week/sub-session/fixed range).</summary>
    public sealed class GofVolumeProfile
    {
        public ProfileKind Kind;
        public string Label;
        public DateTime TradingDay;
        public DateTime StartTime = DateTime.MinValue;
        public DateTime EndTime = DateTime.MinValue;
        public int StartBar = -1;
        public int EndBar = -1;
        public bool IsComplete;
        public readonly PriceLadder Ladder;
        private ProfileStats stats;
        private bool dirty = true;
        private int version;

        public GofVolumeProfile(ProfileKind kind, string label, DateTime tradingDay, int startBar, int capacity)
        {
            Kind = kind;
            Label = label;
            TradingDay = tradingDay;
            StartBar = startBar;
            EndBar = startBar;
            Ladder = new PriceLadder(capacity);
        }

        public int Version { get { return version; } }

        public void Add(ref TradePrint t, int bar)
        {
            Ladder.Add(t.Tick, t.Volume, t.Side);
            if (StartTime == DateTime.MinValue) StartTime = t.Time;
            EndTime = t.Time;
            if (bar > EndBar) EndBar = bar;
            dirty = true;
            version++;
        }

        public void TouchBar(int bar) { if (bar > EndBar) EndBar = bar; }

        public ProfileStats GetStats(ProfileSettings s)
        {
            if (dirty || stats == null)
            {
                stats = ProfileCalculator.Compute(Ladder, s);
                dirty = false;
            }
            return stats;
        }

        public void Invalidate() { dirty = true; }
    }

    /// <summary>
    /// Virgin (naked) POC: the POC of a COMPLETED daily/weekly profile at which no trade has printed since that
    /// profile ended. "Touched" = any later print whose tick lies inside the POC row +/- TouchToleranceTicks.
    /// Detection is per print (not per bar range), so price gapping over the level without a print does not count.
    /// </summary>
    public sealed class VirginLevel
    {
        public ProfileKind SourceKind;
        public DateTime SourceDay;
        public long LowTick;
        public long HighTick;
        public double Price;
        public int OriginBar;       // last bar of the source profile
        public DateTime CreatedTime;
        public int TouchBar = -1;
        public DateTime TouchTime = DateTime.MinValue;
        public bool IsVirgin { get { return TouchBar < 0; } }
    }

    public sealed class VolumeProfileEngine
    {
        public ProfileSettings DaySettings = new ProfileSettings();
        public ProfileSettings WeekSettings = new ProfileSettings();
        public ProfileScope Scope = ProfileScope.ETH;
        public int MaxHistoryDays = 10;
        public int MaxHistoryWeeks = 4;
        public int MaxHistorySubSessions = 30;
        public int VirginToleranceTicks = 0;
        public int MaxVirginLevels = 20;
        public bool TrackSubSessions = true;

        /// <summary>
        /// RTH split of each trading day, always from ALL prints (independent of Scope):
        ///   Overnight = session open until the first print inside the RTH window,
        ///   RTH       = prints inside the RTH window,
        ///   PostRth   = prints after the RTH window until the session ends.
        /// </summary>
        public bool TrackRthSplit = true;
        public GofVolumeProfile CurrentOvernight;
        public GofVolumeProfile CurrentRth;
        public GofVolumeProfile CurrentPostRth;
        public readonly List<GofVolumeProfile> CompletedSegments = new List<GofVolumeProfile>();
        private int segmentPhase;   // 0 overnight, 1 RTH, 2 post-RTH
        public DateTime ContractStartFilter = DateTime.MinValue; // levels from before this trading day are dropped (rollover)

        public GofVolumeProfile CurrentSession;
        public GofVolumeProfile CurrentDay;
        public GofVolumeProfile CurrentWeek;
        public GofVolumeProfile FixedRange;
        public DateTime FixedStart = DateTime.MinValue;
        public DateTime FixedEnd = DateTime.MinValue;

        public readonly List<GofVolumeProfile> CompletedDays = new List<GofVolumeProfile>();
        public readonly List<GofVolumeProfile> CompletedWeeks = new List<GofVolumeProfile>();
        public readonly List<GofVolumeProfile> CompletedSessions = new List<GofVolumeProfile>();
        public readonly List<GofVolumeProfile> CompletedSubSessions = new List<GofVolumeProfile>();
        public readonly Dictionary<string, GofVolumeProfile> ActiveSubSessions = new Dictionary<string, GofVolumeProfile>();
        private readonly Dictionary<string, string> activeSubKeys = new Dictionary<string, string>();
        public readonly List<VirginLevel> VirginLevels = new List<VirginLevel>();
        public double TickSize = 0.1;

        public GofVolumeProfile PreviousDay { get { return CompletedDays.Count > 0 ? CompletedDays[CompletedDays.Count - 1] : null; } }
        public GofVolumeProfile PreviousWeek { get { return CompletedWeeks.Count > 0 ? CompletedWeeks[CompletedWeeks.Count - 1] : null; } }

        public void ConfigureFixedRange(DateTime start, DateTime end)
        {
            FixedStart = start;
            FixedEnd = end;
            FixedRange = (end > start) ? new GofVolumeProfile(ProfileKind.Fixed, "Fixed", start.Date, -1, 256) : null;
        }

        public void OnSessionStart(DateTime tradingDay, int bar, bool newDay, bool newWeek)
        {
            if (newDay || CurrentDay == null)
            {
                CompleteSegment(ref CurrentOvernight);
                CompleteSegment(ref CurrentRth);
                CompleteSegment(ref CurrentPostRth);
                segmentPhase = 0;
            }
            if (CurrentSession != null)
            {
                CurrentSession.IsComplete = true;
                AddLimited(CompletedSessions, CurrentSession, MaxHistoryDays * 2);
            }
            if (newDay || CurrentDay == null)
            {
                if (CurrentDay != null)
                {
                    CurrentDay.IsComplete = true;
                    RegisterVirgin(CurrentDay, DaySettings);
                    AddLimited(CompletedDays, CurrentDay, MaxHistoryDays);
                }
                CurrentDay = new GofVolumeProfile(ProfileKind.Day, tradingDay.ToString("yyyy-MM-dd"), tradingDay, bar, 512);
            }
            if (newWeek || CurrentWeek == null)
            {
                if (CurrentWeek != null)
                {
                    CurrentWeek.IsComplete = true;
                    RegisterVirgin(CurrentWeek, WeekSettings);
                    AddLimited(CompletedWeeks, CurrentWeek, MaxHistoryWeeks);
                }
                CurrentWeek = new GofVolumeProfile(ProfileKind.Week, "W " + PriceMath.WeekStart(tradingDay).ToString("yyyy-MM-dd"), tradingDay, bar, 1024);
            }
            CurrentSession = new GofVolumeProfile(ProfileKind.Session, tradingDay.ToString("yyyy-MM-dd"), tradingDay, bar, 512);
        }

        private static void AddLimited(List<GofVolumeProfile> list, GofVolumeProfile p, int max)
        {
            list.Add(p);
            while (list.Count > Math.Max(1, max)) list.RemoveAt(0);
        }

        private void RegisterVirgin(GofVolumeProfile p, ProfileSettings s)
        {
            if (p.TradingDay < ContractStartFilter) return;
            ProfileStats st = p.GetStats(s);
            if (!st.IsValid) return;
            VirginLevel v = new VirginLevel();
            v.SourceKind = p.Kind;
            v.SourceDay = p.TradingDay;
            v.LowTick = st.RowStartTick(st.PocRow);
            v.HighTick = st.RowEndTick(st.PocRow);
            v.Price = st.PocPrice(TickSize);
            v.OriginBar = p.EndBar;
            v.CreatedTime = p.EndTime;
            VirginLevels.Add(v);
            // keep untouched levels preferentially
            while (VirginLevels.Count > Math.Max(1, MaxVirginLevels))
            {
                int idx = VirginLevels.FindIndex(delegate(VirginLevel x) { return !x.IsVirgin; });
                VirginLevels.RemoveAt(idx >= 0 ? idx : 0);
            }
        }

        /// <summary>Adds an executed print. Virgin levels are checked against EVERY print (ETH, irrespective of scope).</summary>
        public void AddTrade(ref TradePrint t, int bar, bool inRth, List<WindowInstance> windows, out VirginLevel touched)
        {
            touched = null;
            for (int i = 0; i < VirginLevels.Count; i++)
            {
                VirginLevel v = VirginLevels[i];
                if (!v.IsVirgin || bar <= v.OriginBar) continue;
                if (t.Tick >= v.LowTick - VirginToleranceTicks && t.Tick <= v.HighTick + VirginToleranceTicks)
                {
                    v.TouchBar = bar;
                    v.TouchTime = t.Time;
                    touched = v;
                }
            }

            if (FixedRange != null && t.Time >= FixedStart && t.Time <= FixedEnd)
            {
                if (FixedRange.StartBar < 0) { FixedRange.StartBar = bar; FixedRange.EndBar = bar; }
                FixedRange.Add(ref t, bar);
            }

            if (TrackRthSplit)
            {
                int phase = inRth ? 1 : (segmentPhase >= 1 ? 2 : 0);
                if (phase > segmentPhase) segmentPhase = phase;
                DateTime day = CurrentDay != null ? CurrentDay.TradingDay : t.Time.Date;
                if (phase == 0) SegmentFor(ref CurrentOvernight, ProfileKind.Overnight, "ON", day, bar).Add(ref t, bar);
                else if (phase == 1) SegmentFor(ref CurrentRth, ProfileKind.Rth, "RTH", day, bar).Add(ref t, bar);
                else SegmentFor(ref CurrentPostRth, ProfileKind.PostRth, "POST", day, bar).Add(ref t, bar);
            }

            bool include = Scope == ProfileScope.ETH || inRth;
            if (include)
            {
                if (CurrentSession != null) CurrentSession.Add(ref t, bar);
                if (CurrentDay != null) CurrentDay.Add(ref t, bar);
                if (CurrentWeek != null) CurrentWeek.Add(ref t, bar);
            }

            if (TrackSubSessions && windows != null)
            {
                for (int i = 0; i < windows.Count; i++)
                {
                    WindowInstance w = windows[i];
                    GofVolumeProfile sp;
                    string key;
                    if (!ActiveSubSessions.TryGetValue(w.Def.Name, out sp) || !activeSubKeys.TryGetValue(w.Def.Name, out key) || key != w.Key)
                    {
                        if (sp != null)
                        {
                            sp.IsComplete = true;
                            AddLimited(CompletedSubSessions, sp, MaxHistorySubSessions);
                        }
                        sp = new GofVolumeProfile(ProfileKind.SubSession, w.Def.Name, w.LocalStart.Date, bar, 256);
                        ActiveSubSessions[w.Def.Name] = sp;
                        activeSubKeys[w.Def.Name] = w.Key;
                    }
                    sp.Add(ref t, bar);
                }
            }
        }

        public void OnBarClosed(int bar)
        {
            if (CurrentSession != null) CurrentSession.TouchBar(bar);
            if (CurrentDay != null) CurrentDay.TouchBar(bar);
            if (CurrentWeek != null) CurrentWeek.TouchBar(bar);
        }

        private static GofVolumeProfile SegmentFor(ref GofVolumeProfile p, ProfileKind kind, string label, DateTime day, int bar)
        {
            if (p == null) p = new GofVolumeProfile(kind, label, day, bar, 256);
            return p;
        }

        private void CompleteSegment(ref GofVolumeProfile p)
        {
            if (p == null) return;
            p.IsComplete = true;
            AddLimited(CompletedSegments, p, Math.Max(3, MaxHistoryDays * 3));
            p = null;
        }

        public GofVolumeProfile GetSubSession(string name)
        {
            GofVolumeProfile p;
            return ActiveSubSessions.TryGetValue(name, out p) ? p : null;
        }
    }
}
