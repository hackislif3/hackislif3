// Core engine tests. Compiled with LangVersion 5 to guarantee NinjaScript-compatible syntax.
// Run:  dotnet run --project tests/CoreTests
using System;
using System.Collections.Generic;
using System.Globalization;
using NinjaTrader.NinjaScript.AddOns.GoldOrderFlow;

internal static class Program
{
    private static int failures;
    private static int passes;

    private static void Check(bool cond, string name)
    {
        if (cond) { passes++; Console.WriteLine("  PASS " + name); }
        else { failures++; Console.WriteLine("  FAIL " + name); }
    }

    private static int Main()
    {
        Console.WriteLine("GOF core tests");
        LadderTests();
        ProfileTests();
        ClassifierTests();
        SessionClockTests();
        LargeTradeTests();
        DeltaTests();
        VirginPocTest();
        DivergenceTest();
        AbsorptionTest();
        DeterminismTest();
        NewsTest();
        PerfTest();
        Console.WriteLine();
        Console.WriteLine("passed " + passes + ", failed " + failures);
        return failures == 0 ? 0 : 1;
    }

    private static void LadderTests()
    {
        Console.WriteLine("[PriceLadder]");
        PriceLadder l = new PriceLadder(8);
        l.Add(20000, 5, AggressorSide.Buy);
        l.Add(19000, 3, AggressorSide.Sell);   // forces shift down
        l.Add(21000, 2, AggressorSide.Unknown);// forces growth up
        l.Add(20000, 1, AggressorSide.Sell);
        Check(l.BuyAt(20000) == 5 && l.SellAt(20000) == 1, "values survive re-allocation");
        Check(l.SellAt(19000) == 3 && l.NeutralAt(21000) == 2, "edge values");
        Check(l.MinTick == 19000 && l.MaxTick == 21000, "min/max");
        Check(l.TotalVolume == 11 && l.Delta == 1, "totals/delta");
        PriceLadder m = new PriceLadder(4);
        m.AddLadder(l);
        Check(m.TotalVolume == 11 && m.VolumeAt(20000) == 6, "AddLadder merge");
    }

    private static PriceLadder Build(long startTick, long[] vols)
    {
        PriceLadder l = new PriceLadder(16);
        for (int i = 0; i < vols.Length; i++) l.Add(startTick + i, vols[i], AggressorSide.Buy);
        return l;
    }

    private static void ProfileTests()
    {
        Console.WriteLine("[Profile POC / Value Area]");
        PriceLadder l = Build(100, new long[] { 10, 20, 50, 100, 40, 30, 5 });
        ProfileSettings s = new ProfileSettings();
        s.ValueAreaPercent = 70;
        s.VaMethod = ValueAreaMethod.TwoRowAuction;
        ProfileStats st = ProfileCalculator.Compute(l, s);
        Check(st.RowStartTick(st.PocRow) == 103, "POC = highest volume row");
        Check(st.RowStartTick(st.VahRow) == 105 && st.RowStartTick(st.ValRow) == 101, "two-row VA = 101..105");
        s.VaMethod = ValueAreaMethod.SingleRow;
        st = ProfileCalculator.Compute(l, s);
        Check(st.RowStartTick(st.VahRow) == 104 && st.RowStartTick(st.ValRow) == 102, "single-row VA = 102..104");
        long va = 0;
        for (int r = st.ValRow; r <= st.VahRow; r++) va += st.RowVolume[r];
        Check(va >= Math.Ceiling(st.TotalVolume * 0.7), "VA volume >= 70%");

        // tie-break: equal maxima -> closest to range centre
        st = ProfileCalculator.Compute(Build(0, new long[] { 50, 1, 1, 50, 1, 1, 1, 1, 50 }), s);
        Check(st.PocRow == 3, "POC tie -> closest to centre");

        // row aggregation
        s.RowTicks = 2;
        st = ProfileCalculator.Compute(l, s);
        Check(st.RowCount == 4 && st.RowVolume[0] == 30 && st.RowVolume[1] == 150 && st.RowStartTick(st.PocRow) == 102, "row size 2 aggregation");

        // HVN / LVN on a bimodal profile
        ProfileSettings ns = new ProfileSettings();
        long[] bi = new long[41];
        for (int i = 0; i < 41; i++)
        {
            double a = Math.Exp(-Math.Pow(i - 10, 2) / 18.0) * 100, b = Math.Exp(-Math.Pow(i - 30, 2) / 18.0) * 90;
            bi[i] = (long)(a + b) + 1;
        }
        st = ProfileCalculator.Compute(Build(1000, bi), ns);
        Check(st.HvnRows.Length == 2, "two HVNs on bimodal profile (got " + st.HvnRows.Length + ")");
        Check(st.LvnRows.Length >= 1 && Math.Abs(st.LvnRows[0] - 20) <= 2, "LVN in the valley");
    }

    private static void ClassifierTests()
    {
        Console.WriteLine("[TradeClassifier]");
        TradeClassifier c = new TradeClassifier(InsideSpreadMethod.MidpointThenTickRule);
        TradePrint t = P(2000.1, 1, 2000.0, 2000.1);
        c.Classify(ref t, 0.1);
        Check(t.Side == AggressorSide.Buy && t.Source == ClassificationSource.QuoteExact, "at ask = buy (exact)");
        t = P(2000.0, 1, 2000.0, 2000.1); c.Classify(ref t, 0.1);
        Check(t.Side == AggressorSide.Sell && t.Source == ClassificationSource.QuoteExact, "at bid = sell (exact)");
        t = P(2000.2, 1, 2000.0, 2000.3); c.Classify(ref t, 0.1);
        Check(t.Side == AggressorSide.Buy && t.Source == ClassificationSource.QuoteInside, "inside above mid = buy (inferred)");
        t = P(2000.1, 1, 2000.0, 2000.3); c.Classify(ref t, 0.1);
        Check(t.Side == AggressorSide.Sell && t.Source == ClassificationSource.QuoteInside, "inside below mid = sell (inferred)");
        t = P(2000.3, 1, 2000.2, 2000.2); c.Classify(ref t, 0.1);
        Check(t.Side == AggressorSide.Buy && t.Source == ClassificationSource.TickRule, "locked quote -> tick rule (estimated)");
        t = P(2000.3, 1, 0, 0); c.Classify(ref t, 0.1);
        Check(t.Side == AggressorSide.Buy && t.Source == ClassificationSource.TickRule, "zero tick keeps last direction");
        TradeClassifier n = new TradeClassifier(InsideSpreadMethod.Neutral);
        t = P(2000.2, 1, 2000.0, 2000.3); n.Classify(ref t, 0.1);
        Check(t.Side == AggressorSide.Unknown, "neutral inside-spread = no delta");
    }

    private static TradePrint P(double price, long vol, double bid, double ask)
    {
        TradePrint t = new TradePrint();
        t.Price = price; t.Volume = vol; t.Bid = bid; t.Ask = ask; t.Time = new DateTime(2026, 1, 5, 10, 0, 0);
        return t;
    }

    private static void SessionClockTests()
    {
        Console.WriteLine("[SessionClock DST]");
        bool f;
        TimeZoneInfo et = TimeZoneResolver.Find("Eastern Standard Time", out f);
        Check(f, "Eastern zone resolved");
        SessionClock c = new SessionClock();
        c.PlatformZone = et;
        c.AddWindow("London", "08:00-16:30", "GMT Standard Time", true);
        c.AddWindow("Asia", "18:00-03:00", "Eastern Standard Time", true);
        c.SetRth("08:20-13:30", "Eastern Standard Time");
        Check(c.Errors.Count == 0, "no clock errors");
        // 2026-03-20: US already on DST, UK not yet -> London 08:00 GMT = 04:00 ET
        Check(c.ActiveNames(new DateTime(2026, 3, 20, 4, 30, 0)).Contains("London"), "Mar 20 04:30 ET in London");
        Check(!c.ActiveNames(new DateTime(2026, 3, 20, 3, 30, 0)).Contains("London"), "Mar 20 03:30 ET not London");
        // 2026-07-01: both on DST -> London 08:00 BST = 03:00 ET
        Check(c.ActiveNames(new DateTime(2026, 7, 1, 3, 30, 0)).Contains("London"), "Jul 01 03:30 ET in London");
        // midnight-crossing window
        Check(c.ActiveNames(new DateTime(2026, 7, 1, 1, 0, 0)).Contains("Asia"), "Asia crosses midnight");
        Check(c.ActiveNames(new DateTime(2026, 7, 1, 18, 5, 0)).Contains("Asia"), "Asia after 18:00");
        bool rth;
        c.GetActive(new DateTime(2026, 7, 1, 9, 0, 0), out rth);
        Check(rth, "09:00 ET inside RTH");
        c.GetActive(new DateTime(2026, 7, 1, 14, 0, 0), out rth);
        Check(!rth, "14:00 ET outside RTH");
        WindowInstance a, b;
        SessionClock.TryInstance(c.Windows[1], new DateTime(2026, 7, 1, 23, 0, 0), out a);
        SessionClock.TryInstance(c.Windows[1], new DateTime(2026, 7, 2, 1, 0, 0), out b);
        Check(a.Key == b.Key, "same Asia instance across midnight");
        Check(PriceMath.WeekStart(new DateTime(2026, 11, 2)) == new DateTime(2026, 11, 2) &&
              PriceMath.WeekStart(new DateTime(2026, 11, 6)) == new DateTime(2026, 11, 2), "Monday-based trading week");
    }

    private static void LargeTradeTests()
    {
        Console.WriteLine("[LargeTradeEngine]");
        LargeTradeEngine e = new LargeTradeEngine();
        e.S.Mode = LargeTradeThresholdMode.Manual;
        e.S.ManualThreshold = 10;
        e.S.AggregationMs = 5;
        e.Init();
        List<LargeTradeEvent> lt = new List<LargeTradeEvent>();
        DateTime t0 = new DateTime(2026, 1, 5, 10, 0, 0);
        Feed(e, t0, 4, 1, 100, lt);
        Feed(e, t0.AddMilliseconds(2), 4, 1, 101, lt);
        Feed(e, t0.AddMilliseconds(4), 4, 1, 101, lt);
        Check(lt.Count == 0, "aggregate not emitted until it is complete");
        Feed(e, t0.AddMilliseconds(100), 1, -1, 100, lt);
        Check(lt.Count == 1 && lt[0].Volume == 12 && lt[0].Prints == 3 && lt[0].Side == 1, "3 same-side prints aggregated into 12-lot buy");
        // price reversal breaks aggregation
        lt.Clear();
        Feed(e, t0.AddSeconds(1), 6, 1, 105, lt);
        Feed(e, t0.AddSeconds(1).AddMilliseconds(1), 6, 1, 104, lt);
        e.FlushBar(null, lt, null);
        Check(lt.Count == 0, "non-monotonic buy prints are not merged");

        // adaptive, past-only threshold
        LargeTradeEngine a = new LargeTradeEngine();
        a.S.Mode = LargeTradeThresholdMode.Adaptive;
        a.S.MinSamples = 200;
        a.S.Percentile = 99;
        a.S.AggregationMs = -1;
        a.S.RvolExponent = 0;
        a.Init();
        ThresholdSource src;
        a.CurrentThreshold(20, double.NaN, out src);
        Check(src == ThresholdSource.ManualWarmup, "warm-up uses manual threshold");
        Random rnd = new Random(1);
        List<LargeTradeEvent> big = new List<LargeTradeEvent>();
        for (int i = 0; i < 2000; i++) Feed(a, t0.AddSeconds(i), 1 + rnd.Next(5), (i % 2) == 0 ? 1 : -1, 100 + (i % 7), big);
        double thr = a.CurrentThreshold(20, double.NaN, out src);
        Check(src == ThresholdSource.Adaptive && thr >= 4 && thr <= 5, "adaptive p99 of sizes 1..5 is 5 (got " + thr + ")");
        int before = big.Count;
        Feed(a, t0.AddSeconds(5000), 50, 1, 100, big);
        Check(big.Count == before + 1 && big[big.Count - 1].Volume == 50, "50-lot flagged against past distribution");

        // clusters
        LargeTradeEngine cl = new LargeTradeEngine();
        cl.S.Mode = LargeTradeThresholdMode.Manual; cl.S.ManualThreshold = 10; cl.S.AggregationMs = -1; cl.S.ClusterWindowMs = 2000;
        cl.Init();
        List<LargeTradeCluster> cs = new List<LargeTradeCluster>();
        DateTime t1 = t0.AddHours(1);
        FeedC(cl, t1, 15, -1, 200, cs);
        FeedC(cl, t1.AddMilliseconds(500), 12, -1, 199, cs);
        FeedC(cl, t1.AddMilliseconds(1200), 20, -1, 199, cs);
        FeedC(cl, t1.AddSeconds(10), 1, 1, 199, cs);      // timeout closes the cluster
        Check(cs.Count == 1 && cs[0].Count == 3 && cs[0].Volume == 47, "sell cluster of 3 large executions");
        Check(cl.ConsecutiveSide == -1 && cl.ConsecutiveCount == 3, "consecutive large-sell counter");
    }

    private static void Feed(LargeTradeEngine e, DateTime t, long vol, int side, long tick, List<LargeTradeEvent> outList)
    {
        TradePrint p = new TradePrint();
        p.Time = t; p.Volume = vol; p.Side = (AggressorSide)side; p.Tick = tick; p.Price = tick * 0.1; p.Source = ClassificationSource.QuoteExact;
        e.OnPrint(ref p, 1, 20, double.NaN, null, outList, null, null);
    }

    private static void FeedC(LargeTradeEngine e, DateTime t, long vol, int side, long tick, List<LargeTradeCluster> cs)
    {
        TradePrint p = new TradePrint();
        p.Time = t; p.Volume = vol; p.Side = (AggressorSide)side; p.Tick = tick; p.Price = tick * 0.1; p.Source = ClassificationSource.QuoteExact;
        e.OnPrint(ref p, 1, 20, double.NaN, null, null, cs, null);
    }

    private static void DeltaTests()
    {
        Console.WriteLine("[DeltaEngine]");
        DeltaEngine d = new DeltaEngine();
        d.DisplayMode = CvdResetMode.TradingDay;
        d.OnSessionStart(true, true);
        BarFootprint f = new BarFootprint();
        d.BeginBar(f);
        TradePrint b = new TradePrint(); b.Volume = 10; b.Side = AggressorSide.Buy;
        TradePrint s = new TradePrint(); s.Volume = 4; s.Side = AggressorSide.Sell;
        d.OnTrade(ref b, f); d.OnTrade(ref s, f);
        Check(d.SessionCvd == 6 && d.DayCvd == 6 && d.WeekCvd == 6 && f.CvdClose == 6 && f.CvdHigh == 10, "delta = buy - sell, CVD high tracked");
        int rid = d.ResetId;
        d.OnSessionStart(false, false);
        Check(d.SessionCvd == 0 && d.DayCvd == 6 && d.ResetId == rid, "session reset does not reset daily CVD");
        d.OnSessionStart(true, false);
        Check(d.DayCvd == 0 && d.WeekCvd == 6 && d.ResetId == rid + 1, "new day resets daily CVD, not weekly");
    }

    private static EngineSettings TestSettings()
    {
        EngineSettings s = new EngineSettings();
        bool f;
        s.PlatformZone = TimeZoneResolver.Find("Eastern Standard Time", out f);
        s.Signals.AllowEstimated = true;
        return s;
    }

    private static void VirginPocTest()
    {
        Console.WriteLine("[Virgin POC]");
        OrderFlowEngine e = new OrderFlowEngine(TestSettings());
        DateTime d1 = new DateTime(2026, 1, 5, 9, 0, 0);
        e.OnSessionStart(d1.Date, d1, 0);
        e.BeginBar(0, d1);
        for (int i = 0; i < 50; i++) e.ProcessTrade(Pr(d1.AddSeconds(i), 2000.0, 5, 1999.9, 2000.0), 0);
        e.ProcessTrade(Pr(d1.AddSeconds(60), 2001.0, 1, 2000.9, 2001.0), 0);
        e.FinalizeBar(0, d1.AddMinutes(1), 2000, 2001, 2000, 2001, 251);
        DateTime d2 = new DateTime(2026, 1, 6, 9, 0, 0);
        e.OnSessionStart(d2.Date, d2, 1);
        e.BeginBar(1, d2);
        e.ProcessTrade(Pr(d2, 2005.0, 3, 2004.9, 2005.0), 1);
        e.FinalizeBar(1, d2.AddMinutes(1), 2005, 2005, 2005, 2005, 3);
        Check(e.Profiles.VirginLevels.Count == 1 && e.Profiles.VirginLevels[0].IsVirgin && Math.Abs(e.Profiles.VirginLevels[0].Price - 2000.0) < 1e-9, "previous day POC registered as virgin");
        e.BeginBar(2, d2.AddMinutes(1));
        e.ProcessTrade(Pr(d2.AddMinutes(1).AddSeconds(5), 2000.1, 1, 2000.0, 2000.1), 2);
        Check(e.Profiles.VirginLevels[0].IsVirgin, "print 1 tick away (tolerance 0) does not touch");
        e.ProcessTrade(Pr(d2.AddMinutes(1).AddSeconds(6), 2000.0, 1, 2000.0, 2000.1), 2);
        Check(!e.Profiles.VirginLevels[0].IsVirgin && e.Profiles.VirginLevels[0].TouchBar == 2, "print at POC touches it");
    }

    private static TradePrint Pr(DateTime t, double price, long vol, double bid, double ask)
    {
        TradePrint p = new TradePrint();
        p.Time = t; p.Price = price; p.Volume = vol; p.Bid = bid; p.Ask = ask;
        return p;
    }

    private static void DivergenceTest()
    {
        Console.WriteLine("[Divergence]");
        BarHistory h = new BarHistory();
        DivergenceEngine d = new DivergenceEngine();
        d.S.PivotStrength = 2;
        // highs: pivot at 2 (H=10, cvd 100), pivot at 7 (H=11, cvd 80) -> bearish divergence confirmed at bar 9
        double[] highs = { 8, 9, 10, 9, 8, 9, 10, 11, 10, 9, 8 };
        long[] cvd = { 50, 70, 100, 90, 60, 60, 70, 80, 70, 60, 50 };
        List<DivergenceEvent> ev = new List<DivergenceEvent>();
        for (int i = 0; i < highs.Length; i++)
        {
            BarRecord r = new BarRecord();
            r.Bar = i; r.High = highs[i]; r.Low = highs[i] - 1; r.Close = highs[i] - 0.5;
            r.CvdHigh = cvd[i]; r.CvdLow = cvd[i] - 5; r.CvdResetId = 1;
            h.Add(r);
            d.OnBar(h, i, 0.1, ev);
        }
        Check(ev.Count == 1 && ev[0].Direction == -1 && ev[0].PivotBar == 7 && ev[0].PrevPivotBar == 2 && ev[0].ConfirmBar == 9,
              "bearish divergence stamped at confirmation bar (pivot+strength)");
    }

    private static void AbsorptionTest()
    {
        Console.WriteLine("[Absorption]");
        EngineSettings s = TestSettings();
        s.Bars.TodBucketMinutes = 60;
        OrderFlowEngine e = new OrderFlowEngine(s);
        DateTime t = new DateTime(2026, 1, 5, 9, 0, 0);
        e.OnSessionStart(t.Date, t, 0);
        Random rnd = new Random(7);
        int bar = 0;
        double px = 2000.0;
        // 30 balanced bars to build baselines
        for (; bar < 30; bar++)
        {
            DateTime bt = t.AddMinutes(bar);
            e.BeginBar(bar, bt);
            double o = px, hi = px, lo = px;
            for (int k = 0; k < 20; k++)
            {
                int side = rnd.Next(2) == 0 ? 1 : -1;
                px = Math.Round(px + side * 0.1 * rnd.Next(2), 1);
                hi = Math.Max(hi, px); lo = Math.Min(lo, px);
                e.ProcessTrade(side > 0 ? Pr(bt.AddSeconds(k * 2), px, 2, px - 0.1, px) : Pr(bt.AddSeconds(k * 2), px, 2, px, px + 0.1), bar);
            }
            e.FinalizeBar(bar, bt.AddMinutes(1), o, hi, lo, px, 40);
        }
        // absorption bar: heavy selling into 1999.0 that holds, close near the high
        DateTime ab = t.AddMinutes(bar);
        e.BeginBar(bar, ab);
        double open = px;
        double low = Math.Round(px - 0.3, 1);
        for (int k = 0; k < 40; k++) e.ProcessTrade(Pr(ab.AddSeconds(k), low, 5, low, low + 0.1), bar);
        e.ProcessTrade(Pr(ab.AddSeconds(50), Math.Round(px + 0.1, 1), 3, px, Math.Round(px + 0.1, 1)), bar);
        double absHigh = Math.Round(px + 0.1, 1);
        e.FinalizeBar(bar, ab.AddMinutes(1), open, absHigh, low, absHigh, 203);
        Check(e.Absorption.Candidates.Count >= 1 && e.Absorption.Candidates[e.Absorption.Candidates.Count - 1].Direction == 1,
              "bullish absorption candidate created");
        AbsorptionCandidate c = e.Absorption.Candidates[e.Absorption.Candidates.Count - 1];
        bar++;
        DateTime cb = t.AddMinutes(bar);
        e.BeginBar(bar, cb);
        double up = Math.Round(absHigh + 0.3, 1);
        for (int k = 0; k < 10; k++) e.ProcessTrade(Pr(cb.AddSeconds(k), up, 4, up - 0.1, up), bar);
        SignalRecord sig = e.FinalizeBar(bar, cb.AddMinutes(1), absHigh, up, absHigh, up, 40);
        Check(c.State == AbsorptionState.Confirmed && c.ResolvedBar == bar, "candidate confirmed by close above candidate high");
        Console.WriteLine("    signal on confirmation bar: " + (sig == null ? "none" : sig.Kind + " " + sig.Score));
        Check(sig == null || sig.Bar == bar, "signal (if any) is stamped on the confirmation bar");
    }

    // Same prints, two delivery patterns (print-by-print vs buffered batches through TradeIntake):
    // all outputs must be identical -> historical and realtime processing paths agree (no repainting by design).
    private static void DeterminismTest()
    {
        Console.WriteLine("[Determinism historical vs realtime delivery]");
        List<TradePrint> prints = Synthetic(3, 11);
        string a = RunEngine(prints, false);
        string b = RunEngine(prints, true);
        Check(a == b, "identical signals/profiles for both delivery patterns");
        Console.WriteLine("    " + a.Substring(0, Math.Min(140, a.Length)) + "...");
    }

    private static List<TradePrint> Synthetic(int days, int seed)
    {
        Random r = new Random(seed);
        List<TradePrint> l = new List<TradePrint>();
        double px = 2400.0;
        double drift = 0;
        for (int d = 0; d < days; d++)
        {
            DateTime start = new DateTime(2026, 1, 5, 18, 0, 0).AddDays(d);
            DateTime t = start;
            DateTime end = start.AddHours(6);
            while (t < end)
            {
                t = t.AddMilliseconds(200 + r.Next(3000));
                if (r.Next(200) == 0) drift = (r.NextDouble() - 0.5) * 0.6;
                int side = r.NextDouble() < 0.5 + drift ? 1 : -1;
                if (r.Next(3) == 0) px = Math.Round(px + side * 0.1, 1);
                long vol = 1 + (r.Next(20) == 0 ? r.Next(40) : r.Next(4));
                double bid = side > 0 ? Math.Round(px - 0.1, 1) : px;
                double ask = side > 0 ? px : Math.Round(px + 0.1, 1);
                l.Add(Pr(t, px, vol, bid, ask));
            }
        }
        return l;
    }

    private static string RunEngine(List<TradePrint> prints, bool batched)
    {
        EngineSettings s = TestSettings();
        s.Signals.Threshold = 40;
        OrderFlowEngine e = new OrderFlowEngine(s);
        TradeIntake intake = new TradeIntake();
        int bar = -1;
        DateTime barEnd = DateTime.MinValue;
        double o = 0, h = 0, lo = 0, c = 0, v = 0;
        DateTime lastDay = DateTime.MinValue;
        foreach (TradePrint p in prints)
        {
            DateTime end = new DateTime(((p.Time.Ticks - 1) / TimeSpan.TicksPerMinute + 1) * TimeSpan.TicksPerMinute);
            if (end != barEnd)
            {
                if (bar >= 0)
                {
                    if (batched) intake.Commit(e, bar, DateTime.MaxValue);
                    e.FinalizeBar(bar, barEnd, o, h, lo, c, v);
                }
                bar++;
                barEnd = end;
                DateTime td = p.Time.Hour >= 18 ? p.Time.Date.AddDays(1) : p.Time.Date;
                if (td != lastDay) { e.OnSessionStart(td, p.Time, bar); lastDay = td; }
                e.BeginBar(bar, end);
                o = h = lo = c = p.Price; v = 0;
            }
            h = Math.Max(h, p.Price); lo = Math.Min(lo, p.Price); c = p.Price; v += p.Volume;
            if (batched) intake.Add(p); else e.ProcessTrade(p, bar);
        }
        if (batched) intake.Commit(e, bar, DateTime.MaxValue);
        e.FinalizeBar(bar, barEnd, o, h, lo, c, v);

        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.Append("signals=" + e.Signals.Signals.Count + " large=" + e.Large.Events.Count + " abs=" + e.Absorption.Candidates.Count +
                  " div=" + e.Divergence.Events.Count + " zones=" + e.Zones.Count + ";");
        foreach (SignalRecord sr in e.Signals.Signals)
            sb.Append(sr.Bar + ":" + sr.Kind + ":" + sr.Score.ToString(CultureInfo.InvariantCulture) + ";");
        foreach (GofVolumeProfile d in e.Profiles.CompletedDays)
        {
            ProfileStats st = d.GetStats(s.DayProfile);
            sb.Append("POC " + st.PocPrice(0.1).ToString(CultureInfo.InvariantCulture) + " VA " + st.ValPrice(0.1).ToString(CultureInfo.InvariantCulture) + "-" + st.VahPrice(0.1).ToString(CultureInfo.InvariantCulture) + ";");
        }
        return sb.ToString();
    }

    private static void PerfTest()
    {
        Console.WriteLine("[Performance]");
        List<TradePrint> prints = Synthetic(20, 3);
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        string r = RunEngine(prints, true);
        sw.Stop();
        double us = sw.Elapsed.TotalMilliseconds * 1000.0 / prints.Count;
        Console.WriteLine("    " + prints.Count + " prints, " + sw.ElapsedMilliseconds + " ms total, " + us.ToString("0.00", CultureInfo.InvariantCulture) + " us/print (incl. bar finalization)");
        Check(us < 50 && r.Length > 0, "throughput well above GC peak print rates");
    }

    private static void NewsTest()
    {
        Console.WriteLine("[NewsFilter]");
        bool f;
        TimeZoneInfo et = TimeZoneResolver.Find("Eastern Standard Time", out f);
        TimeZoneInfo utc = TimeZoneInfo.Utc;
        NewsFilter n = new NewsFilter();
        n.MinutesBefore = 10; n.MinutesAfter = 30;
        n.Load("2026-11-06 08:30,NFP; bad line; 2026-11-12 08:30,CPI", null, et, utc);
        Check(n.Events.Count == 2 && n.Errors.Count == 1, "parsed 2 events, 1 error reported");
        // 08:30 EST (Nov 6, after DST end) = 13:30 UTC
        Check(n.ActiveAt(new DateTime(2026, 11, 6, 13, 25, 0)) != null && n.ActiveAt(new DateTime(2026, 11, 6, 14, 5, 0)) == null, "blackout window in platform zone");
    }
}
