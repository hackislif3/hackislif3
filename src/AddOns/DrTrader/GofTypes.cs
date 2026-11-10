// Gold Institutional Order Flow Analyzer (GOF) - core types
// Pure C# (no NinjaTrader dependency) so the engines can be unit-tested outside NinjaTrader.
// Language level: C# 5 (compatible with the NinjaScript compiler).
using System;
using System.Collections.Generic;
using System.Globalization;

namespace NinjaTrader.NinjaScript.AddOns.DrTrader
{
    /// <summary>Side of the aggressor (the order that crossed the spread and caused the print).</summary>
    public enum AggressorSide { Unknown = 0, Buy = 1, Sell = -1 }

    /// <summary>How a print was classified. Only QuoteExact is a direct observation of bid/ask at print time.</summary>
    public enum ClassificationSource
    {
        QuoteExact = 0,     // price >= ask (buy) or price <= bid (sell), with a valid, non-locked quote
        QuoteInside = 1,    // print strictly inside the spread, classified by the configured inside-spread rule
        TickRule = 2,       // no usable quote: uptick/downtick rule -> ESTIMATED
        Unclassified = 3    // counted as volume, not as delta
    }

    public enum InsideSpreadMethod { MidpointThenTickRule = 0, TickRule = 1, Neutral = 2 }

    public enum CvdResetMode { Session = 0, TradingDay = 1, Week = 2, Never = 3 }

    public enum LargeTradeThresholdMode { Manual = 0, Adaptive = 1, MaxOfManualAndAdaptive = 2 }

    public enum ThresholdSource { Manual = 0, Adaptive = 1, ManualWarmup = 2 }

    public enum ValueAreaMethod { TwoRowAuction = 0, SingleRow = 1 }

    public enum ProfileScope { ETH = 0, RTH = 1 }

    public enum ProfileKind { Session = 0, Day = 1, Week = 2, SubSession = 3, Range = 4, Fixed = 5, Overnight = 6, Rth = 7, PostRth = 8 }

    public enum SignalKind
    {
        None = 0,
        StrongBullishPressure = 1,
        StrongBearishPressure = 2,
        PotentialBullishReversal = 3,
        PotentialBearishReversal = 4,
        Conflict = 5
    }

    public enum NewsFilterMode { Off = 0, ReduceScore = 1, SuppressSignals = 2, SuppressSignalsAndAlerts = 3 }

    public enum DataQualityLevel { NoData = 0, Exact = 1, MostlyExact = 2, Inferred = 3, Estimated = 4 }

    public enum AbsorptionState { Pending = 0, Confirmed = 1, Invalidated = 2, Expired = 3 }

    public enum OrderFlowEventType
    {
        LargeTrade, LargeTradeCluster, RepeatedExecution, StackedImbalance, VolumeSpike, DeltaSpike,
        DeltaExhaustion, Divergence, AbsorptionCandidate, AbsorptionConfirmed, AbsorptionInvalidated,
        Signal, PocTouch, PocCross, DataGap, Replenishment, Info
    }

    /// <summary>One executed trade as received from the feed (one Last event / one 1-tick bar).</summary>
    public struct TradePrint
    {
        public DateTime Time;
        public double Price;
        public long Volume;
        public double Bid;          // bid at print time (0 = unknown)
        public double Ask;          // ask at print time (0 = unknown)
        public long Tick;           // price expressed in ticks (set by the engine)
        public AggressorSide Side;  // set by the classifier
        public ClassificationSource Source;
        public bool IsRealtime;
    }

    public static class PriceMath
    {
        public static long ToTick(double price, double tickSize)
        {
            return (long)Math.Round(price / tickSize, MidpointRounding.AwayFromZero);
        }

        public static double ToPrice(long tick, double tickSize)
        {
            return Math.Round(tick * tickSize, 10);
        }

        public static long FloorDiv(long a, long b)
        {
            long q = a / b;
            if ((a % b != 0) && ((a < 0) != (b < 0))) q--;
            return q;
        }

        public static double Clamp(double v, double lo, double hi)
        {
            if (double.IsNaN(v)) return lo;
            return v < lo ? lo : (v > hi ? hi : v);
        }

        public static double Clamp01(double v) { return Clamp(v, 0.0, 1.0); }

        public static DateTime WeekStart(DateTime tradingDay)
        {
            // Monday-based trading week. The Sunday-evening Globex session belongs to Monday's trading day,
            // so the week is determined by the exchange trading day, not by the PC calendar date.
            DateTime d = tradingDay.Date;
            int diff = ((int)d.DayOfWeek + 6) % 7;
            return d.AddDays(-diff);
        }
    }

    /// <summary>Contract specification + starting presets. Values are starting points, not optimized parameters.</summary>
    public sealed class GoldContractSpec
    {
        public string Root;
        public double TickSize;
        public double PointValue;
        public int ManualLargeThreshold;
        public int ImbalanceMinVolume;
        public int RepeatMinVolume;
        public bool IsGold;

        public double TickValue { get { return TickSize * PointValue; } }

        public static GoldContractSpec Resolve(string masterName, double instrumentTickSize, double instrumentPointValue, out string warning)
        {
            warning = string.Empty;
            GoldContractSpec s = new GoldContractSpec();
            s.Root = masterName == null ? string.Empty : masterName.Trim().ToUpperInvariant();
            s.TickSize = instrumentTickSize;
            s.PointValue = instrumentPointValue;

            if (s.Root == "GC")
            {
                s.IsGold = true;
                s.ManualLargeThreshold = 20;
                s.ImbalanceMinVolume = 5;
                s.RepeatMinVolume = 100;
                if (Math.Abs(instrumentTickSize - 0.1) > 1e-9 || Math.Abs(instrumentPointValue - 100.0) > 1e-9)
                    warning = "GC spec mismatch: expected tick 0.10 / $10 per tick, instrument reports tick " +
                              instrumentTickSize.ToString(CultureInfo.InvariantCulture) + ", point value " +
                              instrumentPointValue.ToString(CultureInfo.InvariantCulture);
            }
            else if (s.Root == "MGC")
            {
                s.IsGold = true;
                s.ManualLargeThreshold = 30;
                s.ImbalanceMinVolume = 10;
                s.RepeatMinVolume = 150;
                if (Math.Abs(instrumentTickSize - 0.1) > 1e-9 || Math.Abs(instrumentPointValue - 10.0) > 1e-9)
                    warning = "MGC spec mismatch: expected tick 0.10 / $1 per tick";
            }
            else
            {
                s.IsGold = false;
                s.ManualLargeThreshold = 20;
                s.ImbalanceMinVolume = 5;
                s.RepeatMinVolume = 100;
                warning = "Instrument '" + s.Root + "' is not GC/MGC: gold presets are not validated for it.";
            }
            return s;
        }
    }

    /// <summary>Generic event emitted by the engines (used for alerts, logging and markers).</summary>
    public sealed class OrderFlowEvent
    {
        public OrderFlowEventType Type;
        public DateTime Time;
        public int Bar;
        public double Price;
        public int Side;            // +1 bullish/buy, -1 bearish/sell, 0 n/a
        public long Volume;
        public double Value;
        public string Message;
        public bool IsRealtime;
    }

    /// <summary>Fixed-capacity rolling window with O(1) mean / standard deviation.</summary>
    public sealed class RollingWindow
    {
        private readonly double[] data;
        private int count;
        private int head;
        private double sum;
        private double sumSq;

        public RollingWindow(int capacity)
        {
            data = new double[Math.Max(1, capacity)];
        }

        public int Count { get { return count; } }
        public int Capacity { get { return data.Length; } }

        public void Add(double v)
        {
            if (count == data.Length)
            {
                double old = data[head];
                sum -= old;
                sumSq -= old * old;
            }
            else count++;
            data[head] = v;
            sum += v;
            sumSq += v * v;
            head = (head + 1) % data.Length;
        }

        public double Mean { get { return count == 0 ? double.NaN : sum / count; } }

        public double StdDev
        {
            get
            {
                if (count < 2) return double.NaN;
                double m = sum / count;
                double var = (sumSq - count * m * m) / (count - 1);
                return var > 0 ? Math.Sqrt(var) : 0.0;
            }
        }

        public void Clear() { count = 0; head = 0; sum = 0; sumSq = 0; }
    }
}
