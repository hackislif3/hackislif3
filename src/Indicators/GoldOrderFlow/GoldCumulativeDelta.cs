// Gold Order Flow - Cumulative Delta panel (CVD candles / line, bar delta histogram, delta %, divergence markers).
// Uses the same engine as GoldOrderFlowAnalyzer with only the delta + divergence modules enabled.
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.AddOns.GoldOrderFlow;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class GoldCumulativeDelta : Indicator
    {
        private OrderFlowEngine engine;
        private TradeIntake intake;
        private SessionIterator sessionIterator;
        private bool tickReplay;
        private bool useTickSeries;
        private int lastFinalizedBar = -1;
        private double lastBid, lastAsk;
        private bool errorLogged;
        private readonly Dictionary<string, SharpDX.Direct2D1.SolidColorBrush> dxBrushes = new Dictionary<string, SharpDX.Direct2D1.SolidColorBrush>();
        private SharpDX.DirectWrite.TextFormat textFormat;
        private SharpDX.Direct2D1.StrokeStyle dashStyle;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Cumulative volume delta for GC/MGC from classified prints (exact with Tick Replay, estimated otherwise).";
                Name = "GoldCumulativeDelta";
                Calculate = Calculate.OnEachTick;
                IsOverlay = false;
                DisplayInDataBox = true;
                DrawOnPricePanel = false;
                PaintPriceMarkers = true;
                IsSuspendedWhileInactive = false;
                MaximumBarsLookBack = MaximumBarsLookBack.Infinite;
                BarsRequiredToPlot = 0;

                DataSource = GofDataSource.Auto;
                InsideSpread = InsideSpreadMethod.MidpointThenTickRule;
                CvdReset = CvdResetMode.Session;
                PanelMode = GofCvdPanelMode.CvdCandles;
                EnableDivergence = true;
                PivotStrength = 3;
                MaxPivotDistance = 60;
                DeltaSpikeRatio = 2.5;
                ShowEstimatedWarning = true;
                UpBrush = Brushes.LimeGreen;
                DownBrush = Brushes.Crimson;
                SpikeUpBrush = Brushes.Lime;
                SpikeDownBrush = Brushes.Red;
                BullDivBrush = Brushes.DeepSkyBlue;
                BearDivBrush = Brushes.Orange;

                AddPlot(Brushes.DodgerBlue, "CVD");
                AddPlot(new Stroke(Brushes.Gray, 3), PlotStyle.Bar, "Delta");
                AddLine(Brushes.DimGray, 0, "Zero");
            }
            else if (State == State.Configure)
            {
                if (DataSource != GofDataSource.TickReplay) AddDataSeries(BarsPeriodType.Tick, 1);
            }
            else if (State == State.DataLoaded)
            {
                EngineSettings s = new EngineSettings();
                s.TickSize = Instrument.MasterInstrument.TickSize;
                s.InstrumentName = Instrument.FullName;
                s.PlatformZone = NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo;
                s.InsideMethod = InsideSpread;
                s.EnableProfiles = false;
                s.EnableLargeTrades = false;
                s.EnableAbsorption = false;
                s.EnableSignals = false;
                s.EnableSubSessions = false;
                s.EnableDivergence = EnableDivergence;
                s.CvdMode = CvdReset;
                s.Divergence.PivotStrength = PivotStrength;
                s.Divergence.MaxPivotDistance = MaxPivotDistance;
                s.Divergence.RequireSameCvdPeriod = CvdReset != CvdResetMode.Never;
                s.Bars.DeltaSpikeRatio = DeltaSpikeRatio;
                s.MaxFootprintBars = 500;
                engine = new OrderFlowEngine(s);
                intake = new TradeIntake();
                sessionIterator = new SessionIterator(Bars);
                tickReplay = Bars.IsTickReplay;
                useTickSeries = !tickReplay && DataSource != GofDataSource.TickReplay && BarsArray.Length > 1;
                if (PanelMode == GofCvdPanelMode.CvdCandles) Plots[0].Brush = Brushes.Transparent;
            }
            else if (State == State.Realtime)
            {
                if (engine != null) lock (engine.Sync) engine.IsRealtime = true;
            }
            else if (State == State.Terminated)
            {
                DisposeDx();
                if (textFormat != null) { textFormat.Dispose(); textFormat = null; }
            }
        }

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
            p.Time = e.Time; p.Price = e.Price; p.Volume = e.Volume; p.Bid = e.Bid; p.Ask = e.Ask;
            if (!historical) { if (p.Bid <= 0) p.Bid = lastBid; if (p.Ask <= 0) p.Ask = lastAsk; }
            lock (engine.Sync)
            {
                if (historical) { intake.Add(p); intake.NoteHistorical(p.Time); }
                else intake.AddRealtime(p);
            }
        }

        protected override void OnBarUpdate()
        {
            if (engine == null) return;
            if (BarsInProgress == 1)
            {
                if (!useTickSeries || State != State.Historical) return;
                TradePrint p = new TradePrint();
                p.Time = Times[1][0]; p.Price = Closes[1][0]; p.Volume = (long)Volumes[1][0];
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
                            int bar = CurrentBar - 1;
                            engine.FinalizeBar(bar, Time[1], Open[1], High[1], Low[1], Close[1], Volume[1]);
                            lastFinalizedBar = bar;
                            BarRecord r = engine.History.Get(bar);
                            if (r != null) SetValues(1, r.CvdClose, r.Delta, r.DeltaPct, r.DeltaSpike);
                        }
                        if (Bars.IsFirstBarOfSession || !engine.HasSession)
                        {
                            sessionIterator.GetNextSession(Time[0], true);
                            engine.OnSessionStart(sessionIterator.ActualTradingDayExchange, sessionIterator.ActualSessionBegin, CurrentBar);
                        }
                        engine.BeginBar(CurrentBar, Time[0]);
                    }
                    DateTime cutoff = (useTickSeries && State == State.Historical) ? Time[0] : DateTime.MaxValue;
                    intake.Commit(engine, CurrentBar, cutoff);
                    BarFootprint f = engine.Current;
                    if (f != null && f.Bar == CurrentBar)
                        SetValues(0, f.CvdClose, f.Delta, f.Volume > 0 ? (double)f.Delta / f.Volume : 0, false);
                }
                catch (Exception ex)
                {
                    if (!errorLogged) { errorLogged = true; Log("GoldCumulativeDelta: " + ex.Message, LogLevel.Error); }
                }
                // nothing is alerted from this panel; drain so the queue cannot grow
                engine.DrainEvents(null);
                engine.DrainOutcomes(null);
            }
        }

        private void SetValues(int barsAgo, long cvdClose, long delta, double deltaPct, bool spike)
        {
            switch (PanelMode)
            {
                case GofCvdPanelMode.CvdCandles:
                case GofCvdPanelMode.CvdLine:
                    Values[0][barsAgo] = cvdClose;
                    break;
                case GofCvdPanelMode.BarDelta:
                    Values[1][barsAgo] = delta;
                    PlotBrushes[1][barsAgo] = delta >= 0 ? (spike ? SpikeUpBrush : UpBrush) : (spike ? SpikeDownBrush : DownBrush);
                    break;
                case GofCvdPanelMode.DeltaPercent:
                    Values[1][barsAgo] = Math.Round(deltaPct * 100.0, 2);
                    PlotBrushes[1][barsAgo] = deltaPct >= 0 ? UpBrush : DownBrush;
                    break;
            }
        }

        public override void OnCalculateMinMax()
        {
            base.OnCalculateMinMax();
            if (PanelMode != GofCvdPanelMode.CvdCandles || engine == null || ChartBars == null) return;
            double min = double.MaxValue, max = double.MinValue;
            lock (engine.Sync)
            {
                for (int i = ChartBars.FromIndex; i <= ChartBars.ToIndex; i++)
                {
                    long hi, lo;
                    if (!CandleAt(i, out hi, out lo)) continue;
                    if (lo < min) min = lo;
                    if (hi > max) max = hi;
                }
            }
            if (min == double.MaxValue) return;
            double pad = Math.Max(1, (max - min) * 0.05);
            MinValue = min - pad;
            MaxValue = max + pad;
        }

        private bool CandleAt(int bar, out long hi, out long lo)
        {
            BarRecord r = engine.History.Get(bar);
            if (r != null) { hi = r.CvdHigh; lo = r.CvdLow; return true; }
            BarFootprint f = engine.Current;
            if (f != null && f.Bar == bar && f.HasCvd) { hi = f.CvdHigh; lo = f.CvdLow; return true; }
            hi = lo = 0;
            return false;
        }

        public override void OnRenderTargetChanged()
        {
            DisposeDx();
            base.OnRenderTargetChanged();
        }

        private void DisposeDx()
        {
            foreach (SharpDX.Direct2D1.SolidColorBrush b in dxBrushes.Values) if (b != null && !b.IsDisposed) b.Dispose();
            dxBrushes.Clear();
            if (dashStyle != null) { dashStyle.Dispose(); dashStyle = null; }
        }

        private SharpDX.Direct2D1.SolidColorBrush Dx(Brush b, float alpha)
        {
            SolidColorBrush scb = b as SolidColorBrush;
            Color c = scb != null ? scb.Color : Colors.Gray;
            string key = c.R + "," + c.G + "," + c.B + "," + alpha.ToString("0.00", CultureInfo.InvariantCulture);
            SharpDX.Direct2D1.SolidColorBrush dx;
            if (!dxBrushes.TryGetValue(key, out dx) || dx == null || dx.IsDisposed)
            {
                dx = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(c.R / 255f, c.G / 255f, c.B / 255f, alpha));
                dxBrushes[key] = dx;
            }
            return dx;
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);
            if (engine == null || ChartBars == null || RenderTarget == null) return;
            if (textFormat == null)
                textFormat = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Consolas", 10f);
            if (dashStyle == null)
            {
                SharpDX.Direct2D1.StrokeStyleProperties props = new SharpDX.Direct2D1.StrokeStyleProperties();
                props.DashStyle = SharpDX.Direct2D1.DashStyle.Dash;
                dashStyle = new SharpDX.Direct2D1.StrokeStyle(NinjaTrader.Core.Globals.D2DFactory, props);
            }
            lock (engine.Sync)
            {
                try
                {
                    int from = Math.Max(0, ChartBars.FromIndex), to = ChartBars.ToIndex;
                    if (PanelMode == GofCvdPanelMode.CvdCandles) RenderCandles(chartControl, chartScale, from, to);
                    if (EnableDivergence && (PanelMode == GofCvdPanelMode.CvdCandles || PanelMode == GofCvdPanelMode.CvdLine))
                        RenderDivergences(chartControl, chartScale, from, to);
                    if (ShowEstimatedWarning && engine.Quality.Level == DataQualityLevel.Estimated)
                    {
                        using (SharpDX.DirectWrite.TextLayout tl = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory,
                               "ESTIMATED delta (tick rule) - enable Tick Replay for bid/ask classification", textFormat, 800, 20))
                            RenderTarget.DrawTextLayout(new SharpDX.Vector2(ChartPanel.X + 6, ChartPanel.Y + 4), tl, Dx(Brushes.Orange, 1f));
                    }
                }
                catch (Exception ex)
                {
                    if (!errorLogged) { errorLogged = true; Print("GoldCumulativeDelta render error: " + ex); }
                }
            }
        }

        private void RenderCandles(ChartControl cc, ChartScale cs, int from, int to)
        {
            float bw = Math.Max(1, cc.GetBarPaintWidth(ChartBars));
            for (int i = from; i <= to; i++)
            {
                long o, h, l, c;
                bool spike = false;
                BarRecord r = engine.History.Get(i);
                if (r != null) { o = r.CvdOpen; h = r.CvdHigh; l = r.CvdLow; c = r.CvdClose; spike = r.DeltaSpike; }
                else
                {
                    BarFootprint f = engine.Current;
                    if (f == null || f.Bar != i || !f.HasCvd) continue;
                    o = f.CvdOpen; h = f.CvdHigh; l = f.CvdLow; c = f.CvdClose;
                }
                float x = cc.GetXByBarIndex(ChartBars, i);
                float yo = cs.GetYByValue(o), yc = cs.GetYByValue(c), yh = cs.GetYByValue(h), yl = cs.GetYByValue(l);
                Brush b = c >= o ? (spike ? SpikeUpBrush : UpBrush) : (spike ? SpikeDownBrush : DownBrush);
                SharpDX.Direct2D1.SolidColorBrush dx = Dx(b, 1f);
                RenderTarget.DrawLine(new SharpDX.Vector2(x, yh), new SharpDX.Vector2(x, yl), dx, 1f);
                float top = Math.Min(yo, yc), height = Math.Max(1f, Math.Abs(yc - yo));
                RenderTarget.FillRectangle(new SharpDX.RectangleF(x - bw / 2f, top, bw, height), dx);
            }
        }

        private void RenderDivergences(ChartControl cc, ChartScale cs, int from, int to)
        {
            List<DivergenceEvent> l = engine.Divergence.Events;
            for (int i = l.Count - 1; i >= 0; i--)
            {
                DivergenceEvent d = l[i];
                if (d.ConfirmBar < from) break;
                if (d.PrevPivotBar > to) continue;
                SharpDX.Direct2D1.SolidColorBrush b = Dx(d.Direction > 0 ? BullDivBrush : BearDivBrush, 0.9f);
                SharpDX.Vector2 a = new SharpDX.Vector2(cc.GetXByBarIndex(ChartBars, d.PrevPivotBar), cs.GetYByValue(d.PrevCvd));
                SharpDX.Vector2 z = new SharpDX.Vector2(cc.GetXByBarIndex(ChartBars, d.PivotBar), cs.GetYByValue(d.Cvd));
                RenderTarget.DrawLine(a, z, b, 2f, dashStyle);
                string t = (d.Direction > 0 ? "Bull" : "Bear") + " div (confirmed)";
                using (SharpDX.DirectWrite.TextLayout tl = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, t, textFormat, 200, 20))
                {
                    float x = cc.GetXByBarIndex(ChartBars, d.ConfirmBar) - tl.Metrics.Width / 2;
                    float y = z.Y + (d.Direction > 0 ? 4 : -tl.Metrics.Height - 4);
                    RenderTarget.DrawTextLayout(new SharpDX.Vector2(x, y), tl, b);
                }
            }
        }

        #region Properties
        [Display(Name = "Data source", Order = 1, GroupName = "01. Data")] public GofDataSource DataSource { get; set; }
        [Display(Name = "Inside-spread rule", Order = 2, GroupName = "01. Data")] public InsideSpreadMethod InsideSpread { get; set; }
        [Display(Name = "CVD reset", Order = 3, GroupName = "01. Data")] public CvdResetMode CvdReset { get; set; }
        [Display(Name = "Panel mode", Order = 4, GroupName = "02. Display")] public GofCvdPanelMode PanelMode { get; set; }
        [Display(Name = "Divergence", Order = 5, GroupName = "02. Display")] public bool EnableDivergence { get; set; }
        [Range(1, 20)] [Display(Name = "Pivot strength", Order = 6, GroupName = "02. Display")] public int PivotStrength { get; set; }
        [Range(5, 1000)] [Display(Name = "Max pivot distance", Order = 7, GroupName = "02. Display")] public int MaxPivotDistance { get; set; }
        [Range(1.0, 50.0)] [Display(Name = "Delta spike ratio", Order = 8, GroupName = "02. Display")] public double DeltaSpikeRatio { get; set; }
        [Display(Name = "Show estimated warning", Order = 9, GroupName = "02. Display")] public bool ShowEstimatedWarning { get; set; }

        [XmlIgnore] [Display(Name = "Up", Order = 10, GroupName = "03. Colors")] public Brush UpBrush { get; set; }
        [Browsable(false)] public string UpBrushSerializable { get { return Serialize.BrushToString(UpBrush); } set { UpBrush = Serialize.StringToBrush(value); } }
        [XmlIgnore] [Display(Name = "Down", Order = 11, GroupName = "03. Colors")] public Brush DownBrush { get; set; }
        [Browsable(false)] public string DownBrushSerializable { get { return Serialize.BrushToString(DownBrush); } set { DownBrush = Serialize.StringToBrush(value); } }
        [XmlIgnore] [Display(Name = "Spike up", Order = 12, GroupName = "03. Colors")] public Brush SpikeUpBrush { get; set; }
        [Browsable(false)] public string SpikeUpBrushSerializable { get { return Serialize.BrushToString(SpikeUpBrush); } set { SpikeUpBrush = Serialize.StringToBrush(value); } }
        [XmlIgnore] [Display(Name = "Spike down", Order = 13, GroupName = "03. Colors")] public Brush SpikeDownBrush { get; set; }
        [Browsable(false)] public string SpikeDownBrushSerializable { get { return Serialize.BrushToString(SpikeDownBrush); } set { SpikeDownBrush = Serialize.StringToBrush(value); } }
        [XmlIgnore] [Display(Name = "Bullish divergence", Order = 14, GroupName = "03. Colors")] public Brush BullDivBrush { get; set; }
        [Browsable(false)] public string BullDivBrushSerializable { get { return Serialize.BrushToString(BullDivBrush); } set { BullDivBrush = Serialize.StringToBrush(value); } }
        [XmlIgnore] [Display(Name = "Bearish divergence", Order = 15, GroupName = "03. Colors")] public Brush BearDivBrush { get; set; }
        [Browsable(false)] public string BearDivBrushSerializable { get { return Serialize.BrushToString(BearDivBrush); } set { BearDivBrush = Serialize.StringToBrush(value); } }

        [Browsable(false)] [XmlIgnore] public Series<double> CvdSeries { get { return Values[0]; } }
        [Browsable(false)] [XmlIgnore] public Series<double> DeltaSeries { get { return Values[1]; } }
        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private GoldCumulativeDelta[] cacheGoldCumulativeDelta;
		public GoldCumulativeDelta GoldCumulativeDelta()
		{
			return GoldCumulativeDelta(Input);
		}

		public GoldCumulativeDelta GoldCumulativeDelta(ISeries<double> input)
		{
			if (cacheGoldCumulativeDelta != null)
				for (int idx = 0; idx < cacheGoldCumulativeDelta.Length; idx++)
					if (cacheGoldCumulativeDelta[idx] != null &&  cacheGoldCumulativeDelta[idx].EqualsInput(input))
						return cacheGoldCumulativeDelta[idx];
			return CacheIndicator<GoldCumulativeDelta>(new GoldCumulativeDelta(), input, ref cacheGoldCumulativeDelta);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.GoldCumulativeDelta GoldCumulativeDelta()
		{
			return indicator.GoldCumulativeDelta(Input);
		}

		public Indicators.GoldCumulativeDelta GoldCumulativeDelta(ISeries<double> input )
		{
			return indicator.GoldCumulativeDelta(input);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.GoldCumulativeDelta GoldCumulativeDelta()
		{
			return indicator.GoldCumulativeDelta(Input);
		}

		public Indicators.GoldCumulativeDelta GoldCumulativeDelta(ISeries<double> input )
		{
			return indicator.GoldCumulativeDelta(input);
		}
	}
}

#endregion
