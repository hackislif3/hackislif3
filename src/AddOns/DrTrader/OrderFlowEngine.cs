// GOF - engine facade. Owns every module and defines the exact processing order:
//   SessionStart -> BeginBar -> ProcessTrade (per print) -> FinalizeBar (once, when the bar is complete).
// The NinjaScript indicators only translate NinjaTrader events into these calls and render the results.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace NinjaTrader.NinjaScript.AddOns.DrTrader
{
    public sealed class EngineSettings
    {
        public double TickSize = 0.1;
        public string InstrumentName = "GC";
        public TimeZoneInfo PlatformZone = TimeZoneInfo.Local;
        public string ExchangeZoneId = "Eastern Standard Time";
        public InsideSpreadMethod InsideMethod = InsideSpreadMethod.MidpointThenTickRule;

        public bool EnableProfiles = true;
        public bool EnableLargeTrades = true;
        public bool EnableAbsorption = true;
        public bool EnableDivergence = true;
        public bool EnableSignals = true;
        public bool EnableDepth = false;
        public bool EnableSubSessions = true;

        public ProfileSettings DayProfile = new ProfileSettings();
        public ProfileSettings WeekProfile = new ProfileSettings();
        public ProfileScope Scope = ProfileScope.ETH;
        public int MaxHistoryDays = 10;
        public int MaxHistoryWeeks = 4;
        public int VirginToleranceTicks = 0;
        public int MaxVirginLevels = 20;
        public DateTime ContractStart = DateTime.MinValue;
        public DateTime FixedStart = DateTime.MinValue;
        public DateTime FixedEnd = DateTime.MinValue;

        public string RthRange = "08:20-13:30";
        public string AsiaRange = "18:00-03:00";      public string AsiaZone = "Eastern Standard Time";      public bool AsiaEnabled = true;
        public string LondonRange = "08:00-16:30";    public string LondonZone = "GMT Standard Time";        public bool LondonEnabled = true;
        public string NewYorkRange = "08:00-17:00";   public string NewYorkZone = "Eastern Standard Time";   public bool NewYorkEnabled = true;
        public string ComexRange = "08:20-13:30";     public string ComexZone = "Eastern Standard Time";     public bool ComexEnabled = true;
        public string OvernightRange = "18:00-08:20"; public string OvernightZone = "Eastern Standard Time"; public bool OvernightEnabled = true;

        public CvdResetMode CvdMode = CvdResetMode.Session;
        public LargeTradeSettings Large = new LargeTradeSettings();
        public BarAnalyticsSettings Bars = new BarAnalyticsSettings();
        public AbsorptionSettings Absorption = new AbsorptionSettings();
        public DivergenceSettings Divergence = new DivergenceSettings();
        public SignalSettings Signals = new SignalSettings();

        public int MaxFootprintBars = 3000;
        public double GapSeconds = 120;
        public string NewsText = string.Empty;
        public string NewsFile = string.Empty;
        public string NewsZone = "Eastern Standard Time";
        public int NewsMinutesBefore = 10;
        public int NewsMinutesAfter = 20;
        public int[] OutcomeHorizons = new int[] { 5, 10, 20 };
        public int RvolLookbackSessions = 10;
    }

    public sealed class OrderFlowEngine
    {
        public readonly object Sync = new object();
        public readonly EngineSettings Settings;
        public readonly TradeClassifier Classifier;
        public readonly DataQualityMonitor Quality = new DataQualityMonitor();
        public readonly SessionClock Clock = new SessionClock();
        public readonly VolumeProfileEngine Profiles = new VolumeProfileEngine();
        public readonly DeltaEngine Delta = new DeltaEngine();
        public readonly BarAnalytics Analytics = new BarAnalytics();
        public readonly LargeTradeEngine Large = new LargeTradeEngine();
        public readonly AbsorptionEngine Absorption = new AbsorptionEngine();
        public readonly DivergenceEngine Divergence = new DivergenceEngine();
        public readonly SignalEngine Signals = new SignalEngine();
        public readonly OutcomeTracker Outcomes = new OutcomeTracker();
        public readonly NewsFilter News = new NewsFilter();
        public readonly DepthReplenishmentMonitor Depth = new DepthReplenishmentMonitor();
        public readonly FootprintStore Footprints = new FootprintStore();
        public readonly BarHistory History = new BarHistory();
        public readonly List<StackedImbalanceZone> Zones = new List<StackedImbalanceZone>();
        public readonly List<string> Warnings = new List<string>();

        public DateTime CurrentTradingDay = DateTime.MinValue;
        public DateTime CurrentWeekStart = DateTime.MinValue;
        public DateTime SessionBegin = DateTime.MinValue;
        public BarFootprint Current;
        public bool IsRealtime;
        public bool HasSession { get { return CurrentTradingDay != DateTime.MinValue; } }
        public double LastRvol = double.NaN;
        public string LastWindow = "-";
        public double LastPrice = double.NaN;

        private readonly TimeZoneInfo exchangeZone;
        private long exMinute = long.MinValue;
        private TimeSpan exOffset;
        private readonly RelativeVolumeTracker sessionRvol;
        private readonly Dictionary<string, RelativeVolumeTracker> windowRvol = new Dictionary<string, RelativeVolumeTracker>();
        private readonly RollingWindow atrBaseline = new RollingWindow(200);
        private readonly List<OrderFlowEvent> events = new List<OrderFlowEvent>();
        private readonly List<string> outcomeLines = new List<string>();
        private readonly List<RepeatedExecutionEvent> repeatsRecent = new List<RepeatedExecutionEvent>();
        private readonly List<LargeTradeEvent> tmpLarge = new List<LargeTradeEvent>();
        private readonly List<LargeTradeCluster> tmpClusters = new List<LargeTradeCluster>();
        private readonly List<RepeatedExecutionEvent> tmpRepeats = new List<RepeatedExecutionEvent>();
        private readonly Dictionary<string, int> lastTouchBar = new Dictionary<string, int>();
        private readonly PriceLadder rangeLadder = new PriceLadder(512);

        public OrderFlowEngine(EngineSettings s)
        {
            Settings = s;
            Classifier = new TradeClassifier(s.InsideMethod);
            bool found;
            exchangeZone = TimeZoneResolver.Find(s.ExchangeZoneId, out found);
            if (!found) Warnings.Add("Exchange time zone not found: " + s.ExchangeZoneId + " (UTC used)");

            Clock.PlatformZone = s.PlatformZone ?? TimeZoneInfo.Local;
            Clock.SetRth(s.RthRange, s.ExchangeZoneId);
            Clock.AddWindow("Asia", s.AsiaRange, s.AsiaZone, s.AsiaEnabled);
            Clock.AddWindow("London", s.LondonRange, s.LondonZone, s.LondonEnabled);
            Clock.AddWindow("NewYork", s.NewYorkRange, s.NewYorkZone, s.NewYorkEnabled);
            Clock.AddWindow("COMEX", s.ComexRange, s.ComexZone, s.ComexEnabled);
            Clock.AddWindow("Overnight", s.OvernightRange, s.OvernightZone, s.OvernightEnabled);
            Warnings.AddRange(Clock.Errors);

            Profiles.TickSize = s.TickSize;
            Profiles.DaySettings = s.DayProfile;
            Profiles.WeekSettings = s.WeekProfile;
            Profiles.Scope = s.Scope;
            Profiles.MaxHistoryDays = s.MaxHistoryDays;
            Profiles.MaxHistoryWeeks = s.MaxHistoryWeeks;
            Profiles.VirginToleranceTicks = s.VirginToleranceTicks;
            Profiles.MaxVirginLevels = s.MaxVirginLevels;
            Profiles.ContractStartFilter = s.ContractStart;
            Profiles.TrackSubSessions = s.EnableSubSessions;
            Profiles.ConfigureFixedRange(s.FixedStart, s.FixedEnd);

            Delta.DisplayMode = s.CvdMode;
            Large.S = s.Large; Large.Init();
            Analytics.S = s.Bars; Analytics.Init();
            Absorption.S = s.Absorption;
            Divergence.S = s.Divergence;
            Signals.S = s.Signals;
            Outcomes.Horizons = s.OutcomeHorizons;
            Footprints.MaxBars = s.MaxFootprintBars;
            Quality.GapSecondsThreshold = s.GapSeconds;
            sessionRvol = new RelativeVolumeTracker(5, s.RvolLookbackSessions);

            News.MinutesBefore = s.NewsMinutesBefore;
            News.MinutesAfter = s.NewsMinutesAfter;
            bool nf;
            TimeZoneInfo nz = TimeZoneResolver.Find(s.NewsZone, out nf);
            News.Load(s.NewsText, s.NewsFile, nz, Clock.PlatformZone);
            Warnings.AddRange(News.Errors);
        }

        public DateTime ToExchange(DateTime platformTime)
        {
            long m = platformTime.Ticks / TimeSpan.TicksPerMinute;
            if (m != exMinute)
            {
                DateTime minuteStart = new DateTime(m * TimeSpan.TicksPerMinute);
                exOffset = TimeZoneResolver.Convert(minuteStart, Clock.PlatformZone, exchangeZone) - minuteStart;
                exMinute = m;
            }
            return platformTime + exOffset;
        }

        // =====================================================================================
        // Lifecycle
        // =====================================================================================

        public void OnSessionStart(DateTime tradingDay, DateTime sessionBegin, int bar)
        {
            bool first = CurrentTradingDay == DateTime.MinValue;
            bool newDay = first || tradingDay.Date != CurrentTradingDay.Date;
            DateTime ws = PriceMath.WeekStart(tradingDay);
            bool newWeek = first || ws != CurrentWeekStart;
            if (newDay && !first) Large.OnNewTradingDay();

            Profiles.OnSessionStart(tradingDay.Date, bar, newDay, newWeek);
            Delta.OnSessionStart(newDay, newWeek);
            Quality.SessionReset();
            sessionRvol.Begin(sessionBegin.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture));
            CurrentTradingDay = tradingDay.Date;
            CurrentWeekStart = ws;
            SessionBegin = sessionBegin;
        }

        public void BeginBar(int bar, DateTime time)
        {
            if (Current != null && Current.Bar == bar) return;
            Current = Footprints.GetOrCreate(bar, time);
            if (!Current.HasCvd) Delta.BeginBar(Current);
        }

        public void ProcessTrade(TradePrint t, int bar)
        {
            if (t.Volume <= 0 || t.Price <= 0 || double.IsNaN(t.Price) || double.IsInfinity(t.Price)) { Quality.Rejected++; return; }
            if (Current == null || Current.Bar != bar) BeginBar(bar, t.Time);
            double ts = Settings.TickSize;
            t.Tick = PriceMath.ToTick(t.Price, ts);
            t.IsRealtime = IsRealtime;
            Classifier.Classify(ref t, ts);
            LastPrice = t.Price;

            bool inRth;
            List<WindowInstance> win = Clock.GetActive(t.Time, out inRth);
            if (Quality.OnTrade(ref t, inRth))
                AddEvent(OrderFlowEventType.DataGap, t.Time, bar, t.Price, 0, 0, Quality.MaxGapSeconds,
                         "Data gap detected during active hours - values around this time may be incomplete");

            BarFootprint f = Current;
            f.Ladder.Add(t.Tick, t.Volume, t.Side);
            f.Trades.Add(t.Tick, 1, t.Side);
            f.Prints++;
            if (t.Volume > f.MaxPrint) f.MaxPrint = t.Volume;
            switch (t.Source)
            {
                case ClassificationSource.QuoteExact: f.ExactVolume += t.Volume; break;
                case ClassificationSource.QuoteInside: f.InsideVolume += t.Volume; break;
                default: f.EstimatedVolume += t.Volume; break;
            }
            Delta.OnTrade(ref t, f);

            if (Settings.EnableProfiles)
            {
                VirginLevel touched;
                Profiles.AddTrade(ref t, bar, inRth, win, out touched);
                if (touched != null)
                    AddEvent(OrderFlowEventType.PocTouch, t.Time, bar, touched.Price, 0, 0, 0,
                             "Virgin " + (touched.SourceKind == ProfileKind.Week ? "weekly" : "daily") + " POC " +
                             touched.SourceDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " touched");
            }

            double elapsed = (t.Time - SessionBegin).TotalMinutes;
            sessionRvol.Add(elapsed, t.Volume);
            for (int i = 0; i < win.Count; i++)
            {
                RelativeVolumeTracker tr;
                if (!windowRvol.TryGetValue(win[i].Def.Name, out tr))
                {
                    tr = new RelativeVolumeTracker(5, Settings.RvolLookbackSessions);
                    windowRvol[win[i].Def.Name] = tr;
                }
                tr.Begin(win[i].Key);
                tr.Add(win[i].ElapsedMinutes, t.Volume);
            }

            if (Settings.EnableLargeTrades)
            {
                double rvol = sessionRvol.Rvol(elapsed);
                LastRvol = rvol;
                int bucket = Large.BucketOf(ToExchange(t.Time));
                tmpLarge.Clear(); tmpClusters.Clear(); tmpRepeats.Clear();
                Large.OnPrint(ref t, bar, bucket, rvol, f, tmpLarge, tmpClusters, tmpRepeats);
                EmitLarge();
            }

            if (Settings.EnableDepth && IsRealtime && Depth.OnTrade(ref t))
            {
                Absorption.MarkReplenished(t.Tick, (int)t.Side);
                AddEvent(OrderFlowEventType.Replenishment, t.Time, bar, t.Price, -(int)t.Side, 0, 0,
                         "Traded volume exceeded displayed size at this price (realtime DOM observation; not proof of an iceberg)");
            }
        }

        public void OnDepth(bool isBid, double price, long size, DateTime time)
        {
            if (!Settings.EnableDepth) return;
            Depth.OnDepth(isBid, PriceMath.ToTick(price, Settings.TickSize), size, time);
        }

        private void EmitLarge()
        {
            for (int i = 0; i < tmpLarge.Count; i++)
            {
                LargeTradeEvent e = tmpLarge[i];
                AddEvent(OrderFlowEventType.LargeTrade, e.Time, e.Bar, e.Vwap * Settings.TickSize, e.Side, e.Volume, e.Threshold,
                         (e.Side > 0 ? "Large aggressive BUY " : "Large aggressive SELL ") + e.Volume + " (" + e.Prints + " prints, thr " +
                         e.Threshold.ToString("0", CultureInfo.InvariantCulture) + " " + e.ThresholdSource + (e.Estimated ? ", EST side" : string.Empty) + ")");
            }
            for (int i = 0; i < tmpClusters.Count; i++)
            {
                LargeTradeCluster c = tmpClusters[i];
                AddEvent(OrderFlowEventType.LargeTradeCluster, c.EndTime, c.EndBar, (c.LowTick + c.HighTick) * 0.5 * Settings.TickSize, c.Side, c.Volume, c.Count,
                         (c.Side > 0 ? "Buy" : "Sell") + " large-trade cluster: " + c.Count + " executions, " + c.Volume + " lots (not necessarily one parent order)");
            }
            for (int i = 0; i < tmpRepeats.Count; i++)
            {
                RepeatedExecutionEvent r = tmpRepeats[i];
                repeatsRecent.Add(r);
                AddEvent(OrderFlowEventType.RepeatedExecution, r.Time, r.Bar, PriceMath.ToPrice(r.Tick, Settings.TickSize), r.Side, r.Volume, r.Prints,
                         "Repeated aggressive " + (r.Side > 0 ? "buying" : "selling") + " at one price: " + r.Volume + " lots / " + r.Prints + " prints");
            }
            if (repeatsRecent.Count > 200) repeatsRecent.RemoveRange(0, repeatsRecent.Count - 200);
        }

        /// <summary>Called exactly once per bar, when it is complete. Returns the signal of that bar, if any.</summary>
        public SignalRecord FinalizeBar(int bar, DateTime time, double open, double high, double low, double close, double barVolume)
        {
            BarFootprint f = Footprints.Get(bar);
            if (f == null) { f = Footprints.GetOrCreate(bar, time); Delta.BeginBar(f); }
            tmpLarge.Clear(); tmpClusters.Clear();
            Large.FlushBar(f, tmpLarge, tmpClusters);
            tmpRepeats.Clear();
            EmitLarge();

            double ts = Settings.TickSize;
            BarRecord prev = History.Get(bar - 1);
            BarRecord r = new BarRecord();
            r.Bar = bar; r.Time = time; r.Open = open; r.High = high; r.Low = low; r.Close = close;
            r.Volume = f.Volume; r.Buy = f.Buy; r.Sell = f.Sell; r.Delta = f.Delta;
            r.DeltaPct = r.Volume > 0 ? (double)r.Delta / r.Volume : 0;
            r.CvdOpen = f.CvdOpen; r.CvdHigh = f.CvdHigh; r.CvdLow = f.CvdLow; r.CvdClose = f.CvdClose;
            r.CvdResetId = Delta.ResetId;
            r.SessionCvd = Delta.SessionCvd; r.DayCvd = Delta.DayCvd; r.WeekCvd = Delta.WeekCvd;
            r.ExactShare = f.ExactShare;
            r.LargeBuyVolume = f.LargeBuyVolume; r.LargeSellVolume = f.LargeSellVolume;
            r.Window = Clock.ActiveNames(time.AddMilliseconds(-1));
            LastWindow = r.Window;
            r.NewsBlackout = News.ActiveAt(time) != null;
            if (r.Volume == 0 && barVolume > 0) r.ExactShare = 0;

            ProfileStats day = null, week = null;
            if (Settings.EnableProfiles && Profiles.CurrentDay != null)
            {
                day = Profiles.CurrentDay.GetStats(Settings.DayProfile);
                if (day.IsValid) { r.DayPoc = day.PocPrice(ts); r.DayVah = day.VahPrice(ts); r.DayVal = day.ValPrice(ts); }
                if (Profiles.CurrentWeek != null)
                {
                    week = Profiles.CurrentWeek.GetStats(Settings.WeekProfile);
                    if (week.IsValid) r.WeekPoc = week.PocPrice(ts);
                }
            }

            int zoneStart = Zones.Count;
            Analytics.Analyze(r, f, ToExchange(time), History, ts, Zones);
            if (Zones.Count > 5000) Zones.RemoveRange(0, Zones.Count - 5000);
            // mark older zones that this bar traded through (checked once per bar, newest first, bounded)
            for (int i = zoneStart - 1, checkedZones = 0; i >= 0 && checkedZones < 400; i--, checkedZones++)
            {
                if (i >= Zones.Count) continue;
                StackedImbalanceZone z = Zones[i];
                if (z.BrokenBar >= 0 || z.Bar >= bar) continue;
                if (z.Side > 0 ? low < (z.LowTick - 0.5) * ts : high > (z.HighTick + 0.5) * ts) z.BrokenBar = bar;
            }
            for (int i = Math.Min(zoneStart, Zones.Count); i < Zones.Count; i++)
            {
                StackedImbalanceZone z = Zones[i];
                AddEvent(OrderFlowEventType.StackedImbalance, time, bar, (z.LowTick + z.HighTick) * 0.5 * ts, z.Side, 0, z.Count,
                         "Stacked " + (z.Side > 0 ? "buy" : "sell") + " imbalance x" + z.Count);
            }
            if (r.VolumeSpike) AddEvent(OrderFlowEventType.VolumeSpike, time, bar, close, 0, r.Volume, r.VolumeRatio, "Volume spike x" + r.VolumeRatio.ToString("0.0", CultureInfo.InvariantCulture) + " vs time-of-day baseline");
            if (r.DeltaSpike) AddEvent(OrderFlowEventType.DeltaSpike, time, bar, close, Math.Sign(r.Delta), r.Delta, r.DeltaRatio, "Delta spike " + r.Delta + " (x" + r.DeltaRatio.ToString("0.0", CultureInfo.InvariantCulture) + ")");
            if (r.Exhaustion != 0) AddEvent(OrderFlowEventType.DeltaExhaustion, time, bar, r.Exhaustion > 0 ? high : low, -r.Exhaustion, 0, 0,
                (r.Exhaustion > 0 ? "Buying" : "Selling") + " exhaustion at new extreme (thin aggressive volume, fading delta)");

            History.Add(r);
            Profiles.OnBarClosed(bar);
            if (Settings.EnableProfiles) CheckPocInteractions(r, prev);

            List<AbsorptionCandidate> created = new List<AbsorptionCandidate>(), confirmed = new List<AbsorptionCandidate>(), invalid = new List<AbsorptionCandidate>();
            if (Settings.EnableAbsorption)
            {
                List<RepeatedExecutionEvent> barRepeats = new List<RepeatedExecutionEvent>();
                for (int i = 0; i < repeatsRecent.Count; i++) if (repeatsRecent[i].Bar == bar) barRepeats.Add(repeatsRecent[i]);
                Absorption.OnBar(r, f, prev, ts, barRepeats, created, confirmed, invalid);
                for (int i = 0; i < created.Count; i++)
                    AddEvent(OrderFlowEventType.AbsorptionCandidate, time, bar, created[i].LevelPrice, created[i].Direction, created[i].AggressiveVolume, created[i].Strength,
                        (created[i].Direction > 0 ? "Bullish" : "Bearish") + " absorption CANDIDATE (executed-trade evidence only)");
                for (int i = 0; i < confirmed.Count; i++)
                    AddEvent(OrderFlowEventType.AbsorptionConfirmed, time, bar, confirmed[i].LevelPrice, confirmed[i].Direction, confirmed[i].AggressiveVolume, confirmed[i].Strength,
                        (confirmed[i].Direction > 0 ? "Bullish" : "Bearish") + " absorption confirmed by price");
                for (int i = 0; i < invalid.Count; i++)
                    AddEvent(OrderFlowEventType.AbsorptionInvalidated, time, bar, invalid[i].LevelPrice, invalid[i].Direction, 0, 0, "Absorption candidate invalidated (level broken)");
            }

            List<DivergenceEvent> divs = new List<DivergenceEvent>();
            if (Settings.EnableDivergence)
            {
                Divergence.OnBar(History, bar, ts, divs);
                for (int i = 0; i < divs.Count; i++)
                    AddEvent(OrderFlowEventType.Divergence, time, bar, divs[i].Price, divs[i].Direction, divs[i].Cvd, divs[i].PivotBar,
                        (divs[i].Direction > 0 ? "Bullish" : "Bearish") + " CVD divergence (confirmed " + Settings.Divergence.PivotStrength + " bars after pivot) - context, not proof of reversal");
            }

            SignalRecord sig = null;
            if (Settings.EnableSignals)
            {
                SignalContext c = new SignalContext();
                c.Bar = r; c.Prev = prev; c.History = History; c.TickSize = ts;
                c.ConfirmedAbsorption = confirmed; c.Divergence = Divergence;
                c.DayStats = day; c.Window = r.Window; c.NewsActive = r.NewsBlackout; c.IsRealtime = IsRealtime;
                c.AtrBaseline = atrBaseline.Count >= 20 ? atrBaseline.Mean : double.NaN;
                c.LargeThreshold = double.IsNaN(Large.LastThreshold) ? Settings.Large.ManualThreshold : Large.LastThreshold;
                BuildReferenceLevels(day, c);
                sig = Signals.Evaluate(c);
                if (sig != null)
                {
                    Outcomes.Track(sig);
                    AddEvent(OrderFlowEventType.Signal, time, bar, close, sig.Direction, 0, sig.Score,
                             sig.Kind + " score " + sig.Score.ToString("0", CultureInfo.InvariantCulture) + (sig.Flags.Length > 0 ? " [" + sig.Flags + "]" : string.Empty));
                }
            }
            if (!double.IsNaN(r.AtrTicks)) atrBaseline.Add(r.AtrTicks);
            Outcomes.OnBar(r, ts, outcomeLines);
            for (int i = repeatsRecent.Count - 1; i >= 0; i--) if (repeatsRecent[i].Bar < bar - 5) repeatsRecent.RemoveAt(i);
            return sig;
        }

        private void BuildReferenceLevels(ProfileStats day, SignalContext c)
        {
            double ts = Settings.TickSize;
            c.ReferenceLevels = new List<double>();
            c.HvnPrices = new List<double>();
            if (day != null && day.IsValid)
            {
                c.ReferenceLevels.Add(day.ValPrice(ts)); c.ReferenceLevels.Add(day.VahPrice(ts)); c.ReferenceLevels.Add(day.PocPrice(ts));
                for (int i = 0; i < day.HvnRows.Length; i++) { double p = day.RowCenterPrice(day.HvnRows[i], ts); c.ReferenceLevels.Add(p); c.HvnPrices.Add(p); }
                for (int i = 0; i < day.LvnRows.Length; i++) c.ReferenceLevels.Add(day.RowCenterPrice(day.LvnRows[i], ts));
            }
            GofVolumeProfile pd = Profiles.PreviousDay;
            if (pd != null)
            {
                ProfileStats s = pd.GetStats(Settings.DayProfile);
                if (s.IsValid) { c.ReferenceLevels.Add(s.PocPrice(ts)); c.ReferenceLevels.Add(s.VahPrice(ts)); c.ReferenceLevels.Add(s.ValPrice(ts)); }
            }
            GofVolumeProfile pw = Profiles.PreviousWeek;
            if (pw != null)
            {
                ProfileStats s = pw.GetStats(Settings.WeekProfile);
                if (s.IsValid) c.ReferenceLevels.Add(s.PocPrice(ts));
            }
            for (int i = 0; i < Profiles.VirginLevels.Count; i++)
                if (Profiles.VirginLevels[i].IsVirgin) c.ReferenceLevels.Add(Profiles.VirginLevels[i].Price);
        }

        private void CheckPocInteractions(BarRecord r, BarRecord prev)
        {
            CheckLevel("pdPOC", PocOf(Profiles.PreviousDay, Settings.DayProfile), r, prev);
            CheckLevel("pwPOC", PocOf(Profiles.PreviousWeek, Settings.WeekProfile), r, prev);
            if (prev != null)
            {
                CheckLevel("dPOC", prev.DayPoc, r, prev);
                CheckLevel("wPOC", prev.WeekPoc, r, prev);
            }
        }

        private double PocOf(GofVolumeProfile p, ProfileSettings s)
        {
            if (p == null) return double.NaN;
            ProfileStats st = p.GetStats(s);
            return st.IsValid ? st.PocPrice(Settings.TickSize) : double.NaN;
        }

        private void CheckLevel(string name, double level, BarRecord r, BarRecord prev)
        {
            if (double.IsNaN(level) || prev == null) return;
            bool crossUp = prev.Close < level && r.Close > level;
            bool crossDn = prev.Close > level && r.Close < level;
            if (crossUp || crossDn)
            {
                AddEvent(OrderFlowEventType.PocCross, r.Time, r.Bar, level, crossUp ? 1 : -1, 0, 0, "Close crossed " + (crossUp ? "above " : "below ") + name);
                lastTouchBar[name] = r.Bar;
                return;
            }
            if (r.Low <= level && r.High >= level)
            {
                int lb;
                if (!lastTouchBar.TryGetValue(name, out lb) || r.Bar - lb >= 10)
                {
                    AddEvent(OrderFlowEventType.PocTouch, r.Time, r.Bar, level, 0, 0, 0, "Price tested " + name);
                    lastTouchBar[name] = r.Bar;
                }
            }
        }

        private void AddEvent(OrderFlowEventType type, DateTime time, int bar, double price, int side, long volume, double value, string msg)
        {
            OrderFlowEvent e = new OrderFlowEvent();
            e.Type = type; e.Time = time; e.Bar = bar; e.Price = price; e.Side = side; e.Volume = volume; e.Value = value;
            e.Message = msg; e.IsRealtime = IsRealtime;
            events.Add(e);
            if (events.Count > 20000) events.RemoveRange(0, 10000);
        }

        public void DrainEvents(List<OrderFlowEvent> target)
        {
            if (target != null) target.AddRange(events);
            events.Clear();
        }

        public void DrainOutcomes(List<string> target)
        {
            if (target != null) target.AddRange(outcomeLines);
            outcomeLines.Clear();
        }

        // =====================================================================================
        // Query helpers for rendering / dashboard (call under lock(Sync))
        // =====================================================================================

        /// <summary>Volume profile of a bar range, built from retained footprints (exact prints).</summary>
        public ProfileStats BuildRangeStats(int fromBar, int toBar, ProfileSettings s, out bool complete)
        {
            rangeLadder.Clear();
            complete = fromBar >= Footprints.OldestBar;
            for (int b = fromBar; b <= toBar; b++)
            {
                BarFootprint f = Footprints.Get(b);
                if (f != null) rangeLadder.AddLadder(f.Ladder);
            }
            return ProfileCalculator.Compute(rangeLadder, s);
        }

        public double SessionRvolNow()
        {
            return LastRvol;
        }

        public double WindowRvol(string name, double elapsedMinutes)
        {
            RelativeVolumeTracker t;
            return windowRvol.TryGetValue(name, out t) ? t.Rvol(elapsedMinutes) : double.NaN;
        }
    }

    /// <summary>
    /// Buffers prints between OnMarketData and OnBarUpdate so that each print is attributed to the bar
    /// NinjaTrader assigns it to (OnMarketData for the first tick of a new bar fires BEFORE OnBarUpdate opens it).
    /// Each print is committed exactly once -> no double counting.
    /// </summary>
    public sealed class TradeIntake
    {
        private readonly List<TradePrint> pending = new List<TradePrint>();
        public DateTime LastHistoricalTime = DateTime.MinValue;
        public long SuppressedDuplicates;

        public int Count { get { return pending.Count; } }

        public void Add(TradePrint p) { pending.Add(p); }

        /// <summary>Prints older than the last historical print are suppressed at the historical->realtime hand-off.</summary>
        public bool AddRealtime(TradePrint p)
        {
            if (LastHistoricalTime != DateTime.MinValue && p.Time < LastHistoricalTime) { SuppressedDuplicates++; return false; }
            pending.Add(p);
            return true;
        }

        public void NoteHistorical(DateTime t) { if (t > LastHistoricalTime) LastHistoricalTime = t; }

        public int Commit(OrderFlowEngine engine, int bar, DateTime cutoff)
        {
            int n = 0;
            while (n < pending.Count && pending[n].Time <= cutoff)
            {
                engine.ProcessTrade(pending[n], bar);
                n++;
            }
            if (n > 0) pending.RemoveRange(0, n);
            return n;
        }

        public void Clear() { pending.Clear(); }
    }
}
