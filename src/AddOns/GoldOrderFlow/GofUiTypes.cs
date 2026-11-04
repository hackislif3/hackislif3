// GOF - enums used by the NinjaScript indicator property grids.
// Kept outside the indicator classes (NinjaTrader recommendation) so they can be shared and serialized.
namespace NinjaTrader.NinjaScript.AddOns.GoldOrderFlow
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

    /// <summary>Classic: neutral cells, colour only on imbalances, candle strip on the left. Heatmap: cells coloured by delta/volume.</summary>
    public enum GofFootprintStyle { Classic = 0, Heatmap = 1 }

    public enum GofCvdPanelMode { CvdCandles = 0, CvdLine = 1, BarDelta = 2, DeltaPercent = 3 }
}
