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
    public enum EGOrderFlowOverlayLargeTradePrintMode { Lines, BubblesFilled, BubblesUnfilled }

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

        // Running high for standard trade size, used as Strength's denominator. Scoped to the
        // current session (reset on Bars.IsFirstBarOfSession) rather than to a single bar — a
        // per-bar max meant the single biggest standard trade in ANY bar always normalized to
        // Strength = 1.0 regardless of its actual size, so bar after bar the "biggest" line
        // rendered near-maximum length/opacity even when real trade sizes varied a lot bar to
        // bar. A session-scoped running high fixes that while still self-calibrating to the
        // instrument's actual volume that day, instead of requiring a fixed configured number
        // (which crushed most trades toward the floor the one time that was tried — see the
        // comment in FlushPendingTrades).
        private double sessionMaxStandardSize;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Marks individual trade prints on the chart at their exact price, colored by buy/sell side and scaled in opacity by relative size within the bar. Standard trades can optionally extend rightward by strength with a fading tail. Large trades get their own color and can render as filled or unfilled bubbles instead of lines. Uses a hidden 1-tick data series — no Tick Replay required.";
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
                ExtendByStrength = false;
                MaxExtendLengthBars = 5;

                EnableLargeTradeDetection = true;
                LargeTradeThreshold = 30;
                LargeSellColor = Brushes.Red;
                LargeBuyColor = Brushes.Lime;
                LargeTradePrintMode = EGOrderFlowOverlayLargeTradePrintMode.Lines;
                MaxBubbleSize = 14;
                BubbleBorderWidth = 2;
                LargeSizeMinOpacity = 0.35;
                PrintForward = false;
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
                sessionMaxStandardSize = 0;

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

            // Reset the Strength reference at the start of each new session (per the chart's
            // Trading Hours template) rather than never, so a stale running high from a prior,
            // possibly much busier session doesn't keep suppressing today's trades toward the
            // opacity/length floor. Checked here (primary series, BarsInProgress == 0) against
            // the bar that's about to be flushed, before that bar's own trades are compared
            // against it — simplification: this only reliably catches a session boundary when
            // the flush isn't multiple bars behind, which matches how the rest of the deferred
            // flush in FlushPendingTrades is already documented to behave.
            if (Bars.IsFirstBarOfSession)
                sessionMaxStandardSize = 0;

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

                // Largest large-trade size seen in THIS bar (large trades stay per-bar, unchanged)
                // and the running session-high standard trade size (sessionMaxStandardSize field),
                // kept completely separate — same approach as EGFootprintLadder.cs's large trade
                // dots (maxLargeTradeSizeThisBar / sizeRatio) for the large side. Standard trades
                // are never scaled against large-trade sizes: since large trades can be many times
                // bigger, using Large Trade Threshold as the scale's ceiling crushed nearly every
                // standard trade toward the opacity floor.
                //
                // sessionMaxStandardSize is updated here (not reset here — see the
                // Bars.IsFirstBarOfSession check in OnBarUpdate) so a new session-high print in
                // THIS bar still correctly normalizes to Strength = 1.0 for its own bar.
                double maxLargeSizeInBar = 0;
                for (int i = 0; i < trades.Count; i++)
                {
                    if (trades[i].IsLarge)
                    {
                        if (trades[i].Size > maxLargeSizeInBar)
                            maxLargeSizeInBar = trades[i].Size;
                    }
                    else if (trades[i].Size > sessionMaxStandardSize)
                    {
                        sessionMaxStandardSize = trades[i].Size;
                    }
                }

                for (int i = 0; i < trades.Count; i++)
                {
                    PendingTrade trade = trades[i];

                    // Strength: this standard trade's size relative to the biggest standard trade
                    // seen so far this session (large trades excluded entirely from the
                    // comparison). Session-scoped rather than per-bar so the single biggest
                    // standard trade in any one bar doesn't automatically render at full
                    // strength/length regardless of its actual size — see the comment on
                    // sessionMaxStandardSize's declaration.
                    double strength = !trade.IsLarge && sessionMaxStandardSize > 0
                        ? trade.Size / sessionMaxStandardSize
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

        private void DrawTradeLine(float x, float y, SharpDX.Direct2D1.Brush brush, float lengthPixels, float widthPixels)
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

            RenderTarget.DrawLine(new SharpDX.Vector2(xStart, y), new SharpDX.Vector2(xEnd, y), brush, widthPixels);
        }

        // Extend By Strength: the line always extends rightward from the trade's price point
        // (direction is fixed, not controlled by Alignment) with length scaled by trade.Strength —
        // Line Length (Bars) becomes the length AT full strength (the biggest standard trade in
        // the bar); weaker trades draw shorter. It also fades from the trade's already-computed
        // Strength/Sequence opacity (startAlpha) at the origin down to fully transparent at the
        // tip, instead of one flat opacity across the whole line the way the non-extended mode
        // renders.
        //
        // This approximates the fade as a series of short segments with linearly decreasing
        // opacity (mutating one brush's Opacity per segment, same pattern the large-trade
        // rendering already uses) rather than a true Direct2D linear gradient brush — the NT8-
        // exposed RenderTarget doesn't surface CreateGradientStopCollection (confirmed via
        // CS1061 on a real compile), so this sticks to APIs already proven to work in this file.
        private const int ExtendedFadeSegments = 10;

        // Fixed floor for large-trade bubble size (see OnRender) — not exposed as a property.
        // Having this alongside a separate user-facing "Max Bubble Size" as a min/max pair read
        // as two conflicting bubble-size controls in the Properties dialog, so only the ceiling
        // is configurable; the floor stays small and constant.
        private const float MinBubbleRadiusPixels = 2f;

        private void DrawExtendedFadeLine(float x, float y, System.Windows.Media.Color baseColor, byte startAlpha, float lengthPixels, float widthPixels)
        {
            System.Windows.Media.SolidColorBrush wpfBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(255, baseColor.R, baseColor.G, baseColor.B));

            SharpDX.Direct2D1.Brush dxBrush = wpfBrush.ToDxBrush(RenderTarget);
            try
            {
                float startOpacityFraction = startAlpha / 255f;
                float segmentLength = lengthPixels / ExtendedFadeSegments;

                for (int seg = 0; seg < ExtendedFadeSegments; seg++)
                {
                    float fadeT = (float)seg / ExtendedFadeSegments;
                    dxBrush.Opacity = startOpacityFraction * (1f - fadeT);

                    float segStartX = x + seg * segmentLength;
                    float segEndX = segStartX + segmentLength;
                    RenderTarget.DrawLine(new SharpDX.Vector2(segStartX, y), new SharpDX.Vector2(segEndX, y), dxBrush, widthPixels);
                }
            }
            finally
            {
                dxBrush.Opacity = 1f;
                dxBrush.Dispose();
            }
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
            float maxExtendLengthPixels = MaxExtendLengthBars * barSpacing;

            // Length already scales with bar spacing above (it's expressed in bar units), but
            // LineWidth/MaxBubbleSize/BubbleBorderWidth are configured in raw pixels, which stay
            // visually constant regardless of zoom. Once bars get small enough that a fixed pixel
            // size exceeds the space available, the marker stops looking like it's reacting to
            // further zooming. Clamp each to the current bar spacing so they shrink in lockstep
            // once they run out of room, while still respecting the configured value as a ceiling
            // when there's enough space (e.g. zoomed in normally).
            float widthPixels = Math.Max(1f, Math.Min((float)LineWidth, barSpacing));

            // Bubble size also rides the LargeSizeRatio gradient already driving opacity: the
            // smallest qualifying large trade in a bar renders at MinBubbleRadiusPixels (a fixed
            // floor, not user-facing — a separate min/max pair of properties read as conflicting
            // when both showed up in the Properties dialog), the biggest renders at
            // bubbleRadiusMaxPixels (Max Bubble Size), same "min + (max-min)*ratio" shape as the
            // opacity gradient just below. Deliberately NOT clamped to bar spacing like
            // LineWidth/lengthPixels are — a large-trade bubble is meant to stand out past the
            // bar's own width as a callout, and capping it to barSpacing/2 was crushing every
            // bubble into a narrow, visually-indistinguishable size range at normal zoom.
            float bubbleRadiusMinPixels = MinBubbleRadiusPixels;
            float bubbleRadiusMaxPixels = Math.Max(bubbleRadiusMinPixels, (float)MaxBubbleSize);

            System.Windows.Media.Color buyBaseColor = ((System.Windows.Media.SolidColorBrush)BuyColor).Color;
            System.Windows.Media.Color sellBaseColor = ((System.Windows.Media.SolidColorBrush)SellColor).Color;

            SharpDX.Direct2D1.Brush largeBuyBrush = LargeBuyColor.ToDxBrush(RenderTarget);
            SharpDX.Direct2D1.Brush largeSellBrush = LargeSellColor.ToDxBrush(RenderTarget);
            try
            {
                // Print Forward: bubbles are meant to stand out as the "front" layer, so when
                // enabled, skip large trades on this first pass and draw them in a second pass
                // below instead — later draws land on top in this immediate-mode renderer, so
                // that second pass puts every bubble in front of every standard trade's line,
                // regardless of which one was detected first within a bar.
                for (int i = 0; i < confirmedTrades.Count; i++)
                {
                    ConfirmedTrade trade = confirmedTrades[i];
                    if (trade.BarIndex < ChartBars.FromIndex || trade.BarIndex > ChartBars.ToIndex)
                        continue;
                    if (PrintForward && trade.IsLarge)
                        continue;

                    float x = chartControl.GetXByBarIndex(ChartBars, trade.BarIndex);
                    float y = chartScale.GetYByValue(trade.Price);

                    if (trade.IsLarge)
                        RenderLargeTrade(trade, x, y, largeBuyBrush, largeSellBrush, lengthPixels, widthPixels, bubbleRadiusMinPixels, bubbleRadiusMaxPixels);
                    else
                        RenderStandardTrade(trade, x, y, buyBaseColor, sellBaseColor, lengthPixels, widthPixels, maxExtendLengthPixels);
                }

                if (PrintForward)
                {
                    for (int i = 0; i < confirmedTrades.Count; i++)
                    {
                        ConfirmedTrade trade = confirmedTrades[i];
                        if (!trade.IsLarge)
                            continue;
                        if (trade.BarIndex < ChartBars.FromIndex || trade.BarIndex > ChartBars.ToIndex)
                            continue;

                        float x = chartControl.GetXByBarIndex(ChartBars, trade.BarIndex);
                        float y = chartScale.GetYByValue(trade.Price);

                        RenderLargeTrade(trade, x, y, largeBuyBrush, largeSellBrush, lengthPixels, widthPixels, bubbleRadiusMinPixels, bubbleRadiusMaxPixels);
                    }
                }
            }
            finally
            {
                largeBuyBrush.Dispose();
                largeSellBrush.Dispose();
            }
        }

        private void RenderLargeTrade(ConfirmedTrade trade, float x, float y, SharpDX.Direct2D1.Brush largeBuyBrush, SharpDX.Direct2D1.Brush largeSellBrush, float lengthPixels, float widthPixels, float bubbleRadiusMinPixels, float bubbleRadiusMaxPixels)
        {
            SharpDX.Direct2D1.Brush dxBrush = trade.IsBuy ? largeBuyBrush : largeSellBrush;

            // Gradient: the single biggest large trade in this bar renders at full opacity;
            // smaller (but still-qualifying) large trades fade toward LargeSizeMinOpacity, same
            // formula as EGFootprintLadder's large trade dots.
            dxBrush.Opacity = (float)(LargeSizeMinOpacity + (1.0 - LargeSizeMinOpacity) * trade.LargeSizeRatio);

            if (LargeTradePrintMode == EGOrderFlowOverlayLargeTradePrintMode.BubblesFilled ||
                LargeTradePrintMode == EGOrderFlowOverlayLargeTradePrintMode.BubblesUnfilled)
            {
                // Bubble size rides the same LargeSizeRatio gradient as the opacity above:
                // smallest qualifying large trade in the bar -> the fixed floor, biggest -> Max
                // Bubble Size.
                float bubbleRadiusPixels = bubbleRadiusMinPixels + (bubbleRadiusMaxPixels - bubbleRadiusMinPixels) * (float)trade.LargeSizeRatio;
                float bubbleBorderPixels = Math.Max(1f, Math.Min((float)BubbleBorderWidth, bubbleRadiusPixels));

                if (LargeTradePrintMode == EGOrderFlowOverlayLargeTradePrintMode.BubblesFilled)
                    RenderTarget.FillEllipse(new SharpDX.Direct2D1.Ellipse(new SharpDX.Vector2(x, y), bubbleRadiusPixels, bubbleRadiusPixels), dxBrush);
                else
                    RenderTarget.DrawEllipse(new SharpDX.Direct2D1.Ellipse(new SharpDX.Vector2(x, y), bubbleRadiusPixels, bubbleRadiusPixels), dxBrush, bubbleBorderPixels);
            }
            else
            {
                DrawTradeLine(x, y, dxBrush, lengthPixels, widthPixels);
            }

            dxBrush.Opacity = 1f;
        }

        private void RenderStandardTrade(ConfirmedTrade trade, float x, float y, System.Windows.Media.Color buyBaseColor, System.Windows.Media.Color sellBaseColor, float lengthPixels, float widthPixels, float maxExtendLengthPixels)
        {
            System.Windows.Media.Color baseColor = trade.IsBuy ? buyBaseColor : sellBaseColor;

            // Strength and Sequence both drive opacity of this trade's OWN Buy/Sell color —
            // neither ever blends toward the Large Buy/Sell color, so a standard trade can't
            // visually read as an actual large trade.
            double strengthOpacity = StrengthMinOpacity + (1.0 - StrengthMinOpacity) * trade.Strength;
            double sequenceOpacity = SequenceMinOpacity + (1.0 - SequenceMinOpacity) * trade.SequenceFactor;
            double opacity = strengthOpacity * sequenceOpacity;
            byte alphaByte = (byte)(255.0 * Math.Max(0.0, Math.Min(1.0, opacity)));

            if (ExtendByStrength)
            {
                float extendedLengthPixels = Math.Max(2f, maxExtendLengthPixels * (float)trade.Strength);
                DrawExtendedFadeLine(x, y, baseColor, alphaByte, extendedLengthPixels, widthPixels);
            }
            else
            {
                System.Windows.Media.SolidColorBrush wpfBrush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(alphaByte, baseColor.R, baseColor.G, baseColor.B));

                SharpDX.Direct2D1.Brush dxBrush = wpfBrush.ToDxBrush(RenderTarget);
                try
                {
                    DrawTradeLine(x, y, dxBrush, lengthPixels, widthPixels);
                }
                finally
                {
                    dxBrush.Dispose();
                }
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
        [Display(Name = "Line Length (Bars)", Description = "Horizontal length of the line, in units of one bar's on-screen width; extension direction is controlled by Alignment. Not used when Extend By Strength is enabled — see Max Extend Length (Bars) instead", GroupName = "Standard Trade Detector", Order = 4)]
        public int LineLengthBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Line Width", Description = "Pixel width/thickness of the line; capped to the current on-screen bar spacing so it shrinks as bars get smaller instead of staying visually fixed", GroupName = "Standard Trade Detector", Order = 5)]
        public double LineWidth { get; set; }

        [XmlIgnore]
        [Display(Name = "Alignment", Description = "Horizontal alignment of the line relative to the bar it's drawn on. Ignored when Extend By Strength is enabled, which always extends rightward", GroupName = "Standard Trade Detector", Order = 6)]
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
        [Display(Name = "Strength Min Opacity", Description = "Floor opacity for the smallest standard trade relative to the biggest standard trade seen so far this session; that session-high trade renders fully opaque. Large trade sizes are excluded from this comparison, and this never blends toward the Large colors", GroupName = "Standard Trade Detector", Order = 8)]
        public double StrengthMinOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Extend By Strength", Description = "When enabled, each standard trade's line always extends to the right (Alignment is ignored) with length scaled by its Strength — Max Extend Length (Bars) becomes the length at full strength (the biggest standard trade seen so far this session), weaker trades draw shorter. The line also fades from its normal Strength/Sequence opacity at the price origin down to fully transparent at the far tip, instead of one flat opacity across a fixed length", GroupName = "Standard Trade Detector", Order = 9)]
        public bool ExtendByStrength { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Max Extend Length (Bars)", Description = "Maximum horizontal length, in units of one bar's on-screen width, that a standard trade's line can reach when Extend By Strength is enabled — this is the length at Strength 1.0 (the biggest standard trade seen so far this session); weaker trades draw proportionally shorter. Only used when Extend By Strength is enabled", GroupName = "Standard Trade Detector", Order = 10)]
        public int MaxExtendLengthBars { get; set; }

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
        [Display(Name = "Large Trade Print Mode", Description = "How large trades are rendered: Lines (same style as normal trades), Bubbles Filled (a solid circle at the trade's price), or Bubbles Unfilled (an unfilled circle outline at the trade's price)", GroupName = "Large Trade Detection", Order = 5)]
        public EGOrderFlowOverlayLargeTradePrintMode LargeTradePrintMode { get; set; }

        [Browsable(false)]
        public string LargeTradePrintModeSerializable
        {
            get { return LargeTradePrintMode.ToString(); }
            set { LargeTradePrintMode = (EGOrderFlowOverlayLargeTradePrintMode)Enum.Parse(typeof(EGOrderFlowOverlayLargeTradePrintMode), value); }
        }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Max Bubble Size", Description = "Maximum pixel radius a bubble can reach — the largest qualifying large trade in a bar renders at this size (LargeSizeRatio = 1.0); smaller large trades in the same bar scale down toward a small fixed floor. Not clamped to bar spacing, so bubbles can render larger than the bar's own on-screen width", GroupName = "Large Trade Detection", Order = 6)]
        public double MaxBubbleSize { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Bubble Border Width (px)", Description = "Pixel width/thickness of the bubble's border; used as the stroke width when Large Trade Print Mode is Bubbles Unfilled", GroupName = "Large Trade Detection", Order = 7)]
        public double BubbleBorderWidth { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 1.0)]
        [Display(Name = "Large Size Min Opacity", Description = "Floor opacity for the smallest qualifying large trade in a bar; the largest large trade in that bar renders fully opaque — same gradient approach as EGFootprintLadder's large trade dots", GroupName = "Large Trade Detection", Order = 8)]
        public double LargeSizeMinOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Print Forward", Description = "When enabled, large trade bubbles/lines are drawn in a second pass after all standard trades, so they always render in front regardless of print order within the bar", GroupName = "Large Trade Detection", Order = 9)]
        public bool PrintForward { get; set; }

        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private EducatedGambling.EGOrderFlowOverlay[] cacheEGOrderFlowOverlay;
		public EducatedGambling.EGOrderFlowOverlay EGOrderFlowOverlay(int tradeThreshold, System.Windows.Media.Brush sellColor, System.Windows.Media.Brush buyColor, int lineLengthBars, double lineWidth, double sequenceMinOpacity, double strengthMinOpacity, bool extendByStrength, int maxExtendLengthBars, bool enableLargeTradeDetection, int largeTradeThreshold, System.Windows.Media.Brush largeSellColor, System.Windows.Media.Brush largeBuyColor, double maxBubbleSize, double bubbleBorderWidth, double largeSizeMinOpacity, bool printForward)
		{
			return EGOrderFlowOverlay(Input, tradeThreshold, sellColor, buyColor, lineLengthBars, lineWidth, sequenceMinOpacity, strengthMinOpacity, extendByStrength, maxExtendLengthBars, enableLargeTradeDetection, largeTradeThreshold, largeSellColor, largeBuyColor, maxBubbleSize, bubbleBorderWidth, largeSizeMinOpacity, printForward);
		}

		public EducatedGambling.EGOrderFlowOverlay EGOrderFlowOverlay(ISeries<double> input, int tradeThreshold, System.Windows.Media.Brush sellColor, System.Windows.Media.Brush buyColor, int lineLengthBars, double lineWidth, double sequenceMinOpacity, double strengthMinOpacity, bool extendByStrength, int maxExtendLengthBars, bool enableLargeTradeDetection, int largeTradeThreshold, System.Windows.Media.Brush largeSellColor, System.Windows.Media.Brush largeBuyColor, double maxBubbleSize, double bubbleBorderWidth, double largeSizeMinOpacity, bool printForward)
		{
			if (cacheEGOrderFlowOverlay != null)
				for (int idx = 0; idx < cacheEGOrderFlowOverlay.Length; idx++)
					if (cacheEGOrderFlowOverlay[idx] != null && cacheEGOrderFlowOverlay[idx].TradeThreshold == tradeThreshold && cacheEGOrderFlowOverlay[idx].SellColor == sellColor && cacheEGOrderFlowOverlay[idx].BuyColor == buyColor && cacheEGOrderFlowOverlay[idx].LineLengthBars == lineLengthBars && cacheEGOrderFlowOverlay[idx].LineWidth == lineWidth && cacheEGOrderFlowOverlay[idx].SequenceMinOpacity == sequenceMinOpacity && cacheEGOrderFlowOverlay[idx].StrengthMinOpacity == strengthMinOpacity && cacheEGOrderFlowOverlay[idx].ExtendByStrength == extendByStrength && cacheEGOrderFlowOverlay[idx].MaxExtendLengthBars == maxExtendLengthBars && cacheEGOrderFlowOverlay[idx].EnableLargeTradeDetection == enableLargeTradeDetection && cacheEGOrderFlowOverlay[idx].LargeTradeThreshold == largeTradeThreshold && cacheEGOrderFlowOverlay[idx].LargeSellColor == largeSellColor && cacheEGOrderFlowOverlay[idx].LargeBuyColor == largeBuyColor && cacheEGOrderFlowOverlay[idx].MaxBubbleSize == maxBubbleSize && cacheEGOrderFlowOverlay[idx].BubbleBorderWidth == bubbleBorderWidth && cacheEGOrderFlowOverlay[idx].LargeSizeMinOpacity == largeSizeMinOpacity && cacheEGOrderFlowOverlay[idx].PrintForward == printForward && cacheEGOrderFlowOverlay[idx].EqualsInput(input))
						return cacheEGOrderFlowOverlay[idx];
			return CacheIndicator<EducatedGambling.EGOrderFlowOverlay>(new EducatedGambling.EGOrderFlowOverlay(){ TradeThreshold = tradeThreshold, SellColor = sellColor, BuyColor = buyColor, LineLengthBars = lineLengthBars, LineWidth = lineWidth, SequenceMinOpacity = sequenceMinOpacity, StrengthMinOpacity = strengthMinOpacity, ExtendByStrength = extendByStrength, MaxExtendLengthBars = maxExtendLengthBars, EnableLargeTradeDetection = enableLargeTradeDetection, LargeTradeThreshold = largeTradeThreshold, LargeSellColor = largeSellColor, LargeBuyColor = largeBuyColor, MaxBubbleSize = maxBubbleSize, BubbleBorderWidth = bubbleBorderWidth, LargeSizeMinOpacity = largeSizeMinOpacity, PrintForward = printForward }, input, ref cacheEGOrderFlowOverlay);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.EducatedGambling.EGOrderFlowOverlay EGOrderFlowOverlay(int tradeThreshold, System.Windows.Media.Brush sellColor, System.Windows.Media.Brush buyColor, int lineLengthBars, double lineWidth, double sequenceMinOpacity, double strengthMinOpacity, bool extendByStrength, int maxExtendLengthBars, bool enableLargeTradeDetection, int largeTradeThreshold, System.Windows.Media.Brush largeSellColor, System.Windows.Media.Brush largeBuyColor, double maxBubbleSize, double bubbleBorderWidth, double largeSizeMinOpacity, bool printForward)
		{
			return indicator.EGOrderFlowOverlay(Input, tradeThreshold, sellColor, buyColor, lineLengthBars, lineWidth, sequenceMinOpacity, strengthMinOpacity, extendByStrength, maxExtendLengthBars, enableLargeTradeDetection, largeTradeThreshold, largeSellColor, largeBuyColor, maxBubbleSize, bubbleBorderWidth, largeSizeMinOpacity, printForward);
		}

		public Indicators.EducatedGambling.EGOrderFlowOverlay EGOrderFlowOverlay(ISeries<double> input , int tradeThreshold, System.Windows.Media.Brush sellColor, System.Windows.Media.Brush buyColor, int lineLengthBars, double lineWidth, double sequenceMinOpacity, double strengthMinOpacity, bool extendByStrength, int maxExtendLengthBars, bool enableLargeTradeDetection, int largeTradeThreshold, System.Windows.Media.Brush largeSellColor, System.Windows.Media.Brush largeBuyColor, double maxBubbleSize, double bubbleBorderWidth, double largeSizeMinOpacity, bool printForward)
		{
			return indicator.EGOrderFlowOverlay(input, tradeThreshold, sellColor, buyColor, lineLengthBars, lineWidth, sequenceMinOpacity, strengthMinOpacity, extendByStrength, maxExtendLengthBars, enableLargeTradeDetection, largeTradeThreshold, largeSellColor, largeBuyColor, maxBubbleSize, bubbleBorderWidth, largeSizeMinOpacity, printForward);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.EducatedGambling.EGOrderFlowOverlay EGOrderFlowOverlay(int tradeThreshold, System.Windows.Media.Brush sellColor, System.Windows.Media.Brush buyColor, int lineLengthBars, double lineWidth, double sequenceMinOpacity, double strengthMinOpacity, bool extendByStrength, int maxExtendLengthBars, bool enableLargeTradeDetection, int largeTradeThreshold, System.Windows.Media.Brush largeSellColor, System.Windows.Media.Brush largeBuyColor, double maxBubbleSize, double bubbleBorderWidth, double largeSizeMinOpacity, bool printForward)
		{
			return indicator.EGOrderFlowOverlay(Input, tradeThreshold, sellColor, buyColor, lineLengthBars, lineWidth, sequenceMinOpacity, strengthMinOpacity, extendByStrength, maxExtendLengthBars, enableLargeTradeDetection, largeTradeThreshold, largeSellColor, largeBuyColor, maxBubbleSize, bubbleBorderWidth, largeSizeMinOpacity, printForward);
		}

		public Indicators.EducatedGambling.EGOrderFlowOverlay EGOrderFlowOverlay(ISeries<double> input , int tradeThreshold, System.Windows.Media.Brush sellColor, System.Windows.Media.Brush buyColor, int lineLengthBars, double lineWidth, double sequenceMinOpacity, double strengthMinOpacity, bool extendByStrength, int maxExtendLengthBars, bool enableLargeTradeDetection, int largeTradeThreshold, System.Windows.Media.Brush largeSellColor, System.Windows.Media.Brush largeBuyColor, double maxBubbleSize, double bubbleBorderWidth, double largeSizeMinOpacity, bool printForward)
		{
			return indicator.EGOrderFlowOverlay(input, tradeThreshold, sellColor, buyColor, lineLengthBars, lineWidth, sequenceMinOpacity, strengthMinOpacity, extendByStrength, maxExtendLengthBars, enableLargeTradeDetection, largeTradeThreshold, largeSellColor, largeBuyColor, maxBubbleSize, bubbleBorderWidth, largeSizeMinOpacity, printForward);
		}
	}
}

#endregion
