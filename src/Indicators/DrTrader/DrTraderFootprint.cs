// DrTrader Footprint - standalone footprint (cluster) chart for NinjaTrader 8.
// Shows executed volume at every price inside each bar, split by aggressor (sell at bid x buy at ask),
// with diagonal imbalances, stacked-imbalance zones, bar POC / value area, unfinished auctions,
// aggregated large executions ("tape" reconstruction) and a bar-statistics table.
// All data comes from the shared DrTrader engine (AddOns\DrTrader); this file only feeds it and draws.
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
    public class DrTraderFootprint : Indicator
    {
        #region Fields
        private OrderFlowEngine engine;
        private TradeIntake intake;
        private SessionIterator sessionIterator;
        private bool tickReplay;
        private bool useTickSeries;
        private int lastFinalizedBar = -1;
        private double lastBid, lastAsk;
        private bool errorLogged;
        private readonly Dictionary<string, SharpDX.Direct2D1.SolidColorBrush> dxBrushes = new Dictionary<string, SharpDX.Direct2D1.SolidColorBrush>();
        private SharpDX.DirectWrite.TextFormat cellFormat;
        private SharpDX.DirectWrite.TextFormat cellBoldFormat;
        private SharpDX.DirectWrite.TextFormat cellLeftFormat;
        private SharpDX.DirectWrite.TextFormat cellRightFormat;
        private SharpDX.DirectWrite.TextFormat statFormat;
        private SharpDX.Direct2D1.StrokeStyle dashStyle;
        private Palette pal;
        #endregion

        /// <summary>Theme colours as plain RGB (no WPF objects on the render thread).</summary>
        private struct Rgb
        {
            public byte R, G, B;
            public Rgb(byte r, byte g, byte b) { R = r; G = g; B = b; }
        }

        private sealed class Palette
        {
            public Rgb Back, Grid, Text, Dim, Buy, Sell, Poc, Va, Tape, Unfinished;
        }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "DrTrader Footprint: volume at price inside each bar (sell x buy), imbalances, stacked zones, bar POC/VA, unfinished auctions, aggregated executions and bar statistics.";
                Name = "DrTraderFootprint";
                Calculate = Calculate.OnEachTick;
                IsOverlay = true;
                DisplayInDataBox = false;
                DrawOnPricePanel = true;
                PaintPriceMarkers = false;
                IsAutoScale = false;
                IsSuspendedWhileInactive = false;
                MaximumBarsLookBack = MaximumBarsLookBack.Infinite;
                BarsRequiredToPlot = 0;

                DataSource = GofDataSource.Auto;
                InsideSpread = InsideSpreadMethod.MidpointThenTickRule;
                MaxBars = 3000;

                Mode = GofFootprintMode.BidAsk;
                Coloring = GofFootprintColoring.ImbalanceOnly;
                Theme = GofTheme.Dark;
                ColumnWidthPercent = 92;
                ShowCandleStrip = true;
                ShowBarPoc = true;
                ShowBarValueArea = true;
                BarValueAreaPercent = 70;
                ShowUnfinishedAuctions = true;
                CellOpacity = 55;

                ImbalanceRatio = 3.0;
                ImbalanceMinVolume = 5;
                StackedCount = 3;
                ShowStackedZones = true;
                ExtendZonesUntilBroken = true;
                MaxZoneBars = 300;

                ShowStats = true;
                StatVolume = true;
                StatDelta = true;
                StatDeltaPercent = true;
                StatMinMaxDelta = true;
                StatTrades = true;
                StatCvd = true;

                ShowTape = true;
                TapeAggregationMs = 5;
                TapeThresholdMode = LargeTradeThresholdMode.MaxOfManualAndAdaptive;
                TapeMinSize = 20;
                TapeMaxRadius = 14;

                FontSize = 10;
                AutoFontSize = true;
                MaxFontSize = 14;
                TicksPerRow = 0;
                TargetRowPixels = 16;
                StatsFontSize = 11;
                MinRowPixels = 11;
                MinBarPixels = 50;

                BuyBrush = Brushes.LimeGreen;
                SellBrush = Brushes.Crimson;
                BackBrush = Brushes.Black;
                TextBrush = Brushes.WhiteSmoke;
                PocBrush = Brushes.Gold;
                GridBrush = Brushes.DimGray;
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
                s.EnableSubSessions = false;
                s.EnableAbsorption = false;
                s.EnableDivergence = false;
                s.EnableSignals = false;
                s.EnableLargeTrades = ShowTape;
                s.CvdMode = CvdResetMode.Session;
                s.MaxFootprintBars = MaxBars;
                s.Bars.ImbalanceRatio = ImbalanceRatio;
                s.Bars.ImbalanceMinVolume = ImbalanceMinVolume;
                s.Bars.StackedCount = StackedCount;
                s.Large.Mode = TapeThresholdMode;
                s.Large.ManualThreshold = TapeMinSize;
                s.Large.AggregationMs = TapeAggregationMs;
                engine = new OrderFlowEngine(s);
                intake = new TradeIntake();
                sessionIterator = new SessionIterator(Bars);
                tickReplay = Bars.IsTickReplay;
                useTickSeries = !tickReplay && DataSource != GofDataSource.TickReplay && BarsArray.Length > 1;
                pal = BuildPalette();
            }
            else if (State == State.Realtime)
            {
                if (engine != null) lock (engine.Sync) engine.IsRealtime = true;
            }
            else if (State == State.Terminated)
            {
                DisposeDx();
                if (cellFormat != null) { cellFormat.Dispose(); cellFormat = null; }
                if (cellBoldFormat != null) { cellBoldFormat.Dispose(); cellBoldFormat = null; }
                if (cellLeftFormat != null) { cellLeftFormat.Dispose(); cellLeftFormat = null; }
                if (cellRightFormat != null) { cellRightFormat.Dispose(); cellRightFormat = null; }
                if (statFormat != null) { statFormat.Dispose(); statFormat = null; }
            }
        }

        private static Rgb ToRgb(Brush b, Rgb fallback)
        {
            SolidColorBrush s = b as SolidColorBrush;
            return s == null ? fallback : new Rgb(s.Color.R, s.Color.G, s.Color.B);
        }

        private Palette BuildPalette()
        {
            Palette p = new Palette();
            switch (Theme)
            {
                case GofTheme.Light:
                    p.Back = new Rgb(250, 250, 248); p.Grid = new Rgb(214, 218, 224); p.Text = new Rgb(28, 32, 38); p.Dim = new Rgb(140, 146, 156);
                    p.Buy = new Rgb(22, 150, 92); p.Sell = new Rgb(214, 58, 58); p.Poc = new Rgb(214, 150, 0); p.Va = new Rgb(225, 232, 244);
                    p.Tape = new Rgb(40, 110, 210); p.Unfinished = new Rgb(200, 120, 0);
                    break;
                case GofTheme.Midnight:
                    p.Back = new Rgb(10, 22, 40); p.Grid = new Rgb(34, 54, 82); p.Text = new Rgb(214, 226, 240); p.Dim = new Rgb(110, 130, 160);
                    p.Buy = new Rgb(64, 200, 170); p.Sell = new Rgb(240, 96, 120); p.Poc = new Rgb(255, 204, 80); p.Va = new Rgb(22, 42, 70);
                    p.Tape = new Rgb(120, 170, 255); p.Unfinished = new Rgb(255, 170, 60);
                    break;
                case GofTheme.Custom:
                    p.Back = ToRgb(BackBrush, new Rgb(0, 0, 0)); p.Grid = ToRgb(GridBrush, new Rgb(80, 80, 80)); p.Text = ToRgb(TextBrush, new Rgb(230, 230, 230));
                    p.Dim = new Rgb((byte)((p.Text.R + p.Back.R) / 2), (byte)((p.Text.G + p.Back.G) / 2), (byte)((p.Text.B + p.Back.B) / 2));
                    p.Buy = ToRgb(BuyBrush, new Rgb(0, 200, 0)); p.Sell = ToRgb(SellBrush, new Rgb(220, 20, 60)); p.Poc = ToRgb(PocBrush, new Rgb(255, 215, 0));
                    p.Va = new Rgb((byte)Math.Min(255, p.Back.R + 22), (byte)Math.Min(255, p.Back.G + 26), (byte)Math.Min(255, p.Back.B + 34));
                    p.Tape = new Rgb(70, 140, 255); p.Unfinished = new Rgb(255, 165, 0);
                    break;
                default: // Dark
                    p.Back = new Rgb(20, 24, 30); p.Grid = new Rgb(48, 55, 66); p.Text = new Rgb(222, 228, 236); p.Dim = new Rgb(120, 128, 140);
                    p.Buy = new Rgb(38, 166, 120); p.Sell = new Rgb(232, 72, 85); p.Poc = new Rgb(245, 190, 60); p.Va = new Rgb(34, 42, 54);
                    p.Tape = new Rgb(80, 150, 255); p.Unfinished = new Rgb(255, 160, 50);
                    break;
            }
            return p;
        }

        #region Data intake (same contract as the other DrTrader indicators)
        protected override void OnMarketData(MarketDataEventArgs e)
        {
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
                            engine.FinalizeBar(CurrentBar - 1, Time[1], Open[1], High[1], Low[1], Close[1], Volume[1]);
                            lastFinalizedBar = CurrentBar - 1;
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
                }
                catch (Exception ex)
                {
                    if (!errorLogged) { errorLogged = true; Log("DrTraderFootprint: " + ex.Message, LogLevel.Error); }
                }
                engine.DrainEvents(null);
                engine.DrainOutcomes(null);
            }
        }
        #endregion

        #region Rendering helpers
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

        private SharpDX.Direct2D1.SolidColorBrush Dx(Rgb c, double opacityPercent)
        {
            float a = (float)(Math.Max(0, Math.Min(100, opacityPercent)) / 100.0);
            string key = c.R + "," + c.G + "," + c.B + "," + a.ToString("0.000", CultureInfo.InvariantCulture);
            SharpDX.Direct2D1.SolidColorBrush dx;
            if (!dxBrushes.TryGetValue(key, out dx) || dx == null || dx.IsDisposed)
            {
                dx = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(c.R / 255f, c.G / 255f, c.B / 255f, a));
                dxBrushes[key] = dx;
            }
            return dx;
        }

        private SharpDX.DirectWrite.TextFormat MakeFormat(SharpDX.DirectWrite.FontWeight w, float size)
        {
            SharpDX.DirectWrite.TextFormat f = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Consolas", w,
                SharpDX.DirectWrite.FontStyle.Normal, size);
            f.TextAlignment = SharpDX.DirectWrite.TextAlignment.Center;
            f.ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center;
            f.WordWrapping = SharpDX.DirectWrite.WordWrapping.NoWrap;
            return f;
        }

        private void EnsureResources()
        {
            if (statFormat == null || Math.Abs(statFormat.FontSize - (float)StatsFontSize) > 0.01f)
            {
                if (statFormat != null) statFormat.Dispose();
                statFormat = MakeFormat(SharpDX.DirectWrite.FontWeight.Normal, (float)StatsFontSize);
            }
            if (dashStyle == null)
            {
                SharpDX.Direct2D1.StrokeStyleProperties props = new SharpDX.Direct2D1.StrokeStyleProperties();
                props.DashStyle = SharpDX.Direct2D1.DashStyle.Dash;
                dashStyle = new SharpDX.Direct2D1.StrokeStyle(NinjaTrader.Core.Globals.D2DFactory, props);
            }
        }

        private float X(ChartControl cc, int bar) { return cc.GetXByBarIndex(ChartBars, bar); }
        private float Y(ChartScale cs, double price) { return cs.GetYByValue(price); }

        private static string Fmt(long v) { return v.ToString(CultureInfo.InvariantCulture); }
        #endregion

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);
            if (engine == null || ChartBars == null || RenderTarget == null || pal == null) return;
            lock (engine.Sync)
            {
                SharpDX.Direct2D1.AntialiasMode oldMode = RenderTarget.AntialiasMode;
                try
                {
                    EnsureResources();
                    int from = Math.Max(0, ChartBars.FromIndex), to = ChartBars.ToIndex;
                    float colW = Math.Max(3f, chartControl.Properties.BarDistance * (float)(ColumnWidthPercent / 100.0));
                    float rowH = Math.Abs(Y(chartScale, chartScale.MinValue) - Y(chartScale, chartScale.MinValue + TickSize));
                    RenderTarget.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.Aliased;
                    if (ShowStackedZones) RenderZones(chartControl, chartScale, from, to, colW);
                    for (int bar = from; bar <= to; bar++) RenderBar(chartControl, chartScale, bar, colW, rowH);
                    RenderTarget.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.PerPrimitive;
                    if (ShowTape) RenderTape(chartControl, chartScale, from, to, colW);
                    if (ShowStats) RenderStats(chartControl, from, to, colW);
                }
                catch (Exception ex)
                {
                    if (!errorLogged) { errorLogged = true; Print("DrTraderFootprint render error: " + ex); }
                }
                finally { RenderTarget.AntialiasMode = oldMode; }
            }
        }

        private static readonly int[] RowSteps = new int[] { 1, 2, 3, 5, 10, 20, 25, 50, 100, 200 };

        /// <summary>Ticks merged into one footprint row: fixed, or (Auto) the smallest step whose row is at least TargetRowPixels tall.</summary>
        private int RowTicksFor(float pxPerTick)
        {
            if (TicksPerRow > 0) return TicksPerRow;
            for (int i = 0; i < RowSteps.Length; i++)
                if (RowSteps[i] * pxPerTick >= TargetRowPixels) return RowSteps[i];
            return RowSteps[RowSteps.Length - 1];
        }

        private void EnsureCellFormats(float size)
        {
            if (cellFormat != null && Math.Abs(cellFormat.FontSize - size) < 0.25f) return;
            if (cellFormat != null) cellFormat.Dispose();
            if (cellBoldFormat != null) cellBoldFormat.Dispose();
            if (cellLeftFormat != null) cellLeftFormat.Dispose();
            if (cellRightFormat != null) cellRightFormat.Dispose();
            cellFormat = MakeFormat(SharpDX.DirectWrite.FontWeight.Normal, size);
            cellBoldFormat = MakeFormat(SharpDX.DirectWrite.FontWeight.Bold, size);
            // sell number right-aligned to the left of the "x", buy number left-aligned to the right of it
            cellLeftFormat = MakeFormat(SharpDX.DirectWrite.FontWeight.Normal, size);
            cellLeftFormat.TextAlignment = SharpDX.DirectWrite.TextAlignment.Trailing;
            cellRightFormat = MakeFormat(SharpDX.DirectWrite.FontWeight.Normal, size);
            cellRightFormat.TextAlignment = SharpDX.DirectWrite.TextAlignment.Leading;
        }

        private void RenderBar(ChartControl cc, ChartScale cs, int bar, float colW, float pxPerTick)
        {
            BarFootprint f = engine.Footprints.Get(bar);
            if (f == null || !f.Ladder.HasData || pxPerTick <= 0) return;
            PriceLadder l = f.Ladder;
            bool counts = Mode == GofFootprintMode.TradeCount;
            double ts = TickSize;
            double top = cs.MaxValue, bottom = cs.MinValue;

            // ---- merge ticks into rows
            int n = RowTicksFor(pxPerTick);
            long firstRow = PriceMath.FloorDiv(l.MinTick, n), lastRow = PriceMath.FloorDiv(l.MaxTick, n);
            int rows = (int)(lastRow - firstRow + 1);
            long[] buy = new long[rows], sell = new long[rows], neu = new long[rows], tBuy = new long[rows], tSell = new long[rows];
            for (long t = l.MinTick; t <= l.MaxTick; t++)
            {
                int r = (int)(PriceMath.FloorDiv(t, n) - firstRow);
                buy[r] += l.BuyAt(t); sell[r] += l.SellAt(t); neu[r] += l.NeutralAt(t);
                tBuy[r] += f.Trades.BuyAt(t); tSell[r] += f.Trades.SellAt(t);
            }
            long maxVol = 1, maxAbsDelta = 1, total = 0;
            int poc = 0;
            for (int r = 0; r < rows; r++)
            {
                long v = buy[r] + sell[r] + neu[r];
                total += v;
                if (v > maxVol) { maxVol = v; poc = r; }
                long d = Math.Abs(buy[r] - sell[r]);
                if (d > maxAbsDelta) maxAbsDelta = d;
            }
            int vaLo = poc, vaHi = poc;
            if (ShowBarValueArea)
            {
                long acc = buy[poc] + sell[poc] + neu[poc], target = (long)Math.Ceiling(total * BarValueAreaPercent / 100.0);
                while (acc < target && (vaLo > 0 || vaHi < rows - 1))
                {
                    long up = vaHi < rows - 1 ? buy[vaHi + 1] + sell[vaHi + 1] + neu[vaHi + 1] : -1, dn = vaLo > 0 ? buy[vaLo - 1] + sell[vaLo - 1] + neu[vaLo - 1] : -1;
                    if (up >= dn) { vaHi++; acc += up; } else { vaLo--; acc += dn; }
                }
            }

            float rowPx = n * pxPerTick;
            float fontSize = AutoFontSize ? (float)Math.Max(7.0, Math.Min(MaxFontSize, rowPx * 0.62)) : (float)FontSize;
            EnsureCellFormats(fontSize);
            float x0 = X(cc, bar) - colW / 2f;
            float strip = ShowCandleStrip ? Math.Max(2f, Math.Min(5f, colW * 0.06f)) : 0f;
            float cx = x0 + (strip > 0 ? strip + 1f : 0f);
            float cellW = x0 + colW - cx;
            float yHi = Y(cs, ((lastRow + 1) * n - 0.5) * ts), yLo = Y(cs, (firstRow * n - 0.5) * ts);
            bool drawText = rowPx >= MinRowPixels && colW >= MinBarPixels;
            bool split = Mode == GofFootprintMode.BidAsk || counts;
            SharpDX.Direct2D1.SolidColorBrush text = Dx(pal.Text, 100), dim = Dx(pal.Dim, 100);

            RenderTarget.FillRectangle(new SharpDX.RectangleF(x0, yHi, colW, yLo - yHi), Dx(pal.Back, 100));
            if (ShowCandleStrip && bar < Bars.Count)
            {
                double o = Bars.GetOpen(bar), c = Bars.GetClose(bar);
                SharpDX.Direct2D1.SolidColorBrush sb = Dx(c >= o ? pal.Buy : pal.Sell, 100);
                float yo = Y(cs, o), yc = Y(cs, c);
                RenderTarget.DrawLine(new SharpDX.Vector2(x0 + strip / 2f, Y(cs, Bars.GetHigh(bar))), new SharpDX.Vector2(x0 + strip / 2f, Y(cs, Bars.GetLow(bar))), sb, 1f);
                RenderTarget.FillRectangle(new SharpDX.RectangleF(x0, Math.Min(yo, yc), strip, Math.Max(1f, Math.Abs(yc - yo))), sb);
            }

            float gap = 0.5f * fontSize;   // half-width of the "x" separator area
            for (int r = 0; r < rows; r++)
            {
                long rowLowTick = (firstRow + r) * n, rowHighTick = rowLowTick + n - 1;
                if ((rowLowTick - 0.5) * ts > top || (rowHighTick + 0.5) * ts < bottom) continue;
                float y = Y(cs, (rowHighTick + 0.5) * ts);
                float ch = Math.Max(1f, rowPx - (rowPx > 4 ? 1f : 0f));
                long b = buy[r], sl = sell[r], v = b + sl + neu[r];
                // diagonal: buy(row) vs sell(row below), sell(row) vs buy(row above); edge rows have no diagonal partner
                bool buyImb = r > 0 && b >= ImbalanceMinVolume && b >= ImbalanceRatio * sell[r - 1];
                bool sellImb = r < rows - 1 && sl >= ImbalanceMinVolume && sl >= ImbalanceRatio * buy[r + 1];

                if (ShowBarValueArea && r >= vaLo && r <= vaHi) RenderTarget.FillRectangle(new SharpDX.RectangleF(cx, y, cellW, ch), Dx(pal.Va, 100));
                switch (Coloring)
                {
                    case GofFootprintColoring.DeltaHeat:
                        if (v > 0) RenderTarget.FillRectangle(new SharpDX.RectangleF(cx, y, cellW, ch),
                            Dx(b >= sl ? pal.Buy : pal.Sell, CellOpacity * (0.15 + 0.85 * Math.Abs(b - sl) / (double)maxAbsDelta)));
                        break;
                    case GofFootprintColoring.VolumeHeat:
                        if (v > 0) RenderTarget.FillRectangle(new SharpDX.RectangleF(cx, y, cellW, ch), Dx(pal.Tape, CellOpacity * (0.1 + 0.9 * v / (double)maxVol)));
                        break;
                    case GofFootprintColoring.ProfileBars:
                        if (v > 0)
                        {
                            float w = (float)(cellW * v / (double)maxVol);
                            float ws = (float)(w * sl / (double)v);
                            RenderTarget.FillRectangle(new SharpDX.RectangleF(cx, y, ws, ch), Dx(pal.Sell, CellOpacity));
                            RenderTarget.FillRectangle(new SharpDX.RectangleF(cx + ws, y, Math.Max(0f, w - ws), ch), Dx(pal.Buy, CellOpacity));
                        }
                        break;
                    default:
                        if (sellImb) RenderTarget.FillRectangle(new SharpDX.RectangleF(cx, y, cellW / 2f, ch), Dx(pal.Sell, CellOpacity + 20));
                        if (buyImb) RenderTarget.FillRectangle(new SharpDX.RectangleF(cx + cellW / 2f, y, cellW / 2f, ch), Dx(pal.Buy, CellOpacity + 20));
                        break;
                }
                if (ShowBarPoc && r == poc) RenderTarget.DrawRectangle(new SharpDX.RectangleF(cx, y, cellW, ch), Dx(pal.Poc, 100), 1.5f);
                if (!drawText || v == 0) continue;

                bool bold = ShowBarPoc && r == poc;
                bool colourText = Coloring != GofFootprintColoring.ImbalanceOnly;
                if (split)
                {
                    long sv = counts ? tSell[r] : sl, bv = counts ? tBuy[r] : b;
                    float mid = cx + cellW / 2f;
                    SharpDX.Direct2D1.SolidColorBrush lt = colourText && sellImb ? Dx(pal.Sell, 100) : text;
                    SharpDX.Direct2D1.SolidColorBrush rt = colourText && buyImb ? Dx(pal.Buy, 100) : text;
                    if (bold) { lt = Dx(pal.Poc, 100); rt = Dx(pal.Poc, 100); }
                    RenderTarget.DrawText(Fmt(sv), cellLeftFormat, new SharpDX.RectangleF(cx, y, mid - gap - cx, rowPx), lt);
                    RenderTarget.DrawText("x", cellFormat, new SharpDX.RectangleF(mid - gap, y, 2 * gap, rowPx), dim);
                    RenderTarget.DrawText(Fmt(bv), cellRightFormat, new SharpDX.RectangleF(mid + gap, y, cx + cellW - mid - gap, rowPx), rt);
                }
                else
                {
                    long val = Mode == GofFootprintMode.Delta ? b - sl : v;
                    SharpDX.Direct2D1.SolidColorBrush tb = bold ? Dx(pal.Poc, 100) : (Mode == GofFootprintMode.Delta ? Dx(val >= 0 ? pal.Buy : pal.Sell, 100) : text);
                    RenderTarget.DrawText(Fmt(val), bold ? cellBoldFormat : cellFormat, new SharpDX.RectangleF(cx, y, cellW, rowPx), tb);
                }
            }
            RenderTarget.DrawRectangle(new SharpDX.RectangleF(cx, yHi, cellW, yLo - yHi), Dx(pal.Grid, 100), 1f);

            // unfinished auction: both sides traded at the exact bar extreme (no "zero print" at the high/low)
            if (ShowUnfinishedAuctions)
            {
                SharpDX.Direct2D1.SolidColorBrush ub = Dx(pal.Unfinished, 100);
                if (l.BuyAt(l.MaxTick) > 0 && l.SellAt(l.MaxTick) > 0)
                    RenderTarget.DrawLine(new SharpDX.Vector2(cx, yHi - 2f), new SharpDX.Vector2(cx + cellW, yHi - 2f), ub, 2f);
                if (l.BuyAt(l.MinTick) > 0 && l.SellAt(l.MinTick) > 0)
                    RenderTarget.DrawLine(new SharpDX.Vector2(cx, yLo + 2f), new SharpDX.Vector2(cx + cellW, yLo + 2f), ub, 2f);
            }
        }

        /// <summary>Stacked imbalance zones, optionally extended to the right until price trades through them.</summary>
        private void RenderZones(ChartControl cc, ChartScale cs, int from, int to, float colW)
        {
            List<StackedImbalanceZone> z = engine.Zones;
            int lastBar = Math.Max(to, engine.History.LastBar + 1);
            for (int i = z.Count - 1; i >= 0; i--)
            {
                StackedImbalanceZone s = z[i];
                if (s.Bar < from - MaxZoneBars) break;
                int end = ExtendZonesUntilBroken ? (s.BrokenBar >= 0 ? s.BrokenBar : lastBar) : s.Bar;
                if (end < from || s.Bar > to) continue;
                float x0 = X(cc, s.Bar) + colW / 2f;
                float x1 = end == s.Bar ? x0 + colW / 3f : X(cc, Math.Min(end, to)) + colW / 2f;
                float y1 = Y(cs, (s.HighTick + 0.5) * TickSize), y2 = Y(cs, (s.LowTick - 0.5) * TickSize);
                Rgb c = s.Side > 0 ? pal.Buy : pal.Sell;
                RenderTarget.FillRectangle(new SharpDX.RectangleF(x0, y1, Math.Max(2f, x1 - x0), Math.Max(1f, y2 - y1)), Dx(c, s.BrokenBar >= 0 ? 12 : 22));
                RenderTarget.DrawLine(new SharpDX.Vector2(x0, y1), new SharpDX.Vector2(x1, y1), Dx(c, 70), 1f);
                RenderTarget.DrawLine(new SharpDX.Vector2(x0, y2), new SharpDX.Vector2(x1, y2), Dx(c, 70), 1f);
            }
        }

        /// <summary>
        /// "Tape reconstruction": consecutive same-side prints within the aggregation window, executed at monotonic
        /// prices, are shown as one execution bubble when the total reaches the threshold. This groups prints that
        /// very likely belong together; it does not prove they came from a single order.
        /// </summary>
        private void RenderTape(ChartControl cc, ChartScale cs, int from, int to, float colW)
        {
            List<LargeTradeEvent> ev = engine.Large.Events;
            int lo = 0, hi = ev.Count;
            while (lo < hi) { int mid = (lo + hi) / 2; if (ev[mid].Bar < from) lo = mid + 1; else hi = mid; }
            for (int i = lo; i < ev.Count; i++)
            {
                LargeTradeEvent e = ev[i];
                if (e.Bar > to) break;
                double price = e.Vwap * TickSize;
                if (price > cs.MaxValue || price < cs.MinValue) continue;
                double ratio = e.Threshold > 0 ? e.Volume / e.Threshold : 1.0;
                float r = (float)Math.Min(TapeMaxRadius, 3.0 * Math.Sqrt(Math.Max(1.0, ratio)) + 2.0);
                SharpDX.Vector2 c = new SharpDX.Vector2(X(cc, e.Bar) + colW * 0.25f * (e.Side > 0 ? 1f : -1f), Y(cs, price));
                SharpDX.Direct2D1.Ellipse el = new SharpDX.Direct2D1.Ellipse(c, r, r);
                RenderTarget.FillEllipse(el, Dx(e.Side > 0 ? pal.Buy : pal.Sell, 45));
                RenderTarget.DrawEllipse(el, Dx(pal.Tape, 100), 1.2f);
                if (r >= 7) RenderTarget.DrawText(Fmt(e.Volume), statFormat, new SharpDX.RectangleF(c.X - r, c.Y - r, 2 * r, 2 * r), Dx(pal.Text, 100));
            }
        }

        /// <summary>Statistics table docked at the bottom of the price panel, one column per bar.</summary>
        private void RenderStats(ChartControl cc, int from, int to, float colW)
        {
            List<string> names = new List<string>();
            if (StatVolume) names.Add("Vol");
            if (StatDelta) names.Add("Delta");
            if (StatDeltaPercent) names.Add("Delta%");
            if (StatMinMaxDelta) { names.Add("MaxD"); names.Add("MinD"); }
            if (StatTrades) names.Add("Trades");
            if (StatCvd) names.Add("CVD");
            if (names.Count == 0) return;

            float lineH = (float)StatsFontSize + 8f;
            float h = names.Count * lineH;
            float y0 = ChartPanel.Y + ChartPanel.H - h - 2f;
            float labelW = 66f;
            RenderTarget.FillRectangle(new SharpDX.RectangleF(ChartPanel.X, y0, ChartPanel.W, h), Dx(pal.Back, 92));
            bool text = colW >= 30f;
            SharpDX.Direct2D1.SolidColorBrush tb = Dx(pal.Text, 100);

            long maxVol = 1, maxAbsD = 1;
            for (int bar = from; bar <= to; bar++)
            {
                BarFootprint f = engine.Footprints.Get(bar);
                if (f == null) continue;
                if (f.Volume > maxVol) maxVol = f.Volume;
                if (Math.Abs(f.Delta) > maxAbsD) maxAbsD = Math.Abs(f.Delta);
            }
            for (int bar = from; bar <= to; bar++)
            {
                BarFootprint f = engine.Footprints.Get(bar);
                if (f == null || !f.Ladder.HasData) continue;
                float x0 = X(cc, bar) - colW / 2f;
                if (x0 < ChartPanel.X + labelW) continue;
                BarRecord r = engine.History.Get(bar);
                long cvd = r != null ? r.SessionCvd : engine.Delta.SessionCvd;
                long maxD = f.CvdHigh - f.CvdOpen, minD = f.CvdLow - f.CvdOpen;
                for (int k = 0; k < names.Count; k++)
                {
                    float y = y0 + k * lineH;
                    string val;
                    Rgb c = pal.Grid;
                    double op = 60;
                    switch (names[k])
                    {
                        case "Vol": val = Fmt(f.Volume); c = pal.Tape; op = 15 + 60.0 * f.Volume / maxVol; break;
                        case "Delta": val = Fmt(f.Delta); c = f.Delta >= 0 ? pal.Buy : pal.Sell; op = 15 + 70.0 * Math.Abs(f.Delta) / maxAbsD; break;
                        case "Delta%":
                            double dp = f.Volume > 0 ? 100.0 * f.Delta / f.Volume : 0;
                            val = dp.ToString("0", CultureInfo.InvariantCulture) + "%"; c = dp >= 0 ? pal.Buy : pal.Sell; op = 15 + Math.Min(70, Math.Abs(dp) * 1.5); break;
                        case "MaxD": val = Fmt(maxD); c = pal.Buy; op = 25; break;
                        case "MinD": val = Fmt(minD); c = pal.Sell; op = 25; break;
                        case "Trades": val = Fmt(f.Trades.TotalSell) + "x" + Fmt(f.Trades.TotalBuy); c = pal.Grid; op = 40; break;
                        default: val = Fmt(cvd); c = cvd >= 0 ? pal.Buy : pal.Sell; op = 30; break;
                    }
                    RenderTarget.FillRectangle(new SharpDX.RectangleF(x0, y + 1f, colW, lineH - 2f), Dx(c, op));
                    if (text) RenderTarget.DrawText(val, statFormat, new SharpDX.RectangleF(x0, y, colW, lineH), tb);
                }
            }
            for (int k = 0; k < names.Count; k++)
                RenderTarget.DrawText(names[k], statFormat, new SharpDX.RectangleF(ChartPanel.X + 2f, y0 + k * lineH, labelW - 4f, lineH), Dx(pal.Dim, 100));
        }

        #region Properties
        [Display(Name = "Data source", Description = "Auto: Tick Replay if enabled (exact bid/ask), otherwise 1-tick series (aggressor side estimated in history).", Order = 1, GroupName = "01. Data")]
        public GofDataSource DataSource { get; set; }
        [Display(Name = "Inside-spread rule", Order = 2, GroupName = "01. Data")]
        public InsideSpreadMethod InsideSpread { get; set; }
        [Range(100, 100000)] [Display(Name = "Max bars with footprint", Order = 3, GroupName = "01. Data")]
        public int MaxBars { get; set; }

        [Display(Name = "Cell content", Description = "BidAsk: sell x buy contracts. TradeCount: sell x buy number of trades (prints, not orders). Delta: buy - sell. Volume: total.", Order = 1, GroupName = "02. Footprint")]
        public GofFootprintMode Mode { get; set; }
        [Display(Name = "Cell coloring", Order = 2, GroupName = "02. Footprint")]
        public GofFootprintColoring Coloring { get; set; }
        [Range(30, 100)] [Display(Name = "Column width %", Order = 3, GroupName = "02. Footprint")]
        public double ColumnWidthPercent { get; set; }
        [Display(Name = "Candle strip", Order = 4, GroupName = "02. Footprint")]
        public bool ShowCandleStrip { get; set; }
        [Display(Name = "Bar POC", Order = 5, GroupName = "02. Footprint")]
        public bool ShowBarPoc { get; set; }
        [Display(Name = "Bar value area", Order = 6, GroupName = "02. Footprint")]
        public bool ShowBarValueArea { get; set; }
        [Range(1, 100)] [Display(Name = "Bar value area %", Order = 7, GroupName = "02. Footprint")]
        public double BarValueAreaPercent { get; set; }
        [Display(Name = "Unfinished auctions", Description = "Marks a bar high/low where both bid and ask traded (no zero print).", Order = 8, GroupName = "02. Footprint")]
        public bool ShowUnfinishedAuctions { get; set; }
        [Range(0, 80)] [Display(Name = "Cell opacity %", Order = 9, GroupName = "02. Footprint")]
        public double CellOpacity { get; set; }

        [Display(Name = "Theme", Description = "Dark, Light, Midnight or Custom (uses the colours below).", Order = 1, GroupName = "03. Theme")]
        public GofTheme Theme { get; set; }
        [XmlIgnore] [Display(Name = "Custom: buy", Order = 2, GroupName = "03. Theme")] public Brush BuyBrush { get; set; }
        [Browsable(false)] public string BuyBrushSerializable { get { return Serialize.BrushToString(BuyBrush); } set { BuyBrush = Serialize.StringToBrush(value); } }
        [XmlIgnore] [Display(Name = "Custom: sell", Order = 3, GroupName = "03. Theme")] public Brush SellBrush { get; set; }
        [Browsable(false)] public string SellBrushSerializable { get { return Serialize.BrushToString(SellBrush); } set { SellBrush = Serialize.StringToBrush(value); } }
        [XmlIgnore] [Display(Name = "Custom: background", Order = 4, GroupName = "03. Theme")] public Brush BackBrush { get; set; }
        [Browsable(false)] public string BackBrushSerializable { get { return Serialize.BrushToString(BackBrush); } set { BackBrush = Serialize.StringToBrush(value); } }
        [XmlIgnore] [Display(Name = "Custom: text", Order = 5, GroupName = "03. Theme")] public Brush TextBrush { get; set; }
        [Browsable(false)] public string TextBrushSerializable { get { return Serialize.BrushToString(TextBrush); } set { TextBrush = Serialize.StringToBrush(value); } }
        [XmlIgnore] [Display(Name = "Custom: POC", Order = 6, GroupName = "03. Theme")] public Brush PocBrush { get; set; }
        [Browsable(false)] public string PocBrushSerializable { get { return Serialize.BrushToString(PocBrush); } set { PocBrush = Serialize.StringToBrush(value); } }
        [XmlIgnore] [Display(Name = "Custom: grid", Order = 7, GroupName = "03. Theme")] public Brush GridBrush { get; set; }
        [Browsable(false)] public string GridBrushSerializable { get { return Serialize.BrushToString(GridBrush); } set { GridBrush = Serialize.StringToBrush(value); } }

        [Range(1.0, 100.0)] [Display(Name = "Diagonal imbalance ratio", Description = "3 = 300 %", Order = 1, GroupName = "04. Imbalance")]
        public double ImbalanceRatio { get; set; }
        [Range(1, 100000)] [Display(Name = "Imbalance min volume", Order = 2, GroupName = "04. Imbalance")]
        public int ImbalanceMinVolume { get; set; }
        [Range(2, 50)] [Display(Name = "Stacked count", Order = 3, GroupName = "04. Imbalance")]
        public int StackedCount { get; set; }
        [Display(Name = "Show stacked zones", Order = 4, GroupName = "04. Imbalance")]
        public bool ShowStackedZones { get; set; }
        [Display(Name = "Extend zones until broken", Order = 5, GroupName = "04. Imbalance")]
        public bool ExtendZonesUntilBroken { get; set; }
        [Range(10, 5000)] [Display(Name = "Zone look-back (bars)", Order = 6, GroupName = "04. Imbalance")]
        public int MaxZoneBars { get; set; }

        [Display(Name = "Show statistics table", Order = 1, GroupName = "05. Bar Statistics")] public bool ShowStats { get; set; }
        [Display(Name = "Volume", Order = 2, GroupName = "05. Bar Statistics")] public bool StatVolume { get; set; }
        [Display(Name = "Delta", Order = 3, GroupName = "05. Bar Statistics")] public bool StatDelta { get; set; }
        [Display(Name = "Delta %", Order = 4, GroupName = "05. Bar Statistics")] public bool StatDeltaPercent { get; set; }
        [Display(Name = "Max / min delta (intrabar)", Order = 5, GroupName = "05. Bar Statistics")] public bool StatMinMaxDelta { get; set; }
        [Display(Name = "Trades (sell x buy)", Order = 6, GroupName = "05. Bar Statistics")] public bool StatTrades { get; set; }
        [Display(Name = "Session CVD", Order = 7, GroupName = "05. Bar Statistics")] public bool StatCvd { get; set; }

        [Display(Name = "Show aggregated executions", Description = "Tape reconstruction: same-side prints within the window shown as one bubble when large.", Order = 1, GroupName = "06. Tape")]
        public bool ShowTape { get; set; }
        [Range(-1, 1000)] [Display(Name = "Aggregation window (ms)", Description = "-1 = no aggregation (each print alone).", Order = 2, GroupName = "06. Tape")]
        public int TapeAggregationMs { get; set; }
        [Display(Name = "Threshold mode", Order = 3, GroupName = "06. Tape")]
        public LargeTradeThresholdMode TapeThresholdMode { get; set; }
        [Range(1, 100000)] [Display(Name = "Min size (contracts)", Order = 4, GroupName = "06. Tape")]
        public int TapeMinSize { get; set; }
        [Range(4, 60)] [Display(Name = "Max bubble radius", Order = 5, GroupName = "06. Tape")]
        public double TapeMaxRadius { get; set; }

        [Display(Name = "Ticks per row (0 = Auto)", Description = "0 = Auto: merge ticks into one row so each row is at least the target height. Otherwise a fixed number of ticks per row.", Order = 1, GroupName = "07. Rows & Text")]
        [Range(0, 200)] public int TicksPerRow { get; set; }
        [Range(8, 60)] [Display(Name = "Target row height (px, Auto)", Order = 2, GroupName = "07. Rows & Text")] public double TargetRowPixels { get; set; }
        [Display(Name = "Auto font size", Description = "Font follows the row height (between 7 and Max font size).", Order = 3, GroupName = "07. Rows & Text")] public bool AutoFontSize { get; set; }
        [Range(8, 30)] [Display(Name = "Max font size", Order = 4, GroupName = "07. Rows & Text")] public double MaxFontSize { get; set; }
        [Range(6, 30)] [Display(Name = "Fixed font size", Description = "Used when Auto font size is off.", Order = 5, GroupName = "07. Rows & Text")] public double FontSize { get; set; }
        [Range(6, 20)] [Display(Name = "Statistics font size", Order = 6, GroupName = "07. Rows & Text")] public double StatsFontSize { get; set; }
        [Range(4, 60)] [Display(Name = "Min row height for numbers (px)", Order = 7, GroupName = "07. Rows & Text")] public double MinRowPixels { get; set; }
        [Range(10, 400)] [Display(Name = "Min bar width for numbers (px)", Order = 8, GroupName = "07. Rows & Text")] public double MinBarPixels { get; set; }
        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private DrTraderFootprint[] cacheDrTraderFootprint;
		public DrTraderFootprint DrTraderFootprint()
		{
			return DrTraderFootprint(Input);
		}

		public DrTraderFootprint DrTraderFootprint(ISeries<double> input)
		{
			if (cacheDrTraderFootprint != null)
				for (int idx = 0; idx < cacheDrTraderFootprint.Length; idx++)
					if (cacheDrTraderFootprint[idx] != null &&  cacheDrTraderFootprint[idx].EqualsInput(input))
						return cacheDrTraderFootprint[idx];
			return CacheIndicator<DrTraderFootprint>(new DrTraderFootprint(), input, ref cacheDrTraderFootprint);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.DrTraderFootprint DrTraderFootprint()
		{
			return indicator.DrTraderFootprint(Input);
		}

		public Indicators.DrTraderFootprint DrTraderFootprint(ISeries<double> input )
		{
			return indicator.DrTraderFootprint(input);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.DrTraderFootprint DrTraderFootprint()
		{
			return indicator.DrTraderFootprint(Input);
		}

		public Indicators.DrTraderFootprint DrTraderFootprint(ISeries<double> input )
		{
			return indicator.DrTraderFootprint(input);
		}
	}
}

#endregion
