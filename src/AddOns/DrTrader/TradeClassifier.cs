// GOF - aggressor classification and data-quality monitoring.
using System;
using System.Globalization;

namespace NinjaTrader.NinjaScript.AddOns.DrTrader
{
    /// <summary>
    /// Classifies each executed print as aggressive buy / aggressive sell.
    ///  * price >= ask                -> Buy  (QuoteExact)
    ///  * price <= bid                -> Sell (QuoteExact)
    ///  * bid < price < ask           -> InsideSpreadMethod (QuoteInside, inferred)
    ///  * no valid / locked / crossed quote -> tick rule (TickRule, ESTIMATED)
    /// Tick rule: uptick = buy, downtick = sell, zero tick = previous tick direction.
    /// </summary>
    public sealed class TradeClassifier
    {
        public InsideSpreadMethod InsideMethod;
        private double lastPrice;
        private AggressorSide lastDirection = AggressorSide.Unknown;

        public TradeClassifier(InsideSpreadMethod method)
        {
            InsideMethod = method;
        }

        public void Reset()
        {
            lastPrice = 0;
            lastDirection = AggressorSide.Unknown;
        }

        public void Classify(ref TradePrint t, double tickSize)
        {
            double eps = tickSize * 0.01;
            AggressorSide tickDir = TickRule(t.Price);
            bool quoteValid = t.Bid > 0 && t.Ask > 0 && t.Ask > t.Bid + eps;

            if (quoteValid)
            {
                if (t.Price >= t.Ask - eps) { t.Side = AggressorSide.Buy; t.Source = ClassificationSource.QuoteExact; }
                else if (t.Price <= t.Bid + eps) { t.Side = AggressorSide.Sell; t.Source = ClassificationSource.QuoteExact; }
                else
                {
                    switch (InsideMethod)
                    {
                        case InsideSpreadMethod.MidpointThenTickRule:
                            double mid = (t.Bid + t.Ask) * 0.5;
                            if (t.Price > mid + eps) { t.Side = AggressorSide.Buy; t.Source = ClassificationSource.QuoteInside; }
                            else if (t.Price < mid - eps) { t.Side = AggressorSide.Sell; t.Source = ClassificationSource.QuoteInside; }
                            else
                            {
                                t.Side = tickDir;
                                t.Source = tickDir == AggressorSide.Unknown ? ClassificationSource.Unclassified : ClassificationSource.TickRule;
                            }
                            break;
                        case InsideSpreadMethod.TickRule:
                            t.Side = tickDir;
                            t.Source = tickDir == AggressorSide.Unknown ? ClassificationSource.Unclassified : ClassificationSource.TickRule;
                            break;
                        default:
                            t.Side = AggressorSide.Unknown;
                            t.Source = ClassificationSource.Unclassified;
                            break;
                    }
                }
            }
            else
            {
                t.Side = tickDir;
                t.Source = tickDir == AggressorSide.Unknown ? ClassificationSource.Unclassified : ClassificationSource.TickRule;
            }

            lastPrice = t.Price;
            if (tickDir != AggressorSide.Unknown) lastDirection = tickDir;
        }

        private AggressorSide TickRule(double price)
        {
            if (lastPrice <= 0) return AggressorSide.Unknown;
            if (price > lastPrice) return AggressorSide.Buy;
            if (price < lastPrice) return AggressorSide.Sell;
            return lastDirection;
        }
    }

    /// <summary>Tracks how trustworthy the classified data is. Never hides problems: they are surfaced on the dashboard.</summary>
    public sealed class DataQualityMonitor
    {
        public long TotalPrints;
        public long TotalVolume;
        public long ExactVolume;
        public long InsideVolume;
        public long EstimatedVolume;
        public long UnclassifiedVolume;
        public long CrossedOrLockedQuotes;
        public long OutOfOrder;
        public long Rejected;
        public long DuplicatesSuppressed;
        public int Gaps;
        public double MaxGapSeconds;
        public DateTime LastTradeTime = DateTime.MinValue;
        public DateTime LastGapTime = DateTime.MinValue;
        public string SourceDescription = "n/a";
        public string Warning = string.Empty;
        public double GapSecondsThreshold = 120;

        // per-session counters (reset at session start)
        public long SessionVolume;
        public long SessionExactVolume;
        public long SessionEstimatedVolume;

        public void SessionReset()
        {
            SessionVolume = 0;
            SessionExactVolume = 0;
            SessionEstimatedVolume = 0;
        }

        /// <summary>Returns true when a data gap was detected before this print.</summary>
        public bool OnTrade(ref TradePrint t, bool gapCheckActive)
        {
            bool gap = false;
            TotalPrints++;
            TotalVolume += t.Volume;
            SessionVolume += t.Volume;
            switch (t.Source)
            {
                case ClassificationSource.QuoteExact: ExactVolume += t.Volume; SessionExactVolume += t.Volume; break;
                case ClassificationSource.QuoteInside: InsideVolume += t.Volume; break;
                case ClassificationSource.TickRule: EstimatedVolume += t.Volume; SessionEstimatedVolume += t.Volume; break;
                default: UnclassifiedVolume += t.Volume; break;
            }
            if (t.Bid > 0 && t.Ask > 0 && t.Ask <= t.Bid) CrossedOrLockedQuotes++;

            if (LastTradeTime != DateTime.MinValue)
            {
                double dt = (t.Time - LastTradeTime).TotalSeconds;
                if (dt < -1.0) OutOfOrder++;
                if (gapCheckActive && dt > GapSecondsThreshold)
                {
                    Gaps++;
                    gap = true;
                    LastGapTime = t.Time;
                    if (dt > MaxGapSeconds) MaxGapSeconds = dt;
                }
            }
            if (t.Time > LastTradeTime) LastTradeTime = t.Time;
            return gap;
        }

        public double ExactShare { get { return TotalVolume == 0 ? 0 : (double)ExactVolume / TotalVolume; } }
        public double EstimatedShare { get { return TotalVolume == 0 ? 0 : (double)(EstimatedVolume + UnclassifiedVolume) / TotalVolume; } }
        public double SessionExactShare { get { return SessionVolume == 0 ? 0 : (double)SessionExactVolume / SessionVolume; } }

        public DataQualityLevel Level
        {
            get
            {
                if (TotalVolume == 0) return DataQualityLevel.NoData;
                double est = EstimatedShare;
                if (est > 0.5) return DataQualityLevel.Estimated;
                double ex = ExactShare;
                if (ex >= 0.98) return DataQualityLevel.Exact;
                if (ex >= 0.90) return DataQualityLevel.MostlyExact;
                return DataQualityLevel.Inferred;
            }
        }

        public string StatusText
        {
            get
            {
                string s = Level.ToString() + " | exact " + (ExactShare * 100).ToString("0.0", CultureInfo.InvariantCulture) +
                           "% est " + (EstimatedShare * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";
                if (Gaps > 0) s += " | gaps " + Gaps;
                if (OutOfOrder > 0) s += " | ooo " + OutOfOrder;
                if (CrossedOrLockedQuotes > 0) s += " | x-quotes " + CrossedOrLockedQuotes;
                return s;
            }
        }
    }
}
