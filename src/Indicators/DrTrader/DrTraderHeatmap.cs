// DrTrader Heatmap - Level 2 (market depth) heatmap for NinjaTrader 8.
// Draws the RESTING limit orders of the order book over time: each snapshot of the book becomes a thin column,
// each price level a cell whose colour shows how much size was displayed there.
// REALTIME ONLY: depth is not part of NinjaTrader's historical chart data. It works live and in Playback of
// data recorded with "Record live data for playback". Displayed size is not executed volume, and orders can
// be pulled before price reaches them.
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
using NinjaTrader.NinjaScript.AddOns.DrTrader;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class DrTraderHeatmap : Indicator
    {
        #region Fields
        private readonly object sync = new object();
        private DepthBook book;
        private HeatmapStore store;
        private int lastBar = -1;
        private long lastTradeTick = long.MinValue;
        private DateTime lastSnapshot = DateTime.MinValue;
        private long depthEvents;
        private bool errorLogged;
        private readonly Dictionary<string, SharpDX.Direct2D1.SolidColorBrush> dxBrushes = new Dictionary<string, SharpDX.Direct2D1.SolidColorBrush>();
        private SharpDX.DirectWrite.TextFormat textFormat;
        private double scale;
        private int scaleFrom = -1, scaleTo = -1;
        private DateTime scaleBuilt = DateTime.MinValue;
        private const int Shades = 32;
        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "DrTrader Heatmap: resting limit orders of the order book (Level 2) over time. Realtime / Playback only.";
                Name = "DrTraderHeatmap";
                Calculate = Calculate.OnEachTick;
                IsOverlay = true;
                DisplayInDataBox = false;
                DrawOnPricePanel = true;
                PaintPriceMarkers = false;
                IsAutoScale = false;
                IsSuspendedWhileInactive = false;
                BarsRequiredToPlot = 0;

                SnapshotMilliseconds = 500;
                MaxSnapshots = 60000;
                MaxLevels = 50;
                MinSizeShown = 1;

                Palette = GofHeatPalette.Thermal;
                Opacity = 80;
                ScalePercentile = 97;
                FixedScale = 0;
                ShowCurrentBook = true;
                CurrentBookWidth = 130;
                ShowLastPrice = true;
                ShowInfo = true;
                FontSize = 10;
                BidBrush = Brushes.SeaGreen;
                AskBrush = Brushes.IndianRed;
                LastPriceBrush = Brushes.White;
            }
            else if (State == State.DataLoaded)
            {
                book = new DepthBook();
                book.MaxLevels = MaxLevels;
                store = new HeatmapStore();
                store.MaxColumns = MaxSnapshots;
            }
            else if (State == State.Terminated)
            {
                DisposeDx();
                if (textFormat != null) { textFormat.Dispose(); textFormat = null; }
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress == 0) lastBar = CurrentBar;
        }

        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (book == null || BarsInProgress != 0 || State != State.Realtime) return;
            if (e.MarketDataType == MarketDataType.Last)
                lock (sync) lastTradeTick = PriceMath.ToTick(e.Price, TickSize);
        }

        protected override void OnMarketDepth(MarketDepthEventArgs e)
        {
            if (book == null || State != State.Realtime) return;
            if (e.MarketDataType != MarketDataType.Bid && e.MarketDataType != MarketDataType.Ask) return;
            DepthOperation op = e.Operation == Operation.Add ? DepthOperation.Add : (e.Operation == Operation.Update ? DepthOperation.Update : DepthOperation.Remove);
            lock (sync)
            {
                try
                {
                    book.Apply(e.MarketDataType == MarketDataType.Bid, op, e.Position, PriceMath.ToTick(e.Price, TickSize), e.Volume);
                    depthEvents++;
                    if (lastBar >= 0 && (e.Time - lastSnapshot).TotalMilliseconds >= SnapshotMilliseconds)
                    {
                        store.Add(book.Snapshot(lastBar, e.Time, lastTradeTick));
                        lastSnapshot = e.Time;
                    }
                }
                catch (Exception ex)
                {
                    if (!errorLogged) { errorLogged = true; Log("DrTraderHeatmap: " + ex.Message, LogLevel.Error); }
                }
            }
        }

        #region Rendering
        public override void OnRenderTargetChanged()
        {
            DisposeDx();
            base.OnRenderTargetChanged();
        }

        private void DisposeDx()
        {
            foreach (SharpDX.Direct2D1.SolidColorBrush b in dxBrushes.Values) if (b != null && !b.IsDisposed) b.Dispose();
            dxBrushes.Clear();
        }

        private SharpDX.Direct2D1.SolidColorBrush Dx(byte r, byte g, byte b, double alpha)
        {
            string key = r + "," + g + "," + b + "," + alpha.ToString("0.000", CultureInfo.InvariantCulture);
            SharpDX.Direct2D1.SolidColorBrush dx;
            if (!dxBrushes.TryGetValue(key, out dx) || dx == null || dx.IsDisposed)
            {
                dx = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(r / 255f, g / 255f, b / 255f, (float)alpha));
                dxBrushes[key] = dx;
            }
            return dx;
        }

        private SharpDX.Direct2D1.SolidColorBrush Dx(Brush b, double alpha)
        {
            SolidColorBrush s = b as SolidColorBrush;
            Color c = s != null ? s.Color : Colors.Gray;
            return Dx(c.R, c.G, c.B, alpha);
        }

        /// <summary>Colour stops of the built-in palettes (t = 0 cold ... 1 hot).</summary>
        private static byte[][] Stops(GofHeatPalette p)
        {
            switch (p)
            {
                case GofHeatPalette.Ocean:
                    return new byte[][] { new byte[] { 8, 28, 52 }, new byte[] { 14, 92, 150 }, new byte[] { 60, 180, 220 }, new byte[] { 232, 250, 255 } };
                case GofHeatPalette.Mono:
                    return new byte[][] { new byte[] { 40, 44, 52 }, new byte[] { 110, 116, 128 }, new byte[] { 190, 196, 206 }, new byte[] { 255, 255, 255 } };
                default: // Thermal
                    return new byte[][] { new byte[] { 18, 40, 92 }, new byte[] { 26, 150, 150 }, new byte[] { 242, 178, 52 }, new byte[] { 255, 246, 220 } };
            }
        }

        private SharpDX.Direct2D1.SolidColorBrush Shade(double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            int q = (int)Math.Round(t * (Shades - 1));
            double tq = (double)q / (Shades - 1);
            byte[][] s = Stops(Palette);
            double pos = tq * (s.Length - 1);
            int i = Math.Min(s.Length - 2, (int)pos);
            double f = pos - i;
            byte r = (byte)(s[i][0] + (s[i + 1][0] - s[i][0]) * f);
            byte g = (byte)(s[i][1] + (s[i + 1][1] - s[i][1]) * f);
            byte b = (byte)(s[i][2] + (s[i + 1][2] - s[i][2]) * f);
            // low sizes are also more transparent so candles stay readable
            return Dx(r, g, b, Opacity / 100.0 * (0.25 + 0.75 * tq));
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);
            if (store == null || ChartBars == null || RenderTarget == null) return;
            if (textFormat == null || Math.Abs(textFormat.FontSize - (float)FontSize) > 0.01f)
            {
                if (textFormat != null) textFormat.Dispose();
                textFormat = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Consolas", (float)FontSize);
            }
            lock (sync)
            {
                SharpDX.Direct2D1.AntialiasMode old = RenderTarget.AntialiasMode;
                try
                {
                    RenderTarget.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.Aliased;
                    int from = Math.Max(0, ChartBars.FromIndex), to = ChartBars.ToIndex;
                    RenderColumns(chartControl, chartScale, from, to);
                    RenderTarget.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.PerPrimitive;
                    if (ShowCurrentBook) RenderCurrentBook(chartScale);
                    if (ShowInfo) RenderInfo();
                }
                catch (Exception ex)
                {
                    if (!errorLogged) { errorLogged = true; Print("DrTraderHeatmap render error: " + ex); }
                }
                finally { RenderTarget.AntialiasMode = old; }
            }
        }

        private void RenderColumns(ChartControl cc, ChartScale cs, int from, int to)
        {
            if (store.Count == 0) return;
            if (FixedScale > 0) scale = FixedScale;
            else if (from != scaleFrom || to != scaleTo || (DateTime.Now - scaleBuilt).TotalMilliseconds > 1000)
            {
                scale = store.ScaleFor(from, to, ScalePercentile, 20000);
                scaleFrom = from; scaleTo = to; scaleBuilt = DateTime.Now;
            }
            if (scale <= 0) return;
            double ts = TickSize, top = cs.MaxValue, bottom = cs.MinValue;
            float slot = Math.Max(1f, cc.Properties.BarDistance);
            float rowH = Math.Max(1f, Math.Abs(cs.GetYByValue(bottom) - cs.GetYByValue(bottom + ts)));
            SharpDX.Direct2D1.SolidColorBrush lastBrush = Dx(LastPriceBrush, 0.9);

            int i = store.FirstIndexAtOrAfter(from);
            while (i < store.Count && store[i].Bar <= to)
            {
                int bar = store[i].Bar, j = i;
                while (j < store.Count && store[j].Bar == bar) j++;
                int n = j - i;
                // never draw more sub-columns than there are pixels in the bar slot
                int shown = Math.Max(1, Math.Min(n, (int)slot));
                float w = slot / shown;
                float x0 = cc.GetXByBarIndex(ChartBars, bar) - slot / 2f;
                float prevY = float.NaN, prevX = float.NaN;
                for (int k = 0; k < shown; k++)
                {
                    HeatmapColumn c = store[i + (int)((long)k * n / shown)];
                    float x = x0 + k * w;
                    for (int m = 0; m < c.Ticks.Length; m++)
                    {
                        long size = c.Sizes[m];
                        if (size < MinSizeShown) continue;
                        double price = c.Ticks[m] * ts;
                        if (price - ts / 2 > top || price + ts / 2 < bottom) continue;
                        float y = cs.GetYByValue(price + ts / 2);
                        RenderTarget.FillRectangle(new SharpDX.RectangleF(x, y, w + 0.5f, rowH), Shade(size / scale));
                    }
                    if (ShowLastPrice && c.LastTick != long.MinValue)
                    {
                        float ly = cs.GetYByValue(c.LastTick * ts);
                        if (!float.IsNaN(prevY)) RenderTarget.DrawLine(new SharpDX.Vector2(prevX, prevY), new SharpDX.Vector2(x + w / 2f, ly), lastBrush, 1.2f);
                        prevX = x + w / 2f; prevY = ly;
                    }
                }
                i = j;
            }
        }

        /// <summary>The book right now, as horizontal bars at the right edge (bid left-to-right in bid colour, ask in ask colour).</summary>
        private void RenderCurrentBook(ChartScale cs)
        {
            long max = book.MaxSize;
            if (max <= 0) return;
            double ts = TickSize;
            float right = ChartPanel.X + ChartPanel.W - 2f, wMax = (float)CurrentBookWidth;
            float rowH = Math.Max(1f, Math.Abs(cs.GetYByValue(cs.MinValue) - cs.GetYByValue(cs.MinValue + ts)) - 1f);
            bool labels = rowH >= FontSize + 2;
            for (int side = 0; side < 2; side++)
            {
                int count = side == 0 ? book.BidCount : book.AskCount;
                SharpDX.Direct2D1.SolidColorBrush b = Dx(side == 0 ? BidBrush : AskBrush, 0.8);
                for (int i = 0; i < count; i++)
                {
                    long tick = side == 0 ? book.BidTick(i) : book.AskTick(i), size = side == 0 ? book.BidSize(i) : book.AskSize(i);
                    double price = tick * ts;
                    if (price > cs.MaxValue || price < cs.MinValue || size <= 0) continue;
                    float w = wMax * size / max, y = cs.GetYByValue(price + ts / 2);
                    RenderTarget.FillRectangle(new SharpDX.RectangleF(right - w, y, w, rowH), b);
                    if (labels)
                        RenderTarget.DrawText(size.ToString(CultureInfo.InvariantCulture), textFormat,
                            new SharpDX.RectangleF(right - wMax - 50f, y, 46f, rowH + 2f), Dx(LastPriceBrush, 0.9));
                }
            }
        }

        private void RenderInfo()
        {
            string txt;
            if (State != State.Realtime) txt = "DrTrader Heatmap: realtime / Playback only - no historical market depth on charts";
            else if (depthEvents == 0) txt = "DrTrader Heatmap: waiting for market depth (Level 2) from the connection...";
            else txt = "DrTrader Heatmap  snapshots " + store.Count.ToString(CultureInfo.InvariantCulture) + "  scale " +
                       scale.ToString("0", CultureInfo.InvariantCulture) + "  levels " + (book.BidCount + book.AskCount).ToString(CultureInfo.InvariantCulture);
            RenderTarget.DrawText(txt, textFormat, new SharpDX.RectangleF(ChartPanel.X + 6f, (float)(ChartPanel.Y + ChartPanel.H - FontSize - 8), 900f, (float)(FontSize + 6)), Dx(LastPriceBrush, 0.75));
        }
        #endregion

        #region Properties
        [Range(50, 60000)] [Display(Name = "Snapshot interval (ms)", Description = "How often the book is copied into a heatmap column.", Order = 1, GroupName = "01. Data")]
        public int SnapshotMilliseconds { get; set; }
        [Range(100, 2000000)] [Display(Name = "Max snapshots kept", Description = "Memory bound. At 500 ms, 60000 snapshots = about 8 hours.", Order = 2, GroupName = "01. Data")]
        public int MaxSnapshots { get; set; }
        [Range(1, 500)] [Display(Name = "Max levels per side", Order = 3, GroupName = "01. Data")]
        public int MaxLevels { get; set; }
        [Range(1, 1000000)] [Display(Name = "Min size shown", Description = "Hide levels smaller than this (contracts).", Order = 4, GroupName = "01. Data")]
        public int MinSizeShown { get; set; }

        [Display(Name = "Palette", Order = 1, GroupName = "02. Display")] public GofHeatPalette Palette { get; set; }
        [Range(5, 100)] [Display(Name = "Opacity %", Order = 2, GroupName = "02. Display")] public double Opacity { get; set; }
        [Range(50, 100)] [Display(Name = "Scale percentile", Description = "Sizes at or above this percentile of the visible area get the hottest colour.", Order = 3, GroupName = "02. Display")] public double ScalePercentile { get; set; }
        [Range(0, 10000000)] [Display(Name = "Fixed scale (contracts, 0 = auto)", Order = 4, GroupName = "02. Display")] public double FixedScale { get; set; }
        [Display(Name = "Show current book", Order = 5, GroupName = "02. Display")] public bool ShowCurrentBook { get; set; }
        [Range(20, 600)] [Display(Name = "Current book width (px)", Order = 6, GroupName = "02. Display")] public double CurrentBookWidth { get; set; }
        [Display(Name = "Show last price path", Order = 7, GroupName = "02. Display")] public bool ShowLastPrice { get; set; }
        [Display(Name = "Show status line", Order = 8, GroupName = "02. Display")] public bool ShowInfo { get; set; }
        [Range(6, 20)] [Display(Name = "Font size", Order = 9, GroupName = "02. Display")] public double FontSize { get; set; }

        [XmlIgnore] [Display(Name = "Bid", Order = 1, GroupName = "03. Colors")] public Brush BidBrush { get; set; }
        [Browsable(false)] public string BidBrushSerializable { get { return Serialize.BrushToString(BidBrush); } set { BidBrush = Serialize.StringToBrush(value); } }
        [XmlIgnore] [Display(Name = "Ask", Order = 2, GroupName = "03. Colors")] public Brush AskBrush { get; set; }
        [Browsable(false)] public string AskBrushSerializable { get { return Serialize.BrushToString(AskBrush); } set { AskBrush = Serialize.StringToBrush(value); } }
        [XmlIgnore] [Display(Name = "Last price / text", Order = 3, GroupName = "03. Colors")] public Brush LastPriceBrush { get; set; }
        [Browsable(false)] public string LastPriceBrushSerializable { get { return Serialize.BrushToString(LastPriceBrush); } set { LastPriceBrush = Serialize.StringToBrush(value); } }
        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private DrTraderHeatmap[] cacheDrTraderHeatmap;
		public DrTraderHeatmap DrTraderHeatmap()
		{
			return DrTraderHeatmap(Input);
		}

		public DrTraderHeatmap DrTraderHeatmap(ISeries<double> input)
		{
			if (cacheDrTraderHeatmap != null)
				for (int idx = 0; idx < cacheDrTraderHeatmap.Length; idx++)
					if (cacheDrTraderHeatmap[idx] != null &&  cacheDrTraderHeatmap[idx].EqualsInput(input))
						return cacheDrTraderHeatmap[idx];
			return CacheIndicator<DrTraderHeatmap>(new DrTraderHeatmap(), input, ref cacheDrTraderHeatmap);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.DrTraderHeatmap DrTraderHeatmap()
		{
			return indicator.DrTraderHeatmap(Input);
		}

		public Indicators.DrTraderHeatmap DrTraderHeatmap(ISeries<double> input )
		{
			return indicator.DrTraderHeatmap(input);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.DrTraderHeatmap DrTraderHeatmap()
		{
			return indicator.DrTraderHeatmap(Input);
		}

		public Indicators.DrTraderHeatmap DrTraderHeatmap(ISeries<double> input )
		{
			return indicator.DrTraderHeatmap(input);
		}
	}
}

#endregion
