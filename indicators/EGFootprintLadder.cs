#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
#endregion

namespace NinjaTrader.NinjaScript.Indicators.EducatedGambling
{
    [CategoryOrder("Layout", 1)]
    [CategoryOrder("Calculations", 2)]
    [CategoryOrder("Delta", 3)]
    [CategoryOrder("Ladder", 4)]
    [CategoryOrder("Large Trades", 5)]
    [CategoryOrder("Colors", 6)]
    [CategoryOrder("Data Summary", 7)]
    public class EGFootprintLadder : Indicator
    {
        // Built on hidden Last/Bid/Ask 1-tick data series (documented pattern elsewhere in this
        // project, e.g. RangeBarCompressionZones.cs), not AddVolumetric() - its native
        // Volumes[] price-level lookups proved unreliable in testing on this account/data feed -
        // GetMaximumVolume repeatedly returned a price hundreds of dollars away from the current
        // bar's real high/low, even when read at the exact moment OnBarUpdate(BarsInProgress==1)
        // received it. Bucketing trades into price levels ourselves, from data we already trust
        // (the same tick-series accumulation that produced correct running totals), sidesteps
        // that unreliable native API entirely.
        //
        // On first load, all three hidden series (Last, Bid, Ask) replay the chart's entire
        // loaded history before reaching real time - during that one-time backfill, prices can
        // briefly be hundreds of dollars away from the live bar. It resolves on its own once the
        // backfill catches up; see root CLAUDE.md 'Historical trade-print (tick-level) data' for
        // the full writeup, including why a large "Days to Load" makes both the backfill and CPU
        // load worse.
        //
        // Renders via OnRender/SharpDX (not Draw.Text) - this gives pixel-exact, fixed-offset
        // placement of the delta histogram and ladder box relative to the current bar, which
        // Draw.Text cannot do (its X position is always barsAgo-relative). Positioning follows
        // the recipe in root CLAUDE.md ('OnRender positioning recipe'), proven first in
        // FootprintLadderTestv2.cs: X is a single shared horizontal anchor
        // (ChartBars.ToIndex + a fixed pixel offset), Y is chartScale.GetYByValue(price) computed
        // directly per price level - never CurrentBars[0]/Close[0]. Mixing those two "current
        // bar" concepts for X vs Y was the root cause of an earlier OnRender attempt rendering in
        // the wrong place despite sane diagnostics; this version avoids that entirely.
        private double currentBid = double.NaN;
        private double currentAsk = double.NaN;
        private int accumBarIndex = -1;
        private readonly Dictionary<double, double> bidByPrice = new Dictionary<double, double>();
        private readonly Dictionary<double, double> askByPrice = new Dictionary<double, double>();
        private readonly HashSet<double> trackedPrices = new HashSet<double>();
        private double maxAbsDeltaThisBar;

        private class LargeTradeEvent
        {
            public double Size;
            public bool IsBuy;
        }
        private readonly Dictionary<double, List<LargeTradeEvent>> largeTradesByPrice = new Dictionary<double, List<LargeTradeEvent>>();
        private double maxLargeTradeSizeThisBar;

        // Rolling per-closed-bar totals, capped at TrendLookbackBars, used to compare the
        // current (still-forming) bar's live delta/volume/range against the recent average for
        // the Data Summary trend triangles.
        private readonly Queue<double> deltaHistory = new Queue<double>();
        private readonly Queue<double> volumeHistory = new Queue<double>();
        private readonly Queue<double> rangeHistory = new Queue<double>();

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Footprint-style order flow ladder showing per-price bid/ask volume and delta for the current bar, with a delta histogram, POC, and imbalance/large-trade highlighting.";
                Name = "EGFootprintLadder";
                Calculate = Calculate.OnBarClose;
                IsOverlay = true;
                DisplayInDataBox = false;
                DrawOnPricePanel = true;
                PaintPriceMarkers = false;
                IsSuspendedWhileInactive = false;

                OffsetPixels = 10;
                LadderGapPixels = 10;
                LargeTradeGapPixels = 10;

                TicksPerRow = 1;

                ShowDeltaBar = true;
                DeltaBarMaxWidth = 60;
                DeltaBarHeightPixels = 10;
                DeltaBarOpacity = 70;
                ShowPoc = true;
                PocDisplayMode = EGFootprintLadderPocDisplayMode.Line;
                PocLineHeightPixels = 2;
                ShowImbalance = true;
                ImbalanceRatio = 3.0;

                LadderWidth = 140;
                LadderTextAlign = EGFootprintLadderTextAlign.Center;
                TextFontFamily = "Consolas";
                FontSize = 10;
                RowOpacity = 70;
                ExtendLadderBar = false;
                MaxLadderRows = 300;

                ShowLargeTrades = true;
                MaxLargeTradesPerRow = 20;
                LargeTradeThreshold = 50;
                LargeTradeDotDiameterPixels = 8;
                LargeTradeDotSpacingPixels = 4;

                SellRowColor = Brushes.OrangeRed;
                BuyRowColor = Brushes.DodgerBlue;
                NeutralRowColor = Brushes.DimGray;
                RowTextColor = Brushes.White;
                PocLineColor = Brushes.Yellow;
                ImbalanceColor = Brushes.Purple;
                LargeAskColor = Brushes.Blue;
                LargeBidColor = Brushes.DeepPink;
                DeltaColorFill = EGFootprintLadderColorFillMode.Solid;
                LadderColorFill = EGFootprintLadderColorFillMode.Solid;
                GradientLevel = 5;

                ShowDataSummary = true;
                DataSummaryGapPixels = 10;
                ShowTotalDelta = true;
                ShowStrength = true;
                ShowTrendingDelta = true;
                ShowTrendingVolume = true;
                ShowTrendingRange = true;
                TrendLookbackBars = 20;
                TrendDivisorPercent = 20.0;
                MaxTriangles = 5;
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
                accumBarIndex = -1;
                bidByPrice.Clear();
                askByPrice.Clear();
                trackedPrices.Clear();
                maxAbsDeltaThisBar = 0;
                largeTradesByPrice.Clear();
                maxLargeTradeSizeThisBar = 0;
                deltaHistory.Clear();
                volumeHistory.Clear();
                rangeHistory.Clear();
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
                if (CurrentBars[0] < 0)
                    return;

                double rawPrice = Close[0];
                double size = Volume[0];

                // CurrentBars[0] only advances when the primary bar closes (Calculate.OnBarClose),
                // so while a new bar is forming it still names the previous, already-closed bar.
                // A trade belongs to the next bar, not the one CurrentBars[0] currently names,
                // once it happens after that bar's own close timestamp.
                int targetBar = (Time[0] <= Times[0][0]) ? CurrentBars[0] : CurrentBars[0] + 1;

                if (targetBar != accumBarIndex)
                {
                    // Push the bar that's ending into the rolling trend history before clearing
                    // it - accumBarIndex < 0 means this is the very first bar seen, nothing to
                    // push yet. Highs[0][0]/Lows[0][0] (explicit primary-series index, same
                    // pattern as Times[0][0] above) give that bar's range.
                    if (accumBarIndex >= 0)
                    {
                        double endedBidTotal = bidByPrice.Values.Sum();
                        double endedAskTotal = askByPrice.Values.Sum();

                        deltaHistory.Enqueue(endedAskTotal - endedBidTotal);
                        if (deltaHistory.Count > TrendLookbackBars)
                            deltaHistory.Dequeue();

                        volumeHistory.Enqueue(endedAskTotal + endedBidTotal);
                        if (volumeHistory.Count > TrendLookbackBars)
                            volumeHistory.Dequeue();

                        rangeHistory.Enqueue(Highs[0][0] - Lows[0][0]);
                        if (rangeHistory.Count > TrendLookbackBars)
                            rangeHistory.Dequeue();
                    }

                    accumBarIndex = targetBar;
                    bidByPrice.Clear();
                    askByPrice.Clear();
                    trackedPrices.Clear();
                    maxAbsDeltaThisBar = 0;
                    largeTradesByPrice.Clear();
                    maxLargeTradeSizeThisBar = 0;
                }

                if (!double.IsNaN(currentAsk) && !double.IsNaN(currentBid) && currentAsk > currentBid)
                {
                    // Groups TicksPerRow ticks into a single row (TicksPerRow = 1 is one row
                    // per tick, the original behavior). Same rounding technique as
                    // Indicators/DeltaLadder.cs's TicksPerRow.
                    double groupSize = TickSize * TicksPerRow;
                    double price = Math.Round(rawPrice / groupSize) * groupSize;

                    // Safety cap on distinct price levels tracked for this bar (e.g. a freak gap
                    // bar): once at the cap, new levels stop being added but existing ones keep
                    // updating.
                    if (!trackedPrices.Contains(price))
                    {
                        if (trackedPrices.Count >= MaxLadderRows)
                            return;
                        trackedPrices.Add(price);
                    }

                    if (rawPrice >= currentAsk)
                    {
                        double v;
                        askByPrice.TryGetValue(price, out v);
                        askByPrice[price] = v + size;

                        if (ShowLargeTrades && size >= LargeTradeThreshold)
                            RecordLargeTrade(price, size, true);
                    }
                    else if (rawPrice <= currentBid)
                    {
                        double v;
                        bidByPrice.TryGetValue(price, out v);
                        bidByPrice[price] = v + size;

                        if (ShowLargeTrades && size >= LargeTradeThreshold)
                            RecordLargeTrade(price, size, false);
                    }
                    // else: between bid and ask - ambiguous, skip (matches the quote-rule
                    // fallback documented in root CLAUDE.md).

                    double bidAtPrice;
                    bidByPrice.TryGetValue(price, out bidAtPrice);
                    double askAtPrice;
                    askByPrice.TryGetValue(price, out askAtPrice);
                    double absDelta = Math.Abs(askAtPrice - bidAtPrice);
                    if (absDelta > maxAbsDeltaThisBar)
                        maxAbsDeltaThisBar = absDelta;
                }

                return;
            }
        }

        private void RecordLargeTrade(double price, double size, bool isBuy)
        {
            List<LargeTradeEvent> list;
            if (!largeTradesByPrice.TryGetValue(price, out list))
            {
                list = new List<LargeTradeEvent>();
                largeTradesByPrice[price] = list;
            }

            // Safety cap on stacked dots per price level (same pattern as MaxLadderRows):
            // once at the cap, new large trades at this price stop being tracked, existing
            // ones stay.
            if (list.Count >= MaxLargeTradesPerRow)
                return;

            list.Add(new LargeTradeEvent { Size = size, IsBuy = isBuy });

            if (size > maxLargeTradeSizeThisBar)
                maxLargeTradeSizeThisBar = size;
        }

        // Compares current against the average of history (recent closed bars). Percent
        // difference is converted to a triangle count via TrendDivisorPercent (one triangle
        // per that many percent above/below average), capped at maxTriangles. isUp reflects
        // the sign of the difference; 0 triangles means "not enough history yet" (avg == 0 or
        // no history) or the difference hasn't reached one full triangle step.
        private static int CountTrendTriangles(double current, Queue<double> history, double divisorPercent, int maxTriangles, out bool isUp)
        {
            isUp = false;
            if (history.Count == 0)
                return 0;

            double avg = history.Average();
            if (avg == 0)
                return 0;

            double percentDiff = (current - avg) / Math.Abs(avg) * 100.0;
            isUp = percentDiff > 0;

            int triangles = (int)(Math.Abs(percentDiff) / divisorPercent);
            return Math.Min(triangles, maxTriangles);
        }

        private static string BuildTrendGlyphs(int triangleCount, bool isUp)
        {
            return triangleCount > 0 ? new string(isUp ? '▲' : '▼', triangleCount) : "-";
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            if (Bars == null || ChartControl == null || IsInHitTest)
                return;

            int lastBarIndex = ChartBars.ToIndex;
            if (lastBarIndex < 0)
                return;

            if (bidByPrice.Count == 0 && askByPrice.Count == 0)
                return;

            float barX = chartControl.GetXByBarIndex(ChartBars, lastBarIndex);
            float deltaZoneLeft = barX + OffsetPixels;
            float deltaZoneCenter = deltaZoneLeft + DeltaBarMaxWidth / 2f;
            float deltaZoneRight = deltaZoneLeft + DeltaBarMaxWidth;
            float ladderLeft = deltaZoneRight + LadderGapPixels;
            float ladderRight = ExtendLadderBar ? ChartPanel.X + ChartPanel.W : ladderLeft + LadderWidth;
            float ladderRectWidth = Math.Max(ladderRight - ladderLeft, 1f);
            float largeTradeZoneLeft = ladderRight + LargeTradeGapPixels;

            float ladderOpacityFraction = (float)(RowOpacity / 100.0);
            float deltaOpacityFraction = (float)(DeltaBarOpacity / 100.0);

            SharpDX.Direct2D1.Brush buyBrush = BuyRowColor.ToDxBrush(RenderTarget);
            SharpDX.Direct2D1.Brush sellBrush = SellRowColor.ToDxBrush(RenderTarget);
            SharpDX.Direct2D1.Brush neutralBrush = NeutralRowColor.ToDxBrush(RenderTarget);
            SharpDX.Direct2D1.Brush textBrush = RowTextColor.ToDxBrush(RenderTarget);
            SharpDX.Direct2D1.Brush pocBrush = ShowPoc ? PocLineColor.ToDxBrush(RenderTarget) : null;
            SharpDX.Direct2D1.Brush imbalanceBrush = ShowImbalance ? ImbalanceColor.ToDxBrush(RenderTarget) : null;
            SharpDX.Direct2D1.Brush largeAskBrush = ShowLargeTrades ? LargeAskColor.ToDxBrush(RenderTarget) : null;
            SharpDX.Direct2D1.Brush largeBidBrush = ShowLargeTrades ? LargeBidColor.ToDxBrush(RenderTarget) : null;
            // Opacity for buy/sell/neutral/imbalance brushes is set per-row/per-bar inside the
            // render loop below (Ladder/Delta Bar Opacity as a ceiling, scaled down further by
            // Gradient Level + relative strength when the corresponding *Color Fill is Gradient)
            // rather than fixed once here - same brushes are reused for both the ladder row and
            // the delta bar, which need different per-row opacity values. They're also reused
            // (with opacity reset to full) for the Data Summary trend triangles below.
            float gradientFade = GradientLevel / 10f;

            SharpDX.DirectWrite.TextAlignment dxTextAlign;
            switch (LadderTextAlign)
            {
                case EGFootprintLadderTextAlign.Left:
                    dxTextAlign = SharpDX.DirectWrite.TextAlignment.Leading;
                    break;
                case EGFootprintLadderTextAlign.Right:
                    dxTextAlign = SharpDX.DirectWrite.TextAlignment.Trailing;
                    break;
                default:
                    dxTextAlign = SharpDX.DirectWrite.TextAlignment.Center;
                    break;
            }

            SharpDX.DirectWrite.TextFormat textFormat = new SharpDX.DirectWrite.TextFormat(
                Core.Globals.DirectWriteFactory,
                TextFontFamily,
                SharpDX.DirectWrite.FontWeight.Normal,
                SharpDX.DirectWrite.FontStyle.Normal,
                (float)FontSize)
            {
                TextAlignment = dxTextAlign,
                ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center
            };

            HashSet<double> prices = new HashSet<double>(bidByPrice.Keys);
            foreach (double p in askByPrice.Keys)
                prices.Add(p);

            double groupSize = TickSize * TicksPerRow;

            // Also doubles as the "strongest row this bar" reference for Ladder Fill's Gradient
            // mode, computed unconditionally since that's independent of Show POC.
            double pocPrice = double.NaN;
            double maxRowVolume = 0;
            {
                double maxVolume = -1;
                foreach (double price in prices)
                {
                    double b;
                    bidByPrice.TryGetValue(price, out b);
                    double a;
                    askByPrice.TryGetValue(price, out a);
                    double totalVolume = b + a;
                    if (totalVolume > maxVolume)
                    {
                        maxVolume = totalVolume;
                        pocPrice = price;
                    }
                }
                maxRowVolume = Math.Max(maxVolume, 0);
            }

            // Tracks the bottom-most rendered row's Y so the Data Summary box can anchor itself
            // just below the ladder regardless of how many/which price levels rendered this bar.
            float maxRowBottomY = float.MinValue;

            foreach (double price in prices)
            {
                float y = chartScale.GetYByValue(price);
                float rowTop = chartScale.GetYByValue(price + groupSize / 2.0);
                float rowBottom = chartScale.GetYByValue(price - groupSize / 2.0);
                float rowHeight = Math.Max(Math.Abs(rowBottom - rowTop), 6f);

                maxRowBottomY = Math.Max(maxRowBottomY, y + rowHeight / 2f);

                double bid;
                bidByPrice.TryGetValue(price, out bid);
                double ask;
                askByPrice.TryGetValue(price, out ask);
                double delta = ask - bid;

                // Imbalance overrides the normal buy/sell/neutral row color only - the delta
                // bar for this same price level still colors by delta sign, unaffected.
                bool isImbalance = ShowImbalance && (ask >= bid * ImbalanceRatio || bid >= ask * ImbalanceRatio);
                SharpDX.Direct2D1.Brush rowBrush = isImbalance
                    ? imbalanceBrush
                    : (ask > bid ? buyBrush : bid > ask ? sellBrush : neutralBrush);

                // Gradient mode: solid color, but opacity scales with this row's total volume
                // relative to the strongest row this bar (maxRowVolume) - same
                // (1 - fade) + fade * intensity blend as DollarVolumeProfile.cs's BarFill.
                if (LadderColorFill == EGFootprintLadderColorFillMode.Gradient)
                {
                    double rowIntensity = maxRowVolume > 0 ? (bid + ask) / maxRowVolume : 1.0;
                    rowBrush.Opacity = ladderOpacityFraction * (float)((1 - gradientFade) + gradientFade * rowIntensity);
                }
                else
                {
                    rowBrush.Opacity = ladderOpacityFraction;
                }

                SharpDX.RectangleF ladderRect = new SharpDX.RectangleF(ladderLeft, y - rowHeight / 2f, ladderRectWidth, rowHeight);
                RenderTarget.FillRectangle(ladderRect, rowBrush);

                string deltaText = delta > 0 ? "+" + delta.ToString("0") : delta.ToString("0");
                string text = string.Format(CultureInfo.InvariantCulture, "{0,5}  {1,5}  {2,6}",
                    bid.ToString("0", CultureInfo.InvariantCulture),
                    ask.ToString("0", CultureInfo.InvariantCulture),
                    deltaText);
                RenderTarget.DrawText(text, textFormat, ladderRect, textBrush, SharpDX.Direct2D1.DrawTextOptions.None);

                if (ShowDeltaBar && maxAbsDeltaThisBar > 0 && delta != 0)
                {
                    float halfWidth = DeltaBarMaxWidth / 2f;
                    float barW = (float)(halfWidth * (Math.Abs(delta) / maxAbsDeltaThisBar));
                    bool isPocBar = ShowPoc && PocDisplayMode == EGFootprintLadderPocDisplayMode.Bar && price == pocPrice;
                    SharpDX.Direct2D1.Brush deltaBrush = isPocBar ? pocBrush : (delta > 0 ? buyBrush : sellBrush);
                    SharpDX.RectangleF deltaRect = delta > 0
                        ? new SharpDX.RectangleF(deltaZoneCenter, y - DeltaBarHeightPixels / 2f, barW, DeltaBarHeightPixels)
                        : new SharpDX.RectangleF(deltaZoneCenter - barW, y - DeltaBarHeightPixels / 2f, barW, DeltaBarHeightPixels);

                    // POC "Bar" mode overrides the delta bar's color entirely (a distinct
                    // highlight), so it stays solid regardless of Delta Fill. Otherwise,
                    // Gradient mode scales opacity by this bar's own delta magnitude relative
                    // to the strongest delta seen this bar (maxAbsDeltaThisBar) - the same
                    // ratio already used to compute barW, just reused for opacity too.
                    if (!isPocBar)
                    {
                        if (DeltaColorFill == EGFootprintLadderColorFillMode.Gradient)
                        {
                            double deltaIntensity = Math.Abs(delta) / maxAbsDeltaThisBar;
                            deltaBrush.Opacity = deltaOpacityFraction * (float)((1 - gradientFade) + gradientFade * deltaIntensity);
                        }
                        else
                        {
                            deltaBrush.Opacity = deltaOpacityFraction;
                        }
                    }

                    RenderTarget.FillRectangle(deltaRect, deltaBrush);
                }

                if (ShowLargeTrades)
                {
                    List<LargeTradeEvent> events;
                    if (largeTradesByPrice.TryGetValue(price, out events))
                    {
                        float dotRadius = LargeTradeDotDiameterPixels / 2f;
                        float dotStep = LargeTradeDotDiameterPixels + LargeTradeDotSpacingPixels;

                        for (int i = 0; i < events.Count; i++)
                        {
                            LargeTradeEvent evt = events[i];
                            SharpDX.Direct2D1.Brush dotBrush = evt.IsBuy ? largeAskBrush : largeBidBrush;

                            // Gradient: smallest qualifying trade still visible at a floor
                            // opacity, the single biggest large trade seen this bar (across
                            // all prices/sides) renders at full opacity.
                            float sizeRatio = maxLargeTradeSizeThisBar > 0
                                ? (float)(evt.Size / maxLargeTradeSizeThisBar)
                                : 1f;
                            const float minDotOpacity = 0.35f;
                            dotBrush.Opacity = minDotOpacity + (1f - minDotOpacity) * sizeRatio;

                            float dotCenterX = largeTradeZoneLeft + dotRadius + i * dotStep;
                            SharpDX.Direct2D1.Ellipse dot = new SharpDX.Direct2D1.Ellipse(
                                new SharpDX.Vector2(dotCenterX, y), dotRadius, dotRadius);
                            RenderTarget.FillEllipse(dot, dotBrush);
                        }
                    }
                }
            }

            if (ShowPoc && !double.IsNaN(pocPrice) && PocDisplayMode == EGFootprintLadderPocDisplayMode.Line)
            {
                float pocY = chartScale.GetYByValue(pocPrice);
                double pocBid;
                bidByPrice.TryGetValue(pocPrice, out pocBid);
                double pocAsk;
                askByPrice.TryGetValue(pocPrice, out pocAsk);

                float pocLineLength = DeltaBarMaxWidth / 2f;
                // Asks in control (buyers lifting the offer harder) draws right from the
                // zero-point; bids in control (sellers hitting the bid harder) draws left -
                // matches the same ask>bid=right/blue, bid>ask=left/red convention already
                // used by the row coloring and the delta bars (delta > 0 draws right).
                float pocX = pocAsk > pocBid ? deltaZoneCenter : deltaZoneCenter - pocLineLength;

                SharpDX.RectangleF pocRect = new SharpDX.RectangleF(pocX, pocY - PocLineHeightPixels / 2f, pocLineLength, PocLineHeightPixels);
                RenderTarget.FillRectangle(pocRect, pocBrush);
            }

            bool anyDataSummaryLine = ShowTotalDelta || ShowStrength || ShowTrendingDelta || ShowTrendingVolume || ShowTrendingRange;
            if (ShowDataSummary && anyDataSummaryLine && prices.Count > 0)
            {
                // Reset opacity to full - the main loop above left these at whatever
                // per-row/per-bar opacity was last computed for the final price level.
                buyBrush.Opacity = 1f;
                sellBrush.Opacity = 1f;

                // Left-aligned regardless of Ladder Text Align, which only governs the
                // bid/ask/delta ladder rows above.
                SharpDX.DirectWrite.TextFormat summaryTextFormat = new SharpDX.DirectWrite.TextFormat(
                    Core.Globals.DirectWriteFactory,
                    TextFontFamily,
                    SharpDX.DirectWrite.FontWeight.Normal,
                    SharpDX.DirectWrite.FontStyle.Normal,
                    (float)FontSize)
                {
                    TextAlignment = SharpDX.DirectWrite.TextAlignment.Leading,
                    ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center
                };

                double curBidTotal = bidByPrice.Values.Sum();
                double curAskTotal = askByPrice.Values.Sum();
                double curDelta = curAskTotal - curBidTotal;
                double curVolume = curAskTotal + curBidTotal;
                double curRange = Highs[0][0] - Lows[0][0];

                float lineHeight = (float)FontSize + 6f;
                float rowY = maxRowBottomY + DataSummaryGapPixels;

                if (ShowTotalDelta)
                {
                    string totalDeltaText = "Total Δ " + (curDelta > 0 ? "+" + curDelta.ToString("0") : curDelta.ToString("0"));
                    SharpDX.RectangleF rect = new SharpDX.RectangleF(ladderLeft, rowY, ladderRectWidth, lineHeight);
                    RenderTarget.DrawText(totalDeltaText, summaryTextFormat, rect, textBrush, SharpDX.Direct2D1.DrawTextOptions.None);
                    rowY += lineHeight;
                }

                if (ShowStrength)
                {
                    double totalVol = curBidTotal + curAskTotal;
                    double buyPct = totalVol > 0 ? curAskTotal / totalVol * 100.0 : 50.0;
                    string strengthText;
                    SharpDX.Direct2D1.Brush strengthBrush;
                    if (curAskTotal > curBidTotal)
                    {
                        strengthText = "Buy Strength " + buyPct.ToString("0") + "%";
                        strengthBrush = buyBrush;
                    }
                    else if (curBidTotal > curAskTotal)
                    {
                        strengthText = "Sell Strength " + (100.0 - buyPct).ToString("0") + "%";
                        strengthBrush = sellBrush;
                    }
                    else
                    {
                        strengthText = "Strength 50%";
                        strengthBrush = neutralBrush;
                    }
                    SharpDX.RectangleF rect = new SharpDX.RectangleF(ladderLeft, rowY, ladderRectWidth, lineHeight);
                    RenderTarget.DrawText(strengthText, summaryTextFormat, rect, strengthBrush, SharpDX.Direct2D1.DrawTextOptions.None);
                    rowY += lineHeight;
                }

                // Trending Delta has a natural buy/sell direction, so it borrows the same
                // blue/red convention as the ladder rows and delta bars. Trending Volume and
                // Trending Range have no such direction (more/less volume or a wider/narrower
                // bar isn't "bullish"/"bearish"), so they stay in the plain text color
                // regardless of which way they're trending.
                if (ShowTrendingDelta)
                {
                    bool deltaTrendUp;
                    int deltaTriangles = CountTrendTriangles(curDelta, deltaHistory, TrendDivisorPercent, MaxTriangles, out deltaTrendUp);
                    string deltaTrendText = "Trending Δ " + BuildTrendGlyphs(deltaTriangles, deltaTrendUp);
                    SharpDX.Direct2D1.Brush deltaTrendBrush = deltaTriangles > 0 ? (deltaTrendUp ? buyBrush : sellBrush) : textBrush;
                    SharpDX.RectangleF rect = new SharpDX.RectangleF(ladderLeft, rowY, ladderRectWidth, lineHeight);
                    RenderTarget.DrawText(deltaTrendText, summaryTextFormat, rect, deltaTrendBrush, SharpDX.Direct2D1.DrawTextOptions.None);
                    rowY += lineHeight;
                }

                if (ShowTrendingVolume)
                {
                    bool volumeTrendUp;
                    int volumeTriangles = CountTrendTriangles(curVolume, volumeHistory, TrendDivisorPercent, MaxTriangles, out volumeTrendUp);
                    string volumeTrendText = "Trending Vol " + BuildTrendGlyphs(volumeTriangles, volumeTrendUp);
                    SharpDX.RectangleF rect = new SharpDX.RectangleF(ladderLeft, rowY, ladderRectWidth, lineHeight);
                    RenderTarget.DrawText(volumeTrendText, summaryTextFormat, rect, textBrush, SharpDX.Direct2D1.DrawTextOptions.None);
                    rowY += lineHeight;
                }

                if (ShowTrendingRange)
                {
                    bool rangeTrendUp;
                    int rangeTriangles = CountTrendTriangles(curRange, rangeHistory, TrendDivisorPercent, MaxTriangles, out rangeTrendUp);
                    string rangeTrendText = "Trending Range " + BuildTrendGlyphs(rangeTriangles, rangeTrendUp);
                    SharpDX.RectangleF rect = new SharpDX.RectangleF(ladderLeft, rowY, ladderRectWidth, lineHeight);
                    RenderTarget.DrawText(rangeTrendText, summaryTextFormat, rect, textBrush, SharpDX.Direct2D1.DrawTextOptions.None);
                    rowY += lineHeight;
                }

                if (ShowTrendingDelta || ShowTrendingVolume || ShowTrendingRange)
                {
                    string lookbackCaptionText = string.Format(CultureInfo.InvariantCulture, "(calculated over {0} bars)", TrendLookbackBars);
                    SharpDX.RectangleF rect = new SharpDX.RectangleF(ladderLeft, rowY, ladderRectWidth, lineHeight);
                    RenderTarget.DrawText(lookbackCaptionText, summaryTextFormat, rect, textBrush, SharpDX.Direct2D1.DrawTextOptions.None);
                    rowY += lineHeight;
                }

                summaryTextFormat.Dispose();
            }

            textFormat.Dispose();
            buyBrush.Dispose();
            sellBrush.Dispose();
            neutralBrush.Dispose();
            textBrush.Dispose();
            if (pocBrush != null)
                pocBrush.Dispose();
            if (imbalanceBrush != null)
                imbalanceBrush.Dispose();
            if (largeAskBrush != null)
                largeAskBrush.Dispose();
            if (largeBidBrush != null)
                largeBidBrush.Dispose();
        }

        #region Properties

        // ----- Layout -----

        [NinjaScriptProperty]
        [Range(0, 200)]
        [Display(Name = "Offset Pixels", Description = "Horizontal gap from the last bar to the delta bar zone", GroupName = "Layout", Order = 1)]
        public int OffsetPixels { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Delta-Ladder Gap (px)", Description = "Horizontal gap from the delta bar zone to the ladder box", GroupName = "Layout", Order = 2)]
        public int LadderGapPixels { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Ladder-Large Trade Gap (px)", Description = "Horizontal gap from the ladder box to where large-trade dots start rendering", GroupName = "Layout", Order = 3)]
        public int LargeTradeGapPixels { get; set; }

        // ----- Calculations -----

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Ticks Per Row", Description = "Number of ticks grouped into each ladder row; 1 = one row per tick (default), higher values merge nearby prices into coarser rows", GroupName = "Calculations", Order = 1)]
        public int TicksPerRow { get; set; }

        [NinjaScriptProperty]
        [Range(5, 1000)]
        [Display(Name = "Max Ladder Rows", Description = "Safety cap on distinct price levels tracked for the current bar (e.g. a freak gap bar); new levels beyond this stop being tracked, existing ones keep updating", GroupName = "Calculations", Order = 2)]
        public int MaxLadderRows { get; set; }

        // ----- Delta -----

        [NinjaScriptProperty]
        [Display(Name = "Show Delta Bar", Description = "Draw a diverging strength histogram bar between the candle and the ladder, sized relative to the largest delta seen so far this bar", GroupName = "Delta", Order = 1)]
        public bool ShowDeltaBar { get; set; }

        [NinjaScriptProperty]
        [Range(20, 200)]
        [Display(Name = "Delta Bar Max Width (px)", Description = "Max width in pixels of the diverging delta histogram", GroupName = "Delta", Order = 2)]
        public int DeltaBarMaxWidth { get; set; }

        [NinjaScriptProperty]
        [Range(2, 50)]
        [Display(Name = "Delta Bar Height (px)", Description = "Height in pixels of each delta histogram bar, independent of the ladder row height", GroupName = "Delta", Order = 3)]
        public int DeltaBarHeightPixels { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Delta Bar Opacity", Description = "Opacity (%) of the delta histogram bars", GroupName = "Delta", Order = 4)]
        public int DeltaBarOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show POC", Description = "Highlight the price level with the highest total (bid + ask) volume so far this bar, using either POC Display Mode", GroupName = "Delta", Order = 5)]
        public bool ShowPoc { get; set; }

        [XmlIgnore]
        [Display(Name = "POC Display Mode", Description = "Line draws a short marker on the delta bar at the POC price; Bar colors that price level's own delta bar in POC color instead of its usual buy/sell color", GroupName = "Delta", Order = 6)]
        public EGFootprintLadderPocDisplayMode PocDisplayMode { get; set; }

        [Browsable(false)]
        public string PocDisplayModeSerializable
        {
            get { return PocDisplayMode.ToString(); }
            set { PocDisplayMode = (EGFootprintLadderPocDisplayMode)Enum.Parse(typeof(EGFootprintLadderPocDisplayMode), value); }
        }

        [NinjaScriptProperty]
        [Range(1, 30)]
        [Display(Name = "POC Line Height (px)", Description = "Height in pixels of the point-of-control line drawn on the delta bar when POC Display Mode is Line", GroupName = "Delta", Order = 7)]
        public int PocLineHeightPixels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Imbalance", Description = "Enable the ladder row background switching to the Imbalance color when a price level's bid/ask ratio reaches Imbalance Ratio", GroupName = "Delta", Order = 8)]
        public bool ShowImbalance { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 20.0)]
        [Display(Name = "Imbalance Ratio", Description = "Row background switches to the Imbalance color when ask >= bid x this ratio or bid >= ask x this ratio at that price level", GroupName = "Delta", Order = 9)]
        public double ImbalanceRatio { get; set; }

        // ----- Ladder -----

        [NinjaScriptProperty]
        [Range(60, 300)]
        [Display(Name = "Ladder Max Width (px)", Description = "Width in pixels of the bid/ask/delta ladder box", GroupName = "Ladder", Order = 1)]
        public int LadderWidth { get; set; }

        [XmlIgnore]
        [Display(Name = "Ladder Text Align", Description = "Horizontal alignment of the bid/ask/delta text within each ladder row box", GroupName = "Ladder", Order = 2)]
        public EGFootprintLadderTextAlign LadderTextAlign { get; set; }

        [Browsable(false)]
        public string LadderTextAlignSerializable
        {
            get { return LadderTextAlign.ToString(); }
            set { LadderTextAlign = (EGFootprintLadderTextAlign)Enum.Parse(typeof(EGFootprintLadderTextAlign), value); }
        }

        [NinjaScriptProperty]
        [TypeConverter(typeof(EGFootprintLadderFontFamilyConverter))]
        [Display(Name = "Ladder Text Font Family", GroupName = "Ladder", Order = 3)]
        public string TextFontFamily { get; set; }

        [NinjaScriptProperty]
        [TypeConverter(typeof(EGFootprintLadderFontSizeConverter))]
        [Display(Name = "Ladder Text Font Size", GroupName = "Ladder", Order = 4)]
        public double FontSize { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Ladder Row Opacity", Description = "Opacity (%) of the ladder rows", GroupName = "Ladder", Order = 5)]
        public int RowOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Extend Ladder Bar", Description = "Extend the ladder box all the way to the right edge of the chart window instead of stopping at Ladder Max Width", GroupName = "Ladder", Order = 6)]
        public bool ExtendLadderBar { get; set; }

        // ----- Large Trades -----

        [NinjaScriptProperty]
        [Display(Name = "Show Large Trades", Description = "Detect and track individual trade prints at or above Large Trade Threshold, rendered as dots to the right of the ladder", GroupName = "Large Trades", Order = 1)]
        public bool ShowLargeTrades { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Max Large Trades Per Row", Description = "Safety cap on distinct large trades tracked per price level for the current bar; new ones beyond this stop being tracked, existing ones stay", GroupName = "Large Trades", Order = 2)]
        public int MaxLargeTradesPerRow { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Large Trade Threshold", Description = "Minimum size of a single trade print to be tracked as a large trade", GroupName = "Large Trades", Order = 3)]
        public int LargeTradeThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(2, 30)]
        [Display(Name = "Large Trade Dot Diameter (px)", Description = "Diameter in pixels of each large-trade dot", GroupName = "Large Trades", Order = 4)]
        public int LargeTradeDotDiameterPixels { get; set; }

        [NinjaScriptProperty]
        [Range(0, 30)]
        [Display(Name = "Large Trade Dot Spacing (px)", Description = "Horizontal gap between sequential large-trade dots stacked at the same price level", GroupName = "Large Trades", Order = 5)]
        public int LargeTradeDotSpacingPixels { get; set; }

        // ----- Colors -----

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Sell Pressure", Description = "Row background when bid (sell) volume leads at that price level", GroupName = "Colors", Order = 1)]
        public Brush SellRowColor { get; set; }

        [Browsable(false)]
        public string SellRowColorSerializable
        {
            get { return Serialize.BrushToString(SellRowColor); }
            set { SellRowColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Buy Pressure", Description = "Row background when ask (buy) volume leads at that price level", GroupName = "Colors", Order = 2)]
        public Brush BuyRowColor { get; set; }

        [Browsable(false)]
        public string BuyRowColorSerializable
        {
            get { return Serialize.BrushToString(BuyRowColor); }
            set { BuyRowColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Neutral", Description = "Row background when bid and ask volume are equal at that price level", GroupName = "Colors", Order = 3)]
        public Brush NeutralRowColor { get; set; }

        [Browsable(false)]
        public string NeutralRowColorSerializable
        {
            get { return Serialize.BrushToString(NeutralRowColor); }
            set { NeutralRowColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Text", GroupName = "Colors", Order = 4)]
        public Brush RowTextColor { get; set; }

        [Browsable(false)]
        public string RowTextColorSerializable
        {
            get { return Serialize.BrushToString(RowTextColor); }
            set { RowTextColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "POC", Description = "Color used to highlight the POC level, for whichever POC Display Mode is selected", GroupName = "Colors", Order = 5)]
        public Brush PocLineColor { get; set; }

        [Browsable(false)]
        public string PocLineColorSerializable
        {
            get { return Serialize.BrushToString(PocLineColor); }
            set { PocLineColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Imbalance", Description = "Row background used when a price level's bid/ask volume ratio reaches Imbalance Ratio", GroupName = "Colors", Order = 6)]
        public Brush ImbalanceColor { get; set; }

        [Browsable(false)]
        public string ImbalanceColorSerializable
        {
            get { return Serialize.BrushToString(ImbalanceColor); }
            set { ImbalanceColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Large Ask", Description = "Color for large buy-initiated trades (trade price at/above the simultaneous ask)", GroupName = "Colors", Order = 7)]
        public Brush LargeAskColor { get; set; }

        [Browsable(false)]
        public string LargeAskColorSerializable
        {
            get { return Serialize.BrushToString(LargeAskColor); }
            set { LargeAskColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Large Bid", Description = "Color for large sell-initiated trades (trade price at/below the simultaneous bid)", GroupName = "Colors", Order = 8)]
        public Brush LargeBidColor { get; set; }

        [Browsable(false)]
        public string LargeBidColorSerializable
        {
            get { return Serialize.BrushToString(LargeBidColor); }
            set { LargeBidColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Delta Fill", Description = "Solid keeps each delta bar's opacity fixed at Delta Bar Opacity; Gradient scales each bar's opacity down from Delta Bar Opacity based on its delta magnitude relative to the strongest delta this bar", GroupName = "Colors", Order = 9)]
        public EGFootprintLadderColorFillMode DeltaColorFill { get; set; }

        [Browsable(false)]
        public string DeltaColorFillSerializable
        {
            get { return DeltaColorFill.ToString(); }
            set { DeltaColorFill = (EGFootprintLadderColorFillMode)Enum.Parse(typeof(EGFootprintLadderColorFillMode), value); }
        }

        [XmlIgnore]
        [Display(Name = "Ladder Fill", Description = "Solid keeps each ladder row's opacity fixed at Ladder Row Opacity; Gradient scales each row's opacity down from Ladder Row Opacity based on its volume relative to the strongest row this bar", GroupName = "Colors", Order = 10)]
        public EGFootprintLadderColorFillMode LadderColorFill { get; set; }

        [Browsable(false)]
        public string LadderColorFillSerializable
        {
            get { return LadderColorFill.ToString(); }
            set { LadderColorFill = (EGFootprintLadderColorFillMode)Enum.Parse(typeof(EGFootprintLadderColorFillMode), value); }
        }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Gradient Level", Description = "How strongly Gradient mode's opacity scales with relative strength (1 = barely varies, 10 = full range from faint to fully solid); shared by Ladder Fill and Delta Fill", GroupName = "Colors", Order = 11)]
        public int GradientLevel { get; set; }

        // ----- Data Summary -----

        [NinjaScriptProperty]
        [Display(Name = "Show Data Summary", Description = "Show a Data Summary box below the ladder with the enabled data points below", GroupName = "Data Summary", Order = 1)]
        public bool ShowDataSummary { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Data Summary Gap (px)", Description = "Vertical gap between the bottom of the ladder and the Data Summary box", GroupName = "Data Summary", Order = 2)]
        public int DataSummaryGapPixels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Display Total Delta", Description = "Show the running total delta (ask volume - bid volume) for the current bar", GroupName = "Data Summary", Order = 3)]
        public bool ShowTotalDelta { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Display Bid/Ask Strength", Description = "Show which side (buy/sell) leads this bar's volume and by what percentage", GroupName = "Data Summary", Order = 4)]
        public bool ShowStrength { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Display Trending Delta", Description = "Show triangles comparing this bar's live delta to the average delta of the last Trend Lookback Bars", GroupName = "Data Summary", Order = 5)]
        public bool ShowTrendingDelta { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Display Trending Volume", Description = "Show triangles comparing this bar's live volume to the average volume of the last Trend Lookback Bars", GroupName = "Data Summary", Order = 6)]
        public bool ShowTrendingVolume { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Display Trending Range", Description = "Show triangles comparing this bar's high-low range so far to the average range of the last Trend Lookback Bars", GroupName = "Data Summary", Order = 7)]
        public bool ShowTrendingRange { get; set; }

        [NinjaScriptProperty]
        [Range(2, 500)]
        [Display(Name = "Trend Lookback Bars", Description = "Number of recent closed bars averaged for the Trending Delta/Volume/Range comparisons", GroupName = "Data Summary", Order = 8)]
        public int TrendLookbackBars { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 100.0)]
        [Display(Name = "Trend Divisor (%)", Description = "Percent difference from the lookback average required to add one trend triangle; e.g. 20 means each 20% above/below average adds a triangle", GroupName = "Data Summary", Order = 9)]
        public double TrendDivisorPercent { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Max Triangles", Description = "Maximum number of trend triangles rendered for any Trending line, regardless of how large the percent difference is", GroupName = "Data Summary", Order = 10)]
        public int MaxTriangles { get; set; }

        #endregion
    }

    public enum EGFootprintLadderTextAlign { Left, Center, Right }

    public enum EGFootprintLadderPocDisplayMode { Line, Bar }

    public enum EGFootprintLadderColorFillMode { Solid, Gradient }

    public class EGFootprintLadderFontFamilyConverter : TypeConverter
    {
        private static readonly StandardValuesCollection Values = new StandardValuesCollection(
            Fonts.SystemFontFamilies
                .Select(f => f.Source)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList());

        public override bool GetStandardValuesSupported(ITypeDescriptorContext c) => true;
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext c) => false;
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext c) => Values;
    }

    public class EGFootprintLadderFontSizeConverter : TypeConverter
    {
        private static readonly StandardValuesCollection Values = new StandardValuesCollection(
            new double[] { 6, 7, 8, 9, 10, 11, 12, 13, 14, 16, 18, 20 });

        public override bool CanConvertFrom(ITypeDescriptorContext c, Type t)
            => t == typeof(string) || base.CanConvertFrom(c, t);

        public override object ConvertFrom(ITypeDescriptorContext c, CultureInfo cu, object v)
        {
            double d;
            if (v is string s && double.TryParse(s, NumberStyles.Any, cu ?? CultureInfo.InvariantCulture, out d))
                return d;
            return base.ConvertFrom(c, cu, v);
        }

        public override bool CanConvertTo(ITypeDescriptorContext c, Type t)
            => t == typeof(string) || base.CanConvertTo(c, t);

        public override object ConvertTo(ITypeDescriptorContext c, CultureInfo cu, object v, Type t)
        {
            if (t == typeof(string) && v is double d)
                return d.ToString(cu ?? CultureInfo.InvariantCulture);
            return base.ConvertTo(c, cu, v, t);
        }

        public override bool GetStandardValuesSupported(ITypeDescriptorContext c) => true;
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext c) => false;
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext c) => Values;
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private EducatedGambling.EGFootprintLadder[] cacheEGFootprintLadder;
		public EducatedGambling.EGFootprintLadder EGFootprintLadder(int offsetPixels, int ladderGapPixels, int largeTradeGapPixels, int ticksPerRow, int maxLadderRows, bool showDeltaBar, int deltaBarMaxWidth, int deltaBarHeightPixels, int deltaBarOpacity, bool showPoc, int pocLineHeightPixels, bool showImbalance, double imbalanceRatio, int ladderWidth, string textFontFamily, double fontSize, int rowOpacity, bool extendLadderBar, bool showLargeTrades, int maxLargeTradesPerRow, int largeTradeThreshold, int largeTradeDotDiameterPixels, int largeTradeDotSpacingPixels, Brush sellRowColor, Brush buyRowColor, Brush neutralRowColor, Brush rowTextColor, Brush pocLineColor, Brush imbalanceColor, Brush largeAskColor, Brush largeBidColor, int gradientLevel, bool showDataSummary, int dataSummaryGapPixels, bool showTotalDelta, bool showStrength, bool showTrendingDelta, bool showTrendingVolume, bool showTrendingRange, int trendLookbackBars, double trendDivisorPercent, int maxTriangles)
		{
			return EGFootprintLadder(Input, offsetPixels, ladderGapPixels, largeTradeGapPixels, ticksPerRow, maxLadderRows, showDeltaBar, deltaBarMaxWidth, deltaBarHeightPixels, deltaBarOpacity, showPoc, pocLineHeightPixels, showImbalance, imbalanceRatio, ladderWidth, textFontFamily, fontSize, rowOpacity, extendLadderBar, showLargeTrades, maxLargeTradesPerRow, largeTradeThreshold, largeTradeDotDiameterPixels, largeTradeDotSpacingPixels, sellRowColor, buyRowColor, neutralRowColor, rowTextColor, pocLineColor, imbalanceColor, largeAskColor, largeBidColor, gradientLevel, showDataSummary, dataSummaryGapPixels, showTotalDelta, showStrength, showTrendingDelta, showTrendingVolume, showTrendingRange, trendLookbackBars, trendDivisorPercent, maxTriangles);
		}

		public EducatedGambling.EGFootprintLadder EGFootprintLadder(ISeries<double> input, int offsetPixels, int ladderGapPixels, int largeTradeGapPixels, int ticksPerRow, int maxLadderRows, bool showDeltaBar, int deltaBarMaxWidth, int deltaBarHeightPixels, int deltaBarOpacity, bool showPoc, int pocLineHeightPixels, bool showImbalance, double imbalanceRatio, int ladderWidth, string textFontFamily, double fontSize, int rowOpacity, bool extendLadderBar, bool showLargeTrades, int maxLargeTradesPerRow, int largeTradeThreshold, int largeTradeDotDiameterPixels, int largeTradeDotSpacingPixels, Brush sellRowColor, Brush buyRowColor, Brush neutralRowColor, Brush rowTextColor, Brush pocLineColor, Brush imbalanceColor, Brush largeAskColor, Brush largeBidColor, int gradientLevel, bool showDataSummary, int dataSummaryGapPixels, bool showTotalDelta, bool showStrength, bool showTrendingDelta, bool showTrendingVolume, bool showTrendingRange, int trendLookbackBars, double trendDivisorPercent, int maxTriangles)
		{
			if (cacheEGFootprintLadder != null)
				for (int idx = 0; idx < cacheEGFootprintLadder.Length; idx++)
					if (cacheEGFootprintLadder[idx] != null && cacheEGFootprintLadder[idx].OffsetPixels == offsetPixels && cacheEGFootprintLadder[idx].LadderGapPixels == ladderGapPixels && cacheEGFootprintLadder[idx].LargeTradeGapPixels == largeTradeGapPixels && cacheEGFootprintLadder[idx].TicksPerRow == ticksPerRow && cacheEGFootprintLadder[idx].MaxLadderRows == maxLadderRows && cacheEGFootprintLadder[idx].ShowDeltaBar == showDeltaBar && cacheEGFootprintLadder[idx].DeltaBarMaxWidth == deltaBarMaxWidth && cacheEGFootprintLadder[idx].DeltaBarHeightPixels == deltaBarHeightPixels && cacheEGFootprintLadder[idx].DeltaBarOpacity == deltaBarOpacity && cacheEGFootprintLadder[idx].ShowPoc == showPoc && cacheEGFootprintLadder[idx].PocLineHeightPixels == pocLineHeightPixels && cacheEGFootprintLadder[idx].ShowImbalance == showImbalance && cacheEGFootprintLadder[idx].ImbalanceRatio == imbalanceRatio && cacheEGFootprintLadder[idx].LadderWidth == ladderWidth && cacheEGFootprintLadder[idx].TextFontFamily == textFontFamily && cacheEGFootprintLadder[idx].FontSize == fontSize && cacheEGFootprintLadder[idx].RowOpacity == rowOpacity && cacheEGFootprintLadder[idx].ExtendLadderBar == extendLadderBar && cacheEGFootprintLadder[idx].ShowLargeTrades == showLargeTrades && cacheEGFootprintLadder[idx].MaxLargeTradesPerRow == maxLargeTradesPerRow && cacheEGFootprintLadder[idx].LargeTradeThreshold == largeTradeThreshold && cacheEGFootprintLadder[idx].LargeTradeDotDiameterPixels == largeTradeDotDiameterPixels && cacheEGFootprintLadder[idx].LargeTradeDotSpacingPixels == largeTradeDotSpacingPixels && cacheEGFootprintLadder[idx].SellRowColor == sellRowColor && cacheEGFootprintLadder[idx].BuyRowColor == buyRowColor && cacheEGFootprintLadder[idx].NeutralRowColor == neutralRowColor && cacheEGFootprintLadder[idx].RowTextColor == rowTextColor && cacheEGFootprintLadder[idx].PocLineColor == pocLineColor && cacheEGFootprintLadder[idx].ImbalanceColor == imbalanceColor && cacheEGFootprintLadder[idx].LargeAskColor == largeAskColor && cacheEGFootprintLadder[idx].LargeBidColor == largeBidColor && cacheEGFootprintLadder[idx].GradientLevel == gradientLevel && cacheEGFootprintLadder[idx].ShowDataSummary == showDataSummary && cacheEGFootprintLadder[idx].DataSummaryGapPixels == dataSummaryGapPixels && cacheEGFootprintLadder[idx].ShowTotalDelta == showTotalDelta && cacheEGFootprintLadder[idx].ShowStrength == showStrength && cacheEGFootprintLadder[idx].ShowTrendingDelta == showTrendingDelta && cacheEGFootprintLadder[idx].ShowTrendingVolume == showTrendingVolume && cacheEGFootprintLadder[idx].ShowTrendingRange == showTrendingRange && cacheEGFootprintLadder[idx].TrendLookbackBars == trendLookbackBars && cacheEGFootprintLadder[idx].TrendDivisorPercent == trendDivisorPercent && cacheEGFootprintLadder[idx].MaxTriangles == maxTriangles && cacheEGFootprintLadder[idx].EqualsInput(input))
						return cacheEGFootprintLadder[idx];
			return CacheIndicator<EducatedGambling.EGFootprintLadder>(new EducatedGambling.EGFootprintLadder(){ OffsetPixels = offsetPixels, LadderGapPixels = ladderGapPixels, LargeTradeGapPixels = largeTradeGapPixels, TicksPerRow = ticksPerRow, MaxLadderRows = maxLadderRows, ShowDeltaBar = showDeltaBar, DeltaBarMaxWidth = deltaBarMaxWidth, DeltaBarHeightPixels = deltaBarHeightPixels, DeltaBarOpacity = deltaBarOpacity, ShowPoc = showPoc, PocLineHeightPixels = pocLineHeightPixels, ShowImbalance = showImbalance, ImbalanceRatio = imbalanceRatio, LadderWidth = ladderWidth, TextFontFamily = textFontFamily, FontSize = fontSize, RowOpacity = rowOpacity, ExtendLadderBar = extendLadderBar, ShowLargeTrades = showLargeTrades, MaxLargeTradesPerRow = maxLargeTradesPerRow, LargeTradeThreshold = largeTradeThreshold, LargeTradeDotDiameterPixels = largeTradeDotDiameterPixels, LargeTradeDotSpacingPixels = largeTradeDotSpacingPixels, SellRowColor = sellRowColor, BuyRowColor = buyRowColor, NeutralRowColor = neutralRowColor, RowTextColor = rowTextColor, PocLineColor = pocLineColor, ImbalanceColor = imbalanceColor, LargeAskColor = largeAskColor, LargeBidColor = largeBidColor, GradientLevel = gradientLevel, ShowDataSummary = showDataSummary, DataSummaryGapPixels = dataSummaryGapPixels, ShowTotalDelta = showTotalDelta, ShowStrength = showStrength, ShowTrendingDelta = showTrendingDelta, ShowTrendingVolume = showTrendingVolume, ShowTrendingRange = showTrendingRange, TrendLookbackBars = trendLookbackBars, TrendDivisorPercent = trendDivisorPercent, MaxTriangles = maxTriangles }, input, ref cacheEGFootprintLadder);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.EducatedGambling.EGFootprintLadder EGFootprintLadder(int offsetPixels, int ladderGapPixels, int largeTradeGapPixels, int ticksPerRow, int maxLadderRows, bool showDeltaBar, int deltaBarMaxWidth, int deltaBarHeightPixels, int deltaBarOpacity, bool showPoc, int pocLineHeightPixels, bool showImbalance, double imbalanceRatio, int ladderWidth, string textFontFamily, double fontSize, int rowOpacity, bool extendLadderBar, bool showLargeTrades, int maxLargeTradesPerRow, int largeTradeThreshold, int largeTradeDotDiameterPixels, int largeTradeDotSpacingPixels, Brush sellRowColor, Brush buyRowColor, Brush neutralRowColor, Brush rowTextColor, Brush pocLineColor, Brush imbalanceColor, Brush largeAskColor, Brush largeBidColor, int gradientLevel, bool showDataSummary, int dataSummaryGapPixels, bool showTotalDelta, bool showStrength, bool showTrendingDelta, bool showTrendingVolume, bool showTrendingRange, int trendLookbackBars, double trendDivisorPercent, int maxTriangles)
		{
			return indicator.EGFootprintLadder(Input, offsetPixels, ladderGapPixels, largeTradeGapPixels, ticksPerRow, maxLadderRows, showDeltaBar, deltaBarMaxWidth, deltaBarHeightPixels, deltaBarOpacity, showPoc, pocLineHeightPixels, showImbalance, imbalanceRatio, ladderWidth, textFontFamily, fontSize, rowOpacity, extendLadderBar, showLargeTrades, maxLargeTradesPerRow, largeTradeThreshold, largeTradeDotDiameterPixels, largeTradeDotSpacingPixels, sellRowColor, buyRowColor, neutralRowColor, rowTextColor, pocLineColor, imbalanceColor, largeAskColor, largeBidColor, gradientLevel, showDataSummary, dataSummaryGapPixels, showTotalDelta, showStrength, showTrendingDelta, showTrendingVolume, showTrendingRange, trendLookbackBars, trendDivisorPercent, maxTriangles);
		}

		public Indicators.EducatedGambling.EGFootprintLadder EGFootprintLadder(ISeries<double> input , int offsetPixels, int ladderGapPixels, int largeTradeGapPixels, int ticksPerRow, int maxLadderRows, bool showDeltaBar, int deltaBarMaxWidth, int deltaBarHeightPixels, int deltaBarOpacity, bool showPoc, int pocLineHeightPixels, bool showImbalance, double imbalanceRatio, int ladderWidth, string textFontFamily, double fontSize, int rowOpacity, bool extendLadderBar, bool showLargeTrades, int maxLargeTradesPerRow, int largeTradeThreshold, int largeTradeDotDiameterPixels, int largeTradeDotSpacingPixels, Brush sellRowColor, Brush buyRowColor, Brush neutralRowColor, Brush rowTextColor, Brush pocLineColor, Brush imbalanceColor, Brush largeAskColor, Brush largeBidColor, int gradientLevel, bool showDataSummary, int dataSummaryGapPixels, bool showTotalDelta, bool showStrength, bool showTrendingDelta, bool showTrendingVolume, bool showTrendingRange, int trendLookbackBars, double trendDivisorPercent, int maxTriangles)
		{
			return indicator.EGFootprintLadder(input, offsetPixels, ladderGapPixels, largeTradeGapPixels, ticksPerRow, maxLadderRows, showDeltaBar, deltaBarMaxWidth, deltaBarHeightPixels, deltaBarOpacity, showPoc, pocLineHeightPixels, showImbalance, imbalanceRatio, ladderWidth, textFontFamily, fontSize, rowOpacity, extendLadderBar, showLargeTrades, maxLargeTradesPerRow, largeTradeThreshold, largeTradeDotDiameterPixels, largeTradeDotSpacingPixels, sellRowColor, buyRowColor, neutralRowColor, rowTextColor, pocLineColor, imbalanceColor, largeAskColor, largeBidColor, gradientLevel, showDataSummary, dataSummaryGapPixels, showTotalDelta, showStrength, showTrendingDelta, showTrendingVolume, showTrendingRange, trendLookbackBars, trendDivisorPercent, maxTriangles);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.EducatedGambling.EGFootprintLadder EGFootprintLadder(int offsetPixels, int ladderGapPixels, int largeTradeGapPixels, int ticksPerRow, int maxLadderRows, bool showDeltaBar, int deltaBarMaxWidth, int deltaBarHeightPixels, int deltaBarOpacity, bool showPoc, int pocLineHeightPixels, bool showImbalance, double imbalanceRatio, int ladderWidth, string textFontFamily, double fontSize, int rowOpacity, bool extendLadderBar, bool showLargeTrades, int maxLargeTradesPerRow, int largeTradeThreshold, int largeTradeDotDiameterPixels, int largeTradeDotSpacingPixels, Brush sellRowColor, Brush buyRowColor, Brush neutralRowColor, Brush rowTextColor, Brush pocLineColor, Brush imbalanceColor, Brush largeAskColor, Brush largeBidColor, int gradientLevel, bool showDataSummary, int dataSummaryGapPixels, bool showTotalDelta, bool showStrength, bool showTrendingDelta, bool showTrendingVolume, bool showTrendingRange, int trendLookbackBars, double trendDivisorPercent, int maxTriangles)
		{
			return indicator.EGFootprintLadder(Input, offsetPixels, ladderGapPixels, largeTradeGapPixels, ticksPerRow, maxLadderRows, showDeltaBar, deltaBarMaxWidth, deltaBarHeightPixels, deltaBarOpacity, showPoc, pocLineHeightPixels, showImbalance, imbalanceRatio, ladderWidth, textFontFamily, fontSize, rowOpacity, extendLadderBar, showLargeTrades, maxLargeTradesPerRow, largeTradeThreshold, largeTradeDotDiameterPixels, largeTradeDotSpacingPixels, sellRowColor, buyRowColor, neutralRowColor, rowTextColor, pocLineColor, imbalanceColor, largeAskColor, largeBidColor, gradientLevel, showDataSummary, dataSummaryGapPixels, showTotalDelta, showStrength, showTrendingDelta, showTrendingVolume, showTrendingRange, trendLookbackBars, trendDivisorPercent, maxTriangles);
		}

		public Indicators.EducatedGambling.EGFootprintLadder EGFootprintLadder(ISeries<double> input , int offsetPixels, int ladderGapPixels, int largeTradeGapPixels, int ticksPerRow, int maxLadderRows, bool showDeltaBar, int deltaBarMaxWidth, int deltaBarHeightPixels, int deltaBarOpacity, bool showPoc, int pocLineHeightPixels, bool showImbalance, double imbalanceRatio, int ladderWidth, string textFontFamily, double fontSize, int rowOpacity, bool extendLadderBar, bool showLargeTrades, int maxLargeTradesPerRow, int largeTradeThreshold, int largeTradeDotDiameterPixels, int largeTradeDotSpacingPixels, Brush sellRowColor, Brush buyRowColor, Brush neutralRowColor, Brush rowTextColor, Brush pocLineColor, Brush imbalanceColor, Brush largeAskColor, Brush largeBidColor, int gradientLevel, bool showDataSummary, int dataSummaryGapPixels, bool showTotalDelta, bool showStrength, bool showTrendingDelta, bool showTrendingVolume, bool showTrendingRange, int trendLookbackBars, double trendDivisorPercent, int maxTriangles)
		{
			return indicator.EGFootprintLadder(input, offsetPixels, ladderGapPixels, largeTradeGapPixels, ticksPerRow, maxLadderRows, showDeltaBar, deltaBarMaxWidth, deltaBarHeightPixels, deltaBarOpacity, showPoc, pocLineHeightPixels, showImbalance, imbalanceRatio, ladderWidth, textFontFamily, fontSize, rowOpacity, extendLadderBar, showLargeTrades, maxLargeTradesPerRow, largeTradeThreshold, largeTradeDotDiameterPixels, largeTradeDotSpacingPixels, sellRowColor, buyRowColor, neutralRowColor, rowTextColor, pocLineColor, imbalanceColor, largeAskColor, largeBidColor, gradientLevel, showDataSummary, dataSummaryGapPixels, showTotalDelta, showStrength, showTrendingDelta, showTrendingVolume, showTrendingRange, trendLookbackBars, trendDivisorPercent, maxTriangles);
		}
	}
}

#endregion
