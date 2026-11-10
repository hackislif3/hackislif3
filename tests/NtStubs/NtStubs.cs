// Minimal STUBS of the NinjaTrader 8 / WPF / SharpDX surface used by the GOF indicators.
// Purpose: type-check the NinjaScript files on a machine without NinjaTrader (CI / Linux).
// They mirror the public signatures used; they do NOT reproduce NinjaTrader behaviour.
// The real compile target is NinjaTrader's NinjaScript Editor.
using System;
using System.Collections.Generic;

namespace System.Windows.Media
{
    public struct Color
    {
        public byte A, R, G, B;
        public static Color FromArgb(byte a, byte r, byte g, byte b) { Color c = new Color(); c.A = a; c.R = r; c.G = g; c.B = b; return c; }
    }
    public abstract class Brush { }
    public class SolidColorBrush : Brush
    {
        public Color Color;
        public SolidColorBrush(Color c) { Color = c; }
    }
    public static class Colors { public static Color Gray { get { return Color.FromArgb(255, 128, 128, 128); } } }
    public static class Brushes
    {
        private static SolidColorBrush B(byte r, byte g, byte b) { return new SolidColorBrush(Color.FromArgb(255, r, g, b)); }
        public static SolidColorBrush LimeGreen { get { return B(50, 205, 50); } }
        public static SolidColorBrush Crimson { get { return B(220, 20, 60); } }
        public static SolidColorBrush SlateGray { get { return B(112, 128, 144); } }
        public static SolidColorBrush SteelBlue { get { return B(70, 130, 180); } }
        public static SolidColorBrush Gold { get { return B(255, 215, 0); } }
        public static SolidColorBrush DeepSkyBlue { get { return B(0, 191, 255); } }
        public static SolidColorBrush Orange { get { return B(255, 165, 0); } }
        public static SolidColorBrush MediumPurple { get { return B(147, 112, 219); } }
        public static SolidColorBrush Magenta { get { return B(255, 0, 255); } }
        public static SolidColorBrush LightSteelBlue { get { return B(176, 196, 222); } }
        public static SolidColorBrush Teal { get { return B(0, 128, 128); } }
        public static SolidColorBrush Sienna { get { return B(160, 82, 45); } }
        public static SolidColorBrush Lime { get { return B(0, 255, 0); } }
        public static SolidColorBrush Red { get { return B(255, 0, 0); } }
        public static SolidColorBrush Yellow { get { return B(255, 255, 0); } }
        public static SolidColorBrush Gray { get { return B(128, 128, 128); } }
        public static SolidColorBrush Black { get { return B(0, 0, 0); } }
        public static SolidColorBrush White { get { return B(255, 255, 255); } }
        public static SolidColorBrush WhiteSmoke { get { return B(245, 245, 245); } }
        public static SolidColorBrush Transparent { get { return new SolidColorBrush(Color.FromArgb(0, 255, 255, 255)); } }
        public static SolidColorBrush DodgerBlue { get { return B(30, 144, 255); } }
        public static SolidColorBrush DimGray { get { return B(105, 105, 105); } }
        public static SolidColorBrush Orchid { get { return B(128, 128, 128); } }
        public static SolidColorBrush IndianRed { get { return B(128, 128, 128); } }
        public static SolidColorBrush SeaGreen { get { return B(128, 128, 128); } }
    }
}

namespace SharpDX
{
    public struct Color4 { public Color4(float r, float g, float b, float a) { } }
    public struct Vector2 { public float X, Y; public Vector2(float x, float y) { X = x; Y = y; } }
    public struct RectangleF { public RectangleF(float x, float y, float w, float h) { } }
    public abstract class DisposeBase : IDisposable { public bool IsDisposed { get { return false; } } public void Dispose() { } }
}

namespace SharpDX.Direct2D1
{
    public enum AntialiasMode { PerPrimitive, Aliased }
    public enum DashStyle { Solid, Dash, Dot }
    public enum FigureBegin { Filled, Hollow }
    public enum FigureEnd { Open, Closed }
    public class Factory : SharpDX.DisposeBase { }
    public struct StrokeStyleProperties { public DashStyle DashStyle; }
    public class StrokeStyle : SharpDX.DisposeBase { public StrokeStyle(Factory f, StrokeStyleProperties p) { } }
    public struct Ellipse { public Ellipse(SharpDX.Vector2 c, float rx, float ry) { } }
    public abstract class Brush : SharpDX.DisposeBase { }
    public class SolidColorBrush : Brush { public SolidColorBrush(RenderTarget t, SharpDX.Color4 c) { } }
    public class Geometry : SharpDX.DisposeBase { }
    public class GeometrySink : SharpDX.DisposeBase
    {
        public void BeginFigure(SharpDX.Vector2 p, FigureBegin b) { }
        public void AddLine(SharpDX.Vector2 p) { }
        public void EndFigure(FigureEnd e) { }
        public void Close() { }
    }
    public class PathGeometry : Geometry { public PathGeometry(Factory f) { } public GeometrySink Open() { return new GeometrySink(); } }
    public class RenderTarget : SharpDX.DisposeBase
    {
        public AntialiasMode AntialiasMode { get; set; }
        public void FillRectangle(SharpDX.RectangleF r, Brush b) { }
        public void DrawRectangle(SharpDX.RectangleF r, Brush b, float w) { }
        public void DrawRectangle(SharpDX.RectangleF r, Brush b, float w, StrokeStyle s) { }
        public void DrawLine(SharpDX.Vector2 a, SharpDX.Vector2 b, Brush br, float w) { }
        public void DrawLine(SharpDX.Vector2 a, SharpDX.Vector2 b, Brush br, float w, StrokeStyle s) { }
        public void FillEllipse(Ellipse e, Brush b) { }
        public void DrawEllipse(Ellipse e, Brush b, float w) { }
        public void FillGeometry(Geometry g, Brush b) { }
        public void DrawGeometry(Geometry g, Brush b, float w) { }
        public void DrawTextLayout(SharpDX.Vector2 p, SharpDX.DirectWrite.TextLayout l, Brush b) { }
        public void DrawText(string text, SharpDX.DirectWrite.TextFormat f, SharpDX.RectangleF r, Brush b) { }
    }
}

namespace SharpDX.DirectWrite
{
    public enum FontWeight { Normal, Bold }
    public enum FontStyle { Normal, Italic }
    public enum TextAlignment { Leading, Trailing, Center, Justified }
    public enum ParagraphAlignment { Near, Far, Center }
    public enum WordWrapping { Wrap, NoWrap }
    public class Factory : SharpDX.DisposeBase { }
    public class TextFormat : SharpDX.DisposeBase
    {
        public TextFormat(Factory f, string family, float size) { FontSize = size; }
        public TextFormat(Factory f, string family, FontWeight w, FontStyle s, float size) { FontSize = size; }
        public float FontSize { get; private set; }
        public TextAlignment TextAlignment { get; set; }
        public ParagraphAlignment ParagraphAlignment { get; set; }
        public WordWrapping WordWrapping { get; set; }
    }
    public struct TextMetrics { public float Width, Height; }
    public class TextLayout : SharpDX.DisposeBase
    {
        public TextLayout(Factory f, string text, TextFormat fmt, float maxW, float maxH) { }
        public TextMetrics Metrics { get { return new TextMetrics(); } }
    }
}

namespace NinjaTrader.Core
{
    public class GeneralOptionsStub { public TimeZoneInfo TimeZoneInfo { get { return TimeZoneInfo.Local; } } }
    public static class Globals
    {
        public static SharpDX.DirectWrite.Factory DirectWriteFactory;
        public static SharpDX.Direct2D1.Factory D2DFactory;
        public static string UserDataDir = "";
        public static string InstallDir = "";
        public static GeneralOptionsStub GeneralOptions = new GeneralOptionsStub();
    }
}

namespace NinjaTrader.Cbi
{
    public enum Operation { Add, Update, Remove }
    public enum LogLevel { Information, Warning, Error }
    public class MasterInstrument
    {
        public string Name; public double TickSize; public double PointValue;
        public string FormatPrice(double p) { return p.ToString(); }
    }
    public class Instrument { public string FullName; public MasterInstrument MasterInstrument; }
}

namespace NinjaTrader.Data
{
    public enum MarketDataType { Ask, Bid, Last, DailyHigh, DailyLow }
    public enum BarsPeriodType { Tick, Volume, Second, Minute, Range }
    public class MarketDataEventArgs
    {
        public MarketDataType MarketDataType; public double Price; public long Volume; public double Bid; public double Ask; public DateTime Time;
    }
    public class MarketDepthEventArgs
    {
        public MarketDataType MarketDataType; public NinjaTrader.Cbi.Operation Operation; public int Position; public double Price; public long Volume; public DateTime Time;
    }
    public class BarsPeriod { public BarsPeriodType BarsPeriodType; public int Value; }
    public class Bars
    {
        public bool IsTickReplay; public bool IsFirstBarOfSession; public int Count;
        public double GetOpen(int i) { return 0; } public double GetHigh(int i) { return 0; }
        public double GetLow(int i) { return 0; } public double GetClose(int i) { return 0; }
    }
    public class SessionIterator
    {
        public SessionIterator(Bars b) { }
        public bool GetNextSession(DateTime t, bool includesEndTimeStamp) { return true; }
        public DateTime ActualTradingDayExchange { get { return DateTime.Today; } }
        public DateTime ActualSessionBegin { get { return DateTime.Today; } }
    }
}

namespace NinjaTrader.Gui
{
    using System.Windows.Media;
    public enum PlotStyle { Line, Bar, Dot }
    public class Stroke { public Stroke(Brush b, float w) { } }
    public static class Serialize
    {
        public static string BrushToString(Brush b) { return ""; }
        public static Brush StringToBrush(string s) { return Brushes.Gray; }
    }
}

namespace NinjaTrader.Gui.Chart
{
    public class ChartBars { public int FromIndex; public int ToIndex; }
    public class ChartPanel { public int X, Y, W, H; }
    public class ChartControlProperties
    {
        public float BarDistance { get; set; }
        public System.Windows.Media.Brush ChartBackground { get; set; }
    }
    public class ChartControl
    {
        public ChartControlProperties Properties { get { return null; } }
        public int GetXByBarIndex(ChartBars b, int idx) { return idx; }
        public int GetBarPaintWidth(ChartBars b) { return 5; }
    }
    public class ChartScale
    {
        public double MaxValue, MinValue;
        public int GetYByValue(double v) { return (int)v; }
    }
}

namespace NinjaTrader.NinjaScript
{
    using NinjaTrader.Data;
    public enum State { SetDefaults, Configure, Active, DataLoaded, Historical, Transition, Realtime, Terminated }
    public enum Calculate { OnBarClose, OnEachTick, OnPriceChange }
    public enum MaximumBarsLookBack { TwoHundredFiftySix, Infinite }
    public enum ScaleJustification { Left, Right, Overlay }
    public enum Priority { Low, Medium, High }

    public interface ISeries<T> { T this[int barsAgo] { get; } }
    public class Series<T> : ISeries<T>
    {
        public Series(NinjaScriptBase owner, MaximumBarsLookBack l) { }
        public T this[int barsAgo] { get { return default(T); } set { } }
    }
    public class BrushSeries { public System.Windows.Media.Brush this[int barsAgo] { get { return null; } set { } } }
    public class Plot { public System.Windows.Media.Brush Brush { get; set; } }

    public abstract class NinjaScriptBase
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public State State { get; protected set; }
        public Calculate Calculate { get; set; }
        public bool EqualsInput(ISeries<double> input) { return true; }
        public ISeries<double> Input { get { return null; } }
        public void Print(object o) { }
        public void Log(string m, NinjaTrader.Cbi.LogLevel l) { }
        public void Update() { }
        protected virtual void OnStateChange() { }
        protected virtual void OnBarUpdate() { }
    }
    public class MarketAnalyzerColumnBase : NinjaScriptBase { }
}

namespace NinjaTrader.Gui.NinjaScript
{
    using System.Windows.Media;
    using NinjaTrader.Data;
    using NinjaTrader.Gui.Chart;
    using NinjaTrader.NinjaScript;

    public abstract class IndicatorRenderBase : NinjaScriptBase
    {
        public bool IsOverlay { get; set; }
        public bool DisplayInDataBox { get; set; }
        public bool DrawOnPricePanel { get; set; }
        public bool PaintPriceMarkers { get; set; }
        public bool IsAutoScale { get; set; }
        public bool IsSuspendedWhileInactive { get; set; }
        public MaximumBarsLookBack MaximumBarsLookBack { get; set; }
        public int BarsRequiredToPlot { get; set; }
        public ScaleJustification ScaleJustification { get; set; }
        public NinjaTrader.Cbi.Instrument Instrument { get { return null; } }
        public Bars Bars { get { return null; } }
        public Bars[] BarsArray { get { return null; } }
        public BarsPeriod BarsPeriod { get { return null; } }
        public int CurrentBar { get { return 0; } }
        public int BarsInProgress { get { return 0; } }
        public bool IsFirstTickOfBar { get { return true; } }
        public double TickSize { get { return 0.1; } }
        public ISeries<DateTime> Time { get { return null; } }
        public ISeries<double> Open { get { return null; } }
        public ISeries<double> High { get { return null; } }
        public ISeries<double> Low { get { return null; } }
        public ISeries<double> Close { get { return null; } }
        public ISeries<double> Volume { get { return null; } }
        public ISeries<DateTime>[] Times { get { return null; } }
        public ISeries<double>[] Closes { get { return null; } }
        public ISeries<double>[] Volumes { get { return null; } }
        public Series<double>[] Values { get { return null; } }
        public Plot[] Plots { get { return null; } }
        public BrushSeries[] PlotBrushes { get { return null; } }
        public double MinValue { get; set; }
        public double MaxValue { get; set; }
        public SharpDX.Direct2D1.RenderTarget RenderTarget { get { return null; } }
        public ChartBars ChartBars { get { return null; } }
        public ChartPanel ChartPanel { get { return null; } }
        public void AddDataSeries(BarsPeriodType t, int v) { }
        public void AddPlot(Brush b, string name) { }
        public void AddPlot(NinjaTrader.Gui.Stroke s, NinjaTrader.Gui.PlotStyle ps, string name) { }
        public void AddLine(Brush b, double v, string name) { }
        public void Alert(string id, Priority p, string msg, string sound, int rearm, Brush back, Brush fore) { }
        protected virtual void OnMarketData(MarketDataEventArgs e) { }
        protected virtual void OnMarketDepth(MarketDepthEventArgs e) { }
        protected virtual void OnRender(ChartControl cc, ChartScale cs) { }
        public virtual void OnRenderTargetChanged() { }
        public virtual void OnCalculateMinMax() { }
        protected T CacheIndicator<T>(T ind, ISeries<double> input, ref T[] cache) where T : class { return ind; }
    }
    public abstract class StrategyRenderBase : NinjaScriptBase { }
}

namespace NinjaTrader.NinjaScript.Indicators
{
    public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase { }

    // Names of NinjaTrader's BUILT-IN indicators that live in this namespace. Inside it they hide same-named
    // types from 'using' directives, so any GOF type with one of these names breaks the real compile.
    public class VolumeProfile : Indicator { }
    public class VolumeZones : Indicator { }
    public class VolumeCounter : Indicator { }
    public class VolumeUpDown : Indicator { }
    public class BuySellVolume : Indicator { }
    public class BuySellPressure : Indicator { }
    public class BlockVolume : Indicator { }
    public class OrderFlowCumulativeDelta : Indicator { }
    public class OrderFlowVolumeProfile : Indicator { }
    public class OrderFlowTradeDetector : Indicator { }
    public class OrderFlowVWAP : Indicator { }
    public class Range : Indicator { }
    public class Swing : Indicator { }
    public class ZigZag : Indicator { }
    public class Pivots : Indicator { }
    public class ATR : Indicator { }
    public class VWMA : Indicator { }
    public class PriceLine : Indicator { }
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
    public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
    {
        protected NinjaTrader.NinjaScript.Indicators.Indicator indicator;
    }
}

namespace NinjaTrader.NinjaScript.Strategies
{
    public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
    {
        protected NinjaTrader.NinjaScript.Indicators.Indicator indicator;
    }
}
