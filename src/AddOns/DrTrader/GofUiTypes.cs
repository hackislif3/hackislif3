// GOF - enums used by the NinjaScript indicator property grids.
// Kept outside the indicator classes (NinjaTrader recommendation) so they can be shared and serialized.
namespace NinjaTrader.NinjaScript.AddOns.DrTrader
{
    /// <summary>
    /// Auto       : Tick Replay chart -> OnMarketData (exact bid/ask); otherwise a 1-tick secondary series
    ///              (exact volume at price, aggressor side ESTIMATED by tick rule) for history, OnMarketData in realtime.
    /// TickReplay : only OnMarketData; without Tick Replay history is empty (flagged on the dashboard).
    /// TickSeries : always use the 1-tick series for history (no Tick Replay needed; sides estimated).
    /// </summary>
    public enum GofDataSource { Auto = 0, TickReplay = 1, TickSeries = 2 }

    public enum GofInstrumentPreset { Auto = 0, Custom = 1 }

    public enum GofPanelProfileSource { None = 0, Session = 1, Day = 2, Week = 3, VisibleRange = 4, FixedRange = 5 }

    public enum GofProfileSide { Right = 0, Left = 1 }

    public enum GofProfileColorMode { Volume = 0, BuySellSplit = 1 }

    public enum GofCorner { TopLeft = 0, TopRight = 1, BottomLeft = 2, BottomRight = 3 }

    /// <summary>BidAsk: sell x buy contracts. TradeCount: sell x buy number of prints (not orders). Delta: buy - sell. Volume: total.</summary>
    public enum GofFootprintMode { BidAsk = 0, TradeCount = 1, Delta = 2, Volume = 3 }

    /// <summary>Cell colouring of the footprint: only imbalanced sides, heat by delta, heat by volume, or a volume bar inside each cell.</summary>
    public enum GofFootprintColoring { ImbalanceOnly = 0, DeltaHeat = 1, VolumeHeat = 2, ProfileBars = 3 }

    /// <summary>Built-in colour themes of the DrTrader footprint (Custom = use the colour properties).</summary>
    public enum GofTheme { Dark = 0, Light = 1, Midnight = 2, Custom = 3 }

    public enum GofCvdPanelMode { CvdCandles = 0, CvdLine = 1, BarDelta = 2, DeltaPercent = 3 }
}
