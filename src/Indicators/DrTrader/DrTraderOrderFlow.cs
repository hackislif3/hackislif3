// Gold Institutional Order Flow Analyzer - main price-panel indicator for NinjaTrader 8.
// Engines live in AddOns\DrTrader (pure C#). This file only: (1) feeds NinjaTrader events into the engine,
// (2) renders the engine state with SharpDX, (3) raises alerts and writes logs.
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.AddOns.DrTrader;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class DrTraderOrderFlow : Indicator
    {
        #region Fields
        private OrderFlowEngine engine;
        private TradeIntake intake;
        private SessionIterator sessionIterator;
        private GoldContractSpec spec;
        private string specWarning = string.Empty;
        private bool tickReplay;
        private bool useTickSeries;
        private int lastFinalizedBar = -1;
        private double lastBid;
        private double lastAsk;
        private SignalCsvLogger logger;
        private readonly List<OrderFlowEvent> eventBuffer = new List<OrderFlowEvent>();
        private readonly List<string> outcomeBuffer = new List<string>();
        private readonly Dictionary<string, DateTime> alertLast = new Dictionary<string, DateTime>();
        private string lastAlertText = string.Empty;
        private DateTime lastAlertWall = DateTime.MinValue;
        private DateTime lastLiveStats = DateTime.MinValue;
        private DateTime lastRealtimePrintWall = DateTime.MinValue;
        private string sourceText = string.Empty;
        private bool errorLogged;
        private long perfPrintTicks, perfPrints, perfBarTicks, perfBars;   // Stopwatch ticks, for the latency read-out

        private Series<double> signalCode;
        private Series<double> signalScore;
        private Series<double> devPoc;
        private Series<double> devVah;
        private Series<double> devVal;
        private Series<double> barDelta;
        private Series<double> cvd;

        // rendering
        private readonly Dictionary<string, SharpDX.Direct2D1.SolidColorBrush> dxBrushes = new Dictionary<string, SharpDX.Direct2D1.SolidColorBrush>();
        private SharpDX.DirectWrite.TextFormat textFormat;
        private SharpDX.DirectWrite.TextFormat smallFormat;
        private SharpDX.Direct2D1.StrokeStyle dashStyle;
        private ProfileStats rangeStats;
        private int rangeFrom = -1, rangeTo = -1;
        private DateTime rangeBuilt = DateTime.MinValue;
        private bool rangeComplete = true;
        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Gold (GC/MGC) order flow: exact volume profile, CVD, large trades, absorption and a transparent multi-factor signal engine.";
                Name = "DrTraderOrderFlow";
                Calculate = Calculate.OnEachTick;
                IsOverlay = true;
                DisplayInDataBox = false;
                DrawOnPricePanel = true;
                PaintPriceMarkers = false;
                IsAutoScale = false;
                IsSuspendedWhileInactive = false;
                MaximumBarsLookBack = MaximumBarsLookBack.Infinite;
                BarsRequiredToPlot = 0;
                ScaleJustification = ScaleJustification.Right;
                SetPropertyDefaults();
            }
            else if (State == State.Configure)
            {
                if (DataSource != GofDataSource.TickReplay)
                    AddDataSeries(BarsPeriodType.Tick, 1);
            }
            else if (State == State.DataLoaded)
            {
                signalCode = new Series<double>(this, MaximumBarsLookBack.Infinite);
                signalScore = new Series<double>(this, MaximumBarsLookBack.Infinite);
                devPoc = new Series<double>(this, MaximumBarsLookBack.Infinite);
                devVah = new Series<double>(this, MaximumBarsLookBack.Infinite);
                devVal = new Series<double>(this, MaximumBarsLookBack.Infinite);
                barDelta = new Series<double>(this, MaximumBarsLookBack.Infinite);
                cvd = new Series<double>(this, MaximumBarsLookBack.Infinite);

                spec = GoldContractSpec.Resolve(Instrument.MasterInstrument.Name, Instrument.MasterInstrument.TickSize,
                                                Instrument.MasterInstrument.PointValue, out specWarning);
                engine = new OrderFlowEngine(BuildSettings());
                intake = new TradeIntake();
                sessionIterator = new SessionIterator(Bars);
                tickReplay = Bars.IsTickReplay;
                useTickSeries = !tickReplay && DataSource != GofDataSource.TickReplay && BarsArray.Length > 1;
                if (tickReplay) sourceText = "Tick Replay: exact bid/ask history";
                else if (useTickSeries) sourceText = "1-tick series: exact VAP, ESTIMATED sides in history";
                else sourceText = "No historical prints (enable Tick Replay)";
                engine.Quality.SourceDescription = sourceText;
                if (Calculate != Calculate.OnEachTick)
                    engine.Warnings.Add("Calculate must be OnEachTick for exact bar attribution");
                if (!string.IsNullOrEmpty(specWarning)) engine.Warnings.Add(specWarning);
                if (BarsPeriod.BarsPeriodType == BarsPeriodType.Volume)
                    engine.Warnings.Add("Volume bars split prints across bars; attribution is per print");

                if (EnableCsvLog)
                {
                    logger = new SignalCsvLogger();
                    string folder = string.IsNullOrEmpty(LogFolder) ? Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "DrTrader") : LogFolder;
                    if (!logger.Open(folder, Instrument.FullName))
                    {
                        engine.Warnings.Add("CSV log disabled: " + logger.LastError);
                        logger = null;
                    }
                }
            }
            else if (State == State.Realtime)
            {
                if (engine != null)
                    lock (engine.Sync) engine.IsRealtime = true;
            }
            else if (State == State.Terminated)
            {
                if (logger != null) { logger.Dispose(); logger = null; }
                DisposeDx();
                if (textFormat != null) { textFormat.Dispose(); textFormat = null; }
                if (smallFormat != null) { smallFormat.Dispose(); smallFormat = null; }
            }
        }

        private EngineSettings BuildSettings()
        {
            EngineSettings s = new EngineSettings();
            s.TickSize = Instrument.MasterInstrument.TickSize;
            s.InstrumentName = Instrument.FullName;
            s.PlatformZone = NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo;
            s.ExchangeZoneId = ExchangeTimeZone;
            s.InsideMethod = InsideSpread;
            s.EnableProfiles = EnableProfiles;
            s.EnableLargeTrades = EnableLargeTrades;
            s.EnableAbsorption = EnableAbsorption;
            s.EnableDivergence = EnableDivergence;
            s.EnableSignals = EnableSignals;
            s.EnableDepth = EnableDepthEvidence;
            s.Scope = ProfileScopeSetting;
            s.RthRange = RthRange;
            s.MaxHistoryDays = Math.Max(ProfilesToShow + 1, 3);
            s.VirginToleranceTicks = VirginToleranceTicks;
            s.ContractStart = ContractStartDate;
            s.FixedStart = FixedRangeStart;
            s.FixedEnd = FixedRangeEnd;
            s.MaxFootprintBars = MaxFootprintBars;
            s.GapSeconds = GapSeconds;

            s.DayProfile.RowTicks = DayRowTicks;
            s.WeekProfile.RowTicks = WeekRowTicks;
            foreach (ProfileSettings p in new ProfileSettings[] { s.DayProfile, s.WeekProfile })
            {
                p.ValueAreaPercent = ValueAreaPercent;
                p.VaMethod = VaMethod;
                p.SmoothingRows = NodeSmoothingRows;
                p.NodeRadiusRows = NodeRadiusRows;
                p.HvnMinFraction = HvnFraction;
                p.LvnMaxFraction = LvnFraction;
            }

            s.AsiaRange = AsiaRange; s.AsiaZone = AsiaZone; s.AsiaEnabled = AsiaEnabled;
            s.LondonRange = LondonRange; s.LondonZone = LondonZone; s.LondonEnabled = LondonEnabled;
            s.NewYorkRange = NewYorkRange; s.NewYorkZone = NewYorkZone; s.NewYorkEnabled = NewYorkEnabled;
            s.ComexRange = ComexRange; s.ComexZone = ComexZone; s.ComexEnabled = ComexEnabled;
            s.OvernightRange = OvernightRange; s.OvernightZone = OvernightZone; s.OvernightEnabled = OvernightEnabled;
            s.CvdMode = CvdReset;

            bool preset = InstrumentPreset == GofInstrumentPreset.Auto;
            s.Large.Mode = ThresholdMode;
            s.Large.ManualThreshold = preset ? spec.ManualLargeThreshold : ManualThreshold;
            s.Large.Percentile = AdaptivePercentile;
            s.Large.MinSamples = AdaptiveMinSamples;
            s.Large.BucketMinutes = AdaptiveBucketMinutes;
            s.Large.DailyDecay = AdaptiveDailyDecay;
            s.Large.RvolExponent = RvolExponent;
            s.Large.AggregationMs = AggregationMs;
            s.Large.ClusterWindowMs = ClusterWindowMs;
            s.Large.ClusterMaxRangeTicks = ClusterRangeTicks;
            s.Large.ClusterMinCount = ClusterMinCount;
            s.Large.RepeatWindowSeconds = RepeatWindowSeconds;
            s.Large.RepeatMinVolume = preset ? spec.RepeatMinVolume : RepeatMinVolume;
            s.Large.RepeatThresholdMultiple = RepeatThresholdMultiple;
            s.Large.RepeatMinPrints = RepeatMinPrints;

            s.Bars.ImbalanceRatio = ImbalanceRatio;
            s.Bars.ImbalanceMinVolume = preset ? spec.ImbalanceMinVolume : ImbalanceMinVolume;
            s.Bars.StackedCount = StackedCount;
            s.Bars.VolumeSpikeRatio = VolumeSpikeRatio;
            s.Bars.DeltaSpikeRatio = DeltaSpikeRatio;

            s.Absorption.MinAggressionRatio = AbsMinAggression;
            s.Absorption.MinDeltaPct = AbsMinDeltaPct;
            s.Absorption.MaxProgressAtr = AbsMaxProgressAtr;
            s.Absorption.MinCloseLocation = AbsMinCloseLocation;
            s.Absorption.LevelTicks = AbsLevelTicks;
            s.Absorption.MinLevelShare = AbsMinLevelShare;
            s.Absorption.ConfirmBars = AbsConfirmBars;
            s.Absorption.InvalidationTicks = AbsInvalidationTicks;
            s.Absorption.RequireRetest = AbsRequireRetest;

            s.Divergence.PivotStrength = PivotStrength;
            s.Divergence.MaxPivotDistance = MaxPivotDistance;

            SignalSettings g = s.Signals;
            g.Threshold = SignalThreshold;
            g.ConflictLevel = ConflictLevel;
            g.CooldownBars = CooldownBars;
            g.AllowEstimated = AllowEstimatedSignals;
            g.MinExactShare = MinExactShare;
            g.LocationTicks = LocationTicks;
            g.WDelta = WDelta; g.WDeltaSpike = WDeltaSpike; g.WCvd = WCvd; g.WLarge = WLarge; g.WImbalance = WImbalance;
            g.WVolume = WVolume; g.WLocation = WLocation; g.WPrice = WPrice;
            g.WAbsorption = WAbsorption; g.WDivergence = WDivergence; g.WRevLocation = WRevLocation; g.WTurn = WTurn;
            g.AsiaMultiplier = AsiaMultiplier; g.LondonMultiplier = LondonMultiplier; g.NewYorkMultiplier = NewYorkMultiplier; g.OtherMultiplier = OtherMultiplier;
            g.VolatilityHigh = VolatilityHigh; g.VolatilityLow = VolatilityLow; g.VolatilityMultiplier = VolatilityMultiplier;
            g.NewsMode = NewsMode; g.NewsMultiplier = NewsMultiplier;
            g.UseDepthEvidence = EnableDepthEvidence && UseDepthInScore;

            s.NewsText = NewsEvents;
            s.NewsFile = NewsFile;
            s.NewsZone = NewsTimeZone;
            s.NewsMinutesBefore = NewsMinutesBefore;
            s.NewsMinutesAfter = NewsMinutesAfter;
            s.OutcomeHorizons = ParseHorizons(OutcomeHorizons);
            return s;
        }

        private static int[] ParseHorizons(string text)
        {
            List<int> l = new List<int>();
            if (!string.IsNullOrEmpty(text))
                foreach (string p in text.Split(','))
                {
                    int v;
                    if (int.TryParse(p.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) && v > 0) l.Add(v);
                }
            if (l.Count == 0) l.Add(10);
            l.Sort();
            return l.ToArray();
        }

        #region Data intake
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            // with a secondary series NinjaTrader may route market data per series: count each print once (primary only)
            if (engine == null || BarsInProgress != 0) return;
            if (e.MarketDataType == MarketDataType.Bid) { lastBid = e.Price; return; }
            if (e.MarketDataType == MarketDataType.Ask) { lastAsk = e.Price; return; }
            if (e.MarketDataType != MarketDataType.Last) return;

            bool historical = State == State.Historical;
            if (historical && !tickReplay) return;

            TradePrint p = new TradePrint();
            p.Time = e.Time;
            p.Price = e.Price;
            p.Volume = e.Volume;
            p.Bid = e.Bid;
            p.Ask = e.Ask;
            if (!historical)
            {
                // realtime: fall back to the latest Level 1 quote events when the Last event carries no quote
                if (p.Bid <= 0) p.Bid = lastBid;
                if (p.Ask <= 0) p.Ask = lastAsk;
                lastRealtimePrintWall = DateTime.Now;
            }
            lock (engine.Sync)
            {
                if (historical) { intake.Add(p); intake.NoteHistorical(p.Time); }
                else if (!intake.AddRealtime(p)) engine.Quality.DuplicatesSuppressed++;
            }
        }

        protected override void OnMarketDepth(MarketDepthEventArgs e)
        {
            // Level 2 is only used for the optional realtime "replenishment" observation.
            if (engine == null || !EnableDepthEvidence || State != State.Realtime) return;
            if (e.Operation == Operation.Remove) return;
            lock (engine.Sync) engine.OnDepth(e.MarketDataType == MarketDataType.Bid, e.Price, e.Volume, e.Time);
        }

        protected override void OnBarUpdate()
        {
            if (engine == null) return;

            if (BarsInProgress == 1)
            {
                // 1-tick series: used for history only when Tick Replay is not active
                if (!useTickSeries || State != State.Historical) return;
                TradePrint p = new TradePrint();
                p.Time = Times[1][0];
                p.Price = Closes[1][0];
                p.Volume = (long)Volumes[1][0];
                lock (engine.Sync) { intake.Add(p); intake.NoteHistorical(p.Time); }
                return;
            }
            if (BarsInProgress != 0) return;

            lock (engine.Sync)
            {
                try
                {
                    if (IsFirstTickOfBar)
                    {
                        if (CurrentBar > 0 && lastFinalizedBar < CurrentBar - 1)
                        {
                            long b0 = System.Diagnostics.Stopwatch.GetTimestamp();
                            FinalizePreviousBar();
                            perfBarTicks += System.Diagnostics.Stopwatch.GetTimestamp() - b0;
                            perfBars++;
                        }
                        if (Bars.IsFirstBarOfSession || !engine.HasSession) StartSession();
                        engine.BeginBar(CurrentBar, Time[0]);
                        signalCode[0] = 0;
                        signalScore[0] = 0;
                    }
                    DateTime cutoff = (useTickSeries && State == State.Historical) ? Time[0] : DateTime.MaxValue;
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    perfPrints += intake.Commit(engine, CurrentBar, cutoff);
                    perfPrintTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                    UpdateLiveValues();
                }
                catch (Exception ex)
                {
                    if (!errorLogged)
                    {
                        errorLogged = true;
                        Log("DrTraderOrderFlow: " + ex.Message, LogLevel.Error);
                        Print("DrTraderOrderFlow error: " + ex);
                    }
                }
            }
            ProcessEvents();
        }

        private void StartSession()
        {
            sessionIterator.GetNextSession(Time[0], true);
            engine.OnSessionStart(sessionIterator.ActualTradingDayExchange, sessionIterator.ActualSessionBegin, CurrentBar);
        }

        private void FinalizePreviousBar()
        {
            int bar = CurrentBar - 1;
            SignalRecord s = engine.FinalizeBar(bar, Time[1], Open[1], High[1], Low[1], Close[1], Volume[1]);
            lastFinalizedBar = bar;
            BarRecord r = engine.History.Get(bar);
            if (r != null)
            {
                if (!double.IsNaN(r.DayPoc)) { devPoc[1] = r.DayPoc; devVah[1] = r.DayVah; devVal[1] = r.DayVal; }
                barDelta[1] = r.Delta;
                cvd[1] = r.CvdClose;
            }
            if (s != null)
            {
                signalCode[1] = SignalToCode(s);
                signalScore[1] = s.Score;
                if (logger != null && (LogHistorical || s.IsRealtime)) logger.WriteSignal(s);
            }
        }

        private static double SignalToCode(SignalRecord s)
        {
            switch (s.Kind)
            {
                case SignalKind.StrongBullishPressure: return 1;
                case SignalKind.PotentialBullishReversal: return 2;
                case SignalKind.StrongBearishPressure: return -1;
                case SignalKind.PotentialBearishReversal: return -2;
                case SignalKind.Conflict: return 9;
                default: return 0;
            }
        }

        private void UpdateLiveValues()
        {
            BarFootprint f = engine.Current;
            if (f != null && f.Bar == CurrentBar)
            {
                barDelta[0] = f.Delta;
                cvd[0] = f.CvdClose;
            }
            if (State != State.Realtime || !EnableProfiles || engine.Profiles.CurrentDay == null) return;
            if ((DateTime.Now - lastLiveStats).TotalMilliseconds < 250) return;
            lastLiveStats = DateTime.Now;
            ProfileStats st = engine.Profiles.CurrentDay.GetStats(engine.Settings.DayProfile);
            if (!st.IsValid) return;
            devPoc[0] = st.PocPrice(TickSize);
            devVah[0] = st.VahPrice(TickSize);
            devVal[0] = st.ValPrice(TickSize);
        }
        #endregion

        #region Events, alerts, logging
        private void ProcessEvents()
        {
            eventBuffer.Clear();
            outcomeBuffer.Clear();
            lock (engine.Sync)
            {
                engine.DrainEvents(eventBuffer);
                engine.DrainOutcomes(outcomeBuffer);
            }
            if (eventBuffer.Count == 0 && outcomeBuffer.Count == 0) return;
            for (int i = 0; i < eventBuffer.Count; i++)
            {
                OrderFlowEvent e = eventBuffer[i];
                if (logger != null && (LogHistorical || e.IsRealtime)) logger.WriteEvent(e);
                if (EnableAlerts && e.IsRealtime && State == State.Realtime) TryAlert(e);
            }
            if (logger != null)
            {
                for (int i = 0; i < outcomeBuffer.Count; i++) logger.WriteOutcome(outcomeBuffer[i]);
                if (State == State.Realtime || IsFirstTickOfBar) logger.FlushEvents();
            }
        }

        private void TryAlert(OrderFlowEvent e)
        {
            string cat = null;
            switch (e.Type)
            {
                case OrderFlowEventType.LargeTrade:
                    if (AlertLargeTrades && (AlertLargeMinVolume <= 0 || e.Volume >= AlertLargeMinVolume)) cat = "Large"; break;
                case OrderFlowEventType.LargeTradeCluster: if (AlertClusters) cat = "Cluster"; break;
                case OrderFlowEventType.RepeatedExecution: if (AlertClusters) cat = "Repeat"; break;
                case OrderFlowEventType.StackedImbalance: if (AlertImbalance) cat = "Imbalance"; break;
                case OrderFlowEventType.Divergence: if (AlertDivergence) cat = "Divergence"; break;
                case OrderFlowEventType.Signal: if (AlertSignals) cat = "Signal"; break;
                case OrderFlowEventType.PocCross:
                case OrderFlowEventType.PocTouch: if (AlertPoc) cat = "POC"; break;
                case OrderFlowEventType.AbsorptionCandidate:
                case OrderFlowEventType.AbsorptionConfirmed: if (AlertAbsorption) cat = "Absorption"; break;
                case OrderFlowEventType.DataGap: if (AlertDataQuality) cat = "Data"; break;
            }
            if (cat == null) return;
            if (NewsMode == NewsFilterMode.SuppressSignalsAndAlerts && cat != "Data" && engine.News.ActiveAt(e.Time) != null) return;

            string key = cat + "|" + e.Side.ToString(CultureInfo.InvariantCulture);
            DateTime now = DateTime.Now;
            DateTime last;
            if (alertLast.TryGetValue(key, out last) && (now - last).TotalSeconds < AlertCooldownSeconds) return;
            alertLast[key] = now;

            string msg = Instrument.FullName + " " + e.Message + " @ " + Instrument.MasterInstrument.FormatPrice(e.Price);
            string sound = PlaySounds && !string.IsNullOrEmpty(AlertSound) ? Path.Combine(NinjaTrader.Core.Globals.InstallDir, "sounds", AlertSound) : string.Empty;
            Brush back = e.Side > 0 ? BullSignalBrush : (e.Side < 0 ? BearSignalBrush : NeutralBrush);
            Alert("DRT_" + key, cat == "Signal" ? Priority.High : Priority.Medium, msg, sound, Math.Max(1, AlertCooldownSeconds), back, Brushes.White);
            lastAlertText = cat + ": " + e.Message;
            lastAlertWall = now;
        }
        #endregion

        #region Strategy / Market Analyzer access
        [Browsable(false)] [XmlIgnore]
        public Series<double> SignalCode { get { Update(); return signalCode; } }      // +1/+2 bull, -1/-2 bear, 9 conflict
        [Browsable(false)] [XmlIgnore]
        public Series<double> SignalScore { get { Update(); return signalScore; } }
        [Browsable(false)] [XmlIgnore]
        public Series<double> DevelopingPoc { get { Update(); return devPoc; } }
        [Browsable(false)] [XmlIgnore]
        public Series<double> DevelopingVah { get { Update(); return devVah; } }
        [Browsable(false)] [XmlIgnore]
        public Series<double> DevelopingVal { get { Update(); return devVal; } }
        [Browsable(false)] [XmlIgnore]
        public Series<double> BarDelta { get { Update(); return barDelta; } }
        [Browsable(false)] [XmlIgnore]
        public Series<double> Cvd { get { Update(); return cvd; } }
        #endregion

        #region Rendering
        public override void OnRenderTargetChanged()
        {
            DisposeDx();
            base.OnRenderTargetChanged();
        }

        private void DisposeDx()
        {
            foreach (SharpDX.Direct2D1.SolidColorBrush b in dxBrushes.Values)
                if (b != null && !b.IsDisposed) b.Dispose();
            dxBrushes.Clear();
            if (dashStyle != null) { dashStyle.Dispose(); dashStyle = null; }
        }

        private SharpDX.Direct2D1.SolidColorBrush Dx(Brush b, double opacityPercent)
        {
            SolidColorBrush scb = b as SolidColorBrush;
            Color c = scb != null ? scb.Color : Colors.Gray;
            float a = (float)(c.A / 255.0 * PriceMathClamp(opacityPercent, 0, 100) / 100.0);
            string key = c.R + "," + c.G + "," + c.B + "," + a.ToString("0.000", CultureInfo.InvariantCulture);
            SharpDX.Direct2D1.SolidColorBrush dx;
            if (!dxBrushes.TryGetValue(key, out dx) || dx == null || dx.IsDisposed)
            {
                dx = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(c.R / 255f, c.G / 255f, c.B / 255f, a));
                dxBrushes[key] = dx;
            }
            return dx;
        }

        private static double PriceMathClamp(double v, double lo, double hi) { return v < lo ? lo : (v > hi ? hi : v); }

        private void EnsureResources()
        {
            if (textFormat == null || Math.Abs(textFormat.FontSize - FontSize) > 0.01f)
            {
                if (textFormat != null) textFormat.Dispose();
                if (smallFormat != null) smallFormat.Dispose();
                textFormat = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Consolas",
                    SharpDX.DirectWrite.FontWeight.Normal, SharpDX.DirectWrite.FontStyle.Normal, (float)FontSize);
                smallFormat = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Consolas",
                    SharpDX.DirectWrite.FontWeight.Normal, SharpDX.DirectWrite.FontStyle.Normal, (float)Math.Max(7, FontSize - 2));
            }
            if (dashStyle == null)
            {
                SharpDX.Direct2D1.StrokeStyleProperties props = new SharpDX.Direct2D1.StrokeStyleProperties();
                props.DashStyle = SharpDX.Direct2D1.DashStyle.Dash;
                dashStyle = new SharpDX.Direct2D1.StrokeStyle(NinjaTrader.Core.Globals.D2DFactory, props);
            }
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);
            if (engine == null || ChartBars == null || RenderTarget == null) return;
            lock (engine.Sync)
            {
                SharpDX.Direct2D1.AntialiasMode oldMode = RenderTarget.AntialiasMode;
                try
                {
                    EnsureResources();
                    RenderTarget.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.PerPrimitive;
                    int from = Math.Max(0, ChartBars.FromIndex), to = ChartBars.ToIndex;
                    if (EnableProfiles && ShowSessionProfiles)
                    {
                        if (SplitRthOvernight) RenderSegmentProfiles(chartControl, chartScale, from, to);
                        else RenderInPlaceProfiles(chartControl, chartScale, from, to);
                    }
                    if (EnableProfiles && PanelSource != GofPanelProfileSource.None) RenderPanelProfile(chartControl, chartScale, from, to);
                    if (EnableProfiles) RenderLevels(chartControl, chartScale, from, to);
                    if (ShowImbalanceZones) RenderZones(chartControl, chartScale, from, to);
                    if (EnableLargeTrades && ShowLargeTrades) RenderLargeTrades(chartControl, chartScale, from, to);
                    if (EnableAbsorption && ShowAbsorption) RenderAbsorption(chartControl, chartScale, from, to);
                    if (EnableDivergence && ShowDivergenceOnPrice) RenderDivergence(chartControl, chartScale, from, to);
                    if (EnableSignals && ShowSignals) RenderSignals(chartControl, chartScale, from, to);
                    if (ShowDashboard) RenderDashboard();
                }
                catch (Exception ex)
                {
                    if (!errorLogged) { errorLogged = true; Print("DrTraderOrderFlow render error: " + ex); }
                }
                finally
                {
                    RenderTarget.AntialiasMode = oldMode;
                }
            }
        }

        private float X(ChartControl cc, int bar) { return cc.GetXByBarIndex(ChartBars, bar); }
        private float Y(ChartScale cs, double price) { return cs.GetYByValue(price); }
        private float RightEdge { get { return ChartPanel.X + ChartPanel.W; } }

        private void DrawProfileRows(ProfileStats st, ChartScale cs, float xBase, float maxWidth, bool growLeft, double opacity)
        {
            DrawProfileRows(st, cs, xBase, maxWidth, growLeft, opacity, ProfileBrush, ValueAreaBrush);
        }

        private void DrawProfileRows(ProfileStats st, ChartScale cs, float xBase, float maxWidth, bool growLeft, double opacity, Brush baseBrush, Brush vaBrush)
        {
            if (st == null || !st.IsValid || st.MaxRowVolume <= 0 || maxWidth < 2) return;
            double ts = TickSize;
            double top = cs.MaxValue, bottom = cs.MinValue;
            for (int r = 0; r < st.RowCount; r++)
            {
                long v = st.RowVolume[r];
                if (v <= 0) continue;
                double hiEdge = st.RowHighEdge(r, ts), loEdge = st.RowLowEdge(r, ts);
                if (loEdge > top || hiEdge < bottom) continue;
                float y1 = Y(cs, hiEdge), y2 = Y(cs, loEdge);
                float h = Math.Max(1f, y2 - y1 - (y2 - y1 > 3 ? 1f : 0f));
                float w = (float)(maxWidth * v / st.MaxRowVolume);
                bool inVa = r >= st.ValRow && r <= st.VahRow;
                if (ProfileColorMode == GofProfileColorMode.BuySellSplit && v > 0)
                {
                    float ws = (float)(w * st.RowSell[r] / (double)v);
                    float wb = (float)(w * st.RowBuy[r] / (double)v);
                    float x0 = growLeft ? xBase - w : xBase;
                    RenderTarget.FillRectangle(new SharpDX.RectangleF(x0, y1, ws, h), Dx(SellBrush, opacity * (inVa ? 1.0 : 0.6)));
                    RenderTarget.FillRectangle(new SharpDX.RectangleF(x0 + ws, y1, wb, h), Dx(BuyBrush, opacity * (inVa ? 1.0 : 0.6)));
                    RenderTarget.FillRectangle(new SharpDX.RectangleF(x0 + ws + wb, y1, Math.Max(0, w - ws - wb), h), Dx(baseBrush, opacity * 0.6));
                    if (r == st.PocRow) RenderTarget.DrawRectangle(new SharpDX.RectangleF(x0, y1, w, h), Dx(PocBrush, 100), 1f);
                }
                else
                {
                    Brush b = r == st.PocRow ? PocBrush : (inVa ? vaBrush : baseBrush);
                    float x0 = growLeft ? xBase - w : xBase;
                    RenderTarget.FillRectangle(new SharpDX.RectangleF(x0, y1, w, h), Dx(b, r == st.PocRow ? Math.Min(100, opacity * 1.6) : opacity));
                }
            }
        }

        private void RenderInPlaceProfiles(ChartControl cc, ChartScale cs, int from, int to)
        {
            List<GofVolumeProfile> list = new List<GofVolumeProfile>();
            List<GofVolumeProfile> done = engine.Profiles.CompletedDays;
            for (int i = Math.Max(0, done.Count - ProfilesToShow); i < done.Count; i++) list.Add(done[i]);
            if (engine.Profiles.CurrentDay != null) list.Add(engine.Profiles.CurrentDay);
            float bw = Math.Max(1, cc.GetBarPaintWidth(ChartBars));
            for (int i = 0; i < list.Count; i++)
            {
                GofVolumeProfile p = list[i];
                if (p.StartBar < 0 || p.EndBar < from || p.StartBar > to) continue;
                float x0 = X(cc, p.StartBar) - bw / 2f;
                float x1 = X(cc, p.EndBar) + bw / 2f;
                float width = (float)((x1 - x0) * InPlaceWidthPercent / 100.0);
                DrawProfileRows(p.GetStats(engine.Settings.DayProfile), cs, x0, width, false, ProfileOpacity);
            }
        }

        /// <summary>Each trading day drawn as Overnight / RTH / Post-RTH profiles, each anchored at its own first bar.</summary>
        private void RenderSegmentProfiles(ChartControl cc, ChartScale cs, int from, int to)
        {
            VolumeProfileEngine pe = engine.Profiles;
            List<GofVolumeProfile> list = new List<GofVolumeProfile>();
            List<GofVolumeProfile> done = pe.CompletedSegments;
            for (int i = Math.Max(0, done.Count - ProfilesToShow * 3); i < done.Count; i++) list.Add(done[i]);
            if (pe.CurrentOvernight != null) list.Add(pe.CurrentOvernight);
            if (pe.CurrentRth != null) list.Add(pe.CurrentRth);
            if (pe.CurrentPostRth != null) list.Add(pe.CurrentPostRth);
            float bw = Math.Max(1, cc.GetBarPaintWidth(ChartBars));
            double ts = TickSize;
            for (int i = 0; i < list.Count; i++)
            {
                GofVolumeProfile p = list[i];
                if (p.Kind == ProfileKind.PostRth && !ShowPostRthProfile) continue;
                if (p.StartBar < 0 || p.EndBar < from || p.StartBar > to || !p.Ladder.HasData) continue;
                float x0 = X(cc, p.StartBar) - bw / 2f;
                float x1 = X(cc, p.EndBar) + bw / 2f;
                float width = (float)(Math.Max(bw, x1 - x0) * InPlaceWidthPercent / 100.0);
                ProfileStats st = p.GetStats(engine.Settings.DayProfile);
                bool on = p.Kind == ProfileKind.Overnight;
                double op = p.Kind == ProfileKind.PostRth ? ProfileOpacity * 0.6 : ProfileOpacity;
                DrawProfileRows(st, cs, x0, width, false, op, on ? OvernightBrush : ProfileBrush, on ? OvernightBrush : ValueAreaBrush);
                if (ShowLevelLabels && st.IsValid)
                {
                    double topPrice = PriceMath.ToPrice(p.Ladder.MaxTick, ts) + ts;
                    if (topPrice <= cs.MaxValue && topPrice >= cs.MinValue)
                    {
                        string t = p.Label + " POC " + Instrument.MasterInstrument.FormatPrice(st.PocPrice(ts));
                        using (SharpDX.DirectWrite.TextLayout tl = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, t, smallFormat, 300, 20))
                            RenderTarget.DrawTextLayout(new SharpDX.Vector2(x0, Y(cs, topPrice) - tl.Metrics.Height - 1), tl, Dx(on ? OvernightBrush : TextBrush, 85));
                    }
                }
            }
        }

        private void RenderPanelProfile(ChartControl cc, ChartScale cs, int from, int to)
        {
            ProfileStats st = null;
            string label = PanelSource.ToString();
            VolumeProfileEngine pe = engine.Profiles;
            switch (PanelSource)
            {
                case GofPanelProfileSource.Session: if (pe.CurrentSession != null) st = pe.CurrentSession.GetStats(engine.Settings.DayProfile); break;
                case GofPanelProfileSource.Day: if (pe.CurrentDay != null) st = pe.CurrentDay.GetStats(engine.Settings.DayProfile); break;
                case GofPanelProfileSource.Week: if (pe.CurrentWeek != null) st = pe.CurrentWeek.GetStats(engine.Settings.WeekProfile); break;
                case GofPanelProfileSource.FixedRange:
                    if (pe.FixedRange != null) st = pe.FixedRange.GetStats(engine.Settings.DayProfile);
                    label = "Fixed " + FixedRangeStart.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) + " - " + FixedRangeEnd.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
                    break;
                case GofPanelProfileSource.VisibleRange:
                    if (from != rangeFrom || to != rangeTo || (DateTime.Now - rangeBuilt).TotalMilliseconds > 500)
                    {
                        rangeStats = engine.BuildRangeStats(from, to, engine.Settings.DayProfile, out rangeComplete);
                        rangeFrom = from; rangeTo = to; rangeBuilt = DateTime.Now;
                    }
                    st = rangeStats;
                    if (!rangeComplete) label += " (partial: older footprints trimmed)";
                    break;
            }
            if (st == null || !st.IsValid) return;
            float width = (float)PanelWidthPixels;
            bool right = PanelSide == GofProfileSide.Right;
            float xBase = right ? RightEdge - 2 : ChartPanel.X + 2;
            DrawProfileRows(st, cs, xBase, width, right, PanelOpacity);
            string txt = label + "  POC " + Instrument.MasterInstrument.FormatPrice(st.PocPrice(TickSize)) +
                         "  VA " + Instrument.MasterInstrument.FormatPrice(st.ValPrice(TickSize)) + "-" + Instrument.MasterInstrument.FormatPrice(st.VahPrice(TickSize)) +
                         "  Vol " + st.TotalVolume.ToString("N0", CultureInfo.InvariantCulture);
            using (SharpDX.DirectWrite.TextLayout tl = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, txt, smallFormat, 600, 20))
            {
                float tx = right ? RightEdge - tl.Metrics.Width - 4 : ChartPanel.X + 4;
                float ty = ChartPanel.Y + ChartPanel.H - tl.Metrics.Height - 4;
                RenderTarget.DrawTextLayout(new SharpDX.Vector2(tx, ty), tl, Dx(TextBrush, 90));
            }
        }

        private void DrawLevel(ChartControl cc, ChartScale cs, double price, int startBar, int endBar, Brush brush, float width, bool dashed, string label)
        {
            if (double.IsNaN(price) || price > cs.MaxValue || price < cs.MinValue) return;
            float y = Y(cs, price);
            float x0 = startBar >= 0 ? Math.Max(ChartPanel.X, X(cc, startBar)) : ChartPanel.X;
            float x1 = endBar >= 0 ? X(cc, endBar) : (ExtendLinesRight ? RightEdge : X(cc, Math.Max(0, ChartBars.ToIndex)));
            if (x1 <= x0) return;
            SharpDX.Direct2D1.SolidColorBrush b = Dx(brush, 100);
            if (dashed) RenderTarget.DrawLine(new SharpDX.Vector2(x0, y), new SharpDX.Vector2(x1, y), b, width, dashStyle);
            else RenderTarget.DrawLine(new SharpDX.Vector2(x0, y), new SharpDX.Vector2(x1, y), b, width);
            if (ShowLevelLabels && !string.IsNullOrEmpty(label))
            {
                string t = label + " " + Instrument.MasterInstrument.FormatPrice(price);
                using (SharpDX.DirectWrite.TextLayout tl = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, t, smallFormat, 300, 20))
                {
                    float tx = x1 - tl.Metrics.Width - 4 - (PanelSide == GofProfileSide.Right && PanelSource != GofPanelProfileSource.None && endBar < 0 ? (float)PanelWidthPixels : 0f);
                    RenderTarget.DrawTextLayout(new SharpDX.Vector2(Math.Max(x0, tx), y - tl.Metrics.Height - 1), tl, b);
                }
            }
        }

        private void RenderLevels(ChartControl cc, ChartScale cs, int from, int to)
        {
            VolumeProfileEngine pe = engine.Profiles;
            double ts = TickSize;
            ProfileStats day = pe.CurrentDay != null ? pe.CurrentDay.GetStats(engine.Settings.DayProfile) : null;
            int dayStart = pe.CurrentDay != null ? pe.CurrentDay.StartBar : -1;
            int weekStart = pe.CurrentWeek != null ? pe.CurrentWeek.StartBar : -1;
            float lw = (float)LineWidth;

            if (ShowVirginPocs)
                for (int i = 0; i < pe.VirginLevels.Count; i++)
                {
                    VirginLevel v = pe.VirginLevels[i];
                    if (!v.IsVirgin && !ShowTouchedVirgins) continue;
                    if (v.IsVirgin) DrawLevel(cc, cs, v.Price, v.OriginBar, -1, VirginPocBrush, lw, true, (v.SourceKind == ProfileKind.Week ? "vwPOC " : "vPOC ") + v.SourceDay.ToString("MM-dd", CultureInfo.InvariantCulture));
                    else if (v.TouchBar >= from) DrawLevel(cc, cs, v.Price, v.OriginBar, v.TouchBar, NeutralBrush, 1f, true, null);
                }
            if (ShowPrevDayLevels && pe.PreviousDay != null)
            {
                ProfileStats p = pe.PreviousDay.GetStats(engine.Settings.DayProfile);
                if (p.IsValid)
                {
                    DrawLevel(cc, cs, p.PocPrice(ts), dayStart, -1, PrevDayBrush, lw, true, "pdPOC");
                    DrawLevel(cc, cs, p.VahPrice(ts), dayStart, -1, PrevDayBrush, 1f, true, "pdVAH");
                    DrawLevel(cc, cs, p.ValPrice(ts), dayStart, -1, PrevDayBrush, 1f, true, "pdVAL");
                }
            }
            if (ShowPrevWeekPoc && pe.PreviousWeek != null)
            {
                ProfileStats p = pe.PreviousWeek.GetStats(engine.Settings.WeekProfile);
                if (p.IsValid) DrawLevel(cc, cs, p.PocPrice(ts), weekStart, -1, PrevWeekBrush, lw, true, "pwPOC");
            }
            if (ShowWeeklyPoc && pe.CurrentWeek != null)
            {
                ProfileStats w = pe.CurrentWeek.GetStats(engine.Settings.WeekProfile);
                if (w.IsValid) DrawLevel(cc, cs, w.PocPrice(ts), weekStart, -1, WeeklyPocBrush, lw + 1, false, "wPOC");
            }
            if (day != null && day.IsValid)
            {
                if (ShowValueAreaLines)
                {
                    DrawLevel(cc, cs, day.VahPrice(ts), dayStart, -1, ValueAreaLineBrush, 1f, false, "VAH");
                    DrawLevel(cc, cs, day.ValPrice(ts), dayStart, -1, ValueAreaLineBrush, 1f, false, "VAL");
                }
                if (ShowNodes)
                {
                    for (int i = 0; i < day.HvnRows.Length; i++) DrawLevel(cc, cs, day.RowCenterPrice(day.HvnRows[i], ts), dayStart, -1, HvnBrush, 1f, true, "HVN");
                    for (int i = 0; i < day.LvnRows.Length; i++) DrawLevel(cc, cs, day.RowCenterPrice(day.LvnRows[i], ts), dayStart, -1, LvnBrush, 1f, true, "LVN");
                }
                if (ShowDailyPoc) DrawLevel(cc, cs, day.PocPrice(ts), dayStart, -1, DailyPocBrush, lw + 1, false, "dPOC");
            }
            if (ShowOvernightLevels && pe.CurrentOvernight != null && pe.CurrentOvernight.Ladder.HasData)
            {
                GofVolumeProfile on = pe.CurrentOvernight;
                int start = pe.CurrentRth != null ? pe.CurrentRth.StartBar : on.StartBar;
                ProfileStats ost = on.GetStats(engine.Settings.DayProfile);
                DrawLevel(cc, cs, PriceMath.ToPrice(on.Ladder.MaxTick, ts), start, -1, OvernightBrush, 1f, true, "ONH");
                DrawLevel(cc, cs, PriceMath.ToPrice(on.Ladder.MinTick, ts), start, -1, OvernightBrush, 1f, true, "ONL");
                if (ost.IsValid) DrawLevel(cc, cs, ost.PocPrice(ts), start, -1, OvernightBrush, lw, false, "onPOC");
            }
            if (ShowDevelopingPocPath)
            {
                SharpDX.Direct2D1.SolidColorBrush b = Dx(DailyPocBrush, 70);
                BarRecord prev = null;
                for (int i = from; i <= to; i++)
                {
                    BarRecord r = engine.History.Get(i);
                    if (r != null && prev != null && !double.IsNaN(r.DayPoc) && !double.IsNaN(prev.DayPoc) && r.CvdResetId == prev.CvdResetId)
                        RenderTarget.DrawLine(new SharpDX.Vector2(X(cc, prev.Bar), Y(cs, prev.DayPoc)), new SharpDX.Vector2(X(cc, r.Bar), Y(cs, r.DayPoc)), b, 1f);
                    prev = r;
                }
            }
        }

        private void RenderZones(ChartControl cc, ChartScale cs, int from, int to)
        {
            List<StackedImbalanceZone> z = engine.Zones;
            float bw = Math.Max(2, cc.GetBarPaintWidth(ChartBars));
            for (int i = z.Count - 1; i >= 0; i--)
            {
                StackedImbalanceZone s = z[i];
                if (s.Bar < from) break;
                if (s.Bar > to) continue;
                float x = X(cc, s.Bar) + bw / 2f;
                float y1 = Y(cs, (s.HighTick + 0.5) * TickSize), y2 = Y(cs, (s.LowTick - 0.5) * TickSize);
                RenderTarget.FillRectangle(new SharpDX.RectangleF(x, y1, Math.Max(3f, bw / 3f), Math.Max(1f, y2 - y1)), Dx(s.Side > 0 ? BuyBrush : SellBrush, 70));
            }
        }

        private void RenderLargeTrades(ChartControl cc, ChartScale cs, int from, int to)
        {
            List<LargeTradeEvent> ev = engine.Large.Events;
            int start = FirstIndex(ev, from);
            for (int i = start; i < ev.Count; i++)
            {
                LargeTradeEvent e = ev[i];
                if (e.Bar > to) break;
                double price = e.Vwap * TickSize;
                if (price > cs.MaxValue || price < cs.MinValue) continue;
                double ratio = e.Threshold > 0 ? e.Volume / e.Threshold : 1.0;
                float r = (float)Math.Min(MarkerMaxRadius, MarkerMinRadius * Math.Sqrt(Math.Max(1.0, ratio)) * 1.5);
                SharpDX.Vector2 c = new SharpDX.Vector2(X(cc, e.Bar), Y(cs, price));
                Brush b = e.Side > 0 ? BuyBrush : SellBrush;
                SharpDX.Direct2D1.Ellipse el = new SharpDX.Direct2D1.Ellipse(c, r, r);
                RenderTarget.FillEllipse(el, Dx(b, MarkerOpacity));
                RenderTarget.DrawEllipse(el, Dx(e.Estimated ? NeutralBrush : b, 100), 1f);
                if (ShowLargeTradeLabels && r >= 6)
                {
                    string t = e.Volume.ToString(CultureInfo.InvariantCulture);
                    using (SharpDX.DirectWrite.TextLayout tl = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, t, smallFormat, 80, 20))
                        RenderTarget.DrawTextLayout(new SharpDX.Vector2(c.X + r + 2, c.Y - tl.Metrics.Height / 2), tl, Dx(TextBrush, 90));
                }
            }
            if (ShowClusters)
            {
                List<LargeTradeCluster> cl = engine.Large.Clusters;
                float bw = Math.Max(2, cc.GetBarPaintWidth(ChartBars));
                for (int i = cl.Count - 1; i >= 0; i--)
                {
                    LargeTradeCluster k = cl[i];
                    if (k.EndBar < from) break;
                    if (k.StartBar > to) continue;
                    float x0 = X(cc, k.StartBar) - bw, x1 = X(cc, k.EndBar) + bw;
                    float y1 = Y(cs, (k.HighTick + 1.5) * TickSize), y2 = Y(cs, (k.LowTick - 1.5) * TickSize);
                    RenderTarget.DrawRectangle(new SharpDX.RectangleF(x0, y1, x1 - x0, y2 - y1), Dx(k.Side > 0 ? BuyBrush : SellBrush, 90), 1.5f, dashStyle);
                }
            }
        }

        private static int FirstIndex(List<LargeTradeEvent> ev, int bar)
        {
            int lo = 0, hi = ev.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (ev[mid].Bar < bar) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        private void FillTriangle(SharpDX.Vector2 a, SharpDX.Vector2 b, SharpDX.Vector2 c, SharpDX.Direct2D1.Brush fill, SharpDX.Direct2D1.Brush stroke)
        {
            using (SharpDX.Direct2D1.PathGeometry g = new SharpDX.Direct2D1.PathGeometry(NinjaTrader.Core.Globals.D2DFactory))
            {
                using (SharpDX.Direct2D1.GeometrySink sink = g.Open())
                {
                    sink.BeginFigure(a, SharpDX.Direct2D1.FigureBegin.Filled);
                    sink.AddLine(b);
                    sink.AddLine(c);
                    sink.EndFigure(SharpDX.Direct2D1.FigureEnd.Closed);
                    sink.Close();
                }
                if (fill != null) RenderTarget.FillGeometry(g, fill);
                if (stroke != null) RenderTarget.DrawGeometry(g, stroke, 1.5f);
            }
        }

        private void RenderAbsorption(ChartControl cc, ChartScale cs, int from, int to)
        {
            List<AbsorptionCandidate> l = engine.Absorption.Candidates;
            float s = (float)Math.Max(4, ArrowSize * 0.6);
            for (int i = l.Count - 1; i >= 0; i--)
            {
                AbsorptionCandidate a = l[i];
                if (a.Bar < from) break;
                if (a.Bar > to) continue;
                float x = X(cc, a.Bar), y = Y(cs, a.LevelPrice) + (a.Direction > 0 ? s : -s);
                SharpDX.Vector2 top = new SharpDX.Vector2(x, y - s), right = new SharpDX.Vector2(x + s, y), bottom = new SharpDX.Vector2(x, y + s), left = new SharpDX.Vector2(x - s, y);
                SharpDX.Direct2D1.SolidColorBrush stroke = Dx(a.State == AbsorptionState.Confirmed || a.State == AbsorptionState.Pending ? AbsorptionBrush : NeutralBrush, 100);
                SharpDX.Direct2D1.SolidColorBrush fill = a.State == AbsorptionState.Confirmed ? Dx(AbsorptionBrush, 85) : null;
                FillTriangle(top, right, bottom, fill, stroke);
                FillTriangle(bottom, left, top, fill, stroke);
                if (a.State == AbsorptionState.Invalidated)
                    RenderTarget.DrawLine(new SharpDX.Vector2(x - s, y - s), new SharpDX.Vector2(x + s, y + s), Dx(NeutralBrush, 100), 1.5f);
            }
        }

        private void RenderDivergence(ChartControl cc, ChartScale cs, int from, int to)
        {
            List<DivergenceEvent> l = engine.Divergence.Events;
            for (int i = l.Count - 1; i >= 0; i--)
            {
                DivergenceEvent d = l[i];
                if (d.ConfirmBar < from) break;
                if (d.PrevPivotBar > to) continue;
                SharpDX.Direct2D1.SolidColorBrush b = Dx(d.Direction > 0 ? BullSignalBrush : BearSignalBrush, 80);
                RenderTarget.DrawLine(new SharpDX.Vector2(X(cc, d.PrevPivotBar), Y(cs, d.PrevPrice)), new SharpDX.Vector2(X(cc, d.PivotBar), Y(cs, d.Price)), b, 1.5f, dashStyle);
                using (SharpDX.DirectWrite.TextLayout tl = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, "DIV", smallFormat, 40, 20))
                {
                    float y = Y(cs, d.Price) + (d.Direction > 0 ? 4 : -tl.Metrics.Height - 4);
                    RenderTarget.DrawTextLayout(new SharpDX.Vector2(X(cc, d.ConfirmBar) - tl.Metrics.Width / 2, y), tl, b);
                }
            }
        }

        private void RenderSignals(ChartControl cc, ChartScale cs, int from, int to)
        {
            List<SignalRecord> l = engine.Signals.Signals;
            float s = (float)ArrowSize;
            for (int i = l.Count - 1; i >= 0; i--)
            {
                SignalRecord sig = l[i];
                if (sig.Bar < from) break;
                if (sig.Bar > to) continue;
                BarRecord r = engine.History.Get(sig.Bar);
                if (r == null) continue;
                float x = X(cc, sig.Bar);
                if (sig.Kind == SignalKind.Conflict)
                {
                    if (!ShowConflicts) continue;
                    using (SharpDX.DirectWrite.TextLayout tl = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, "?", textFormat, 20, 20))
                        RenderTarget.DrawTextLayout(new SharpDX.Vector2(x - tl.Metrics.Width / 2, Y(cs, r.High) - tl.Metrics.Height - 2), tl, Dx(NeutralBrush, 100));
                    continue;
                }
                bool reversal = sig.Kind == SignalKind.PotentialBullishReversal || sig.Kind == SignalKind.PotentialBearishReversal;
                SharpDX.Direct2D1.SolidColorBrush b = Dx(sig.Direction > 0 ? BullSignalBrush : BearSignalBrush, 100);
                SharpDX.Direct2D1.SolidColorBrush fill = reversal ? Dx(sig.Direction > 0 ? BullSignalBrush : BearSignalBrush, 35) : b;
                float labelY;
                if (sig.Direction > 0)
                {
                    float y = Y(cs, r.Low) + 4;
                    FillTriangle(new SharpDX.Vector2(x, y), new SharpDX.Vector2(x + s * 0.7f, y + s), new SharpDX.Vector2(x - s * 0.7f, y + s), fill, b);
                    labelY = y + s + 1;
                }
                else
                {
                    float y = Y(cs, r.High) - 4;
                    FillTriangle(new SharpDX.Vector2(x, y), new SharpDX.Vector2(x + s * 0.7f, y - s), new SharpDX.Vector2(x - s * 0.7f, y - s), fill, b);
                    labelY = y - s - (float)FontSize - 3;
                }
                if (ShowScores)
                {
                    string t = (reversal ? "R" : "P") + sig.Score.ToString("0", CultureInfo.InvariantCulture) + (sig.Flags.Length > 0 ? "*" : string.Empty);
                    using (SharpDX.DirectWrite.TextLayout tl = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, t, smallFormat, 60, 20))
                        RenderTarget.DrawTextLayout(new SharpDX.Vector2(x - tl.Metrics.Width / 2, labelY), tl, b);
                }
            }
        }

        private void RenderDashboard()
        {
            List<string> lines = new List<string>();
            DeltaEngine d = engine.Delta;
            double ts = TickSize;
            lines.Add("DrTrader " + Instrument.FullName + "  |  " + engine.Quality.SourceDescription);
            lines.Add("Day " + (engine.HasSession ? engine.CurrentTradingDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "-") + "  window " + engine.LastWindow +
                      "  RVOL " + (double.IsNaN(engine.LastRvol) ? "n/a" : engine.LastRvol.ToString("0.00", CultureInfo.InvariantCulture)));
            double dp = d.SessionVolume > 0 ? 100.0 * d.SessionCvd / d.SessionVolume : 0;
            lines.Add("Vol " + d.SessionVolume.ToString("N0", CultureInfo.InvariantCulture) + "  Buy " + d.SessionBuy.ToString("N0", CultureInfo.InvariantCulture) +
                      "  Sell " + d.SessionSell.ToString("N0", CultureInfo.InvariantCulture));
            lines.Add("Delta " + d.SessionCvd.ToString("N0", CultureInfo.InvariantCulture) + " (" + dp.ToString("0.0", CultureInfo.InvariantCulture) + "%)  dayCVD " +
                      d.DayCvd.ToString("N0", CultureInfo.InvariantCulture) + "  wkCVD " + d.WeekCvd.ToString("N0", CultureInfo.InvariantCulture) +
                      "  " + (dp > 10 ? "BUY pressure" : (dp < -10 ? "SELL pressure" : "balanced")));
            if (EnableProfiles && engine.Profiles.CurrentDay != null)
            {
                ProfileStats st = engine.Profiles.CurrentDay.GetStats(engine.Settings.DayProfile);
                if (st.IsValid)
                    lines.Add("dPOC " + Instrument.MasterInstrument.FormatPrice(st.PocPrice(ts)) + "  VA " + Instrument.MasterInstrument.FormatPrice(st.ValPrice(ts)) + "-" +
                              Instrument.MasterInstrument.FormatPrice(st.VahPrice(ts)) + "  [" + ProfileScopeSetting + "]");
            }
            if (EnableLargeTrades)
                lines.Add("Large thr " + (double.IsNaN(engine.Large.LastThreshold) ? "-" : engine.Large.LastThreshold.ToString("0", CultureInfo.InvariantCulture)) +
                          " (" + engine.Large.LastThresholdSource + ")  run " + (engine.Large.ConsecutiveSide > 0 ? "BUY x" : (engine.Large.ConsecutiveSide < 0 ? "SELL x" : "-")) +
                          (engine.Large.ConsecutiveSide != 0 ? engine.Large.ConsecutiveCount.ToString(CultureInfo.InvariantCulture) : string.Empty));
            if (EnableSignals)
            {
                SignalRecord ls = engine.Signals.LastSignal;
                string sigTxt = ls == null ? "none" : (ls.Kind + " " + ls.Score.ToString("0", CultureInfo.InvariantCulture) + " (" + (engine.History.LastBar + 1 - ls.Bar) + " bars ago)");
                lines.Add("Signal " + sigTxt + "  state " + engine.Signals.LastState + "  bull " + engine.Signals.LastBullScore.ToString("0", CultureInfo.InvariantCulture) +
                          " / bear " + engine.Signals.LastBearScore.ToString("0", CultureInfo.InvariantCulture));
            }
            // render thread: never touch Time[]/CurrentBar here, use engine state only
            NewsEvent ne = engine.Quality.LastTradeTime != DateTime.MinValue ? engine.News.ActiveAt(engine.Quality.LastTradeTime) : null;
            if (ne != null) lines.Add("NEWS BLACKOUT: " + ne.Label + " " + ne.PlatformTime.ToString("HH:mm", CultureInfo.InvariantCulture) + " (" + NewsMode + ")");
            string age = State == State.Realtime && lastRealtimePrintWall != DateTime.MinValue
                ? "  last print " + (DateTime.Now - lastRealtimePrintWall).TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + "s ago" : string.Empty;
            lines.Add("Data " + engine.Quality.StatusText + age);
            double f = System.Diagnostics.Stopwatch.Frequency;
            if (perfPrints > 0 && perfBars > 0)
                lines.Add("Perf " + (perfPrintTicks / f * 1e6 / perfPrints).ToString("0.0", CultureInfo.InvariantCulture) + " us/print, " +
                          (perfBarTicks / f * 1e3 / perfBars).ToString("0.00", CultureInfo.InvariantCulture) + " ms/bar close, prints " +
                          perfPrints.ToString("N0", CultureInfo.InvariantCulture));
            if (lastAlertText.Length > 0 && (DateTime.Now - lastAlertWall).TotalSeconds < 30) lines.Add("ALERT " + lastAlertText);
            for (int i = 0; i < engine.Warnings.Count && i < 3; i++) lines.Add("! " + engine.Warnings[i]);

            float pad = 6, lineH = (float)FontSize + 4, maxW = 0;
            List<SharpDX.DirectWrite.TextLayout> layouts = new List<SharpDX.DirectWrite.TextLayout>();
            try
            {
                for (int i = 0; i < lines.Count; i++)
                {
                    SharpDX.DirectWrite.TextLayout tl = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, lines[i], textFormat, 1200, lineH);
                    layouts.Add(tl);
                    if (tl.Metrics.Width > maxW) maxW = tl.Metrics.Width;
                }
                float w = maxW + 2 * pad, h = lines.Count * lineH + 2 * pad;
                float x = (DashboardPosition == GofCorner.TopLeft || DashboardPosition == GofCorner.BottomLeft) ? ChartPanel.X + 8 : RightEdge - w - 8 - (PanelSide == GofProfileSide.Right && PanelSource != GofPanelProfileSource.None ? (float)PanelWidthPixels : 0f);
                float y = (DashboardPosition == GofCorner.TopLeft || DashboardPosition == GofCorner.TopRight) ? ChartPanel.Y + 8 : ChartPanel.Y + ChartPanel.H - h - 8;
                RenderTarget.FillRectangle(new SharpDX.RectangleF(x, y, w, h), Dx(DashboardBackBrush, DashboardOpacity));
                RenderTarget.DrawRectangle(new SharpDX.RectangleF(x, y, w, h), Dx(NeutralBrush, 60), 1f);
                for (int i = 0; i < layouts.Count; i++)
                {
                    Brush b = lines[i].StartsWith("!") || lines[i].StartsWith("NEWS") ? BearSignalBrush : (lines[i].StartsWith("ALERT") ? AbsorptionBrush : TextBrush);
                    RenderTarget.DrawTextLayout(new SharpDX.Vector2(x + pad, y + pad + i * lineH), layouts[i], Dx(b, 100));
                }
            }
            finally
            {
                for (int i = 0; i < layouts.Count; i++) layouts[i].Dispose();
            }
        }
        #endregion

        #region Defaults
        private void SetPropertyDefaults()
        {
            DataSource = GofDataSource.Auto;
            InsideSpread = InsideSpreadMethod.MidpointThenTickRule;
            InstrumentPreset = GofInstrumentPreset.Auto;
            MaxFootprintBars = 3000;
            ContractStartDate = new DateTime(2000, 1, 1);
            GapSeconds = 120;
            ExchangeTimeZone = "Eastern Standard Time";

            EnableProfiles = true;
            ProfileScopeSetting = ProfileScope.ETH;
            RthRange = "08:20-13:30";
            ValueAreaPercent = 70;
            VaMethod = ValueAreaMethod.TwoRowAuction;
            DayRowTicks = 1;
            WeekRowTicks = 1;
            NodeSmoothingRows = 5;
            NodeRadiusRows = 6;
            HvnFraction = 0.5;
            LvnFraction = 0.3;
            ShowSessionProfiles = true;
            SplitRthOvernight = true;
            ShowPostRthProfile = true;
            ShowOvernightLevels = true;
            ProfilesToShow = 5;
            InPlaceWidthPercent = 35;
            PanelSource = GofPanelProfileSource.Week;
            PanelSide = GofProfileSide.Right;
            PanelWidthPixels = 160;
            ProfileColorMode = GofProfileColorMode.Volume;
            ProfileOpacity = 35;
            PanelOpacity = 45;
            FixedRangeStart = DateTime.Today.AddDays(-1).AddHours(8);
            FixedRangeEnd = DateTime.Today.AddHours(17);

            ShowDailyPoc = true;
            ShowWeeklyPoc = true;
            ShowPrevDayLevels = true;
            ShowPrevWeekPoc = true;
            ShowValueAreaLines = true;
            ShowNodes = false;
            ShowVirginPocs = true;
            ShowTouchedVirgins = false;
            VirginToleranceTicks = 0;
            ExtendLinesRight = true;
            ShowDevelopingPocPath = false;
            ShowLevelLabels = true;
            LineWidth = 1;

            AsiaRange = "18:00-03:00"; AsiaZone = "Eastern Standard Time"; AsiaEnabled = true;
            LondonRange = "08:00-16:30"; LondonZone = "GMT Standard Time"; LondonEnabled = true;
            NewYorkRange = "08:00-17:00"; NewYorkZone = "Eastern Standard Time"; NewYorkEnabled = true;
            ComexRange = "08:20-13:30"; ComexZone = "Eastern Standard Time"; ComexEnabled = true;
            OvernightRange = "18:00-08:20"; OvernightZone = "Eastern Standard Time"; OvernightEnabled = true;

            CvdReset = CvdResetMode.Session;

            EnableLargeTrades = true;
            ThresholdMode = LargeTradeThresholdMode.MaxOfManualAndAdaptive;
            ManualThreshold = 20;
            AdaptivePercentile = 99.0;
            AdaptiveMinSamples = 500;
            AdaptiveBucketMinutes = 30;
            AdaptiveDailyDecay = 0.85;
            RvolExponent = 0.25;
            AggregationMs = 5;
            ClusterWindowMs = 3000;
            ClusterRangeTicks = 10;
            ClusterMinCount = 2;
            RepeatWindowSeconds = 60;
            RepeatMinVolume = 100;
            RepeatThresholdMultiple = 4.0;
            RepeatMinPrints = 10;
            ShowLargeTrades = true;
            ShowClusters = true;
            ShowLargeTradeLabels = true;
            MarkerMinRadius = 3;
            MarkerMaxRadius = 14;
            MarkerOpacity = 55;

            ImbalanceRatio = 3.0;
            ImbalanceMinVolume = 5;
            StackedCount = 3;
            ShowImbalanceZones = true;
            VolumeSpikeRatio = 2.5;
            DeltaSpikeRatio = 2.5;

            EnableAbsorption = true;
            AbsMinAggression = 1.8;
            AbsMinDeltaPct = 0.15;
            AbsMaxProgressAtr = 0.30;
            AbsMinCloseLocation = 0.50;
            AbsLevelTicks = 3;
            AbsMinLevelShare = 0.30;
            AbsConfirmBars = 3;
            AbsInvalidationTicks = 2;
            AbsRequireRetest = false;
            ShowAbsorption = true;
            EnableDepthEvidence = false;
            UseDepthInScore = false;

            EnableDivergence = true;
            PivotStrength = 3;
            MaxPivotDistance = 60;
            ShowDivergenceOnPrice = true;

            EnableSignals = true;
            SignalThreshold = 60;
            ConflictLevel = 45;
            CooldownBars = 5;
            AllowEstimatedSignals = false;
            MinExactShare = 0.90;
            LocationTicks = 5;
            WDelta = 1.5; WDeltaSpike = 1.0; WCvd = 1.0; WLarge = 1.0; WImbalance = 1.0; WVolume = 0.75; WLocation = 0.75; WPrice = 1.0;
            WAbsorption = 2.0; WDivergence = 1.25; WRevLocation = 1.0; WTurn = 1.0;
            AsiaMultiplier = 0.9; LondonMultiplier = 1.0; NewYorkMultiplier = 1.0; OtherMultiplier = 0.9;
            VolatilityHigh = 2.0; VolatilityLow = 0.4; VolatilityMultiplier = 0.8;
            ShowSignals = true;
            ShowConflicts = false;
            ShowScores = true;
            ArrowSize = 10;

            NewsMode = NewsFilterMode.ReduceScore;
            NewsMultiplier = 0.5;
            NewsEvents = string.Empty;
            NewsFile = string.Empty;
            NewsTimeZone = "Eastern Standard Time";
            NewsMinutesBefore = 10;
            NewsMinutesAfter = 20;

            EnableAlerts = true;
            AlertSignals = true;
            AlertLargeTrades = false;
            AlertLargeMinVolume = 0;
            AlertClusters = true;
            AlertImbalance = false;
            AlertDivergence = true;
            AlertPoc = true;
            AlertAbsorption = true;
            AlertDataQuality = true;
            AlertCooldownSeconds = 60;
            AlertSound = "Alert2.wav";
            PlaySounds = true;

            ShowDashboard = true;
            DashboardPosition = GofCorner.TopLeft;
            FontSize = 11;
            DashboardOpacity = 80;

            BuyBrush = Brushes.LimeGreen;
            SellBrush = Brushes.Crimson;
            ProfileBrush = Brushes.SlateGray;
            ValueAreaBrush = Brushes.SteelBlue;
            PocBrush = Brushes.Gold;
            DailyPocBrush = Brushes.Gold;
            WeeklyPocBrush = Brushes.DeepSkyBlue;
            PrevDayBrush = Brushes.Orange;
            PrevWeekBrush = Brushes.MediumPurple;
            OvernightBrush = Brushes.Orchid;
            VirginPocBrush = Brushes.Magenta;
            ValueAreaLineBrush = Brushes.LightSteelBlue;
            HvnBrush = Brushes.Teal;
            LvnBrush = Brushes.Sienna;
            BullSignalBrush = Brushes.Lime;
            BearSignalBrush = Brushes.Red;
            AbsorptionBrush = Brushes.Yellow;
            NeutralBrush = Brushes.Gray;
            DashboardBackBrush = Brushes.Black;
            TextBrush = Brushes.WhiteSmoke;

            EnableCsvLog = false;
            LogHistorical = true;
            LogFolder = string.Empty;
            OutcomeHorizons = "5,10,20";
        }
        #endregion

        #region Properties
        // ---------------- 01. Data
        [Display(Name = "Data source", Description = "Auto: Tick Replay if enabled (exact), else 1-tick series (sides estimated in history).", Order = 1, GroupName = "01. Data")]
        public GofDataSource DataSource { get; set; }
        [Display(Name = "Inside-spread rule", Description = "Classification of prints strictly between bid and ask.", Order = 2, GroupName = "01. Data")]
        public InsideSpreadMethod InsideSpread { get; set; }
        [Display(Name = "Instrument preset", Description = "Auto: GC/MGC presets for manual large-trade, imbalance and repeat thresholds. Custom: use the values below.", Order = 3, GroupName = "01. Data")]
        public GofInstrumentPreset InstrumentPreset { get; set; }
        [Range(100, 100000)]
        [Display(Name = "Max footprint bars kept", Order = 4, GroupName = "01. Data")]
        public int MaxFootprintBars { get; set; }
        [Display(Name = "Contract start (rollover)", Description = "Virgin POCs from trading days before this date are ignored.", Order = 5, GroupName = "01. Data")]
        public DateTime ContractStartDate { get; set; }
        [Range(5, 3600)]
        [Display(Name = "Data gap seconds", Description = "Gap in prints during RTH that is flagged as a data problem.", Order = 6, GroupName = "01. Data")]
        public double GapSeconds { get; set; }
        [Display(Name = "Exchange time zone", Description = "Used for time-of-day buckets and RTH.", Order = 7, GroupName = "01. Data")]
        public string ExchangeTimeZone { get; set; }

        // ---------------- 02. Volume Profile
        [Display(Name = "Enable profiles", Order = 1, GroupName = "02. Volume Profile")]
        public bool EnableProfiles { get; set; }
        [Display(Name = "Profile scope", Description = "ETH: all prints. RTH: only prints inside the RTH window below.", Order = 2, GroupName = "02. Volume Profile")]
        public ProfileScope ProfileScopeSetting { get; set; }
        [Display(Name = "RTH window (exchange time)", Order = 3, GroupName = "02. Volume Profile")]
        public string RthRange { get; set; }
        [Range(1, 100)]
        [Display(Name = "Value area %", Order = 4, GroupName = "02. Volume Profile")]
        public double ValueAreaPercent { get; set; }
        [Display(Name = "Value area method", Order = 5, GroupName = "02. Volume Profile")]
        public ValueAreaMethod VaMethod { get; set; }
        [Range(1, 100)]
        [Display(Name = "Daily row size (ticks)", Order = 6, GroupName = "02. Volume Profile")]
        public int DayRowTicks { get; set; }
        [Range(1, 100)]
        [Display(Name = "Weekly row size (ticks)", Order = 7, GroupName = "02. Volume Profile")]
        public int WeekRowTicks { get; set; }
        [Range(1, 51)]
        [Display(Name = "Node smoothing rows", Order = 8, GroupName = "02. Volume Profile")]
        public int NodeSmoothingRows { get; set; }
        [Range(1, 100)]
        [Display(Name = "Node radius rows", Order = 9, GroupName = "02. Volume Profile")]
        public int NodeRadiusRows { get; set; }
        [Range(0.0, 1.0)]
        [Display(Name = "HVN min fraction of max", Order = 10, GroupName = "02. Volume Profile")]
        public double HvnFraction { get; set; }
        [Range(0.0, 1.0)]
        [Display(Name = "LVN max fraction of max", Order = 11, GroupName = "02. Volume Profile")]
        public double LvnFraction { get; set; }
        [Display(Name = "Show daily profiles in place", Order = 12, GroupName = "02. Volume Profile")]
        public bool ShowSessionProfiles { get; set; }
        [Range(0, 30)]
        [Display(Name = "Previous days shown", Order = 13, GroupName = "02. Volume Profile")]
        public int ProfilesToShow { get; set; }
        [Range(5, 100)]
        [Display(Name = "In-place width %", Order = 14, GroupName = "02. Volume Profile")]
        public double InPlaceWidthPercent { get; set; }
        [Display(Name = "Panel profile source", Order = 15, GroupName = "02. Volume Profile")]
        public GofPanelProfileSource PanelSource { get; set; }
        [Display(Name = "Panel profile side", Order = 16, GroupName = "02. Volume Profile")]
        public GofProfileSide PanelSide { get; set; }
        [Range(20, 1000)]
        [Display(Name = "Panel profile width (px)", Order = 17, GroupName = "02. Volume Profile")]
        public int PanelWidthPixels { get; set; }
        [Display(Name = "Profile coloring", Order = 18, GroupName = "02. Volume Profile")]
        public GofProfileColorMode ProfileColorMode { get; set; }
        [Range(1, 100)]
        [Display(Name = "In-place opacity %", Order = 19, GroupName = "02. Volume Profile")]
        public double ProfileOpacity { get; set; }
        [Range(1, 100)]
        [Display(Name = "Panel opacity %", Order = 20, GroupName = "02. Volume Profile")]
        public double PanelOpacity { get; set; }
        [Display(Name = "Fixed range start", Order = 21, GroupName = "02. Volume Profile")]
        public DateTime FixedRangeStart { get; set; }
        [Display(Name = "Fixed range end", Order = 22, GroupName = "02. Volume Profile")]
        public DateTime FixedRangeEnd { get; set; }

        [Display(Name = "Split RTH / Overnight", Description = "Draw each day as Overnight (session open to RTH open), RTH and Post-RTH profiles instead of one daily profile.", Order = 23, GroupName = "02. Volume Profile")]
        public bool SplitRthOvernight { get; set; }
        [Display(Name = "Show Post-RTH profile", Description = "Profile of the prints after the RTH window until the session ends.", Order = 24, GroupName = "02. Volume Profile")]
        public bool ShowPostRthProfile { get; set; }

        // ---------------- 03. Levels
        [Display(Name = "Overnight high / low / POC", Description = "ONH, ONL and onPOC of the current day, extended from the RTH open.", Order = 14, GroupName = "03. Levels")]
        public bool ShowOvernightLevels { get; set; }
        [Display(Name = "Daily POC", Order = 1, GroupName = "03. Levels")]
        public bool ShowDailyPoc { get; set; }
        [Display(Name = "Weekly POC", Order = 2, GroupName = "03. Levels")]
        public bool ShowWeeklyPoc { get; set; }
        [Display(Name = "Previous day POC/VAH/VAL", Order = 3, GroupName = "03. Levels")]
        public bool ShowPrevDayLevels { get; set; }
        [Display(Name = "Previous week POC", Order = 4, GroupName = "03. Levels")]
        public bool ShowPrevWeekPoc { get; set; }
        [Display(Name = "Daily VAH/VAL", Order = 5, GroupName = "03. Levels")]
        public bool ShowValueAreaLines { get; set; }
        [Display(Name = "HVN/LVN lines", Order = 6, GroupName = "03. Levels")]
        public bool ShowNodes { get; set; }
        [Display(Name = "Virgin POCs", Order = 7, GroupName = "03. Levels")]
        public bool ShowVirginPocs { get; set; }
        [Display(Name = "Show touched virgin POCs", Order = 8, GroupName = "03. Levels")]
        public bool ShowTouchedVirgins { get; set; }
        [Range(0, 50)]
        [Display(Name = "Virgin touch tolerance (ticks)", Order = 9, GroupName = "03. Levels")]
        public int VirginToleranceTicks { get; set; }
        [Display(Name = "Extend lines to right edge", Order = 10, GroupName = "03. Levels")]
        public bool ExtendLinesRight { get; set; }
        [Display(Name = "Developing POC path", Order = 11, GroupName = "03. Levels")]
        public bool ShowDevelopingPocPath { get; set; }
        [Display(Name = "Level labels", Order = 12, GroupName = "03. Levels")]
        public bool ShowLevelLabels { get; set; }
        [Range(1, 10)]
        [Display(Name = "Line width", Order = 13, GroupName = "03. Levels")]
        public double LineWidth { get; set; }

        // ---------------- 04. Sessions (each window in its own time zone -> DST handled per zone)
        [Display(Name = "Asia window", Order = 1, GroupName = "04. Sessions")] public string AsiaRange { get; set; }
        [Display(Name = "Asia time zone", Order = 2, GroupName = "04. Sessions")] public string AsiaZone { get; set; }
        [Display(Name = "Asia enabled", Order = 3, GroupName = "04. Sessions")] public bool AsiaEnabled { get; set; }
        [Display(Name = "London window", Order = 4, GroupName = "04. Sessions")] public string LondonRange { get; set; }
        [Display(Name = "London time zone", Order = 5, GroupName = "04. Sessions")] public string LondonZone { get; set; }
        [Display(Name = "London enabled", Order = 6, GroupName = "04. Sessions")] public bool LondonEnabled { get; set; }
        [Display(Name = "New York window", Order = 7, GroupName = "04. Sessions")] public string NewYorkRange { get; set; }
        [Display(Name = "New York time zone", Order = 8, GroupName = "04. Sessions")] public string NewYorkZone { get; set; }
        [Display(Name = "New York enabled", Order = 9, GroupName = "04. Sessions")] public bool NewYorkEnabled { get; set; }
        [Display(Name = "COMEX active window", Order = 10, GroupName = "04. Sessions")] public string ComexRange { get; set; }
        [Display(Name = "COMEX time zone", Order = 11, GroupName = "04. Sessions")] public string ComexZone { get; set; }
        [Display(Name = "COMEX enabled", Order = 12, GroupName = "04. Sessions")] public bool ComexEnabled { get; set; }
        [Display(Name = "Overnight window", Order = 13, GroupName = "04. Sessions")] public string OvernightRange { get; set; }
        [Display(Name = "Overnight time zone", Order = 14, GroupName = "04. Sessions")] public string OvernightZone { get; set; }
        [Display(Name = "Overnight enabled", Order = 15, GroupName = "04. Sessions")] public bool OvernightEnabled { get; set; }

        // ---------------- 05. Delta
        [Display(Name = "CVD reset", Order = 1, GroupName = "05. Delta")]
        public CvdResetMode CvdReset { get; set; }

        // ---------------- 06. Large trades
        [Display(Name = "Enable large trades", Order = 1, GroupName = "06. Large Trades")] public bool EnableLargeTrades { get; set; }
        [Display(Name = "Threshold mode", Order = 2, GroupName = "06. Large Trades")] public LargeTradeThresholdMode ThresholdMode { get; set; }
        [Range(1, 100000)] [Display(Name = "Manual threshold (contracts)", Description = "Used when preset = Custom.", Order = 3, GroupName = "06. Large Trades")] public int ManualThreshold { get; set; }
        [Range(50.0, 99.99)] [Display(Name = "Adaptive percentile", Order = 4, GroupName = "06. Large Trades")] public double AdaptivePercentile { get; set; }
        [Range(10, 1000000)] [Display(Name = "Adaptive min samples", Order = 5, GroupName = "06. Large Trades")] public int AdaptiveMinSamples { get; set; }
        [Range(5, 240)] [Display(Name = "Time-of-day bucket (min)", Order = 6, GroupName = "06. Large Trades")] public int AdaptiveBucketMinutes { get; set; }
        [Range(0.1, 1.0)] [Display(Name = "Daily decay", Order = 7, GroupName = "06. Large Trades")] public double AdaptiveDailyDecay { get; set; }
        [Range(0.0, 2.0)] [Display(Name = "RVOL exponent", Description = "0 = threshold ignores relative volume.", Order = 8, GroupName = "06. Large Trades")] public double RvolExponent { get; set; }
        [Range(-1, 1000)] [Display(Name = "Print aggregation (ms)", Description = "-1 = off. Same-side monotonic prints within N ms form one execution.", Order = 9, GroupName = "06. Large Trades")] public int AggregationMs { get; set; }
        [Range(100, 600000)] [Display(Name = "Cluster window (ms)", Order = 10, GroupName = "06. Large Trades")] public int ClusterWindowMs { get; set; }
        [Range(0, 1000)] [Display(Name = "Cluster max range (ticks)", Order = 11, GroupName = "06. Large Trades")] public int ClusterRangeTicks { get; set; }
        [Range(2, 100)] [Display(Name = "Cluster min count", Order = 12, GroupName = "06. Large Trades")] public int ClusterMinCount { get; set; }
        [Range(1, 3600)] [Display(Name = "Repeat window (s)", Order = 13, GroupName = "06. Large Trades")] public int RepeatWindowSeconds { get; set; }
        [Range(1, 1000000)] [Display(Name = "Repeat min volume", Description = "Used when preset = Custom.", Order = 14, GroupName = "06. Large Trades")] public int RepeatMinVolume { get; set; }
        [Range(0.0, 100.0)] [Display(Name = "Repeat x large threshold", Order = 15, GroupName = "06. Large Trades")] public double RepeatThresholdMultiple { get; set; }
        [Range(1, 10000)] [Display(Name = "Repeat min prints", Order = 16, GroupName = "06. Large Trades")] public int RepeatMinPrints { get; set; }
        [Display(Name = "Show large trades", Order = 17, GroupName = "06. Large Trades")] public bool ShowLargeTrades { get; set; }
        [Display(Name = "Show clusters", Order = 18, GroupName = "06. Large Trades")] public bool ShowClusters { get; set; }
        [Display(Name = "Show volume labels", Order = 19, GroupName = "06. Large Trades")] public bool ShowLargeTradeLabels { get; set; }
        [Range(1, 50)] [Display(Name = "Marker min radius", Order = 20, GroupName = "06. Large Trades")] public double MarkerMinRadius { get; set; }
        [Range(2, 100)] [Display(Name = "Marker max radius", Order = 21, GroupName = "06. Large Trades")] public double MarkerMaxRadius { get; set; }
        [Range(1, 100)] [Display(Name = "Marker opacity %", Order = 22, GroupName = "06. Large Trades")] public double MarkerOpacity { get; set; }

        // ---------------- 07. Imbalance & spikes
        [Range(1.0, 100.0)] [Display(Name = "Diagonal imbalance ratio", Description = "3 = 300%", Order = 1, GroupName = "07. Imbalance & Spikes")] public double ImbalanceRatio { get; set; }
        [Range(1, 100000)] [Display(Name = "Imbalance min volume", Description = "Used when preset = Custom.", Order = 2, GroupName = "07. Imbalance & Spikes")] public int ImbalanceMinVolume { get; set; }
        [Range(2, 50)] [Display(Name = "Stacked count", Order = 3, GroupName = "07. Imbalance & Spikes")] public int StackedCount { get; set; }
        [Display(Name = "Show stacked imbalances", Order = 4, GroupName = "07. Imbalance & Spikes")] public bool ShowImbalanceZones { get; set; }
        [Range(1.0, 50.0)] [Display(Name = "Volume spike ratio", Order = 5, GroupName = "07. Imbalance & Spikes")] public double VolumeSpikeRatio { get; set; }
        [Range(1.0, 50.0)] [Display(Name = "Delta spike ratio", Order = 6, GroupName = "07. Imbalance & Spikes")] public double DeltaSpikeRatio { get; set; }

        // ---------------- 08. Absorption
        [Display(Name = "Enable absorption", Order = 1, GroupName = "08. Absorption")] public bool EnableAbsorption { get; set; }
        [Range(1.0, 20.0)] [Display(Name = "Min aggression ratio", Order = 2, GroupName = "08. Absorption")] public double AbsMinAggression { get; set; }
        [Range(0.0, 1.0)] [Display(Name = "Min |delta %|", Order = 3, GroupName = "08. Absorption")] public double AbsMinDeltaPct { get; set; }
        [Range(0.0, 5.0)] [Display(Name = "Max progress (ATR)", Order = 4, GroupName = "08. Absorption")] public double AbsMaxProgressAtr { get; set; }
        [Range(0.0, 1.0)] [Display(Name = "Min close location", Order = 5, GroupName = "08. Absorption")] public double AbsMinCloseLocation { get; set; }
        [Range(1, 50)] [Display(Name = "Level ticks", Order = 6, GroupName = "08. Absorption")] public int AbsLevelTicks { get; set; }
        [Range(0.0, 1.0)] [Display(Name = "Min level share", Order = 7, GroupName = "08. Absorption")] public double AbsMinLevelShare { get; set; }
        [Range(1, 50)] [Display(Name = "Confirm within bars", Order = 8, GroupName = "08. Absorption")] public int AbsConfirmBars { get; set; }
        [Range(0, 100)] [Display(Name = "Invalidation ticks", Order = 9, GroupName = "08. Absorption")] public int AbsInvalidationTicks { get; set; }
        [Display(Name = "Require retest", Order = 10, GroupName = "08. Absorption")] public bool AbsRequireRetest { get; set; }
        [Display(Name = "Show absorption", Order = 11, GroupName = "08. Absorption")] public bool ShowAbsorption { get; set; }
        [Display(Name = "Depth replenishment (realtime L2)", Description = "Subscribes nothing extra; uses OnMarketDepth only in realtime.", Order = 12, GroupName = "08. Absorption")] public bool EnableDepthEvidence { get; set; }
        [Display(Name = "Use depth in score", Description = "Makes realtime scores differ from historical ones. Off by default.", Order = 13, GroupName = "08. Absorption")] public bool UseDepthInScore { get; set; }

        // ---------------- 09. Divergence
        [Display(Name = "Enable divergence", Order = 1, GroupName = "09. Divergence")] public bool EnableDivergence { get; set; }
        [Range(1, 20)] [Display(Name = "Pivot strength (bars)", Order = 2, GroupName = "09. Divergence")] public int PivotStrength { get; set; }
        [Range(5, 1000)] [Display(Name = "Max pivot distance", Order = 3, GroupName = "09. Divergence")] public int MaxPivotDistance { get; set; }
        [Display(Name = "Show on price panel", Order = 4, GroupName = "09. Divergence")] public bool ShowDivergenceOnPrice { get; set; }

        // ---------------- 10. Signals
        [Display(Name = "Enable signals", Order = 1, GroupName = "10. Signals")] public bool EnableSignals { get; set; }
        [Range(1, 100)] [Display(Name = "Score threshold", Order = 2, GroupName = "10. Signals")] public double SignalThreshold { get; set; }
        [Range(0, 100)] [Display(Name = "Conflict level", Order = 3, GroupName = "10. Signals")] public double ConflictLevel { get; set; }
        [Range(0, 1000)] [Display(Name = "Cooldown bars", Order = 4, GroupName = "10. Signals")] public int CooldownBars { get; set; }
        [Display(Name = "Allow estimated-data signals", Order = 5, GroupName = "10. Signals")] public bool AllowEstimatedSignals { get; set; }
        [Range(0.0, 1.0)] [Display(Name = "Min exact share", Order = 6, GroupName = "10. Signals")] public double MinExactShare { get; set; }
        [Range(0, 100)] [Display(Name = "Location tolerance (ticks)", Order = 7, GroupName = "10. Signals")] public int LocationTicks { get; set; }
        [Range(0.0, 10.0)] [Display(Name = "W pressure: delta %", Order = 8, GroupName = "10. Signals")] public double WDelta { get; set; }
        [Range(0.0, 10.0)] [Display(Name = "W pressure: delta spike", Order = 9, GroupName = "10. Signals")] public double WDeltaSpike { get; set; }
        [Range(0.0, 10.0)] [Display(Name = "W pressure: CVD trend", Order = 10, GroupName = "10. Signals")] public double WCvd { get; set; }
        [Range(0.0, 10.0)] [Display(Name = "W pressure: large trades", Order = 11, GroupName = "10. Signals")] public double WLarge { get; set; }
        [Range(0.0, 10.0)] [Display(Name = "W pressure: imbalance", Order = 12, GroupName = "10. Signals")] public double WImbalance { get; set; }
        [Range(0.0, 10.0)] [Display(Name = "W pressure: volume spike", Order = 13, GroupName = "10. Signals")] public double WVolume { get; set; }
        [Range(0.0, 10.0)] [Display(Name = "W pressure: profile location", Order = 14, GroupName = "10. Signals")] public double WLocation { get; set; }
        [Range(0.0, 10.0)] [Display(Name = "W pressure: price confirmation", Order = 15, GroupName = "10. Signals")] public double WPrice { get; set; }
        [Range(0.0, 10.0)] [Display(Name = "W reversal: absorption", Order = 16, GroupName = "10. Signals")] public double WAbsorption { get; set; }
        [Range(0.0, 10.0)] [Display(Name = "W reversal: divergence", Order = 17, GroupName = "10. Signals")] public double WDivergence { get; set; }
        [Range(0.0, 10.0)] [Display(Name = "W reversal: level location", Order = 18, GroupName = "10. Signals")] public double WRevLocation { get; set; }
        [Range(0.0, 10.0)] [Display(Name = "W reversal: delta turn", Order = 19, GroupName = "10. Signals")] public double WTurn { get; set; }
        [Range(0.0, 2.0)] [Display(Name = "Asia multiplier", Order = 20, GroupName = "10. Signals")] public double AsiaMultiplier { get; set; }
        [Range(0.0, 2.0)] [Display(Name = "London multiplier", Order = 21, GroupName = "10. Signals")] public double LondonMultiplier { get; set; }
        [Range(0.0, 2.0)] [Display(Name = "New York multiplier", Order = 22, GroupName = "10. Signals")] public double NewYorkMultiplier { get; set; }
        [Range(0.0, 2.0)] [Display(Name = "Other-hours multiplier", Order = 23, GroupName = "10. Signals")] public double OtherMultiplier { get; set; }
        [Range(1.0, 10.0)] [Display(Name = "High volatility (x ATR baseline)", Order = 24, GroupName = "10. Signals")] public double VolatilityHigh { get; set; }
        [Range(0.0, 1.0)] [Display(Name = "Low volatility (x ATR baseline)", Order = 25, GroupName = "10. Signals")] public double VolatilityLow { get; set; }
        [Range(0.0, 1.0)] [Display(Name = "Volatility multiplier", Order = 26, GroupName = "10. Signals")] public double VolatilityMultiplier { get; set; }
        [Display(Name = "Show signals", Order = 27, GroupName = "10. Signals")] public bool ShowSignals { get; set; }
        [Display(Name = "Show conflicts", Order = 28, GroupName = "10. Signals")] public bool ShowConflicts { get; set; }
        [Display(Name = "Show scores", Order = 29, GroupName = "10. Signals")] public bool ShowScores { get; set; }
        [Range(4, 40)] [Display(Name = "Arrow size", Order = 30, GroupName = "10. Signals")] public double ArrowSize { get; set; }

        // ---------------- 11. News filter (user-defined, no built-in calendar)
        [Display(Name = "News mode", Order = 1, GroupName = "11. News Filter")] public NewsFilterMode NewsMode { get; set; }
        [Range(0.0, 1.0)] [Display(Name = "News score multiplier", Order = 2, GroupName = "11. News Filter")] public double NewsMultiplier { get; set; }
        [Display(Name = "Events", Description = "yyyy-MM-dd HH:mm,LABEL ; separated. Example: 2026-11-06 08:30,NFP; 2026-12-09 14:00,FOMC", Order = 3, GroupName = "11. News Filter")] public string NewsEvents { get; set; }
        [Display(Name = "Events file (CSV)", Description = "Optional file, one 'yyyy-MM-dd HH:mm,LABEL' per line.", Order = 4, GroupName = "11. News Filter")] public string NewsFile { get; set; }
        [Display(Name = "Events time zone", Order = 5, GroupName = "11. News Filter")] public string NewsTimeZone { get; set; }
        [Range(0, 600)] [Display(Name = "Minutes before", Order = 6, GroupName = "11. News Filter")] public int NewsMinutesBefore { get; set; }
        [Range(0, 600)] [Display(Name = "Minutes after", Order = 7, GroupName = "11. News Filter")] public int NewsMinutesAfter { get; set; }

        // ---------------- 12. Alerts
        [Display(Name = "Enable alerts", Order = 1, GroupName = "12. Alerts")] public bool EnableAlerts { get; set; }
        [Display(Name = "Signals", Order = 2, GroupName = "12. Alerts")] public bool AlertSignals { get; set; }
        [Display(Name = "Large trades", Order = 3, GroupName = "12. Alerts")] public bool AlertLargeTrades { get; set; }
        [Range(0, 100000)] [Display(Name = "Large trade alert min volume", Description = "0 = any large trade", Order = 4, GroupName = "12. Alerts")] public int AlertLargeMinVolume { get; set; }
        [Display(Name = "Clusters / repeated execution", Order = 5, GroupName = "12. Alerts")] public bool AlertClusters { get; set; }
        [Display(Name = "Stacked imbalance", Order = 6, GroupName = "12. Alerts")] public bool AlertImbalance { get; set; }
        [Display(Name = "CVD divergence", Order = 7, GroupName = "12. Alerts")] public bool AlertDivergence { get; set; }
        [Display(Name = "POC cross / touch", Order = 8, GroupName = "12. Alerts")] public bool AlertPoc { get; set; }
        [Display(Name = "Absorption", Order = 9, GroupName = "12. Alerts")] public bool AlertAbsorption { get; set; }
        [Display(Name = "Data quality", Order = 10, GroupName = "12. Alerts")] public bool AlertDataQuality { get; set; }
        [Range(0, 86400)] [Display(Name = "Cooldown (s)", Order = 11, GroupName = "12. Alerts")] public int AlertCooldownSeconds { get; set; }
        [Display(Name = "Sound file", Description = "File in NinjaTrader 8\\sounds", Order = 12, GroupName = "12. Alerts")] public string AlertSound { get; set; }
        [Display(Name = "Play sounds", Order = 13, GroupName = "12. Alerts")] public bool PlaySounds { get; set; }

        // ---------------- 13. Visual
        [Display(Name = "Show dashboard", Order = 1, GroupName = "13. Visual")] public bool ShowDashboard { get; set; }
        [Display(Name = "Dashboard position", Order = 2, GroupName = "13. Visual")] public GofCorner DashboardPosition { get; set; }
        [Range(6, 30)] [Display(Name = "Font size", Order = 3, GroupName = "13. Visual")] public double FontSize { get; set; }
        [Range(0, 100)] [Display(Name = "Dashboard opacity %", Order = 4, GroupName = "13. Visual")] public double DashboardOpacity { get; set; }

        [XmlIgnore] [Display(Name = "Buy", Order = 10, GroupName = "13. Visual")] public Brush BuyBrush { get; set; }
        [Browsable(false)] public string BuyBrushSerializable { get { return Serialize.BrushToString(BuyBrush); } set { BuyBrush = Serialize.StringToBrush(value); } }
        [XmlIgnore] [Display(Name = "Sell", Order = 11, GroupName = "13. Visual")] public Brush SellBrush { get; set; }
        [Browsable(false)] public string SellBrushSerializable { get { return Serialize.BrushToString(SellBrush); } set { SellBrush = Serialize.StringToBrush(value); } }
        [XmlIgnore] [Display(Name = "Profile", Order = 12, GroupName = "13. Visual")] public Brush ProfileBrush { get; set; }
        [Browsable(false)] public string ProfileBrushSerializable { get { return Serialize.BrushToString(ProfileBrush); } set { ProfileBrush = Serialize.StringToBrush(value); } }
        [XmlIgnore] [Display(Name = "Value area rows", Order = 13, GroupName = "13. Visual")] public Brush ValueAreaBrush { get; set; }
        [Browsable(false)] public string ValueAreaBrushSerializable { get { return Serialize.BrushToString(ValueAreaBrush); } set { ValueAreaBrush = Serialize.StringToBrush(value); } }
        [XmlIgnore] [Display(Name = "POC row", Order = 14, GroupName = "13. Visual")] public Brush PocBrush { get; set; }
        [Browsable(false)] public string PocBrushSerializable { get { return Serialize.BrushToString(PocBrush); } set { PocBrush = Serialize.StringToBrush(value); } }
        [XmlIgnore] [Display(Name = "Daily POC line", Order = 15, GroupName = "13. Visual")] public Brush DailyPocBrush { get; set; }
        [Browsable(false)] public string DailyPocBrushSerializable { get { return Serialize.BrushToString(DailyPocBrush); } set { DailyPocBrush = Serialize.StringToBrush(value); } }
        [XmlIgnore] [Display(Name = "Weekly POC line", Order = 16, GroupName = "13. Visual")] public Brush WeeklyPocBrush { get; set; }
        [Browsable(false)] public string WeeklyPocBrushSerializable { get { return Serialize.BrushToString(WeeklyPocBrush); } set { WeeklyPocBrush = Serialize.StringToBrush(value); } }
        [XmlIgnore] [Display(Name = "Previous day levels", Order = 17, GroupName = "13. Visual")] public Brush PrevDayBrush { get; set; }
        [Browsable(false)] public string PrevDayBrushSerializable { get { return Serialize.BrushToString(PrevDayBrush); } set { PrevDayBrush = Serialize.StringToBrush(value); } }
        [XmlIgnore] [Display(Name = "Previous week POC", Order = 18, GroupName = "13. Visual")] public Brush PrevWeekBrush { get; set; }
        [XmlIgnore] [Display(Name = "Overnight profile / levels", Order = 29, GroupName = "13. Visual")] public Brush OvernightBrush { get; set; }
        [Browsable(false)] public string OvernightBrushSerializable { get { return Serialize.BrushToString(OvernightBrush); } set { OvernightBrush = Serialize.StringToBrush(value); } }
        [Browsable(false)] public string PrevWeekBrushSerializable { get { return Serialize.BrushToString(PrevWeekBrush); } set { PrevWeekBrush = Serialize.StringToBrush(value); } }
        [XmlIgnore] [Display(Name = "Virgin POC", Order = 19, GroupName = "13. Visual")] public Brush VirginPocBrush { get; set; }
        [Browsable(false)] public string VirginPocBrushSerializable { get { return Serialize.BrushToString(VirginPocBrush); } set { VirginPocBrush = Serialize.StringToBrush(value); } }
        [XmlIgnore] [Display(Name = "VAH/VAL lines", Order = 20, GroupName = "13. Visual")] public Brush ValueAreaLineBrush { get; set; }
        [Browsable(false)] public string ValueAreaLineBrushSerializable { get { return Serialize.BrushToString(ValueAreaLineBrush); } set { ValueAreaLineBrush = Serialize.StringToBrush(value); } }
        [XmlIgnore] [Display(Name = "HVN", Order = 21, GroupName = "13. Visual")] public Brush HvnBrush { get; set; }
        [Browsable(false)] public string HvnBrushSerializable { get { return Serialize.BrushToString(HvnBrush); } set { HvnBrush = Serialize.StringToBrush(value); } }
        [XmlIgnore] [Display(Name = "LVN", Order = 22, GroupName = "13. Visual")] public Brush LvnBrush { get; set; }
        [Browsable(false)] public string LvnBrushSerializable { get { return Serialize.BrushToString(LvnBrush); } set { LvnBrush = Serialize.StringToBrush(value); } }
        [XmlIgnore] [Display(Name = "Bullish signal", Order = 23, GroupName = "13. Visual")] public Brush BullSignalBrush { get; set; }
        [Browsable(false)] public string BullSignalBrushSerializable { get { return Serialize.BrushToString(BullSignalBrush); } set { BullSignalBrush = Serialize.StringToBrush(value); } }
        [XmlIgnore] [Display(Name = "Bearish signal", Order = 24, GroupName = "13. Visual")] public Brush BearSignalBrush { get; set; }
        [Browsable(false)] public string BearSignalBrushSerializable { get { return Serialize.BrushToString(BearSignalBrush); } set { BearSignalBrush = Serialize.StringToBrush(value); } }
        [XmlIgnore] [Display(Name = "Absorption", Order = 25, GroupName = "13. Visual")] public Brush AbsorptionBrush { get; set; }
        [Browsable(false)] public string AbsorptionBrushSerializable { get { return Serialize.BrushToString(AbsorptionBrush); } set { AbsorptionBrush = Serialize.StringToBrush(value); } }
        [XmlIgnore] [Display(Name = "Neutral", Order = 26, GroupName = "13. Visual")] public Brush NeutralBrush { get; set; }
        [Browsable(false)] public string NeutralBrushSerializable { get { return Serialize.BrushToString(NeutralBrush); } set { NeutralBrush = Serialize.StringToBrush(value); } }
        [XmlIgnore] [Display(Name = "Dashboard background", Order = 27, GroupName = "13. Visual")] public Brush DashboardBackBrush { get; set; }
        [Browsable(false)] public string DashboardBackBrushSerializable { get { return Serialize.BrushToString(DashboardBackBrush); } set { DashboardBackBrush = Serialize.StringToBrush(value); } }
        [XmlIgnore] [Display(Name = "Text", Order = 28, GroupName = "13. Visual")] public Brush TextBrush { get; set; }
        [Browsable(false)] public string TextBrushSerializable { get { return Serialize.BrushToString(TextBrush); } set { TextBrush = Serialize.StringToBrush(value); } }

        // ---------------- 14. Logging / validation
        [Display(Name = "Write CSV logs", Description = "signals_*.csv, outcomes_*.csv, events_*.csv in Documents\\NinjaTrader 8\\DrTrader", Order = 1, GroupName = "14. Logging")] public bool EnableCsvLog { get; set; }
        [Display(Name = "Log historical bars", Order = 2, GroupName = "14. Logging")] public bool LogHistorical { get; set; }
        [Display(Name = "Log folder (empty = default)", Order = 3, GroupName = "14. Logging")] public string LogFolder { get; set; }
        [Display(Name = "Outcome horizons (bars)", Order = 4, GroupName = "14. Logging")] public string OutcomeHorizons { get; set; }
        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private DrTraderOrderFlow[] cacheDrTraderOrderFlow;
		public DrTraderOrderFlow DrTraderOrderFlow()
		{
			return DrTraderOrderFlow(Input);
		}

		public DrTraderOrderFlow DrTraderOrderFlow(ISeries<double> input)
		{
			if (cacheDrTraderOrderFlow != null)
				for (int idx = 0; idx < cacheDrTraderOrderFlow.Length; idx++)
					if (cacheDrTraderOrderFlow[idx] != null &&  cacheDrTraderOrderFlow[idx].EqualsInput(input))
						return cacheDrTraderOrderFlow[idx];
			return CacheIndicator<DrTraderOrderFlow>(new DrTraderOrderFlow(), input, ref cacheDrTraderOrderFlow);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.DrTraderOrderFlow DrTraderOrderFlow()
		{
			return indicator.DrTraderOrderFlow(Input);
		}

		public Indicators.DrTraderOrderFlow DrTraderOrderFlow(ISeries<double> input )
		{
			return indicator.DrTraderOrderFlow(input);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.DrTraderOrderFlow DrTraderOrderFlow()
		{
			return indicator.DrTraderOrderFlow(Input);
		}

		public Indicators.DrTraderOrderFlow DrTraderOrderFlow(ISeries<double> input )
		{
			return indicator.DrTraderOrderFlow(input);
		}
	}
}

#endregion
