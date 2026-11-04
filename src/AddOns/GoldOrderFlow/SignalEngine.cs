// GOF - multi-factor signal engine, outcome tracker (for validation) and CSV logger.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NinjaTrader.NinjaScript.AddOns.GoldOrderFlow
{
    public sealed class SignalSettings
    {
        public double Threshold = 60;           // minimum score (0..100) to emit a signal
        public double ConflictLevel = 45;       // opposite side score at/above this while a side qualifies -> Conflict
        public int CooldownBars = 5;            // per direction
        public int CvdTrendBars = 10;
        public int LargeTradeBars = 3;
        public int DivergenceMemoryBars = 5;
        public double DeltaPctFull = 0.40;      // |delta%| that maps to component = 1
        public double MinDeltaPct = 0.10;       // gate for pressure signals
        public int LocationTicks = 5;           // proximity to a reference level (GC: 5 ticks = $0.50)
        public double VolatilityHigh = 2.0;     // ATR / ATR baseline above -> multiplier
        public double VolatilityLow = 0.4;
        public double VolatilityMultiplier = 0.8;
        public double AsiaMultiplier = 0.9;
        public double LondonMultiplier = 1.0;
        public double NewYorkMultiplier = 1.0;
        public double OtherMultiplier = 0.9;
        public NewsFilterMode NewsMode = NewsFilterMode.ReduceScore;
        public double NewsMultiplier = 0.5;
        public double MinExactShare = 0.90;     // below this the bar is "estimated"
        public bool AllowEstimated = false;     // false: no signals on estimated bars
        public double EstimatedMultiplier = 0.8;
        public bool UseDepthEvidence = false;   // realtime-only evidence; OFF keeps historical == realtime logic

        // pressure weights
        public double WDelta = 1.5, WDeltaSpike = 1.0, WCvd = 1.0, WLarge = 1.0, WImbalance = 1.0, WVolume = 0.75, WLocation = 0.75, WPrice = 1.0;
        // reversal weights
        public double WAbsorption = 2.0, WDivergence = 1.25, WRevLocation = 1.0, WAbsorbedLarge = 0.75, WTurn = 1.0, WRevVolume = 0.5, WRevPrice = 1.0, WDepth = 0.5;
    }

    /// <summary>Immutable once created. Stamped with the CLOSE of the bar it was evaluated on.</summary>
    public sealed class SignalRecord
    {
        public int Id;
        public int Bar;
        public DateTime Time;           // bar timestamp (for time-based bars = bar close time)
        public double Price;            // bar close
        public SignalKind Kind;
        public int Direction;
        public double Score;
        public double BullPressure, BearPressure, BullReversal, BearReversal;
        public string Components;
        public string Flags;
        public bool IsRealtime;
        public string Window;
        public long ReferenceLevelTick = long.MinValue;
    }

    public sealed class SignalContext
    {
        public BarRecord Bar;
        public BarRecord Prev;
        public BarHistory History;
        public double TickSize;
        public List<AbsorptionCandidate> ConfirmedAbsorption;
        public DivergenceEngine Divergence;
        public List<double> ReferenceLevels;    // VAL/VAH/POCs/virgin POCs/LVN/HVN prices
        public List<double> HvnPrices;
        public ProfileStats DayStats;
        public double AtrBaseline;
        public string Window;
        public bool NewsActive;
        public bool IsRealtime;
        public double LargeThreshold;
    }

    public sealed class SignalEngine
    {
        public SignalSettings S = new SignalSettings();
        public readonly List<SignalRecord> Signals = new List<SignalRecord>();
        public SignalRecord LastSignal;
        public SignalKind LastState = SignalKind.None;
        public double LastBullScore, LastBearScore;
        private int nextId = 1;
        private int lastBullBar = -1000000, lastBearBar = -1000000;

        public SignalRecord Evaluate(SignalContext c)
        {
            BarRecord r = c.Bar, p = c.Prev;
            if (r == null || p == null || r.Volume <= 0) { LastState = SignalKind.None; return null; }
            StringBuilder comp = new StringBuilder();
            List<string> flags = new List<string>();

            // ---------- shared features
            double deltaPct = r.DeltaPct;
            double cvdTrend = CvdTrend(c);
            double largeNet = LargeNet(c);
            double volC = double.IsNaN(r.VolumeRatio) ? 0 : PriceMath.Clamp01((r.VolumeRatio - 1.0) / 1.5);
            double dSpike = double.IsNaN(r.DeltaRatio) ? 0 : PriceMath.Clamp01((r.DeltaRatio - 1.0) / 1.5);
            double range = r.High - r.Low;
            double closeLoc = range > 0 ? (r.Close - r.Low) / range : 0.5;

            // ---------- bullish pressure
            double bp = 0;
            {
                double cDelta = PriceMath.Clamp01(deltaPct / S.DeltaPctFull);
                double cSpike = r.Delta > 0 ? dSpike : 0;
                double cCvd = PriceMath.Clamp01(cvdTrend);
                double cLarge = PriceMath.Clamp01(largeNet);
                double cImb = r.BuyStacks >= 2 ? 1.0 : (r.BuyStacks == 1 ? 0.7 : 0.0);
                double cVol = r.Delta > 0 ? volC : 0;
                double cLoc = PressureLocation(c, +1);
                double cPrice = r.Close > p.High ? 1.0 : ((r.Close > p.Close && closeLoc >= 0.7) ? 0.6 : 0.0);
                bool gate = r.Delta > 0 && deltaPct >= S.MinDeltaPct && cPrice >= 0.6;
                if (gate)
                    bp = Weighted(new double[] { cDelta, cSpike, cCvd, cLarge, cImb, cVol, cLoc, cPrice },
                                  new double[] { S.WDelta, S.WDeltaSpike, S.WCvd, S.WLarge, S.WImbalance, S.WVolume, S.WLocation, S.WPrice });
                comp.AppendFormat(CultureInfo.InvariantCulture, "BP[d={0:0.00};sp={1:0.00};cvd={2:0.00};lg={3:0.00};imb={4:0.00};vol={5:0.00};loc={6:0.00};px={7:0.00};gate={8}] ",
                    cDelta, cSpike, cCvd, cLarge, cImb, cVol, cLoc, cPrice, gate ? 1 : 0);
            }
            // ---------- bearish pressure
            double sp = 0;
            {
                double cDelta = PriceMath.Clamp01(-deltaPct / S.DeltaPctFull);
                double cSpike = r.Delta < 0 ? dSpike : 0;
                double cCvd = PriceMath.Clamp01(-cvdTrend);
                double cLarge = PriceMath.Clamp01(-largeNet);
                double cImb = r.SellStacks >= 2 ? 1.0 : (r.SellStacks == 1 ? 0.7 : 0.0);
                double cVol = r.Delta < 0 ? volC : 0;
                double cLoc = PressureLocation(c, -1);
                double cPrice = r.Close < p.Low ? 1.0 : ((r.Close < p.Close && closeLoc <= 0.3) ? 0.6 : 0.0);
                bool gate = r.Delta < 0 && -deltaPct >= S.MinDeltaPct && cPrice >= 0.6;
                if (gate)
                    sp = Weighted(new double[] { cDelta, cSpike, cCvd, cLarge, cImb, cVol, cLoc, cPrice },
                                  new double[] { S.WDelta, S.WDeltaSpike, S.WCvd, S.WLarge, S.WImbalance, S.WVolume, S.WLocation, S.WPrice });
                comp.AppendFormat(CultureInfo.InvariantCulture, "SP[d={0:0.00};sp={1:0.00};cvd={2:0.00};lg={3:0.00};imb={4:0.00};vol={5:0.00};loc={6:0.00};px={7:0.00};gate={8}] ",
                    cDelta, cSpike, cCvd, cLarge, cImb, cVol, cLoc, cPrice, gate ? 1 : 0);
            }
            // ---------- reversals
            long bullRef, bearRef;
            double br = Reversal(c, +1, comp, out bullRef);
            double sr = Reversal(c, -1, comp, out bearRef);

            // ---------- context multipliers
            double mult = SessionMultiplier(c.Window);
            if (!double.IsNaN(c.AtrBaseline) && c.AtrBaseline > 0 && !double.IsNaN(r.AtrTicks))
            {
                double vr = r.AtrTicks / c.AtrBaseline;
                if (vr > S.VolatilityHigh) { mult *= S.VolatilityMultiplier; flags.Add("HIGHVOL"); }
                else if (vr < S.VolatilityLow) { mult *= S.VolatilityMultiplier; flags.Add("LOWVOL"); }
            }
            bool suppress = false;
            if (c.NewsActive && S.NewsMode != NewsFilterMode.Off)
            {
                flags.Add("NEWS");
                if (S.NewsMode == NewsFilterMode.ReduceScore) mult *= S.NewsMultiplier;
                else suppress = true;
            }
            if (r.ExactShare < S.MinExactShare)
            {
                flags.Add("EST");
                if (!S.AllowEstimated) suppress = true;
                else mult *= S.EstimatedMultiplier;
            }
            bp *= mult; sp *= mult; br *= mult; sr *= mult;

            double bull = Math.Max(bp, br), bear = Math.Max(sp, sr);
            LastBullScore = bull; LastBearScore = bear;

            SignalKind kind = SignalKind.None;
            int dir = 0;
            double score = 0;
            long refTick = long.MinValue;
            if (bull >= S.Threshold && bear >= S.ConflictLevel) kind = SignalKind.Conflict;
            else if (bear >= S.Threshold && bull >= S.ConflictLevel) kind = SignalKind.Conflict;
            else if (bull >= S.Threshold)
            {
                dir = +1; score = bull;
                kind = br >= bp ? SignalKind.PotentialBullishReversal : SignalKind.StrongBullishPressure;
                if (kind == SignalKind.PotentialBullishReversal) refTick = bullRef;
            }
            else if (bear >= S.Threshold)
            {
                dir = -1; score = bear;
                kind = sr >= sp ? SignalKind.PotentialBearishReversal : SignalKind.StrongBearishPressure;
                if (kind == SignalKind.PotentialBearishReversal) refTick = bearRef;
            }
            if (kind == SignalKind.Conflict) score = Math.Max(bull, bear);

            if (suppress && kind != SignalKind.None && kind != SignalKind.Conflict)
            {
                LastState = SignalKind.None;
                return null;
            }
            if (dir > 0 && r.Bar - lastBullBar < S.CooldownBars) { LastState = kind; return null; }
            if (dir < 0 && r.Bar - lastBearBar < S.CooldownBars) { LastState = kind; return null; }

            LastState = kind;
            if (kind == SignalKind.None) return null;

            SignalRecord s = new SignalRecord();
            s.Id = nextId++;
            s.Bar = r.Bar; s.Time = r.Time; s.Price = r.Close; s.Kind = kind; s.Direction = dir;
            s.Score = Math.Round(PriceMath.Clamp(score, 0, 100), 1);
            s.BullPressure = Math.Round(bp, 1); s.BearPressure = Math.Round(sp, 1);
            s.BullReversal = Math.Round(br, 1); s.BearReversal = Math.Round(sr, 1);
            s.Components = comp.ToString().Trim();
            s.Flags = string.Join("|", flags.ToArray());
            s.IsRealtime = c.IsRealtime;
            s.Window = c.Window;
            s.ReferenceLevelTick = refTick;
            if (dir > 0) lastBullBar = r.Bar;
            if (dir < 0) lastBearBar = r.Bar;
            Signals.Add(s);
            if (Signals.Count > 5000) Signals.RemoveAt(0);
            LastSignal = s;
            return s;
        }

        private static double Weighted(double[] c, double[] w)
        {
            double sum = 0, ws = 0;
            for (int i = 0; i < c.Length; i++) { sum += PriceMath.Clamp01(c[i]) * Math.Max(0, w[i]); ws += Math.Max(0, w[i]); }
            return ws > 0 ? 100.0 * sum / ws : 0;
        }

        private double CvdTrend(SignalContext c)
        {
            BarRecord r = c.Bar;
            double absSum = 0;
            BarRecord first = null;
            for (int k = 0; k < S.CvdTrendBars; k++)
            {
                BarRecord b = c.History.Get(r.Bar - k);
                if (b == null || b.CvdResetId != r.CvdResetId) break;
                absSum += Math.Abs(b.Delta);
                first = b;
            }
            if (first == null || absSum <= 0) return 0;
            return (r.CvdClose - first.CvdOpen) / absSum;   // in [-1, 1]
        }

        private double LargeNet(SignalContext c)
        {
            long lb = 0, ls = 0;
            for (int k = 0; k < S.LargeTradeBars; k++)
            {
                BarRecord b = c.History.Get(c.Bar.Bar - k);
                if (b == null) break;
                lb += b.LargeBuyVolume; ls += b.LargeSellVolume;
            }
            if (lb + ls == 0) return 0;
            return (double)(lb - ls) / (lb + ls);
        }

        private double PressureLocation(SignalContext c, int dir)
        {
            ProfileStats st = c.DayStats;
            if (st == null || !st.IsValid) return 0.3;
            double ts = c.TickSize;
            double close = c.Bar.Close, poc = st.PocPrice(ts), vah = st.VahPrice(ts), val = st.ValPrice(ts);
            double v;
            if (dir > 0) v = close > vah ? 1.0 : (close > poc ? 0.6 : 0.2);
            else v = close < val ? 1.0 : (close < poc ? 0.6 : 0.2);
            // opposing HVN right in front of price acts as a magnet/obstacle -> halve
            if (c.HvnPrices != null)
                for (int i = 0; i < c.HvnPrices.Count; i++)
                {
                    double d = (c.HvnPrices[i] - close) * dir / ts;
                    if (d > 0 && d <= S.LocationTicks) { v *= 0.5; break; }
                }
            return v;
        }

        private double Reversal(SignalContext c, int dir, StringBuilder comp, out long refTick)
        {
            refTick = long.MinValue;
            BarRecord r = c.Bar, p = c.Prev;
            AbsorptionCandidate abs = null;
            if (c.ConfirmedAbsorption != null)
                for (int i = 0; i < c.ConfirmedAbsorption.Count; i++)
                    if (c.ConfirmedAbsorption[i].Direction == dir && (abs == null || c.ConfirmedAbsorption[i].Strength > abs.Strength))
                        abs = c.ConfirmedAbsorption[i];
            int lastDiv = dir > 0 ? c.Divergence.LastBullBar : c.Divergence.LastBearBar;
            bool divRecent = r.Bar - lastDiv <= S.DivergenceMemoryBars;

            double cAbs = abs != null ? abs.Strength : 0;
            double cDiv = divRecent ? 1.0 - (double)(r.Bar - lastDiv) / (S.DivergenceMemoryBars + 1) : 0;
            double level = abs != null ? abs.LevelPrice : (dir > 0 ? r.Low : r.High);
            if (abs != null) refTick = abs.LevelTick;
            double cLoc = 0;
            if (c.ReferenceLevels != null)
                for (int i = 0; i < c.ReferenceLevels.Count; i++)
                    if (Math.Abs(c.ReferenceLevels[i] - level) <= S.LocationTicks * c.TickSize) { cLoc = 1.0; break; }
            double thr = Math.Max(1, c.LargeThreshold);
            double cLarge = abs != null ? PriceMath.Clamp01(abs.LargeVolume / (2.0 * thr)) : 0;
            double cTurn = PriceMath.Clamp01(dir * r.DeltaPct / 0.3);
            BarRecord vb = abs != null ? c.History.Get(abs.Bar) : r;
            double cVol = (vb != null && !double.IsNaN(vb.VolumeRatio)) ? PriceMath.Clamp01((vb.VolumeRatio - 1.0) / 1.5) : 0;
            double cPrice = (dir > 0 ? r.Close > p.High : r.Close < p.Low) ? 1.0 : 0.5;
            double cDepth = (S.UseDepthEvidence && abs != null && abs.Replenished) ? 1.0 : 0.0;
            bool gate = (abs != null || divRecent) && (dir > 0 ? r.Close > p.Close : r.Close < p.Close);

            double score = 0;
            if (gate)
            {
                List<double> cs = new List<double>(new double[] { cAbs, cDiv, cLoc, cLarge, cTurn, cVol, cPrice });
                List<double> ws = new List<double>(new double[] { S.WAbsorption, S.WDivergence, S.WRevLocation, S.WAbsorbedLarge, S.WTurn, S.WRevVolume, S.WRevPrice });
                if (S.UseDepthEvidence) { cs.Add(cDepth); ws.Add(S.WDepth); }
                score = Weighted(cs.ToArray(), ws.ToArray());
            }
            comp.AppendFormat(CultureInfo.InvariantCulture, "{0}[abs={1:0.00};div={2:0.00};loc={3:0.00};lg={4:0.00};turn={5:0.00};vol={6:0.00};px={7:0.00};dom={8:0.00};gate={9}] ",
                dir > 0 ? "BR" : "SR", cAbs, cDiv, cLoc, cLarge, cTurn, cVol, cPrice, cDepth, gate ? 1 : 0);
            return score;
        }

        private double SessionMultiplier(string window)
        {
            if (string.IsNullOrEmpty(window) || window == "-") return S.OtherMultiplier;
            if (window.IndexOf("NewYork", StringComparison.OrdinalIgnoreCase) >= 0 || window.IndexOf("COMEX", StringComparison.OrdinalIgnoreCase) >= 0) return S.NewYorkMultiplier;
            if (window.IndexOf("London", StringComparison.OrdinalIgnoreCase) >= 0) return S.LondonMultiplier;
            if (window.IndexOf("Asia", StringComparison.OrdinalIgnoreCase) >= 0) return S.AsiaMultiplier;
            return S.OtherMultiplier;
        }
    }

    /// <summary>
    /// Post-hoc outcome measurement for statistical validation ONLY. It never feeds back into signal generation.
    /// For each signal: MFE / MAE (ticks, in the signal direction) and close-to-close move after H bars.
    /// </summary>
    public sealed class OutcomeTracker
    {
        public int[] Horizons = new int[] { 5, 10, 20 };
        private sealed class Open
        {
            public SignalRecord Signal;
            public double Mfe, Mae;
            public int Done;
        }
        private readonly List<Open> open = new List<Open>();

        public void Track(SignalRecord s) { Open o = new Open(); o.Signal = s; open.Add(o); }

        public void OnBar(BarRecord r, double tickSize, List<string> outcomeLines)
        {
            for (int i = open.Count - 1; i >= 0; i--)
            {
                Open o = open[i];
                int age = r.Bar - o.Signal.Bar;
                if (age <= 0) continue;
                int d = o.Signal.Direction == 0 ? 1 : o.Signal.Direction;
                double fav = d > 0 ? (r.High - o.Signal.Price) : (o.Signal.Price - r.Low);
                double adv = d > 0 ? (o.Signal.Price - r.Low) : (r.High - o.Signal.Price);
                o.Mfe = Math.Max(o.Mfe, fav / tickSize);
                o.Mae = Math.Max(o.Mae, adv / tickSize);
                while (o.Done < Horizons.Length && age >= Horizons[o.Done])
                {
                    double ret = d * (r.Close - o.Signal.Price) / tickSize;
                    if (outcomeLines != null)
                        outcomeLines.Add(string.Format(CultureInfo.InvariantCulture, "{0},{1},{2:yyyy-MM-dd HH:mm:ss.fff},{3},{4},{5:0.0},{6:0.0},{7:0.0}",
                            o.Signal.Id, o.Signal.Kind, o.Signal.Time, Horizons[o.Done], o.Signal.IsRealtime ? "RT" : "HIST", o.Mfe, o.Mae, ret));
                    o.Done++;
                }
                if (o.Done >= Horizons.Length) open.RemoveAt(i);
            }
        }
    }

    /// <summary>Append-only CSV logger. Failures are reported, never thrown into the data thread.</summary>
    public sealed class SignalCsvLogger : IDisposable
    {
        private StreamWriter signals;
        private StreamWriter outcomes;
        private StreamWriter events;
        public string LastError = string.Empty;
        public string Folder = string.Empty;

        public bool Open(string folder, string instrument)
        {
            try
            {
                Folder = folder;
                Directory.CreateDirectory(folder);
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                string safe = MakeSafe(instrument);
                signals = new StreamWriter(Path.Combine(folder, "signals_" + safe + "_" + stamp + ".csv"), false, Encoding.UTF8);
                signals.WriteLine("id,mode,bar_time,bar,kind,direction,score,price,bull_pressure,bear_pressure,bull_reversal,bear_reversal,window,flags,components");
                outcomes = new StreamWriter(Path.Combine(folder, "outcomes_" + safe + "_" + stamp + ".csv"), false, Encoding.UTF8);
                outcomes.WriteLine("id,kind,bar_time,horizon_bars,mode,mfe_ticks,mae_ticks,close_ret_ticks");
                events = new StreamWriter(Path.Combine(folder, "events_" + safe + "_" + stamp + ".csv"), false, Encoding.UTF8);
                events.WriteLine("time,bar,type,side,price,volume,value,mode,message");
                return true;
            }
            catch (Exception ex) { LastError = ex.Message; Dispose(); return false; }
        }

        private static string MakeSafe(string s)
        {
            StringBuilder b = new StringBuilder();
            foreach (char ch in s ?? "inst") b.Append(char.IsLetterOrDigit(ch) ? ch : '_');
            return b.ToString();
        }

        private static string Q(string s) { return "\"" + (s ?? string.Empty).Replace("\"", "'") + "\""; }

        public void WriteSignal(SignalRecord s)
        {
            if (signals == null) return;
            try
            {
                signals.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0},{1},{2:yyyy-MM-dd HH:mm:ss.fff},{3},{4},{5},{6:0.0},{7},{8:0.0},{9:0.0},{10:0.0},{11:0.0},{12},{13},{14}",
                    s.Id, s.IsRealtime ? "RT" : "HIST", s.Time, s.Bar, s.Kind, s.Direction, s.Score, s.Price.ToString(CultureInfo.InvariantCulture),
                    s.BullPressure, s.BearPressure, s.BullReversal, s.BearReversal, Q(s.Window), Q(s.Flags), Q(s.Components)));
                signals.Flush();
            }
            catch (Exception ex) { LastError = ex.Message; }
        }

        public void WriteOutcome(string line)
        {
            if (outcomes == null) return;
            try { outcomes.WriteLine(line); outcomes.Flush(); } catch (Exception ex) { LastError = ex.Message; }
        }

        public void WriteEvent(OrderFlowEvent e)
        {
            if (events == null) return;
            try
            {
                events.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0:yyyy-MM-dd HH:mm:ss.fff},{1},{2},{3},{4},{5},{6:0.###},{7},{8}",
                    e.Time, e.Bar, e.Type, e.Side, e.Price.ToString(CultureInfo.InvariantCulture), e.Volume, e.Value, e.IsRealtime ? "RT" : "HIST", Q(e.Message)));
            }
            catch (Exception ex) { LastError = ex.Message; }
        }

        public void FlushEvents() { try { if (events != null) events.Flush(); } catch (Exception) { } }

        public void Dispose()
        {
            try { if (signals != null) signals.Dispose(); } catch (Exception) { }
            try { if (outcomes != null) outcomes.Dispose(); } catch (Exception) { }
            try { if (events != null) events.Dispose(); } catch (Exception) { }
            signals = null; outcomes = null; events = null;
        }
    }
}
