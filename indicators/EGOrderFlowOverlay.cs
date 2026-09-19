#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using SharpDX;
using SharpDX.Direct2D1;
#endregion

namespace NinjaTrader.NinjaScript.Indicators.EducatedGambling
{
    public enum EGOrderFlowOverlayLineAlignment { Left, Center, Right }
    public enum EGOrderFlowOverlayLargeTradePrintMode { Lines, Bubbles }

    [CategoryOrder("Standard Trade Detector", 1)]
    [CategoryOrder("Large Trade Detection", 2)]
    public class EGOrderFlowOverlay : Indicator
    {
        private struct PendingTrade
        {
            public double Price;
            public double Size;
            public bool IsBuy;
            public bool IsLarge;
        }

        private struct ConfirmedTrade
        {
            public int BarIndex;
            public double Price;
            public bool IsBuy;
            public bool IsLarge;
            public double Strength;
            public double SequenceFactor;
            public double LargeSizeRatio;
        }

        private double currentBid;
        private double currentAsk;
        private Dictionary<int, List<PendingTrade>> pendingByBar;
        private List<ConfirmedTrade> confirmedTrades;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Marks individual trade prints on the chart at their exact price, colored by buy/sell side and scaled in opacity by relative size within the bar. Large trades get their own color and can render as bubbles instead of lines. Uses a hidden 1-tick data series — no Tick Replay required.";
                Name = "EGOrderFlowOverlay";
                Calculate = Calculate.OnBarClose;
                IsOverlay = true;
                DisplayInDataBox = false;
                DrawOnPricePanel = true;

                TradeThreshold = 10;
                SellColor = Brushes.IndianRed;
                BuyColor = Brushes.SteelBlue;
                LineLengthBars = 1;
                LineWidth = 2;
                Alignment = EGOrderFlowOverlayLineAlignment.Left;
                SequenceMinOpacity = 0.35;
                StrengthMinOpacity = 0.35;

                EnableLargeTradeDetection = true;
                LargeTradeThreshold = 30;
                LargeSellColor = Brushes.Red;
                LargeBuyColor = Brushes.Lime;
                LargeTradePrintMode = EGOrderFlowOverlayLargeTradePrintMode.Lines;
                BubbleRadius = 6;
                LargeSizeMinOpacity = 0.35;
            }
            else if (State == State.Configure)
            {
                AddDataSeries(Instrument.FullName, BarsPeriodType.Tick, 1, MarketDataType.Last);
                AddDataSeries(Instrument.FullName, BarsPeriodType.Tick, 1, MarketDataType.Bid);
                AddDataSeries(Instrument.FullName, BarsPeriodType.Tick, 1, MarketDataType.Ask);
            }
            else if (State == State.DataLoaded)
            {
                currentBid = double.NaN;
                currentAsk = double.NaN;
                pendingByBar = new Dictionary<int, List<PendingTrade>>();
                confirmedTrades = new List<ConfirmedTrade>();

                // Rendering used to go through tagged Draw.Line objects before this indicator
                // switched to OnRender/SharpDX. Those chart-persisted objects don't get cleaned
                // up just because the source no longer creates them, so an instance that's been
                // sitting on a chart since that earlier version can show stale lines alongside
                // the new bubbles. Wipe anything this indicator instance previously drew.
                RemoveDrawObjects();
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress == 2)
            {
                currentBid = Close[0];
                return;
            }

            if (BarsInProgress == 3)
            {
                currentAsk = Close[0];
                return;
            }

            if (BarsInProgress == 1)
            {
                double price = Close[0];
                double size = Volume[0];

                if (CurrentBars[0] >= 0 && size >= TradeThreshold &&
                    !double.IsNaN(currentAsk) && !double.IsNaN(currentBid) && currentAsk > currentBid &&
                    (price >= currentAsk || price <= currentBid))
                {
                    bool isBuy = price >= currentAsk;
                    bool isLarge = EnableLargeTradeDetection && size >= LargeTradeThreshold;

                    // If this tick happened after the current known bar's own close time,
                    // it actually belongs to the next bar, which hasn't closed (or been
                    // indexed by CurrentBars[0]) yet.
                    int targetBar = (Time[0] <= Times[0][0]) ? CurrentBars[0] : CurrentBars[0] + 1;

                    List<PendingTrade> list;
                    if (!pendingByBar.TryGetValue(targetBar, out list))
                    {
                        list = new List<PendingTrade>();
                        pendingByBar[targetBar] = list;
                    }
                    list.Add(new PendingTrade { Price = price, Size = size, IsBuy = isBuy, IsLarge = isLarge });

                    Print(string.Format("[EGOrderFlowOverlay] Trade queued: TargetBar {0} Time {1} Price {2} Size {3} Side {4} Large {5} Bid {6} Ask {7}",
                        targetBar, Time[0], price, size, isBuy ? "BUY" : "SELL", isLarge, currentBid, currentAsk));
                }

                return;
            }

            FlushPendingTrades();
        }

        private void FlushPendingTrades()
        {
            if (pendingByBar.Count == 0)
                return;

            List<int> readyBars = new List<int>();
            foreach (KeyValuePair<int, List<PendingTrade>> entry in pendingByBar)
            {
                if (entry.Key <= CurrentBar)
                    readyBars.Add(entry.Key);
            }

            foreach (int barIndex in readyBars)
            {
                List<PendingTrade> trades = pendingByBar[barIndex];
                pendingByBar.Remove(barIndex);

                // Largest large-trade size and largest standard-trade size seen in this bar, kept
                // completely separate — same approach as EGFootprintLadder.cs's large trade dots
                // (maxLargeTradeSizeThisBar / sizeRatio), just computed per historical bar here
                // instead of live against the one forming bar. Standard trades are never scaled
                // against large-trade sizes: since large trades can be many times bigger, using
                // Large Trade Threshold as the scale's ceiling crushed nearly every standard trade
                // toward the opacity floor. Each population is normalized against its own max.
                double maxLargeSizeInBar = 0;
                double maxStandardSizeInBar = 0;
                for (int i = 0; i < trades.Count; i++)
                {
                    if (trades[i].IsLarge)
                    {
                        if (trades[i].Size > maxLargeSizeInBar)
                            maxLargeSizeInBar = trades[i].Size;
                    }
                    else if (trades[i].Size > maxStandardSizeInBar)
                    {
                        maxStandardSizeInBar = trades[i].Size;
                    }
                }

                for (int i = 0; i < trades.Count; i++)
                {
                    PendingTrade trade = trades[i];

                    // Strength: this standard trade's size relative to the biggest standard trade
                    // in the same bar (large trades excluded entirely from the comparison).
                    double strength = !trade.IsLarge && maxStandardSizeInBar > 0
                        ? trade.Size / maxStandardSizeInBar
                        : 1.0;

                    // Sequence: this trade's position among all qualifying trades in the same bar
                    // (0 = first printed, 1 = most recent), used to fade earlier trades in.
                    double sequenceFactor = trades.Count > 1 ? (double)i / (trades.Count - 1) : 1.0;

                    double largeSizeRatio = trade.IsLarge && maxLargeSizeInBar > 0
                        ? trade.Size / maxLargeSizeInBar
                        : 1.0;

                    confirmedTrades.Add(new ConfirmedTrade
                    {
                        BarIndex = barIndex,
                        Price = trade.Price,
                        IsBuy = trade.IsBuy,
                        IsLarge = trade.IsLarge,
                        Strength = strength,
                        SequenceFactor = sequenceFactor,
                        LargeSizeRatio = largeSizeRatio
                    });
                }
            }
        }

        private void DrawTradeLine(float x, float y, SharpDX.Direct2D1.Brush brush, float lengthPixels)
        {
            float xStart, xEnd;
            switch (Alignment)
            {
                case EGOrderFlowOverlayLineAlignment.Right:
                    xStart = x;
                    xEnd = x + lengthPixels;
                    break;
                case EGOrderFlowOverlayLineAlignment.Center:
                    xStart = x - lengthPixels / 2f;
                    xEnd = x + lengthPixels / 2f;
                    break;
                case EGOrderFlowOverlayLineAlignment.Left:
                default:
                    xStart = x - lengthPixels;
                    xEnd = x;
                    break;
            }

            RenderTarget.DrawLine(new SharpDX.Vector2(xStart, y), new SharpDX.Vector2(xEnd, y), brush, (float)LineWidth);
        }

        // Left/Center/Right alignment needs to sit within a bar's own pixel footprint rather than
        // jump between bar centers — Draw.Line only addresses whole bar-center-to-bar-center spans
        // (see root CLAUDE.md "Draw.* position is bar-relative"), so Center at a 1-bar length was
        // indistinguishable from Right (integer bar-hop math floors the split toward one side).
        // OnRender gives continuous pixel positioning, so Center is a true symmetric split instead.
        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);

            if (Bars == null || ChartControl == null || IsInHitTest || ChartBars == null || confirmedTrades == null)
                return;

            if (ChartBars.ToIndex < ChartBars.FromIndex)
                return;

            int spacingSampleIndex = (ChartBars.FromIndex + 1 <= ChartBars.ToIndex) ? ChartBars.FromIndex : ChartBars.FromIndex - 1;
            float xA = chartControl.GetXByBarIndex(ChartBars, ChartBars.FromIndex);
            float xB = chartControl.GetXByBarIndex(ChartBars, spacingSampleIndex + 1);
            float barSpacing = Math.Abs(xB - xA);
            if (barSpacing <= 0)
                barSpacing = 1f;

            float lengthPixels = LineLengthBars * barSpacing;

            System.Windows.Media.Color buyBaseColor = ((System.Windows.Media.SolidColorBrush)BuyColor).Color;
            System.Windows.Media.Color sellBaseColor = ((System.Windows.Media.SolidColorBrush)SellColor).Color;

            SharpDX.Direct2D1.Brush largeBuyBrush = LargeBuyColor.ToDxBrush(RenderTarget);
            SharpDX.Direct2D1.Brush largeSellBrush = LargeSellColor.ToDxBrush(RenderTarget);
            try
            {
                for (int i = 0; i < confirmedTrades.Count; i++)
                {
                    ConfirmedTrade trade = confirmedTrades[i];
                    if (trade.BarIndex < ChartBars.FromIndex || trade.BarIndex > ChartBars.ToIndex)
                        continue;

                    float x = chartControl.GetXByBarIndex(ChartBars, trade.BarIndex);
                    float y = chartScale.GetYByValue(trade.Price);

                    if (trade.IsLarge)
                    {
                        SharpDX.Direct2D1.Brush dxBrush = trade.IsBuy ? largeBuyBrush : largeSellBrush;

                        // Gradient: the single biggest large trade in this bar renders at full
                        // opacity; smaller (but still-qualifying) large trades fade toward
                        // LargeSizeMinOpacity, same formula as EGFootprintLadder's large trade dots.
                        dxBrush.Opacity = (float)(LargeSizeMinOpacity + (1.0 - LargeSizeMinOpacity) * trade.LargeSizeRatio);

                        if (LargeTradePrintMode == EGOrderFlowOverlayLargeTradePrintMode.Bubbles)
                            RenderTarget.FillEllipse(new SharpDX.Direct2D1.Ellipse(new SharpDX.Vector2(x, y), (float)BubbleRadius, (float)BubbleRadius), dxBrush);
                        else
                            DrawTradeLine(x, y, dxBrush, lengthPixels);

                        dxBrush.Opacity = 1f;
                    }
                    else
                    {
                        System.Windows.Media.Color baseColor = trade.IsBuy ? buyBaseColor : sellBaseColor;

                        // Strength and Sequence both drive opacity of this trade's OWN Buy/Sell
                        // color — neither ever blends toward the Large Buy/Sell color, so a
                        // standard trade can't visually read as an actual large trade.
                        double strengthOpacity = StrengthMinOpacity + (1.0 - StrengthMinOpacity) * trade.Strength;
                        double sequenceOpacity = SequenceMinOpacity + (1.0 - SequenceMinOpacity) * trade.SequenceFactor;
                        double opacity = strengthOpacity * sequenceOpacity;
                        byte alphaByte = (byte)(255.0 * Math.Max(0.0, Math.Min(1.0, opacity)));

                        System.Windows.Media.SolidColorBrush wpfBrush = new System.Windows.Media.SolidColorBrush(
                            System.Windows.Media.Color.FromArgb(alphaByte, baseColor.R, baseColor.G, baseColor.B));

                        SharpDX.Direct2D1.Brush dxBrush = wpfBrush.ToDxBrush(RenderTarget);
                        try
                        {
                            DrawTradeLine(x, y, dxBrush, lengthPixels);
                        }
                        finally
                        {
                            dxBrush.Dispose();
                        }
                    }
                }
            }
            finally
            {
                largeBuyBrush.Dispose();
                largeSellBrush.Dispose();
            }
        }

        #region Properties

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Trade Threshold", Description = "Single individual trade print size at or above which a line is drawn on the bar", GroupName = "Standard Trade Detector", Order = 1)]
        public int TradeThreshold { get; set; }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Sell Color", Description = "Color for standard trades printed at or below the bid (seller-initiated); opacity, not hue, scales with size and print order", GroupName = "Standard Trade Detector", Order = 2)]
        public System.Windows.Media.Brush SellColor { get; set; }

        [Browsable(false)]
        public string SellColorSerializable
        {
            get { return Serialize.BrushToString(SellColor); }
            set { SellColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Buy Color", Description = "Color for standard trades printed at or above the ask (buyer-initiated); opacity, not hue, scales with size and print order", GroupName = "Standard Trade Detector", Order = 3)]
        public System.Windows.Media.Brush BuyColor { get; set; }

        [Browsable(false)]
        public string BuyColorSerializable
        {
            get { return Serialize.BrushToString(BuyColor); }
            set { BuyColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Line Length (Bars)", Description = "Horizontal length of the line, in units of one bar's on-screen width; extension direction is controlled by Alignment", GroupName = "Standard Trade Detector", Order = 4)]
        public int LineLengthBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Line Width", Description = "Pixel width/thickness of the line", GroupName = "Standard Trade Detector", Order = 5)]
        public double LineWidth { get; set; }

        [XmlIgnore]
        [Display(Name = "Alignment", Description = "Horizontal alignment of the line relative to the bar it's drawn on", GroupName = "Standard Trade Detector", Order = 6)]
        public EGOrderFlowOverlayLineAlignment Alignment { get; set; }

        [Browsable(false)]
        public string AlignmentSerializable
        {
            get { return Alignment.ToString(); }
            set { Alignment = (EGOrderFlowOverlayLineAlignment)Enum.Parse(typeof(EGOrderFlowOverlayLineAlignment), value); }
        }

        [NinjaScriptProperty]
        [Range(0.0, 1.0)]
        [Display(Name = "Sequence Min Opacity", Description = "Opacity of the first qualifying standard trade in a bar; later trades in the same bar fade in toward full opacity, visualizing print order", GroupName = "Standard Trade Detector", Order = 7)]
        public double SequenceMinOpacity { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 1.0)]
        [Display(Name = "Strength Min Opacity", Description = "Floor opacity for the smallest standard trade in a bar; the largest standard trade in that bar renders fully opaque. Large trade sizes are excluded from this comparison, and this never blends toward the Large colors", GroupName = "Standard Trade Detector", Order = 8)]
        public double StrengthMinOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Large Trade Detection", Description = "When disabled, no trade is ever treated as large — all qualifying trades render using only the Standard Trade Detector's line, regardless of size", GroupName = "Large Trade Detection", Order = 1)]
        public bool EnableLargeTradeDetection { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Large Trade Threshold", Description = "Single individual trade print size at or above which the trade uses the solid Large Sell/Large Buy Color and Large Trade Print Mode", GroupName = "Large Trade Detection", Order = 2)]
        public int LargeTradeThreshold { get; set; }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Large Sell Color", Description = "Color when a large trade printed at or below the bid (large seller-initiated)", GroupName = "Large Trade Detection", Order = 3)]
        public System.Windows.Media.Brush LargeSellColor { get; set; }

        [Browsable(false)]
        public string LargeSellColorSerializable
        {
            get { return Serialize.BrushToString(LargeSellColor); }
            set { LargeSellColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Large Buy Color", Description = "Color when a large trade printed at or above the ask (large buyer-initiated)", GroupName = "Large Trade Detection", Order = 4)]
        public System.Windows.Media.Brush LargeBuyColor { get; set; }

        [Browsable(false)]
        public string LargeBuyColorSerializable
        {
            get { return Serialize.BrushToString(LargeBuyColor); }
            set { LargeBuyColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Large Trade Print Mode", Description = "How large trades are rendered: Lines (same style as normal trades) or Bubbles (a filled circle at the trade's price, instead of a line)", GroupName = "Large Trade Detection", Order = 5)]
        public EGOrderFlowOverlayLargeTradePrintMode LargeTradePrintMode { get; set; }

        [Browsable(false)]
        public string LargeTradePrintModeSerializable
        {
            get { return LargeTradePrintMode.ToString(); }
            set { LargeTradePrintMode = (EGOrderFlowOverlayLargeTradePrintMode)Enum.Parse(typeof(EGOrderFlowOverlayLargeTradePrintMode), value); }
        }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "Bubble Radius", Description = "Pixel radius of each bubble", GroupName = "Large Trade Detection", Order = 6)]
        public double BubbleRadius { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 1.0)]
        [Display(Name = "Large Size Min Opacity", Description = "Floor opacity for the smallest qualifying large trade in a bar; the largest large trade in that bar renders fully opaque — same gradient approach as EGFootprintLadder's large trade dots", GroupName = "Large Trade Detection", Order = 7)]
        public double LargeSizeMinOpacity { get; set; }

        #endregion
    }
}
