#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
#endregion

namespace NinjaTrader.NinjaScript.Indicators.EducatedGambling
{
    // Cumulative dollar-volume profile over a trailing N-session window (default 5, "Trailing
    // Days" - today counts as one of the N). Merges EGRollingVolumeProfile's structural
    // clustering (POC/VA/SVA/HVN, right-anchored pixel-pinned rendering) with
    // EGDollarVolumeProfile's buy/sell aggressor-split dollar volume (single hidden Last-tick
    // series, trade-synchronized GetAsk/GetBid historically, OnMarketData live - see root
    // CLAUDE.md "Better technique" under "Classifying buy vs sell aggressor").
    //
    // Deliberately cumulative, not recency-weighted - EGRollingVolumeProfile already covers the
    // short-term/current-session rolling view; this indicator's whole point is the bigger,
    // higher-timeframe volume narrative, so every session inside the window counts equally.
    //
    // Day-bucketed aggregate: rather than queueing every individual trade (as
    // EGRollingVolumeProfile does for an hours-scale window), this keeps one
    // Dictionary<double,double> pair (buy/sell) PER SESSION DAY plus a running aggregate. When a
    // new session begins, a new day bucket opens; once the window holds more than Trailing Days
    // buckets, the oldest day's contribution is subtracted back out of the aggregate and
    // discarded. This keeps memory bounded by (days x price levels) instead of (total ticks
    // across N days), which matters at this timescale.
    //
    // New-session detection: HISTORICAL day-buckets open from the HIDDEN TICK SERIES' own
    // Bars.IsFirstBarOfSession, checked inside ProcessHistoricalTick where Bars correctly
    // resolves to BarsArray[1] (BarsInProgress == 1 there) - not the primary series'. During
    // bulk historical replay the primary series and the hidden tick series are two independent
    // streams whose OnBarUpdate callbacks aren't guaranteed to interleave in true chronological
    // lockstep; deciding "new session" from one series while counting trades from the other let
    // a handful of trades right at a session boundary get processed before the "new session"
    // signal had fired - on a fast, sharp session-open move that's not a handful, it's the whole
    // opening thrust, silently undercounting the true LOW (confirmed: worse on coarser chart
    // periods, which have fewer primary-bar close events near the boundary to catch the primary
    // series' own state up). Checking the tick series' own flag makes session detection and
    // trade processing come from the exact same stream in the exact same order, eliminating the
    // race entirely. LIVE day-buckets still open from the primary series' own
    // Bars.IsFirstBarOfSession (OnBarUpdate, BarsInProgress == 0, gated to State.Realtime,
    // guarded via lastSessionFirstBarIndex) - live ticks arrive one at a time in genuine
    // chronological order, so this race is specific to bulk historical replay and doesn't apply
    // there.
    //
    // Profile Anchor: Current Bar (default) pins the profile's pivot to the right of the last
    // bar via GetXByBarIndex + Bar-profile Right Offset (px) - a bar-index position, so scrolling
    // the chart left eventually carries the profile out of view along with that bar. Chart Edge
    // pins the same pivot to ChartPanel.X + ChartPanel.W instead - a fixed screen position, not a
    // bar index - so the profile always hugs the chart's right wall regardless of scroll
    // position, same mechanism as EGVolumeProfile's "Most Right" Profile Alignment option;
    // Bar-profile Right Offset (px) is ignored in this mode. Side effect worth knowing: since
    // every label column sits at anchorFarX + Profile-Label Offset (px), pinning anchorFarX to
    // the panel's own right edge pushes that whole column past the panel's render bounds, so NT8
    // clips it - price tags and the Dominant Levels readout end up effectively invisible in Chart
    // Edge mode without needing a separate toggle for it.
    //
    // Display Mode: Net Bid/Ask colors every row flat green/red by which side (buy/sell)
    // dominated - no opacity/gradient shading by delta strength; Dominant Levels' text readout is
    // the only place delta strength shows up numerically. Normal ignores the buy/sell split and
    // draws every row in the flat Profile color instead, UNLESS that row falls inside a
    // Value Area / Secondary Value Area / POC range whose own "Display ..." checkbox is on, in
    // which case it's recolored with that level's own color at that level's own "... Opacity"
    // (same layered priority as EGRollingVolumeProfile's row-highlight scheme: POC > Secondary VA
    // > VA > Profile). This recoloring only applies in Normal mode - the "Display ..." checkboxes
    // have no effect on bar color in Net Bid/Ask mode.
    //
    // Shading vs. Display vs. recolor Opacity: "Display Value Area" / "Display Secondary Value
    // Area" / "Display POC" control bar recoloring in Normal mode (above, strength set by their
    // own "... Opacity" property) and still gate their own labels, same as before. The
    // translucent background BAND those checkboxes used to draw is a separate, independent
    // toggle - "Shade Value Area" / "Shade Secondary Value Area" / "Shade POC", strength set by
    // "... Shade Opacity" - so a band and a bar recolor can be turned on/off, and dimmed,
    // independently of each other. High Volume Nodes use this same DrawBand shading mechanism too
    // (their own "Display High Volume Nodes" + "High Volume Node Shade Opacity"), just with no
    // separate bar-recolor concept of their own - a cluster can span multiple rows, so there's no
    // single "the HVN row" to recolor the way POC has one.
    //
    // Rendering split: POC/VA/SVA/HVN all render as a translucent background band across their
    // price range (same DrawBand mechanism; POC's band spans a single row, a HVN cluster's band
    // spans its own BandLow..BandHigh range, each with its own opacity property) so the bars
    // painted afterward stay on top and legible. POC/VA/SVA/HVN are all computed against TOTAL
    // (buy+sell) dollar volume per price level regardless of Display Mode - they identify WHERE
    // the action concentrated. Labels are kept minimal for now - name and price only, no dollar
    // amounts.
    //
    // Mid Line/Label: a plain marker at the midpoint of the profile's own HIGH-LOW price range
    // ((HIGH+LOW)/2, rounded to the instrument's tick size) - not volume-weighted like POC, just
    // the geometric center of the range. Deliberately reuses the existing Boundary Line
    // Width/Style/Opacity properties and the Profile color rather than adding its own, since it's
    // just a third line alongside the HIGH/LOW boundary lines, not a new visual category.
    //
    // Dominant Levels: a per-row readout of the delta-percent metric (|buy-sell| / total, i.e.
    // |NinjaTrader BarDelta| / total volume, expressed as %) - not a "share of total volume"
    // reading. Unlike EGDollarVolumeProfile's Highlight Dominant Levels (which always draws every
    // row's label and only recolors the percentage once it clears the threshold), here the text
    // is only drawn AT ALL once a row's delta% clears Minimum Dominant Threshold (%) - this
    // profile can have far more rows than a session ladder, so most rows should stay silent.
    // Drawn as bare "NN%" text (no dollar total), right-aligned so it ends at anchorFarX - the
    // same fixed pivot every bar grows from - so it sits at each bar's base rather than drifting
    // with bar length, matching EGDollarVolumeProfile's own convention of anchoring per-row text
    // at the bar's base. Applies regardless of Display Mode (it reads the underlying buy/sell
    // split even when Normal mode isn't showing it via color).
    [CategoryOrder("Settings", 1)]
    [CategoryOrder("Profile", 2)]
    [CategoryOrder("Value Area", 3)]
    [CategoryOrder("Secondary Value Area", 4)]
    [CategoryOrder("Point Of Control", 5)]
    [CategoryOrder("High Volume Nodes", 6)]
    [CategoryOrder("Dominant Levels", 7)]
    [CategoryOrder("Labels", 8)]
    [CategoryOrder("Colors", 9)]
    public class EGInterestProfile : Indicator
    {
        private class DayBucket
        {
            public int FirstBarIndex;
            public readonly Dictionary<double, double> BuyUsd = new Dictionary<double, double>();
            public readonly Dictionary<double, double> SellUsd = new Dictionary<double, double>();
        }

        private struct HvnCluster
        {
            public double Peak;
            public double BandLow;
            public double BandHigh;
        }

        // One price-tag candidate awaiting the same-level merge in DrawPriceLabels.
        // PriorityRank follows POC(0) > Secondary Value Area(1) > Value Area(2) > Profile
        // Boundaries(3) - lower rank wins the combined label's color when levels coincide.
        private struct LabelEntry
        {
            public string Name;
            public int PriorityRank;
            public SharpDX.Direct2D1.Brush Brush;
        }

        private static readonly TimeSpan RthStart = new TimeSpan(9, 30, 0);
        private static readonly TimeSpan RthEnd = new TimeSpan(16, 0, 0);
        private static readonly TimeZoneInfo EasternTz = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

        // Safety cap on distinct price levels tracked at once - set generously above realistic
        // need rather than guessing a tight round number (see root CLAUDE.md "Silent-failure
        // safety caps"). A multi-day window at a coarse Price Grouping (ticks) default should
        // stay far under this even for a wide-ranging instrument.
        private const int MaxTrackedBuckets = 10000;
        private const double EvictEpsilon = 1e-6;

        private const float LabelHeight = 18f;
        // Wide enough for the longest realistic label, e.g. "SVAH / VAH 30775.00".
        private const float LabelMaxWidth = 180f;
        // Wide enough for "100%".
        private const float DominantLabelWidth = 50f;

        private double lastLastPrice;
        private int lastSessionFirstBarIndex = -1;
        private int lastSessionFirstTickBarIndex = -1;

        private readonly List<DayBucket> dayBuckets = new List<DayBucket>();
        private readonly Dictionary<double, double> buyUsdByPrice = new Dictionary<double, double>();
        private readonly Dictionary<double, double> sellUsdByPrice = new Dictionary<double, double>();

        private SharpDX.Direct2D1.Brush profileBrushDx;
        private SharpDX.Direct2D1.Brush buyBrushDx;
        private SharpDX.Direct2D1.Brush sellBrushDx;
        private SharpDX.Direct2D1.Brush dominantBrushDx;
        private SharpDX.Direct2D1.Brush valueAreaBrushDx;
        private SharpDX.Direct2D1.Brush secondaryValueAreaBrushDx;
        private SharpDX.Direct2D1.Brush pocBrushDx;
        private SharpDX.Direct2D1.Brush highVolumeNodeBrushDx;
        private NinjaTrader.Gui.Stroke boundaryLineStroke;
        private NinjaTrader.Gui.Stroke valueAreaLineStroke;
        private NinjaTrader.Gui.Stroke secondaryValueAreaLineStroke;
        private NinjaTrader.Gui.Stroke pocLineStroke;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "EGInterestProfile";
                Description = "Cumulative buy/sell dollar-volume profile over a trailing N-session window (default 5), from a single hidden Last-tick series using trade-synchronized bid/ask classification. POC, Value Area, Secondary Value Area, and High Volume Node clusters are ranked by total (buy+sell) dollar volume across the whole window. Display Mode Net Bid/Ask colors each row flat by which side dominated; Normal draws a flat single-color profile, optionally recoloring VA/SVA/POC rows with their own color and opacity. Profile Anchor Chart Edge keeps the profile pinned to the chart's right wall regardless of scroll position. Pair with EGRollingVolumeProfile for a short-term rolling view of the current session.";
                Calculate = Calculate.OnEachTick;
                IsOverlay = true;
                DisplayInDataBox = false;
                DrawOnPricePanel = true;
                PaintPriceMarkers = false;
                IsSuspendedWhileInactive = false;

                SessionFilter = EGInterestProfileSessionFilter.RTH;
                TrailingDays = 5;
                PriceGroupingTicks = 16;
                ProfileAnchor = EGInterestProfileAnchorMode.CurrentBar;
                RightOffsetPixels = 10;
                ProfileLabelOffsetPixels = 8;
                DisplayMode = EGInterestProfileDisplayMode.NetBidAsk;
                ProfileWidthPercent = 30;

                ShowBoundaryLine = false;
                BoundaryLineWidthPixels = 2;
                BoundaryLineStyle = DashStyleHelper.Solid;
                BoundaryLineOpacity = 100;
                ShowBoundaryLabels = true;
                ShowMidLine = false;
                ShowMidLabel = false;

                ShowValueArea = true;
                ValueAreaPercent = 70;
                ValueAreaOpacity = 85;
                ShadeValueArea = true;
                ValueAreaShadeOpacity = 20;
                ExtendValueAreaLine = false;
                ValueAreaLineWidthPixels = 2;
                ValueAreaLineStyle = DashStyleHelper.Solid;
                ValueAreaLineOpacity = 100;
                ShowValueAreaLabels = false;

                ShowSecondaryValueArea = false;
                SecondaryValueAreaPercent = 40;
                SecondaryValueAreaFromPrimary = false;
                SecondaryValueAreaOpacity = 40;
                ShadeSecondaryValueArea = false;
                SecondaryValueAreaShadeOpacity = 15;
                ExtendSecondaryValueAreaLine = false;
                SecondaryValueAreaLineWidthPixels = 2;
                SecondaryValueAreaLineStyle = DashStyleHelper.Solid;
                SecondaryValueAreaLineOpacity = 100;
                ShowSecondaryValueAreaLabels = false;

                ShowPOC = true;
                POCOpacity = 100;
                ShadePOC = true;
                POCShadeOpacity = 30;
                ShowPOCLine = true;
                POCLineWidthPixels = 2;
                POCLineStyle = DashStyleHelper.Dot;
                POCLineOpacity = 100;
                ShowPOCLabels = true;

                ShowHighVolumeNodes = false;
                HighVolumeNodeCount = 3;
                HighVolumeNodeClusterExpansionPercent = 50;
                HighVolumeNodeMinSpacingRows = 3;
                HighVolumeNodeShadeOpacity = 25;
                ExtendHighVolumeNodeLines = false;
                HighVolumeNodeLineOpacity = 100;
                ShowHighVolumeNodeLabels = false;

                HighlightDominantLevels = false;
                MinimumDominantThresholdPercent = 70;

                LabelFontFamily = "Consolas";
                LabelFontSize = 9;

                ProfileColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 70, 130, 180));
                BuyColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 0, 180, 120));
                SellColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 200, 50, 50));
                DominantColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 255, 255, 255));
                ValueAreaColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 255, 165, 0));
                SecondaryValueAreaColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 135, 206, 235));
                POCColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 220, 20, 60));
                HighVolumeNodeColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 255, 215, 0));
            }
            else if (State == State.Configure)
            {
                // Single hidden series (Last only) - historical ask/bid comes from this same tick
                // record via BarsArray[1].GetAsk/GetBid; live ask/bid comes from OnMarketData
                // instead. See root CLAUDE.md "Better technique" under "Classifying buy vs sell
                // aggressor".
                AddDataSeries(Instrument.FullName, BarsPeriodType.Tick, 1);
            }
            else if (State == State.DataLoaded)
            {
                dayBuckets.Clear();
                buyUsdByPrice.Clear();
                sellUsdByPrice.Clear();
                lastLastPrice = 0;
                lastSessionFirstBarIndex = -1;
                lastSessionFirstTickBarIndex = -1;
            }
            else if (State == State.Terminated)
            {
                DisposeDxBrushes();
            }
        }

        private static System.Windows.Media.Brush Frz(System.Windows.Media.Brush b, byte alpha = 0xFF)
        {
            if (b == null) return null;
            var s = b as System.Windows.Media.SolidColorBrush;
            var col = s != null ? s.Color : System.Windows.Media.Colors.Gray;
            if (alpha != 0xFF) col = System.Windows.Media.Color.FromArgb(alpha, col.R, col.G, col.B);
            var o = new System.Windows.Media.SolidColorBrush(col);
            o.Freeze();
            return o;
        }

        private void DisposeDxBrushes()
        {
            if (profileBrushDx != null) { profileBrushDx.Dispose(); profileBrushDx = null; }
            if (buyBrushDx != null) { buyBrushDx.Dispose(); buyBrushDx = null; }
            if (sellBrushDx != null) { sellBrushDx.Dispose(); sellBrushDx = null; }
            if (dominantBrushDx != null) { dominantBrushDx.Dispose(); dominantBrushDx = null; }
            if (valueAreaBrushDx != null) { valueAreaBrushDx.Dispose(); valueAreaBrushDx = null; }
            if (secondaryValueAreaBrushDx != null) { secondaryValueAreaBrushDx.Dispose(); secondaryValueAreaBrushDx = null; }
            if (pocBrushDx != null) { pocBrushDx.Dispose(); pocBrushDx = null; }
            if (highVolumeNodeBrushDx != null) { highVolumeNodeBrushDx.Dispose(); highVolumeNodeBrushDx = null; }
        }

        public override void OnRenderTargetChanged()
        {
            DisposeDxBrushes();

            if (RenderTarget == null) return;

            if (ProfileColor != null) profileBrushDx = ProfileColor.ToDxBrush(RenderTarget);
            if (BuyColor != null) buyBrushDx = BuyColor.ToDxBrush(RenderTarget);
            if (SellColor != null) sellBrushDx = SellColor.ToDxBrush(RenderTarget);
            if (DominantColor != null) dominantBrushDx = DominantColor.ToDxBrush(RenderTarget);
            if (ValueAreaColor != null) valueAreaBrushDx = ValueAreaColor.ToDxBrush(RenderTarget);
            if (SecondaryValueAreaColor != null) secondaryValueAreaBrushDx = SecondaryValueAreaColor.ToDxBrush(RenderTarget);
            if (POCColor != null) pocBrushDx = POCColor.ToDxBrush(RenderTarget);
            if (HighVolumeNodeColor != null) highVolumeNodeBrushDx = HighVolumeNodeColor.ToDxBrush(RenderTarget);

            boundaryLineStroke = new NinjaTrader.Gui.Stroke(ProfileColor ?? System.Windows.Media.Brushes.SteelBlue, BoundaryLineStyle, BoundaryLineWidthPixels);
            boundaryLineStroke.RenderTarget = RenderTarget;

            valueAreaLineStroke = new NinjaTrader.Gui.Stroke(ValueAreaColor ?? System.Windows.Media.Brushes.Orange, ValueAreaLineStyle, ValueAreaLineWidthPixels);
            valueAreaLineStroke.RenderTarget = RenderTarget;

            secondaryValueAreaLineStroke = new NinjaTrader.Gui.Stroke(SecondaryValueAreaColor ?? System.Windows.Media.Brushes.LightBlue, SecondaryValueAreaLineStyle, SecondaryValueAreaLineWidthPixels);
            secondaryValueAreaLineStroke.RenderTarget = RenderTarget;

            pocLineStroke = new NinjaTrader.Gui.Stroke(POCColor ?? System.Windows.Media.Brushes.Crimson, POCLineStyle, POCLineWidthPixels);
            pocLineStroke.RenderTarget = RenderTarget;
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress == 0)
            {
                // Historical day-buckets open from ProcessHistoricalTick instead (the hidden
                // tick series' own IsFirstBarOfSession, not this one) - see the class-level
                // "New-session detection" doc comment for why. Live ticks arrive one at a time
                // in genuine chronological order with the primary series, so the cross-series
                // race that motivated moving the historical path doesn't apply here.
                if (State == State.Realtime && Bars.IsFirstBarOfSession && CurrentBars[0] != lastSessionFirstBarIndex)
                {
                    lastSessionFirstBarIndex = CurrentBars[0];
                    StartNewDayBucket(CurrentBars[0]);
                }
                return;
            }

            // The hidden tick series is only acted on during State.Historical - historical
            // price/ask/bid come from that same tick record. Once live, it keeps ticking in the
            // background but is ignored; OnMarketData below takes over instead.
            if (BarsInProgress == 1 && State == State.Historical)
                ProcessHistoricalTick();
        }

        private void StartNewDayBucket(int firstBarIndex)
        {
            dayBuckets.Add(new DayBucket { FirstBarIndex = firstBarIndex });
            EvictOldDays();
        }

        // Keeps at most Trailing Days session buckets (today counts as one of them). Rather than
        // clearing everything (a plain session profile's approach), the oldest day's own
        // contribution is subtracted back out of the running aggregate so the remaining days'
        // volume is preserved untouched.
        private void EvictOldDays()
        {
            int cap = Math.Max(1, TrailingDays);
            while (dayBuckets.Count > cap)
            {
                DayBucket oldest = dayBuckets[0];
                dayBuckets.RemoveAt(0);
                SubtractDay(buyUsdByPrice, oldest.BuyUsd);
                SubtractDay(sellUsdByPrice, oldest.SellUsd);
            }
        }

        private static void SubtractDay(Dictionary<double, double> aggregate, Dictionary<double, double> day)
        {
            foreach (KeyValuePair<double, double> kv in day)
            {
                double existing;
                if (!aggregate.TryGetValue(kv.Key, out existing))
                    continue;

                double updated = existing - kv.Value;
                if (updated <= EvictEpsilon)
                    aggregate.Remove(kv.Key);
                else
                    aggregate[kv.Key] = updated;
            }
        }

        private void ProcessHistoricalTick()
        {
            if (CurrentBars[1] < 0)
                return;

            // Bars correctly resolves to BarsArray[1] here (BarsInProgress == 1), so this is the
            // hidden tick series' OWN first-bar-of-session flag - i.e. "is this exact trade the
            // first trade of a new session" - checked in the same call, same order, as the trade
            // data below, rather than racing the primary series' own copy of this flag. See the
            // class-level "New-session detection" doc comment for the full reasoning.
            if (Bars.IsFirstBarOfSession && CurrentBars[1] != lastSessionFirstTickBarIndex)
            {
                lastSessionFirstTickBarIndex = CurrentBars[1];
                StartNewDayBucket(CurrentBars[0]);
            }

            double price = BarsArray[1].GetClose(CurrentBars[1]);
            double size = BarsArray[1].GetVolume(CurrentBars[1]);
            double ask = Instrument.MasterInstrument.RoundToTickSize(BarsArray[1].GetAsk(CurrentBars[1]));
            double bid = Instrument.MasterInstrument.RoundToTickSize(BarsArray[1].GetBid(CurrentBars[1]));
            DateTime tickTime = BarsArray[1].GetTime(CurrentBars[1]);

            ProcessTrade(price, size, ask, bid, tickTime);
        }

        // Live trade-synchronized classification: OnMarketData hands us the feed's own Ask/Bid
        // for THIS specific trade event directly - no hidden series needed at all in real time.
        protected override void OnMarketData(MarketDataEventArgs marketData)
        {
            if (State != State.Realtime || marketData.MarketDataType != MarketDataType.Last)
                return;

            double ask = Instrument.MasterInstrument.RoundToTickSize(marketData.Ask);
            double bid = Instrument.MasterInstrument.RoundToTickSize(marketData.Bid);

            ProcessTrade(marketData.Price, marketData.Volume, ask, bid, marketData.Time);
        }

        // Shared classification + accumulation for both the historical (ProcessHistoricalTick)
        // and live (OnMarketData) paths. Quote rule first (ask/bid from the trade's own record),
        // tick rule as a fallback when neither side is decisively crossed. Adds to both the
        // current (most recent) day bucket and the running aggregate.
        private void ProcessTrade(double price, double size, double ask, double bid, DateTime tickTime)
        {
            if (dayBuckets.Count == 0)
                return;

            if (SessionFilter == EGInterestProfileSessionFilter.RTH && !IsInsideRth(tickTime))
            {
                lastLastPrice = price;
                return;
            }

            bool? isBuy = null;
            if (ask > 0 && price >= ask) isBuy = true;
            else if (bid > 0 && price <= bid) isBuy = false;
            else if (lastLastPrice > 0)
            {
                if (price > lastLastPrice) isBuy = true;
                else if (price < lastLastPrice) isBuy = false;
            }

            lastLastPrice = price;

            if (isBuy == null) return;

            double usd = price * size * Instrument.MasterInstrument.PointValue;
            double bucket = GetBucket(price);

            DayBucket current = dayBuckets[dayBuckets.Count - 1];
            Dictionary<double, double> dayTarget = isBuy.Value ? current.BuyUsd : current.SellUsd;
            Dictionary<double, double> aggTarget = isBuy.Value ? buyUsdByPrice : sellUsdByPrice;

            if (!aggTarget.ContainsKey(bucket) && aggTarget.Count >= MaxTrackedBuckets)
                return;

            Accumulate(dayTarget, bucket, usd);
            Accumulate(aggTarget, bucket, usd);
        }

        private static void Accumulate(Dictionary<double, double> target, double bucket, double usd)
        {
            double existing;
            target[bucket] = (target.TryGetValue(bucket, out existing) ? existing : 0) + usd;
        }

        private static bool IsInsideRth(DateTime t)
        {
            DateTime easternTime = TimeZoneInfo.ConvertTime(t, EasternTz);
            TimeSpan tod = easternTime.TimeOfDay;
            return tod >= RthStart && tod <= RthEnd;
        }

        private double GetBucket(double price)
        {
            double width = Instrument.MasterInstrument.TickSize * Math.Max(1, PriceGroupingTicks);
            return Instrument.MasterInstrument.RoundToTickSize(Math.Floor(price / width) * width);
        }

        // Same expanding-outward-from-POC algorithm as EGRollingVolumeProfile/EGVolumeProfile,
        // fed TOTAL (buy+sell) dollar volume per level rather than raw contract volume.
        private void ComputeValueArea(Dictionary<double, double> data, List<double> sortedBuckets, double pocPrice, double targetPercent, double totalUsd, out double low, out double high)
        {
            int pocIdx = sortedBuckets.IndexOf(pocPrice);
            int lowIdx = pocIdx;
            int highIdx = pocIdx;
            double target = totalUsd * Math.Max(0, Math.Min(100, targetPercent)) / 100.0;
            double accumulated = data[pocPrice];

            while (accumulated < target && (lowIdx > 0 || highIdx < sortedBuckets.Count - 1))
            {
                double volBelow = lowIdx > 0 ? data[sortedBuckets[lowIdx - 1]] : -1;
                double volAbove = highIdx < sortedBuckets.Count - 1 ? data[sortedBuckets[highIdx + 1]] : -1;

                if (volAbove >= volBelow)
                {
                    highIdx++;
                    accumulated += data[sortedBuckets[highIdx]];
                }
                else
                {
                    lowIdx--;
                    accumulated += data[sortedBuckets[lowIdx]];
                }
            }

            low = sortedBuckets[lowIdx];
            high = sortedBuckets[highIdx];
        }

        private void ComputeSecondaryValueArea(Dictionary<double, double> data, List<double> buckets, double pocPrice,
            double vaLow, double vaHigh, double totalUsd, out double svaLow, out double svaHigh)
        {
            if (!SecondaryValueAreaFromPrimary)
            {
                ComputeValueArea(data, buckets, pocPrice, SecondaryValueAreaPercent, totalUsd, out svaLow, out svaHigh);
                return;
            }

            List<double> vaBuckets = buckets.Where(p => p >= vaLow && p <= vaHigh).ToList();
            double vaTotalUsd = vaBuckets.Sum(p => data[p]);
            ComputeValueArea(data, vaBuckets, pocPrice, SecondaryValueAreaPercent, vaTotalUsd, out svaLow, out svaHigh);
        }

        // Isolates the top Node Count local-maximum PEAKS (by total dollar volume) and, for each,
        // the contiguous band of neighboring rows whose volume stays at/above HVN Cluster
        // Expansion (%) of that peak's own volume - same peak-detection as
        // EGRollingVolumeProfile.ComputeHighVolumeNodeClusters, but returning each cluster's own
        // band range (rather than a flat membership set) since bands are drawn via DrawBand here,
        // not per-row fill overrides.
        private static void ComputeHighVolumeNodeClusters(Dictionary<double, double> data, List<double> buckets, int peakCount, double expansionPercent, int minSpacingRows,
            out List<HvnCluster> clusters)
        {
            clusters = new List<HvnCluster>();

            if (buckets.Count == 0 || peakCount <= 0)
                return;

            var localMaxima = new List<double>();
            for (int i = 0; i < buckets.Count; i++)
            {
                double price = buckets[i];
                double vol = data[price];
                if (vol <= 0) continue;

                double prevVol = i > 0 ? data[buckets[i - 1]] : double.NegativeInfinity;
                double nextVol = i < buckets.Count - 1 ? data[buckets[i + 1]] : double.NegativeInfinity;

                if (vol >= prevVol && vol >= nextVol)
                    localMaxima.Add(price);
            }

            double rowSize = buckets.Count >= 2 ? Math.Abs(buckets[1] - buckets[0]) : 0;
            double minSpacingPrice = rowSize * Math.Max(0, minSpacingRows);

            var rankedPeaks = new List<double>();
            foreach (double candidate in localMaxima.OrderByDescending(p => data[p]))
            {
                if (rankedPeaks.Count >= peakCount) break;
                if (rankedPeaks.Any(p => Math.Abs(p - candidate) < minSpacingPrice)) continue;
                rankedPeaks.Add(candidate);
            }

            foreach (double peak in rankedPeaks)
            {
                int peakIdx = buckets.IndexOf(peak);
                double floor = data[peak] * Math.Max(0, Math.Min(100, expansionPercent)) / 100.0;
                double bandLow = peak;
                double bandHigh = peak;

                for (int i = peakIdx - 1; i >= 0; i--)
                {
                    if (data[buckets[i]] < floor) break;
                    bandLow = buckets[i];
                }

                for (int i = peakIdx + 1; i < buckets.Count; i++)
                {
                    if (data[buckets[i]] < floor) break;
                    bandHigh = buckets[i];
                }

                clusters.Add(new HvnCluster { Peak = peak, BandLow = bandLow, BandHigh = bandHigh });
            }
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            if (Bars == null || ChartControl == null || IsInHitTest)
                return;

            int lastBarIndex = ChartBars.ToIndex;
            if (lastBarIndex < 0)
                return;

            if (dayBuckets.Count == 0 || (buyUsdByPrice.Count == 0 && sellUsdByPrice.Count == 0))
                return;

            if (profileBrushDx == null || buyBrushDx == null || sellBrushDx == null || dominantBrushDx == null || valueAreaBrushDx == null || secondaryValueAreaBrushDx == null || pocBrushDx == null || highVolumeNodeBrushDx == null)
                return;

            double rowSize = Instrument.MasterInstrument.TickSize * Math.Max(1, PriceGroupingTicks);

            // ChartPanel.W can transiently read 0 during a chart resize/dock/workspace-switch
            // layout pass. In Current Bar mode that just makes the profile momentarily thinner
            // (the anchor itself is bar-based, not W-based), but in Chart Edge mode the anchor
            // IS ChartPanel.X + ChartPanel.W, so a 0 width collapses it all the way to
            // ChartPanel.X (the panel's far left) - producing degenerate/negative-width bars and
            // zero-length lines, i.e. the profile appears to vanish for that frame. Skip the
            // frame instead of drawing a collapsed/misplaced profile.
            if (ChartPanel.W <= 0)
                return;

            float maxBarWidth = (float)(ProfileWidthPercent / 100.0 * ChartPanel.W);
            float anchorFarX;
            if (ProfileAnchor == EGInterestProfileAnchorMode.ChartEdge)
            {
                // Pinned to the chart panel's actual right edge (a fixed screen position, not a
                // bar index) - see the "Profile Anchor" class-level doc comment. Bar-profile
                // Right Offset (px) is meaningless here since there's no bar to offset from.
                anchorFarX = ChartPanel.X + ChartPanel.W;
            }
            else
            {
                float nearEdgeX = chartControl.GetXByBarIndex(ChartBars, lastBarIndex) + RightOffsetPixels;
                anchorFarX = nearEdgeX + maxBarWidth;
            }

            int lookbackStartBar = dayBuckets[0].FirstBarIndex;
            float lookbackStartX = chartControl.GetXByBarIndex(ChartBars, Math.Max(ChartBars.FromIndex, lookbackStartBar));

            var bucketSet = new HashSet<double>(buyUsdByPrice.Keys);
            bucketSet.UnionWith(sellUsdByPrice.Keys);
            List<double> buckets = bucketSet.OrderBy(p => p).ToList();
            if (buckets.Count == 0) return;

            double midPrice = Instrument.MasterInstrument.RoundToTickSize((buckets[0] + buckets[buckets.Count - 1]) / 2.0);

            var totalUsdByPrice = new Dictionary<double, double>();
            foreach (double p in buckets)
            {
                double b, s;
                buyUsdByPrice.TryGetValue(p, out b);
                sellUsdByPrice.TryGetValue(p, out s);
                totalUsdByPrice[p] = b + s;
            }

            double totalUsd = totalUsdByPrice.Values.Sum();
            if (totalUsd <= 0) return;
            double maxTotalUsd = totalUsdByPrice.Values.Max();

            double pocPrice = buckets.OrderByDescending(p => totalUsdByPrice[p]).First();

            double vaLow, vaHigh;
            ComputeValueArea(totalUsdByPrice, buckets, pocPrice, ValueAreaPercent, totalUsd, out vaLow, out vaHigh);

            double svaLow, svaHigh;
            ComputeSecondaryValueArea(totalUsdByPrice, buckets, pocPrice, vaLow, vaHigh, totalUsd, out svaLow, out svaHigh);

            List<HvnCluster> hvnClusters;
            ComputeHighVolumeNodeClusters(totalUsdByPrice, buckets, HighVolumeNodeCount, HighVolumeNodeClusterExpansionPercent, HighVolumeNodeMinSpacingRows, out hvnClusters);

            // Value Area / Secondary Value Area / POC / High Volume Node clusters all render as
            // background bands first (when their own toggle is on), so the bars painted
            // afterward stay on top and legible.
            if (ShadeValueArea)
                DrawBand(chartScale, vaLow, vaHigh, rowSize, lookbackStartX, anchorFarX, valueAreaBrushDx, ValueAreaShadeOpacity);

            if (ShadeSecondaryValueArea)
                DrawBand(chartScale, svaLow, svaHigh, rowSize, lookbackStartX, anchorFarX, secondaryValueAreaBrushDx, SecondaryValueAreaShadeOpacity);

            if (ShadePOC)
                DrawBand(chartScale, pocPrice, pocPrice, rowSize, lookbackStartX, anchorFarX, pocBrushDx, POCShadeOpacity);

            if (ShowHighVolumeNodes)
            {
                foreach (HvnCluster cluster in hvnClusters)
                    DrawBand(chartScale, cluster.BandLow, cluster.BandHigh, rowSize, lookbackStartX, anchorFarX, highVolumeNodeBrushDx, HighVolumeNodeShadeOpacity);
            }

            foreach (double price in buckets)
            {
                if (price < chartScale.MinValue || price > chartScale.MaxValue)
                    continue;

                double buy, sell;
                buyUsdByPrice.TryGetValue(price, out buy);
                sellUsdByPrice.TryGetValue(price, out sell);
                double total = buy + sell;
                if (total <= 0) continue;
                double net = buy - sell;

                float y = chartScale.GetYByValue(price);
                float rowTop = chartScale.GetYByValue(price + rowSize / 2.0);
                float rowBottom = chartScale.GetYByValue(price - rowSize / 2.0);
                float rowHeight = Math.Max(1f, Math.Abs(rowBottom - rowTop) - 1f);
                float width = (float)(total / maxTotalUsd * maxBarWidth);

                SharpDX.Direct2D1.Brush brush;
                if (DisplayMode == EGInterestProfileDisplayMode.NetBidAsk)
                {
                    brush = net >= 0 ? buyBrushDx : sellBrushDx;
                    brush.Opacity = 1f;
                }
                else
                {
                    // Normal: flat Profile color, unless this row falls inside a VA/SVA/POC range
                    // whose own "Display ..." checkbox is on - same layered priority as
                    // EGRollingVolumeProfile's row highlighting (POC > Secondary VA > VA), each
                    // with its own recolor opacity.
                    brush = profileBrushDx;
                    double opacityPercent = 100.0;
                    if (ShowValueArea && price >= vaLow && price <= vaHigh) { brush = valueAreaBrushDx; opacityPercent = ValueAreaOpacity; }
                    if (ShowSecondaryValueArea && price >= svaLow && price <= svaHigh) { brush = secondaryValueAreaBrushDx; opacityPercent = SecondaryValueAreaOpacity; }
                    if (ShowPOC && price == pocPrice) { brush = pocBrushDx; opacityPercent = POCOpacity; }
                    brush.Opacity = (float)(Math.Max(0, Math.Min(100, opacityPercent)) / 100.0);
                }

                float barLeft = anchorFarX - width;
                var rect = new SharpDX.RectangleF(barLeft, y - rowHeight / 2f, width, rowHeight);
                RenderTarget.FillRectangle(rect, brush);
            }

            if (HighlightDominantLevels)
                DrawDominantLabels(chartScale, anchorFarX, buckets);

            if (ShowBoundaryLine)
            {
                DrawExtendedLine(chartScale, buckets[buckets.Count - 1], lookbackStartX, anchorFarX, profileBrushDx, BoundaryLineWidthPixels, BoundaryLineOpacity, boundaryLineStroke);
                DrawExtendedLine(chartScale, buckets[0], lookbackStartX, anchorFarX, profileBrushDx, BoundaryLineWidthPixels, BoundaryLineOpacity, boundaryLineStroke);
            }

            if (ShowMidLine)
                DrawExtendedLine(chartScale, midPrice, lookbackStartX, anchorFarX, profileBrushDx, BoundaryLineWidthPixels, BoundaryLineOpacity, boundaryLineStroke);

            if (ExtendValueAreaLine)
            {
                DrawExtendedLine(chartScale, vaHigh, lookbackStartX, anchorFarX, valueAreaBrushDx, ValueAreaLineWidthPixels, ValueAreaLineOpacity, valueAreaLineStroke);
                DrawExtendedLine(chartScale, vaLow, lookbackStartX, anchorFarX, valueAreaBrushDx, ValueAreaLineWidthPixels, ValueAreaLineOpacity, valueAreaLineStroke);
            }

            if (ExtendSecondaryValueAreaLine)
            {
                DrawExtendedLine(chartScale, svaHigh, lookbackStartX, anchorFarX, secondaryValueAreaBrushDx, SecondaryValueAreaLineWidthPixels, SecondaryValueAreaLineOpacity, secondaryValueAreaLineStroke);
                DrawExtendedLine(chartScale, svaLow, lookbackStartX, anchorFarX, secondaryValueAreaBrushDx, SecondaryValueAreaLineWidthPixels, SecondaryValueAreaLineOpacity, secondaryValueAreaLineStroke);
            }

            if (ShowPOCLine)
                DrawExtendedLine(chartScale, pocPrice, lookbackStartX, anchorFarX, pocBrushDx, POCLineWidthPixels, POCLineOpacity, pocLineStroke);

            if (ExtendHighVolumeNodeLines)
            {
                foreach (HvnCluster cluster in hvnClusters)
                {
                    if (cluster.Peak < chartScale.MinValue || cluster.Peak > chartScale.MaxValue) continue;

                    float peakRowTop = chartScale.GetYByValue(cluster.Peak + rowSize / 2.0);
                    float peakRowBottom = chartScale.GetYByValue(cluster.Peak - rowSize / 2.0);
                    float peakRowHeight = Math.Max(1f, Math.Abs(peakRowBottom - peakRowTop) - 1f);

                    DrawExtendedLine(chartScale, cluster.Peak, lookbackStartX, anchorFarX, highVolumeNodeBrushDx, peakRowHeight, HighVolumeNodeLineOpacity, null);
                }
            }

            DrawPriceLabels(chartScale, anchorFarX, buckets, vaLow, vaHigh, svaLow, svaHigh, pocPrice, midPrice, hvnClusters);
        }

        // Translucent full-span background rectangle behind the bars, from the start of the
        // trailing window out to the profile's own pivot - shared by the Value Area, Secondary
        // Value Area, POC, and High Volume Node bands (POC's low/high are the same single price,
        // so its band collapses to one row's height; a HVN cluster's band spans its own
        // BandLow..BandHigh range, which can cover multiple rows).
        private void DrawBand(ChartScale chartScale, double low, double high, double rowSizeValue, float x0, float x1, SharpDX.Direct2D1.Brush brush, double opacityPercent)
        {
            if (brush == null) return;

            double top = Math.Max(low, high) + rowSizeValue / 2.0;
            double bottom = Math.Min(low, high) - rowSizeValue / 2.0;
            if (top < chartScale.MinValue || bottom > chartScale.MaxValue) return;

            float yTop = chartScale.GetYByValue(top);
            float yBottom = chartScale.GetYByValue(bottom);
            brush.Opacity = (float)(Math.Max(0, Math.Min(100, opacityPercent)) / 100.0);
            var rect = new SharpDX.RectangleF(x0, yTop, x1 - x0, yBottom - yTop);
            RenderTarget.FillRectangle(rect, brush);
        }

        private void DrawExtendedLine(ChartScale chartScale, double price, float x0, float x1, SharpDX.Direct2D1.Brush brush, float widthPixels, double opacityPercent,
            NinjaTrader.Gui.Stroke stroke)
        {
            if (brush == null) return;
            if (price < chartScale.MinValue || price > chartScale.MaxValue) return;

            float y = chartScale.GetYByValue(price);
            brush.Opacity = (float)(Math.Max(0, Math.Min(100, opacityPercent)) / 100.0);
            RenderTarget.DrawLine(new SharpDX.Vector2(x0, y), new SharpDX.Vector2(x1, y), brush, widthPixels, stroke == null ? null : stroke.StrokeStyle);
        }

        // Delta% readout per row (|buy-sell| / total), drawn only for rows that clear Minimum
        // Dominant Threshold (%) - see the "Dominant Levels" class-level doc comment.
        // Right-aligned ending at anchorFarX (the bars' shared pivot) so it sits at each bar's
        // base rather than the far label column, the same anchoring convention
        // EGDollarVolumeProfile uses for its own per-row text.
        private void DrawDominantLabels(ChartScale chartScale, float anchorFarX, List<double> buckets)
        {
            using (SharpDX.DirectWrite.TextFormat format = BuildLabelTextFormat(LabelFontFamily, LabelFontSize, SharpDX.DirectWrite.TextAlignment.Trailing))
            {
                foreach (double price in buckets)
                {
                    if (price < chartScale.MinValue || price > chartScale.MaxValue)
                        continue;

                    double buy, sell;
                    buyUsdByPrice.TryGetValue(price, out buy);
                    sellUsdByPrice.TryGetValue(price, out sell);
                    double total = buy + sell;
                    if (total <= 0) continue;

                    double deltaPercent = Math.Abs(buy - sell) / total * 100.0;
                    if (deltaPercent < MinimumDominantThresholdPercent) continue;

                    float y = chartScale.GetYByValue(price);
                    string text = deltaPercent.ToString("0") + "%";

                    dominantBrushDx.Opacity = 1f;
                    var rect = new SharpDX.RectangleF(anchorFarX - DominantLabelWidth, y - LabelHeight / 2f, DominantLabelWidth, LabelHeight);
                    RenderTarget.DrawText(text, format, rect, dominantBrushDx, SharpDX.Direct2D1.DrawTextOptions.None);
                }
            }
        }

        // Price tags sit in one fixed column at anchorFarX + Profile-Label Offset (px). HIGH/LOW,
        // MID, VAH/VAL, SVAH/SVAL and POC are grouped by exact price and merged into one combined
        // label when they coincide (same rule as EGRollingVolumeProfile). Kept minimal for now -
        // name and price only, no dollar amounts.
        private void DrawPriceLabels(ChartScale chartScale, float anchorFarX, List<double> buckets, double vaLow, double vaHigh, double svaLow, double svaHigh, double pocPrice, double midPrice, List<HvnCluster> hvnClusters)
        {
            float labelX = anchorFarX + ProfileLabelOffsetPixels;

            using (SharpDX.DirectWrite.TextFormat labelFormat = BuildLabelTextFormat(LabelFontFamily, LabelFontSize))
            {
                var entriesByPrice = new Dictionary<double, List<LabelEntry>>();

                if (ShowBoundaryLabels)
                {
                    AddLabelEntry(entriesByPrice, buckets[buckets.Count - 1], "HIGH", 3, profileBrushDx);
                    AddLabelEntry(entriesByPrice, buckets[0], "LOW", 3, profileBrushDx);
                }

                if (ShowMidLabel)
                    AddLabelEntry(entriesByPrice, midPrice, "MID", 3, profileBrushDx);

                if (ShowValueArea && ShowValueAreaLabels)
                {
                    AddLabelEntry(entriesByPrice, vaHigh, "VAH", 2, valueAreaBrushDx);
                    AddLabelEntry(entriesByPrice, vaLow, "VAL", 2, valueAreaBrushDx);
                }

                if (ShowSecondaryValueArea && ShowSecondaryValueAreaLabels)
                {
                    AddLabelEntry(entriesByPrice, svaHigh, "SVAH", 1, secondaryValueAreaBrushDx);
                    AddLabelEntry(entriesByPrice, svaLow, "SVAL", 1, secondaryValueAreaBrushDx);
                }

                if (ShowPOCLabels)
                    AddLabelEntry(entriesByPrice, pocPrice, "POC", 0, pocBrushDx);

                foreach (KeyValuePair<double, List<LabelEntry>> group in entriesByPrice)
                {
                    List<LabelEntry> entries = group.Value;
                    entries.Sort((a, b) => a.PriorityRank.CompareTo(b.PriorityRank));

                    string combinedName = string.Join(" / ", entries.Select(e => e.Name));
                    DrawPriceLabel(chartScale, labelX, group.Key, combinedName, entries[0].Brush, labelFormat);
                }

                if (ShowHighVolumeNodes && ShowHighVolumeNodeLabels)
                {
                    for (int i = 0; i < hvnClusters.Count; i++)
                        DrawPriceLabel(chartScale, labelX, hvnClusters[i].Peak, "HVN" + (i + 1), highVolumeNodeBrushDx, labelFormat);
                }
            }
        }

        private static void AddLabelEntry(Dictionary<double, List<LabelEntry>> entriesByPrice, double price, string name, int priorityRank, SharpDX.Direct2D1.Brush brush)
        {
            List<LabelEntry> list;
            if (!entriesByPrice.TryGetValue(price, out list))
            {
                list = new List<LabelEntry>();
                entriesByPrice[price] = list;
            }
            list.Add(new LabelEntry { Name = name, PriorityRank = priorityRank, Brush = brush });
        }

        private static SharpDX.DirectWrite.TextFormat BuildLabelTextFormat(string fontFamily, double fontSize, SharpDX.DirectWrite.TextAlignment alignment = SharpDX.DirectWrite.TextAlignment.Leading)
        {
            SimpleFont font = new SimpleFont(fontFamily, fontSize);
            SharpDX.DirectWrite.TextFormat format = font.ToDirectWriteTextFormat();
            format.TextAlignment = alignment;
            format.ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center;
            return format;
        }

        private void DrawPriceLabel(ChartScale chartScale, float labelX, double price, string name, SharpDX.Direct2D1.Brush textBrush, SharpDX.DirectWrite.TextFormat textFormat)
        {
            if (price < chartScale.MinValue || price > chartScale.MaxValue)
                return;

            float y = chartScale.GetYByValue(price);
            string text = name + " " + Instrument.MasterInstrument.FormatPrice(price);

            textBrush.Opacity = 1f;
            var rect = new SharpDX.RectangleF(labelX, y - LabelHeight / 2f, LabelMaxWidth, LabelHeight);
            RenderTarget.DrawText(text, textFormat, rect, textBrush, SharpDX.Direct2D1.DrawTextOptions.None);
        }

        #region Properties

        // ----- Settings -----

        [XmlIgnore]
        [Display(Name = "Session", Description = "Restrict accumulation to Regular Trading Hours (RTH) or include the full extended session (ETH)", GroupName = "Settings", Order = 1)]
        public EGInterestProfileSessionFilter SessionFilter { get; set; }

        [Browsable(false)]
        public string SessionFilterSerializable
        {
            get { return SessionFilter.ToString(); }
            set { SessionFilter = (EGInterestProfileSessionFilter)Enum.Parse(typeof(EGInterestProfileSessionFilter), value); }
        }

        [NinjaScriptProperty]
        [Range(1, 60)]
        [Display(Name = "Trailing Days", Description = "Cumulative dollar-volume window, in trading sessions (today counts as one); once a new session begins, the oldest session's contribution ages out of the aggregate entirely - no recency weighting inside the window", GroupName = "Settings", Order = 2)]
        public int TrailingDays { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100000)]
        [Display(Name = "Price Grouping (ticks)", Description = "Number of ticks grouped into each profile row (row width = tick size x this value)", GroupName = "Settings", Order = 3)]
        public int PriceGroupingTicks { get; set; }

        [XmlIgnore]
        [Display(Name = "Profile Anchor", Description = "Current Bar pins the profile to the right of the last bar (Bar-profile Right Offset (px) applies); scrolls out of view when you scroll the chart left. Chart Edge pins the profile to the chart panel's own right edge instead - a fixed screen position, always visible regardless of scroll - same mechanism as EGVolumeProfile's Most Right alignment; Bar-profile Right Offset (px) is ignored, and price/dominant labels typically fall outside the visible panel", GroupName = "Settings", Order = 4)]
        public EGInterestProfileAnchorMode ProfileAnchor { get; set; }

        [Browsable(false)]
        public string ProfileAnchorSerializable
        {
            get { return ProfileAnchor.ToString(); }
            set { ProfileAnchor = (EGInterestProfileAnchorMode)Enum.Parse(typeof(EGInterestProfileAnchorMode), value); }
        }

        [NinjaScriptProperty]
        [Range(0, 500)]
        [Display(Name = "Bar-profile Right Offset (px)", Description = "Horizontal pixel gap from the last bar to where the nearest (longest) profile row can reach; the profile grows leftward, toward the bars, from its own fixed pivot further right. Only applies when Profile Anchor is Current Bar", GroupName = "Settings", Order = 5)]
        public int RightOffsetPixels { get; set; }

        [NinjaScriptProperty]
        [Range(0, 200)]
        [Display(Name = "Profile-Label Offset (px)", Description = "Horizontal pixel gap from the profile's own pivot (its right edge) to the price labels", GroupName = "Settings", Order = 6)]
        public int ProfileLabelOffsetPixels { get; set; }

        [XmlIgnore]
        [Display(Name = "Display Mode", Description = "Net Bid/Ask colors each row flat green/red by which side dominated (no opacity shading by strength - see Dominant Levels for that); Normal draws every row in the flat Profile color, optionally recolored per-row by the Value Area / Secondary Value Area / POC \"Display ...\" checkboxes", GroupName = "Settings", Order = 7)]
        public EGInterestProfileDisplayMode DisplayMode { get; set; }

        [Browsable(false)]
        public string DisplayModeSerializable
        {
            get { return DisplayMode.ToString(); }
            set { DisplayMode = (EGInterestProfileDisplayMode)Enum.Parse(typeof(EGInterestProfileDisplayMode), value); }
        }

        // ----- Profile -----

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Profile Width (%)", Description = "Max bar length as a percentage of the chart panel width", GroupName = "Profile", Order = 1)]
        public double ProfileWidthPercent { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Display Boundary Line", Description = "Draw the profile's own HIGH and LOW rows as horizontal lines from the start of the trailing window out to the profile's own pivot", GroupName = "Profile", Order = 2)]
        public bool ShowBoundaryLine { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Boundary Line Width (px)", Description = "Width in pixels of the extended Boundary and Mid lines", GroupName = "Profile", Order = 3)]
        public int BoundaryLineWidthPixels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Boundary Line Style", Description = "Dash style of the extended Boundary and Mid lines", GroupName = "Profile", Order = 4)]
        public DashStyleHelper BoundaryLineStyle { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Boundary Line Opacity", Description = "Opacity of the extended Boundary and Mid lines", GroupName = "Profile", Order = 5)]
        public double BoundaryLineOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Display Boundary Labels", Description = "Show the HIGH/LOW price tags at the profile's own top and bottom rows; independent of Display Boundary Line", GroupName = "Profile", Order = 6)]
        public bool ShowBoundaryLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Display Mid Line", Description = "Draw a horizontal line at the midpoint of the profile's HIGH-LOW price range ((HIGH+LOW)/2), from the start of the trailing window out to the profile's own pivot - shares Boundary Line Width/Style/Opacity and the Profile color", GroupName = "Profile", Order = 7)]
        public bool ShowMidLine { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Display Mid Label", Description = "Show the MID price tag at the profile's midpoint price; independent of Display Mid Line", GroupName = "Profile", Order = 8)]
        public bool ShowMidLabel { get; set; }

        // ----- Value Area -----

        [NinjaScriptProperty]
        [Display(Name = "Display Value Area", Description = "In Normal Display Mode, recolor rows inside the Value Area using Value Area color/opacity instead of the flat Profile color (no effect in Net Bid/Ask mode); also gates Display Value Area Labels", GroupName = "Value Area", Order = 1)]
        public bool ShowValueArea { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Value Area (%)", Description = "Percentage of the window's total (buy+sell) dollar volume contained in the Value Area", GroupName = "Value Area", Order = 2)]
        public double ValueAreaPercent { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Value Area Opacity", Description = "Opacity of Value Area rows when recolored in Normal Display Mode (requires Display Value Area)", GroupName = "Value Area", Order = 3)]
        public double ValueAreaOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Shade Value Area", Description = "Draw a translucent background band across the Value Area's price range", GroupName = "Value Area", Order = 4)]
        public bool ShadeValueArea { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Value Area Shade Opacity", Description = "Opacity of the Value Area background band (requires Shade Value Area)", GroupName = "Value Area", Order = 5)]
        public double ValueAreaShadeOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Extend Value Area Line", Description = "Draw the Value Area High and Low as horizontal lines from the start of the trailing window out to the profile's own pivot", GroupName = "Value Area", Order = 6)]
        public bool ExtendValueAreaLine { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Value Area Line Width (px)", Description = "Width in pixels of the extended Value Area lines", GroupName = "Value Area", Order = 7)]
        public int ValueAreaLineWidthPixels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Value Area Line Style", Description = "Dash style of the extended Value Area lines", GroupName = "Value Area", Order = 8)]
        public DashStyleHelper ValueAreaLineStyle { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Value Area Line Opacity", Description = "Opacity of the extended Value Area lines", GroupName = "Value Area", Order = 9)]
        public double ValueAreaLineOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Display Value Area Labels", Description = "Show the VAH/VAL price tags (requires Display Value Area); independent of Extend Value Area Line", GroupName = "Value Area", Order = 10)]
        public bool ShowValueAreaLabels { get; set; }

        // ----- Secondary Value Area -----

        [NinjaScriptProperty]
        [Display(Name = "Display Secondary Value Area", Description = "In Normal Display Mode, recolor rows inside the Secondary Value Area using Secondary Value Area color/opacity instead of the flat Profile color (no effect in Net Bid/Ask mode); also gates Display Secondary Value Area Labels", GroupName = "Secondary Value Area", Order = 1)]
        public bool ShowSecondaryValueArea { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Secondary Value Area (%)", Description = "Percentage of total (buy+sell) dollar volume contained in the Secondary Value Area", GroupName = "Secondary Value Area", Order = 2)]
        public double SecondaryValueAreaPercent { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Secondary Value Area From Primary", Description = "When enabled, Secondary Value Area (%) is taken against the Primary Value Area's own volume and price range instead of the whole profile's", GroupName = "Secondary Value Area", Order = 3)]
        public bool SecondaryValueAreaFromPrimary { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Secondary Value Area Opacity", Description = "Opacity of Secondary Value Area rows when recolored in Normal Display Mode (requires Display Secondary Value Area)", GroupName = "Secondary Value Area", Order = 4)]
        public double SecondaryValueAreaOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Shade Secondary Value Area", Description = "Draw a translucent background band across the Secondary Value Area's price range", GroupName = "Secondary Value Area", Order = 5)]
        public bool ShadeSecondaryValueArea { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Secondary Value Area Shade Opacity", Description = "Opacity of the Secondary Value Area background band (requires Shade Secondary Value Area)", GroupName = "Secondary Value Area", Order = 6)]
        public double SecondaryValueAreaShadeOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Extend Secondary Value Area Line", Description = "Draw the Secondary Value Area High and Low as horizontal lines from the start of the trailing window out to the profile's own pivot", GroupName = "Secondary Value Area", Order = 7)]
        public bool ExtendSecondaryValueAreaLine { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Secondary Value Area Line Width (px)", Description = "Width in pixels of the extended Secondary Value Area lines", GroupName = "Secondary Value Area", Order = 8)]
        public int SecondaryValueAreaLineWidthPixels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Secondary Value Area Line Style", Description = "Dash style of the extended Secondary Value Area lines", GroupName = "Secondary Value Area", Order = 9)]
        public DashStyleHelper SecondaryValueAreaLineStyle { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Secondary Value Area Line Opacity", Description = "Opacity of the extended Secondary Value Area lines", GroupName = "Secondary Value Area", Order = 10)]
        public double SecondaryValueAreaLineOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Display Secondary Value Area Labels", Description = "Show the SVAH/SVAL price tags (requires Display Secondary Value Area); independent of Extend Secondary Value Area Line", GroupName = "Secondary Value Area", Order = 11)]
        public bool ShowSecondaryValueAreaLabels { get; set; }

        // ----- Point Of Control -----

        [NinjaScriptProperty]
        [Display(Name = "Display POC", Description = "In Normal Display Mode, recolor the POC row using POC color/opacity instead of the flat Profile color (no effect in Net Bid/Ask mode); also gates Display POC Labels", GroupName = "Point Of Control", Order = 1)]
        public bool ShowPOC { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "POC Opacity", Description = "Opacity of the POC row when recolored in Normal Display Mode (requires Display POC)", GroupName = "Point Of Control", Order = 2)]
        public double POCOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Shade POC", Description = "Draw a translucent background band across the POC row - same shading system as Value Area / Secondary Value Area", GroupName = "Point Of Control", Order = 3)]
        public bool ShadePOC { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "POC Shade Opacity", Description = "Opacity of the POC background band (requires Shade POC)", GroupName = "Point Of Control", Order = 4)]
        public double POCShadeOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Display POC Line", Description = "Draw a line at the POC price, from the start of the trailing window out to the profile's own pivot; independent of Display POC", GroupName = "Point Of Control", Order = 5)]
        public bool ShowPOCLine { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "POC Line Width (px)", Description = "Width in pixels of the POC line", GroupName = "Point Of Control", Order = 6)]
        public int POCLineWidthPixels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "POC Line Style", Description = "Dash style of the POC line", GroupName = "Point Of Control", Order = 7)]
        public DashStyleHelper POCLineStyle { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "POC Line Opacity", Description = "Opacity of the POC line", GroupName = "Point Of Control", Order = 8)]
        public double POCLineOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Display POC Labels", Description = "Show the POC price tag; independent of Display POC / Display POC Line", GroupName = "Point Of Control", Order = 9)]
        public bool ShowPOCLabels { get; set; }

        // ----- High Volume Nodes -----

        [NinjaScriptProperty]
        [Display(Name = "Display High Volume Nodes", Description = "Draw a translucent background band across each of the Node Count highest total-dollar-volume peak clusters - same shading system as Value Area / Secondary Value Area / POC", GroupName = "High Volume Nodes", Order = 1)]
        public bool ShowHighVolumeNodes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Node Count", Description = "Number of highest total-dollar-volume peak clusters isolated - a peak is a row whose volume is a local maximum, not simply the single highest rows", GroupName = "High Volume Nodes", Order = 2)]
        public int HighVolumeNodeCount { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "HVN Cluster Expansion (%)", Description = "From each isolated peak, the shaded band expands outward in both directions as long as volume stays at or above this percentage of the peak's own volume - turns a single peak row into the tapered band a real High Volume Node zone should look like", GroupName = "High Volume Nodes", Order = 3)]
        public double HighVolumeNodeClusterExpansionPercent { get; set; }

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name = "Min HVN Spacing (rows)", Description = "Minimum number of rows required between two isolated peaks, so two nearby bumps on the same underlying peak don't both count as separate nodes", GroupName = "High Volume Nodes", Order = 4)]
        public int HighVolumeNodeMinSpacingRows { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "High Volume Node Shade Opacity", Description = "Opacity of each High Volume Node background band (requires Display High Volume Nodes)", GroupName = "High Volume Nodes", Order = 5)]
        public double HighVolumeNodeShadeOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Extend High Volume Node Lines", Description = "Draw a line for each of the top Node Count peaks from the start of the trailing window out to the profile's own pivot - thickness dynamically matches that row's own bar height rather than a fixed width", GroupName = "High Volume Nodes", Order = 6)]
        public bool ExtendHighVolumeNodeLines { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "High Volume Node Line Opacity", Description = "Opacity of the extended High Volume Node lines", GroupName = "High Volume Nodes", Order = 7)]
        public double HighVolumeNodeLineOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Display High Volume Node Labels", Description = "Show the HVN1/HVN2/... price tags (rank 1 = highest peak volume; requires Display High Volume Nodes); independent of Extend High Volume Node Lines", GroupName = "High Volume Nodes", Order = 8)]
        public bool ShowHighVolumeNodeLabels { get; set; }

        // ----- Dominant Levels -----

        [NinjaScriptProperty]
        [Display(Name = "Highlight Dominant Levels", Description = "Show each row's delta% (|buy-sell| / total) as text once it clears Minimum Dominant Threshold (%); rows below the threshold show nothing", GroupName = "Dominant Levels", Order = 1)]
        public bool HighlightDominantLevels { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Minimum Dominant Threshold (%)", Description = "Minimum delta% (|buy-sell| / total) a row must reach before its dominant score text is drawn at all", GroupName = "Dominant Levels", Order = 2)]
        public double MinimumDominantThresholdPercent { get; set; }

        // ----- Labels -----

        [NinjaScriptProperty]
        [TypeConverter(typeof(EGInterestProfileFontFamilyConverter))]
        [Display(Name = "Label Font Family", Description = "Font family shared by every price tag label (HIGH/LOW, MID, POC, VAH/VAL, SVAH/SVAL, HVN#) and the Dominant Levels delta% readout", GroupName = "Labels", Order = 1)]
        public string LabelFontFamily { get; set; }

        [NinjaScriptProperty]
        [TypeConverter(typeof(EGInterestProfileFontSizeConverter))]
        [Display(Name = "Label Font Size", Description = "Font size shared by every price tag label (HIGH/LOW, MID, POC, VAH/VAL, SVAH/SVAL, HVN#) and the Dominant Levels delta% readout", GroupName = "Labels", Order = 2)]
        public double LabelFontSize { get; set; }

        // ----- Colors -----

        private System.Windows.Media.Brush profileColor;
        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Profile", Description = "Color of the HIGH/LOW Boundary line, Mid line, and their labels, and every row's bar when Display Mode is Normal and no Value Area / Secondary Value Area / POC recolor applies", GroupName = "Colors", Order = 1)]
        public System.Windows.Media.Brush ProfileColor
        {
            get { return profileColor; }
            set { profileColor = Frz(value); }
        }

        [Browsable(false)]
        public string ProfileColorSerializable
        {
            get { return Serialize.BrushToString(ProfileColor); }
            set { ProfileColor = Serialize.StringToBrush(value); }
        }

        private System.Windows.Media.Brush buyColor;
        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Net Buy Color", Description = "Bar color for levels where total dollar volume favors buyers (Display Mode: Net Bid/Ask)", GroupName = "Colors", Order = 2)]
        public System.Windows.Media.Brush BuyColor
        {
            get { return buyColor; }
            set { buyColor = Frz(value); }
        }

        [Browsable(false)]
        public string BuyColorSerializable
        {
            get { return Serialize.BrushToString(BuyColor); }
            set { BuyColor = Serialize.StringToBrush(value); }
        }

        private System.Windows.Media.Brush sellColor;
        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Net Sell Color", Description = "Bar color for levels where total dollar volume favors sellers (Display Mode: Net Bid/Ask)", GroupName = "Colors", Order = 3)]
        public System.Windows.Media.Brush SellColor
        {
            get { return sellColor; }
            set { sellColor = Frz(value); }
        }

        [Browsable(false)]
        public string SellColorSerializable
        {
            get { return Serialize.BrushToString(SellColor); }
            set { SellColor = Serialize.StringToBrush(value); }
        }

        private System.Windows.Media.Brush dominantColor;
        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Dominant Level Text", Description = "Color of the Dominant Levels delta% readout text", GroupName = "Colors", Order = 4)]
        public System.Windows.Media.Brush DominantColor
        {
            get { return dominantColor; }
            set { dominantColor = Frz(value); }
        }

        [Browsable(false)]
        public string DominantColorSerializable
        {
            get { return Serialize.BrushToString(DominantColor); }
            set { DominantColor = Serialize.StringToBrush(value); }
        }

        private System.Windows.Media.Brush valueAreaColor;
        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Value Area", Description = "Color of the Value Area background band, extended lines, and (Normal Display Mode) recolored bars", GroupName = "Colors", Order = 5)]
        public System.Windows.Media.Brush ValueAreaColor
        {
            get { return valueAreaColor; }
            set { valueAreaColor = Frz(value); }
        }

        [Browsable(false)]
        public string ValueAreaColorSerializable
        {
            get { return Serialize.BrushToString(ValueAreaColor); }
            set { ValueAreaColor = Serialize.StringToBrush(value); }
        }

        private System.Windows.Media.Brush secondaryValueAreaColor;
        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Secondary Value Area", Description = "Color of the Secondary Value Area background band, extended lines, and (Normal Display Mode) recolored bars", GroupName = "Colors", Order = 6)]
        public System.Windows.Media.Brush SecondaryValueAreaColor
        {
            get { return secondaryValueAreaColor; }
            set { secondaryValueAreaColor = Frz(value); }
        }

        [Browsable(false)]
        public string SecondaryValueAreaColorSerializable
        {
            get { return Serialize.BrushToString(SecondaryValueAreaColor); }
            set { SecondaryValueAreaColor = Serialize.StringToBrush(value); }
        }

        private System.Windows.Media.Brush pocColor;
        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "POC", Description = "Color of the POC background band, line, label, and (Normal Display Mode) recolored bar", GroupName = "Colors", Order = 7)]
        public System.Windows.Media.Brush POCColor
        {
            get { return pocColor; }
            set { pocColor = Frz(value); }
        }

        [Browsable(false)]
        public string POCColorSerializable
        {
            get { return Serialize.BrushToString(POCColor); }
            set { POCColor = Serialize.StringToBrush(value); }
        }

        private System.Windows.Media.Brush highVolumeNodeColor;
        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "High Volume Node", Description = "Color of the High Volume Node background bands and their extended lines", GroupName = "Colors", Order = 8)]
        public System.Windows.Media.Brush HighVolumeNodeColor
        {
            get { return highVolumeNodeColor; }
            set { highVolumeNodeColor = Frz(value); }
        }

        [Browsable(false)]
        public string HighVolumeNodeColorSerializable
        {
            get { return Serialize.BrushToString(HighVolumeNodeColor); }
            set { HighVolumeNodeColor = Serialize.StringToBrush(value); }
        }

        #endregion
    }

    public enum EGInterestProfileSessionFilter { RTH, ETH }

    public enum EGInterestProfileAnchorMode { CurrentBar, ChartEdge }

    public enum EGInterestProfileDisplayMode { NetBidAsk, Normal }

    public class EGInterestProfileFontFamilyConverter : TypeConverter
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

    public class EGInterestProfileFontSizeConverter : TypeConverter
    {
        private static readonly StandardValuesCollection Values = new StandardValuesCollection(
            new double[] { 6, 7, 8, 9, 10, 11, 12, 13, 14, 16, 18, 20, 22, 24, 28, 32, 36, 48, 72 });

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
		private EducatedGambling.EGInterestProfile[] cacheEGInterestProfile;
		public EducatedGambling.EGInterestProfile EGInterestProfile(int trailingDays, int priceGroupingTicks, int rightOffsetPixels, int profileLabelOffsetPixels, double profileWidthPercent, bool showBoundaryLine, int boundaryLineWidthPixels, DashStyleHelper boundaryLineStyle, double boundaryLineOpacity, bool showBoundaryLabels, bool showMidLine, bool showMidLabel, bool showValueArea, double valueAreaPercent, double valueAreaOpacity, bool shadeValueArea, double valueAreaShadeOpacity, bool extendValueAreaLine, int valueAreaLineWidthPixels, DashStyleHelper valueAreaLineStyle, double valueAreaLineOpacity, bool showValueAreaLabels, bool showSecondaryValueArea, double secondaryValueAreaPercent, bool secondaryValueAreaFromPrimary, double secondaryValueAreaOpacity, bool shadeSecondaryValueArea, double secondaryValueAreaShadeOpacity, bool extendSecondaryValueAreaLine, int secondaryValueAreaLineWidthPixels, DashStyleHelper secondaryValueAreaLineStyle, double secondaryValueAreaLineOpacity, bool showSecondaryValueAreaLabels, bool showPOC, double pOCOpacity, bool shadePOC, double pOCShadeOpacity, bool showPOCLine, int pOCLineWidthPixels, DashStyleHelper pOCLineStyle, double pOCLineOpacity, bool showPOCLabels, bool showHighVolumeNodes, int highVolumeNodeCount, double highVolumeNodeClusterExpansionPercent, int highVolumeNodeMinSpacingRows, double highVolumeNodeShadeOpacity, bool extendHighVolumeNodeLines, double highVolumeNodeLineOpacity, bool showHighVolumeNodeLabels, bool highlightDominantLevels, double minimumDominantThresholdPercent, string labelFontFamily, double labelFontSize, System.Windows.Media.Brush profileColor, System.Windows.Media.Brush buyColor, System.Windows.Media.Brush sellColor, System.Windows.Media.Brush dominantColor, System.Windows.Media.Brush valueAreaColor, System.Windows.Media.Brush secondaryValueAreaColor, System.Windows.Media.Brush pOCColor, System.Windows.Media.Brush highVolumeNodeColor)
		{
			return EGInterestProfile(Input, trailingDays, priceGroupingTicks, rightOffsetPixels, profileLabelOffsetPixels, profileWidthPercent, showBoundaryLine, boundaryLineWidthPixels, boundaryLineStyle, boundaryLineOpacity, showBoundaryLabels, showMidLine, showMidLabel, showValueArea, valueAreaPercent, valueAreaOpacity, shadeValueArea, valueAreaShadeOpacity, extendValueAreaLine, valueAreaLineWidthPixels, valueAreaLineStyle, valueAreaLineOpacity, showValueAreaLabels, showSecondaryValueArea, secondaryValueAreaPercent, secondaryValueAreaFromPrimary, secondaryValueAreaOpacity, shadeSecondaryValueArea, secondaryValueAreaShadeOpacity, extendSecondaryValueAreaLine, secondaryValueAreaLineWidthPixels, secondaryValueAreaLineStyle, secondaryValueAreaLineOpacity, showSecondaryValueAreaLabels, showPOC, pOCOpacity, shadePOC, pOCShadeOpacity, showPOCLine, pOCLineWidthPixels, pOCLineStyle, pOCLineOpacity, showPOCLabels, showHighVolumeNodes, highVolumeNodeCount, highVolumeNodeClusterExpansionPercent, highVolumeNodeMinSpacingRows, highVolumeNodeShadeOpacity, extendHighVolumeNodeLines, highVolumeNodeLineOpacity, showHighVolumeNodeLabels, highlightDominantLevels, minimumDominantThresholdPercent, labelFontFamily, labelFontSize, profileColor, buyColor, sellColor, dominantColor, valueAreaColor, secondaryValueAreaColor, pOCColor, highVolumeNodeColor);
		}

		public EducatedGambling.EGInterestProfile EGInterestProfile(ISeries<double> input, int trailingDays, int priceGroupingTicks, int rightOffsetPixels, int profileLabelOffsetPixels, double profileWidthPercent, bool showBoundaryLine, int boundaryLineWidthPixels, DashStyleHelper boundaryLineStyle, double boundaryLineOpacity, bool showBoundaryLabels, bool showMidLine, bool showMidLabel, bool showValueArea, double valueAreaPercent, double valueAreaOpacity, bool shadeValueArea, double valueAreaShadeOpacity, bool extendValueAreaLine, int valueAreaLineWidthPixels, DashStyleHelper valueAreaLineStyle, double valueAreaLineOpacity, bool showValueAreaLabels, bool showSecondaryValueArea, double secondaryValueAreaPercent, bool secondaryValueAreaFromPrimary, double secondaryValueAreaOpacity, bool shadeSecondaryValueArea, double secondaryValueAreaShadeOpacity, bool extendSecondaryValueAreaLine, int secondaryValueAreaLineWidthPixels, DashStyleHelper secondaryValueAreaLineStyle, double secondaryValueAreaLineOpacity, bool showSecondaryValueAreaLabels, bool showPOC, double pOCOpacity, bool shadePOC, double pOCShadeOpacity, bool showPOCLine, int pOCLineWidthPixels, DashStyleHelper pOCLineStyle, double pOCLineOpacity, bool showPOCLabels, bool showHighVolumeNodes, int highVolumeNodeCount, double highVolumeNodeClusterExpansionPercent, int highVolumeNodeMinSpacingRows, double highVolumeNodeShadeOpacity, bool extendHighVolumeNodeLines, double highVolumeNodeLineOpacity, bool showHighVolumeNodeLabels, bool highlightDominantLevels, double minimumDominantThresholdPercent, string labelFontFamily, double labelFontSize, System.Windows.Media.Brush profileColor, System.Windows.Media.Brush buyColor, System.Windows.Media.Brush sellColor, System.Windows.Media.Brush dominantColor, System.Windows.Media.Brush valueAreaColor, System.Windows.Media.Brush secondaryValueAreaColor, System.Windows.Media.Brush pOCColor, System.Windows.Media.Brush highVolumeNodeColor)
		{
			if (cacheEGInterestProfile != null)
				for (int idx = 0; idx < cacheEGInterestProfile.Length; idx++)
					if (cacheEGInterestProfile[idx] != null && cacheEGInterestProfile[idx].TrailingDays == trailingDays && cacheEGInterestProfile[idx].PriceGroupingTicks == priceGroupingTicks && cacheEGInterestProfile[idx].RightOffsetPixels == rightOffsetPixels && cacheEGInterestProfile[idx].ProfileLabelOffsetPixels == profileLabelOffsetPixels && cacheEGInterestProfile[idx].ProfileWidthPercent == profileWidthPercent && cacheEGInterestProfile[idx].ShowBoundaryLine == showBoundaryLine && cacheEGInterestProfile[idx].BoundaryLineWidthPixels == boundaryLineWidthPixels && cacheEGInterestProfile[idx].BoundaryLineStyle == boundaryLineStyle && cacheEGInterestProfile[idx].BoundaryLineOpacity == boundaryLineOpacity && cacheEGInterestProfile[idx].ShowBoundaryLabels == showBoundaryLabels && cacheEGInterestProfile[idx].ShowMidLine == showMidLine && cacheEGInterestProfile[idx].ShowMidLabel == showMidLabel && cacheEGInterestProfile[idx].ShowValueArea == showValueArea && cacheEGInterestProfile[idx].ValueAreaPercent == valueAreaPercent && cacheEGInterestProfile[idx].ValueAreaOpacity == valueAreaOpacity && cacheEGInterestProfile[idx].ShadeValueArea == shadeValueArea && cacheEGInterestProfile[idx].ValueAreaShadeOpacity == valueAreaShadeOpacity && cacheEGInterestProfile[idx].ExtendValueAreaLine == extendValueAreaLine && cacheEGInterestProfile[idx].ValueAreaLineWidthPixels == valueAreaLineWidthPixels && cacheEGInterestProfile[idx].ValueAreaLineStyle == valueAreaLineStyle && cacheEGInterestProfile[idx].ValueAreaLineOpacity == valueAreaLineOpacity && cacheEGInterestProfile[idx].ShowValueAreaLabels == showValueAreaLabels && cacheEGInterestProfile[idx].ShowSecondaryValueArea == showSecondaryValueArea && cacheEGInterestProfile[idx].SecondaryValueAreaPercent == secondaryValueAreaPercent && cacheEGInterestProfile[idx].SecondaryValueAreaFromPrimary == secondaryValueAreaFromPrimary && cacheEGInterestProfile[idx].SecondaryValueAreaOpacity == secondaryValueAreaOpacity && cacheEGInterestProfile[idx].ShadeSecondaryValueArea == shadeSecondaryValueArea && cacheEGInterestProfile[idx].SecondaryValueAreaShadeOpacity == secondaryValueAreaShadeOpacity && cacheEGInterestProfile[idx].ExtendSecondaryValueAreaLine == extendSecondaryValueAreaLine && cacheEGInterestProfile[idx].SecondaryValueAreaLineWidthPixels == secondaryValueAreaLineWidthPixels && cacheEGInterestProfile[idx].SecondaryValueAreaLineStyle == secondaryValueAreaLineStyle && cacheEGInterestProfile[idx].SecondaryValueAreaLineOpacity == secondaryValueAreaLineOpacity && cacheEGInterestProfile[idx].ShowSecondaryValueAreaLabels == showSecondaryValueAreaLabels && cacheEGInterestProfile[idx].ShowPOC == showPOC && cacheEGInterestProfile[idx].POCOpacity == pOCOpacity && cacheEGInterestProfile[idx].ShadePOC == shadePOC && cacheEGInterestProfile[idx].POCShadeOpacity == pOCShadeOpacity && cacheEGInterestProfile[idx].ShowPOCLine == showPOCLine && cacheEGInterestProfile[idx].POCLineWidthPixels == pOCLineWidthPixels && cacheEGInterestProfile[idx].POCLineStyle == pOCLineStyle && cacheEGInterestProfile[idx].POCLineOpacity == pOCLineOpacity && cacheEGInterestProfile[idx].ShowPOCLabels == showPOCLabels && cacheEGInterestProfile[idx].ShowHighVolumeNodes == showHighVolumeNodes && cacheEGInterestProfile[idx].HighVolumeNodeCount == highVolumeNodeCount && cacheEGInterestProfile[idx].HighVolumeNodeClusterExpansionPercent == highVolumeNodeClusterExpansionPercent && cacheEGInterestProfile[idx].HighVolumeNodeMinSpacingRows == highVolumeNodeMinSpacingRows && cacheEGInterestProfile[idx].HighVolumeNodeShadeOpacity == highVolumeNodeShadeOpacity && cacheEGInterestProfile[idx].ExtendHighVolumeNodeLines == extendHighVolumeNodeLines && cacheEGInterestProfile[idx].HighVolumeNodeLineOpacity == highVolumeNodeLineOpacity && cacheEGInterestProfile[idx].ShowHighVolumeNodeLabels == showHighVolumeNodeLabels && cacheEGInterestProfile[idx].HighlightDominantLevels == highlightDominantLevels && cacheEGInterestProfile[idx].MinimumDominantThresholdPercent == minimumDominantThresholdPercent && cacheEGInterestProfile[idx].LabelFontFamily == labelFontFamily && cacheEGInterestProfile[idx].LabelFontSize == labelFontSize && cacheEGInterestProfile[idx].ProfileColor == profileColor && cacheEGInterestProfile[idx].BuyColor == buyColor && cacheEGInterestProfile[idx].SellColor == sellColor && cacheEGInterestProfile[idx].DominantColor == dominantColor && cacheEGInterestProfile[idx].ValueAreaColor == valueAreaColor && cacheEGInterestProfile[idx].SecondaryValueAreaColor == secondaryValueAreaColor && cacheEGInterestProfile[idx].POCColor == pOCColor && cacheEGInterestProfile[idx].HighVolumeNodeColor == highVolumeNodeColor && cacheEGInterestProfile[idx].EqualsInput(input))
						return cacheEGInterestProfile[idx];
			return CacheIndicator<EducatedGambling.EGInterestProfile>(new EducatedGambling.EGInterestProfile(){ TrailingDays = trailingDays, PriceGroupingTicks = priceGroupingTicks, RightOffsetPixels = rightOffsetPixels, ProfileLabelOffsetPixels = profileLabelOffsetPixels, ProfileWidthPercent = profileWidthPercent, ShowBoundaryLine = showBoundaryLine, BoundaryLineWidthPixels = boundaryLineWidthPixels, BoundaryLineStyle = boundaryLineStyle, BoundaryLineOpacity = boundaryLineOpacity, ShowBoundaryLabels = showBoundaryLabels, ShowMidLine = showMidLine, ShowMidLabel = showMidLabel, ShowValueArea = showValueArea, ValueAreaPercent = valueAreaPercent, ValueAreaOpacity = valueAreaOpacity, ShadeValueArea = shadeValueArea, ValueAreaShadeOpacity = valueAreaShadeOpacity, ExtendValueAreaLine = extendValueAreaLine, ValueAreaLineWidthPixels = valueAreaLineWidthPixels, ValueAreaLineStyle = valueAreaLineStyle, ValueAreaLineOpacity = valueAreaLineOpacity, ShowValueAreaLabels = showValueAreaLabels, ShowSecondaryValueArea = showSecondaryValueArea, SecondaryValueAreaPercent = secondaryValueAreaPercent, SecondaryValueAreaFromPrimary = secondaryValueAreaFromPrimary, SecondaryValueAreaOpacity = secondaryValueAreaOpacity, ShadeSecondaryValueArea = shadeSecondaryValueArea, SecondaryValueAreaShadeOpacity = secondaryValueAreaShadeOpacity, ExtendSecondaryValueAreaLine = extendSecondaryValueAreaLine, SecondaryValueAreaLineWidthPixels = secondaryValueAreaLineWidthPixels, SecondaryValueAreaLineStyle = secondaryValueAreaLineStyle, SecondaryValueAreaLineOpacity = secondaryValueAreaLineOpacity, ShowSecondaryValueAreaLabels = showSecondaryValueAreaLabels, ShowPOC = showPOC, POCOpacity = pOCOpacity, ShadePOC = shadePOC, POCShadeOpacity = pOCShadeOpacity, ShowPOCLine = showPOCLine, POCLineWidthPixels = pOCLineWidthPixels, POCLineStyle = pOCLineStyle, POCLineOpacity = pOCLineOpacity, ShowPOCLabels = showPOCLabels, ShowHighVolumeNodes = showHighVolumeNodes, HighVolumeNodeCount = highVolumeNodeCount, HighVolumeNodeClusterExpansionPercent = highVolumeNodeClusterExpansionPercent, HighVolumeNodeMinSpacingRows = highVolumeNodeMinSpacingRows, HighVolumeNodeShadeOpacity = highVolumeNodeShadeOpacity, ExtendHighVolumeNodeLines = extendHighVolumeNodeLines, HighVolumeNodeLineOpacity = highVolumeNodeLineOpacity, ShowHighVolumeNodeLabels = showHighVolumeNodeLabels, HighlightDominantLevels = highlightDominantLevels, MinimumDominantThresholdPercent = minimumDominantThresholdPercent, LabelFontFamily = labelFontFamily, LabelFontSize = labelFontSize, ProfileColor = profileColor, BuyColor = buyColor, SellColor = sellColor, DominantColor = dominantColor, ValueAreaColor = valueAreaColor, SecondaryValueAreaColor = secondaryValueAreaColor, POCColor = pOCColor, HighVolumeNodeColor = highVolumeNodeColor }, input, ref cacheEGInterestProfile);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.EducatedGambling.EGInterestProfile EGInterestProfile(int trailingDays, int priceGroupingTicks, int rightOffsetPixels, int profileLabelOffsetPixels, double profileWidthPercent, bool showBoundaryLine, int boundaryLineWidthPixels, DashStyleHelper boundaryLineStyle, double boundaryLineOpacity, bool showBoundaryLabels, bool showMidLine, bool showMidLabel, bool showValueArea, double valueAreaPercent, double valueAreaOpacity, bool shadeValueArea, double valueAreaShadeOpacity, bool extendValueAreaLine, int valueAreaLineWidthPixels, DashStyleHelper valueAreaLineStyle, double valueAreaLineOpacity, bool showValueAreaLabels, bool showSecondaryValueArea, double secondaryValueAreaPercent, bool secondaryValueAreaFromPrimary, double secondaryValueAreaOpacity, bool shadeSecondaryValueArea, double secondaryValueAreaShadeOpacity, bool extendSecondaryValueAreaLine, int secondaryValueAreaLineWidthPixels, DashStyleHelper secondaryValueAreaLineStyle, double secondaryValueAreaLineOpacity, bool showSecondaryValueAreaLabels, bool showPOC, double pOCOpacity, bool shadePOC, double pOCShadeOpacity, bool showPOCLine, int pOCLineWidthPixels, DashStyleHelper pOCLineStyle, double pOCLineOpacity, bool showPOCLabels, bool showHighVolumeNodes, int highVolumeNodeCount, double highVolumeNodeClusterExpansionPercent, int highVolumeNodeMinSpacingRows, double highVolumeNodeShadeOpacity, bool extendHighVolumeNodeLines, double highVolumeNodeLineOpacity, bool showHighVolumeNodeLabels, bool highlightDominantLevels, double minimumDominantThresholdPercent, string labelFontFamily, double labelFontSize, System.Windows.Media.Brush profileColor, System.Windows.Media.Brush buyColor, System.Windows.Media.Brush sellColor, System.Windows.Media.Brush dominantColor, System.Windows.Media.Brush valueAreaColor, System.Windows.Media.Brush secondaryValueAreaColor, System.Windows.Media.Brush pOCColor, System.Windows.Media.Brush highVolumeNodeColor)
		{
			return indicator.EGInterestProfile(Input, trailingDays, priceGroupingTicks, rightOffsetPixels, profileLabelOffsetPixels, profileWidthPercent, showBoundaryLine, boundaryLineWidthPixels, boundaryLineStyle, boundaryLineOpacity, showBoundaryLabels, showMidLine, showMidLabel, showValueArea, valueAreaPercent, valueAreaOpacity, shadeValueArea, valueAreaShadeOpacity, extendValueAreaLine, valueAreaLineWidthPixels, valueAreaLineStyle, valueAreaLineOpacity, showValueAreaLabels, showSecondaryValueArea, secondaryValueAreaPercent, secondaryValueAreaFromPrimary, secondaryValueAreaOpacity, shadeSecondaryValueArea, secondaryValueAreaShadeOpacity, extendSecondaryValueAreaLine, secondaryValueAreaLineWidthPixels, secondaryValueAreaLineStyle, secondaryValueAreaLineOpacity, showSecondaryValueAreaLabels, showPOC, pOCOpacity, shadePOC, pOCShadeOpacity, showPOCLine, pOCLineWidthPixels, pOCLineStyle, pOCLineOpacity, showPOCLabels, showHighVolumeNodes, highVolumeNodeCount, highVolumeNodeClusterExpansionPercent, highVolumeNodeMinSpacingRows, highVolumeNodeShadeOpacity, extendHighVolumeNodeLines, highVolumeNodeLineOpacity, showHighVolumeNodeLabels, highlightDominantLevels, minimumDominantThresholdPercent, labelFontFamily, labelFontSize, profileColor, buyColor, sellColor, dominantColor, valueAreaColor, secondaryValueAreaColor, pOCColor, highVolumeNodeColor);
		}

		public Indicators.EducatedGambling.EGInterestProfile EGInterestProfile(ISeries<double> input , int trailingDays, int priceGroupingTicks, int rightOffsetPixels, int profileLabelOffsetPixels, double profileWidthPercent, bool showBoundaryLine, int boundaryLineWidthPixels, DashStyleHelper boundaryLineStyle, double boundaryLineOpacity, bool showBoundaryLabels, bool showMidLine, bool showMidLabel, bool showValueArea, double valueAreaPercent, double valueAreaOpacity, bool shadeValueArea, double valueAreaShadeOpacity, bool extendValueAreaLine, int valueAreaLineWidthPixels, DashStyleHelper valueAreaLineStyle, double valueAreaLineOpacity, bool showValueAreaLabels, bool showSecondaryValueArea, double secondaryValueAreaPercent, bool secondaryValueAreaFromPrimary, double secondaryValueAreaOpacity, bool shadeSecondaryValueArea, double secondaryValueAreaShadeOpacity, bool extendSecondaryValueAreaLine, int secondaryValueAreaLineWidthPixels, DashStyleHelper secondaryValueAreaLineStyle, double secondaryValueAreaLineOpacity, bool showSecondaryValueAreaLabels, bool showPOC, double pOCOpacity, bool shadePOC, double pOCShadeOpacity, bool showPOCLine, int pOCLineWidthPixels, DashStyleHelper pOCLineStyle, double pOCLineOpacity, bool showPOCLabels, bool showHighVolumeNodes, int highVolumeNodeCount, double highVolumeNodeClusterExpansionPercent, int highVolumeNodeMinSpacingRows, double highVolumeNodeShadeOpacity, bool extendHighVolumeNodeLines, double highVolumeNodeLineOpacity, bool showHighVolumeNodeLabels, bool highlightDominantLevels, double minimumDominantThresholdPercent, string labelFontFamily, double labelFontSize, System.Windows.Media.Brush profileColor, System.Windows.Media.Brush buyColor, System.Windows.Media.Brush sellColor, System.Windows.Media.Brush dominantColor, System.Windows.Media.Brush valueAreaColor, System.Windows.Media.Brush secondaryValueAreaColor, System.Windows.Media.Brush pOCColor, System.Windows.Media.Brush highVolumeNodeColor)
		{
			return indicator.EGInterestProfile(input, trailingDays, priceGroupingTicks, rightOffsetPixels, profileLabelOffsetPixels, profileWidthPercent, showBoundaryLine, boundaryLineWidthPixels, boundaryLineStyle, boundaryLineOpacity, showBoundaryLabels, showMidLine, showMidLabel, showValueArea, valueAreaPercent, valueAreaOpacity, shadeValueArea, valueAreaShadeOpacity, extendValueAreaLine, valueAreaLineWidthPixels, valueAreaLineStyle, valueAreaLineOpacity, showValueAreaLabels, showSecondaryValueArea, secondaryValueAreaPercent, secondaryValueAreaFromPrimary, secondaryValueAreaOpacity, shadeSecondaryValueArea, secondaryValueAreaShadeOpacity, extendSecondaryValueAreaLine, secondaryValueAreaLineWidthPixels, secondaryValueAreaLineStyle, secondaryValueAreaLineOpacity, showSecondaryValueAreaLabels, showPOC, pOCOpacity, shadePOC, pOCShadeOpacity, showPOCLine, pOCLineWidthPixels, pOCLineStyle, pOCLineOpacity, showPOCLabels, showHighVolumeNodes, highVolumeNodeCount, highVolumeNodeClusterExpansionPercent, highVolumeNodeMinSpacingRows, highVolumeNodeShadeOpacity, extendHighVolumeNodeLines, highVolumeNodeLineOpacity, showHighVolumeNodeLabels, highlightDominantLevels, minimumDominantThresholdPercent, labelFontFamily, labelFontSize, profileColor, buyColor, sellColor, dominantColor, valueAreaColor, secondaryValueAreaColor, pOCColor, highVolumeNodeColor);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.EducatedGambling.EGInterestProfile EGInterestProfile(int trailingDays, int priceGroupingTicks, int rightOffsetPixels, int profileLabelOffsetPixels, double profileWidthPercent, bool showBoundaryLine, int boundaryLineWidthPixels, DashStyleHelper boundaryLineStyle, double boundaryLineOpacity, bool showBoundaryLabels, bool showMidLine, bool showMidLabel, bool showValueArea, double valueAreaPercent, double valueAreaOpacity, bool shadeValueArea, double valueAreaShadeOpacity, bool extendValueAreaLine, int valueAreaLineWidthPixels, DashStyleHelper valueAreaLineStyle, double valueAreaLineOpacity, bool showValueAreaLabels, bool showSecondaryValueArea, double secondaryValueAreaPercent, bool secondaryValueAreaFromPrimary, double secondaryValueAreaOpacity, bool shadeSecondaryValueArea, double secondaryValueAreaShadeOpacity, bool extendSecondaryValueAreaLine, int secondaryValueAreaLineWidthPixels, DashStyleHelper secondaryValueAreaLineStyle, double secondaryValueAreaLineOpacity, bool showSecondaryValueAreaLabels, bool showPOC, double pOCOpacity, bool shadePOC, double pOCShadeOpacity, bool showPOCLine, int pOCLineWidthPixels, DashStyleHelper pOCLineStyle, double pOCLineOpacity, bool showPOCLabels, bool showHighVolumeNodes, int highVolumeNodeCount, double highVolumeNodeClusterExpansionPercent, int highVolumeNodeMinSpacingRows, double highVolumeNodeShadeOpacity, bool extendHighVolumeNodeLines, double highVolumeNodeLineOpacity, bool showHighVolumeNodeLabels, bool highlightDominantLevels, double minimumDominantThresholdPercent, string labelFontFamily, double labelFontSize, System.Windows.Media.Brush profileColor, System.Windows.Media.Brush buyColor, System.Windows.Media.Brush sellColor, System.Windows.Media.Brush dominantColor, System.Windows.Media.Brush valueAreaColor, System.Windows.Media.Brush secondaryValueAreaColor, System.Windows.Media.Brush pOCColor, System.Windows.Media.Brush highVolumeNodeColor)
		{
			return indicator.EGInterestProfile(Input, trailingDays, priceGroupingTicks, rightOffsetPixels, profileLabelOffsetPixels, profileWidthPercent, showBoundaryLine, boundaryLineWidthPixels, boundaryLineStyle, boundaryLineOpacity, showBoundaryLabels, showMidLine, showMidLabel, showValueArea, valueAreaPercent, valueAreaOpacity, shadeValueArea, valueAreaShadeOpacity, extendValueAreaLine, valueAreaLineWidthPixels, valueAreaLineStyle, valueAreaLineOpacity, showValueAreaLabels, showSecondaryValueArea, secondaryValueAreaPercent, secondaryValueAreaFromPrimary, secondaryValueAreaOpacity, shadeSecondaryValueArea, secondaryValueAreaShadeOpacity, extendSecondaryValueAreaLine, secondaryValueAreaLineWidthPixels, secondaryValueAreaLineStyle, secondaryValueAreaLineOpacity, showSecondaryValueAreaLabels, showPOC, pOCOpacity, shadePOC, pOCShadeOpacity, showPOCLine, pOCLineWidthPixels, pOCLineStyle, pOCLineOpacity, showPOCLabels, showHighVolumeNodes, highVolumeNodeCount, highVolumeNodeClusterExpansionPercent, highVolumeNodeMinSpacingRows, highVolumeNodeShadeOpacity, extendHighVolumeNodeLines, highVolumeNodeLineOpacity, showHighVolumeNodeLabels, highlightDominantLevels, minimumDominantThresholdPercent, labelFontFamily, labelFontSize, profileColor, buyColor, sellColor, dominantColor, valueAreaColor, secondaryValueAreaColor, pOCColor, highVolumeNodeColor);
		}

		public Indicators.EducatedGambling.EGInterestProfile EGInterestProfile(ISeries<double> input , int trailingDays, int priceGroupingTicks, int rightOffsetPixels, int profileLabelOffsetPixels, double profileWidthPercent, bool showBoundaryLine, int boundaryLineWidthPixels, DashStyleHelper boundaryLineStyle, double boundaryLineOpacity, bool showBoundaryLabels, bool showMidLine, bool showMidLabel, bool showValueArea, double valueAreaPercent, double valueAreaOpacity, bool shadeValueArea, double valueAreaShadeOpacity, bool extendValueAreaLine, int valueAreaLineWidthPixels, DashStyleHelper valueAreaLineStyle, double valueAreaLineOpacity, bool showValueAreaLabels, bool showSecondaryValueArea, double secondaryValueAreaPercent, bool secondaryValueAreaFromPrimary, double secondaryValueAreaOpacity, bool shadeSecondaryValueArea, double secondaryValueAreaShadeOpacity, bool extendSecondaryValueAreaLine, int secondaryValueAreaLineWidthPixels, DashStyleHelper secondaryValueAreaLineStyle, double secondaryValueAreaLineOpacity, bool showSecondaryValueAreaLabels, bool showPOC, double pOCOpacity, bool shadePOC, double pOCShadeOpacity, bool showPOCLine, int pOCLineWidthPixels, DashStyleHelper pOCLineStyle, double pOCLineOpacity, bool showPOCLabels, bool showHighVolumeNodes, int highVolumeNodeCount, double highVolumeNodeClusterExpansionPercent, int highVolumeNodeMinSpacingRows, double highVolumeNodeShadeOpacity, bool extendHighVolumeNodeLines, double highVolumeNodeLineOpacity, bool showHighVolumeNodeLabels, bool highlightDominantLevels, double minimumDominantThresholdPercent, string labelFontFamily, double labelFontSize, System.Windows.Media.Brush profileColor, System.Windows.Media.Brush buyColor, System.Windows.Media.Brush sellColor, System.Windows.Media.Brush dominantColor, System.Windows.Media.Brush valueAreaColor, System.Windows.Media.Brush secondaryValueAreaColor, System.Windows.Media.Brush pOCColor, System.Windows.Media.Brush highVolumeNodeColor)
		{
			return indicator.EGInterestProfile(input, trailingDays, priceGroupingTicks, rightOffsetPixels, profileLabelOffsetPixels, profileWidthPercent, showBoundaryLine, boundaryLineWidthPixels, boundaryLineStyle, boundaryLineOpacity, showBoundaryLabels, showMidLine, showMidLabel, showValueArea, valueAreaPercent, valueAreaOpacity, shadeValueArea, valueAreaShadeOpacity, extendValueAreaLine, valueAreaLineWidthPixels, valueAreaLineStyle, valueAreaLineOpacity, showValueAreaLabels, showSecondaryValueArea, secondaryValueAreaPercent, secondaryValueAreaFromPrimary, secondaryValueAreaOpacity, shadeSecondaryValueArea, secondaryValueAreaShadeOpacity, extendSecondaryValueAreaLine, secondaryValueAreaLineWidthPixels, secondaryValueAreaLineStyle, secondaryValueAreaLineOpacity, showSecondaryValueAreaLabels, showPOC, pOCOpacity, shadePOC, pOCShadeOpacity, showPOCLine, pOCLineWidthPixels, pOCLineStyle, pOCLineOpacity, showPOCLabels, showHighVolumeNodes, highVolumeNodeCount, highVolumeNodeClusterExpansionPercent, highVolumeNodeMinSpacingRows, highVolumeNodeShadeOpacity, extendHighVolumeNodeLines, highVolumeNodeLineOpacity, showHighVolumeNodeLabels, highlightDominantLevels, minimumDominantThresholdPercent, labelFontFamily, labelFontSize, profileColor, buyColor, sellColor, dominantColor, valueAreaColor, secondaryValueAreaColor, pOCColor, highVolumeNodeColor);
		}
	}
}

#endregion
