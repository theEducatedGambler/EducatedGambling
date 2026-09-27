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
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
#endregion

namespace NinjaTrader.NinjaScript.Indicators.EducatedGambling
{
    // Built on a single hidden Last 1-tick data series (same AddDataSeries pattern as
    // EGVolumeProfile / EGFootprintLadder), but with no buy/sell aggressor classification at
    // all - this profile tracks raw traded volume by price only, so it never needs bid/ask
    // (no OnMarketData Ask/Bid reads, no BarsArray[1].GetAsk/GetBid).
    //
    // "Rolling" means a trailing time window, not a session: every incoming trade is queued
    // with its own timestamp AND the primary-series bar index it belongs to, and trades older
    // than Lookback (hrs) - measured against the latest trade's own time, not wall-clock time,
    // so this also rolls correctly through historical replay - are evicted from the
    // price/volume dictionary as new ones arrive. There is no session/TradingHours concept
    // anywhere in this file.
    //
    // Direction: always right-anchored, growing left (toward the bars) - there is no Left/Right
    // Profile Alignment property. The pivot (0-volume edge, where every row's width starts from)
    // sits at anchorFarX; each row's bar then extends LEFTWARD from there, toward the actual
    // price action, by an amount proportional to its volume - the opposite of a plain
    // fixed-offset ladder that grows away from the bars.
    //
    // Profile Anchor controls where that pivot itself sits. Current Bar (default) computes it as
    // Right Offset (px) + Profile Width (%) out past the last bar's own x position - a bar-index
    // position, so scrolling the chart left eventually carries the profile out of view along with
    // that bar. Chart Edge pins the same pivot to ChartPanel.X + ChartPanel.W instead - a fixed
    // screen position, not a bar index - so the profile always hugs the chart's right wall
    // regardless of scroll position, same mechanism as EGVolumeProfile's "Most Right" Profile
    // Alignment option; Right Offset (px) is ignored in this mode. Side effect worth knowing:
    // since the price-tag column sits at anchorFarX + Profile-Label Offset (px), pinning
    // anchorFarX to the panel's own right edge pushes that whole column past the panel's render
    // bounds, so NT8 clips it - the price labels end up effectively invisible in Chart Edge mode
    // without needing a separate toggle for it.
    //
    // Point of Control has its own category, split into three independent toggles - Display POC
    // (row highlight), Display POC Line (the extended line), Display POC Labels (the price tag)
    // - the same three-way split the Profile group's boundary (HIGH/LOW) settings and the
    // Value/Secondary Value Area groups already use (row highlight vs Extend .. Line vs Display
    // .. Labels). Every line (Boundary, POC, VA, SVA) is a NinjaTrader.Gui.Stroke with its own
    // Width/Style/Opacity property, so those support the same dash styles - except the High
    // Volume Node line, which has no Width/Style property at all: its thickness dynamically
    // matches that row's own bar height instead, so it reads as a continuation of the bar.
    //
    // High Volume Nodes isolate PEAKS (local maxima), not just the top N rows by raw volume -
    // see ComputeHighVolumeNodeClusters for the reasoning and algorithm.
    //
    // Labels: every price level (HIGH/LOW profile bounds, VAH/VAL, SVAH/SVAL, POC, HVN#) has
    // exactly ONE label, drawn once in the fixed price-tag column at anchorFarX + Profile-Label
    // Offset (px), sharing a single Label Font Family/Size (Labels group) across all of them.
    // Line toggles draw pure geometry (no text); label toggles are fully independent of their
    // corresponding line toggle - DrawPriceLabels is called unconditionally in OnRender, never
    // nested inside an `if (line-toggle)` block, so a label can be shown with the line off, or
    // the line shown with the label off, in any combination. HIGH/LOW/POC/VAH/VAL/SVAH/SVAL are
    // all exact members of the same rounded bucket grid, so two of them can coincide on the
    // identical price (e.g. a narrow profile where VAH and SVAH match) - DrawPriceLabels groups
    // by exact price and merges any such group into one "NAME1 / NAME2 xxxx.xx" line instead of
    // stacking labels on top of each other, using the color of whichever level ranks highest
    // (POC > Secondary Value Area > Value Area > Profile Boundaries). HVN labels are peaks, not
    // fixed structural levels, so they're deliberately left out of that merge.
    //
    // Rendering follows the OnRender positioning recipe in root CLAUDE.md (proven in
    // EGFootprintLadder / EGVolumeProfile): X is fixed pixel anchors derived from
    // ChartBars.ToIndex via GetXByBarIndex, never a bar index per price row. Y is
    // chartScale.GetYByValue(price) computed directly per price bucket. The configurable
    // Profile/Value Area/Secondary Value Area/High Volume Node colors are cached as DX brushes
    // in OnRenderTargetChanged (same architecture as EGVolumeProfile); the price-tag labels reuse
    // those same cached brushes (Profile for the top/bottom bounds, Value Area for VAH/VAL,
    // Secondary Value Area for SVAH/SVAL, POC for POC, High Volume Node for HVN#) rather than a
    // separate hardcoded color set, so a label always matches what it's pointing at.
    [CategoryOrder("Settings", 1)]
    [CategoryOrder("Profile", 2)]
    [CategoryOrder("Value Area", 3)]
    [CategoryOrder("Secondary Value Area", 4)]
    [CategoryOrder("Point Of Control", 5)]
    [CategoryOrder("High Volume Nodes", 6)]
    [CategoryOrder("Labels", 7)]
    [CategoryOrder("Colors", 8)]
    public class EGRollingVolumeProfile : Indicator
    {
        private struct TradeRecord
        {
            public DateTime Time;
            public double Bucket;
            public double Volume;
            public int BarIndex;
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

        // Safety cap on distinct price levels tracked at once (e.g. a huge intraday range at a
        // fine Tick Aggregation) - set generously above realistic need rather than guessing a
        // tight round number (see root CLAUDE.md "Silent-failure safety caps").
        private const int MaxTrackedBuckets = 5000;
        private const double EvictEpsilon = 1e-9;

        private const float LabelHeight = 18f;
        // Wide enough for the longest realistic combined label, e.g. "SVAH / VAH 30775.00".
        private const float LabelMaxWidth = 180f;

        private readonly Queue<TradeRecord> tradeQueue = new Queue<TradeRecord>();
        private readonly Dictionary<double, double> volumeByPrice = new Dictionary<double, double>();

        private SharpDX.Direct2D1.Brush profileBrushDx;
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
                Name = "EGRollingVolumeProfile";
                Description = "Ongoing volume profile over a trailing time window, continuously updated tick-by-tick and pinned at a fixed pixel offset from the current bar, growing toward the bars. POC, Value Area, Secondary Value Area, and High Volume Nodes, with price labels on the right. No session/trading-hours concept - purely a rolling lookback.";
                Calculate = Calculate.OnBarClose;
                IsOverlay = true;
                DisplayInDataBox = false;
                DrawOnPricePanel = true;
                PaintPriceMarkers = false;
                IsSuspendedWhileInactive = false;

                TickAggregation = 10;
                LookbackHours = 4.0;
                ProfileAnchor = EGRollingVolumeProfileAnchorMode.CurrentBar;
                RightOffsetPixels = 10;
                ProfileLabelOffsetPixels = 8;

                ProfileWidthPercent = 30;
                ProfileOpacity = 70;
                ShowBoundaryLine = false;
                BoundaryLineWidthPixels = 2;
                BoundaryLineStyle = DashStyleHelper.Solid;
                BoundaryLineOpacity = 100;
                ShowBoundaryLabels = true;

                ShowValueArea = true;
                ValueAreaPercent = 70;
                ValueAreaOpacity = 85;
                ExtendValueAreaLine = false;
                ValueAreaLineWidthPixels = 2;
                ValueAreaLineStyle = DashStyleHelper.Solid;
                ValueAreaLineOpacity = 100;
                ShowValueAreaLabels = false;

                ShowSecondaryValueArea = false;
                SecondaryValueAreaPercent = 40;
                SecondaryValueAreaFromPrimary = false;
                SecondaryValueAreaOpacity = 40;
                ExtendSecondaryValueAreaLine = false;
                SecondaryValueAreaLineWidthPixels = 2;
                SecondaryValueAreaLineStyle = DashStyleHelper.Solid;
                SecondaryValueAreaLineOpacity = 100;
                ShowSecondaryValueAreaLabels = false;

                ShowPOC = true;
                POCOpacity = 100;
                ShowPOCLine = true;
                POCLineWidthPixels = 2;
                POCLineStyle = DashStyleHelper.Dot;
                POCLineOpacity = 100;
                ShowPOCLabels = true;

                ShowHighVolumeNodes = false;
                HighVolumeNodeCount = 3;
                HighVolumeNodeClusterExpansionPercent = 50;
                HighVolumeNodeMinSpacingRows = 3;
                HighVolumeNodeOpacity = 90;
                ExtendHighVolumeNodeLines = false;
                HighVolumeNodeLineOpacity = 100;
                ShowHighVolumeNodeLabels = false;

                LabelFontFamily = "Consolas";
                LabelFontSize = 9;

                ProfileColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 70, 130, 180));
                ValueAreaColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 255, 165, 0));
                SecondaryValueAreaColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 135, 206, 235));
                POCColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 220, 20, 60));
                HighVolumeNodeColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 255, 215, 0));
            }
            else if (State == State.Configure)
            {
                AddDataSeries(Instrument.FullName, BarsPeriodType.Tick, 1);
            }
            else if (State == State.DataLoaded)
            {
                tradeQueue.Clear();
                volumeByPrice.Clear();
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
            // The hidden tick series is only acted on during State.Historical - historical
            // price/volume/time come from that same tick record. Once live, it keeps ticking in
            // the background but is ignored; OnMarketData below takes over instead.
            if (BarsInProgress == 1 && State == State.Historical)
                ProcessHistoricalTick();
        }

        private void ProcessHistoricalTick()
        {
            if (CurrentBars[1] < 0 || CurrentBars[0] < 0)
                return;

            double price = BarsArray[1].GetClose(CurrentBars[1]);
            double vol = BarsArray[1].GetVolume(CurrentBars[1]);
            DateTime time = BarsArray[1].GetTime(CurrentBars[1]);

            // CurrentBars[0] only advances when the primary bar closes (Calculate.OnBarClose),
            // so while a new bar is forming it still names the previous, already-closed bar - a
            // tick belongs to the next bar, not the one CurrentBars[0] currently names, once it
            // happens after that bar's own close timestamp. Same technique as
            // EGFootprintLadder.ProcessHistoricalTick - see root CLAUDE.md.
            int barIndex = (time <= Times[0][0]) ? CurrentBars[0] : CurrentBars[0] + 1;

            ProcessTrade(price, vol, time, barIndex);
        }

        protected override void OnMarketData(MarketDataEventArgs marketData)
        {
            if (State != State.Realtime || marketData.MarketDataType != MarketDataType.Last)
                return;

            if (CurrentBars[0] < 0)
                return;

            // A live trade always belongs to the currently-forming bar, so barIndex is just
            // CurrentBars[0] directly - no close-time comparison needed the way historical does.
            ProcessTrade(marketData.Price, marketData.Volume, marketData.Time, CurrentBars[0]);
        }

        private void ProcessTrade(double price, double vol, DateTime time, int barIndex)
        {
            double tickSize = Instrument.MasterInstrument.TickSize;
            double rowSize = tickSize * Math.Max(1, TickAggregation);
            double bucket = Instrument.MasterInstrument.RoundToTickSize(Math.Floor(price / rowSize) * rowSize);

            if (!volumeByPrice.ContainsKey(bucket) && volumeByPrice.Count >= MaxTrackedBuckets)
                return;

            double existing;
            volumeByPrice[bucket] = (volumeByPrice.TryGetValue(bucket, out existing) ? existing : 0) + vol;
            tradeQueue.Enqueue(new TradeRecord { Time = time, Bucket = bucket, Volume = vol, BarIndex = barIndex });

            EvictOld(time);
        }

        // Ages trades out once they fall outside the trailing Lookback (hrs) window, measured
        // against the most recent trade's own timestamp rather than wall-clock DateTime.Now, so
        // the window rolls correctly during historical replay too, not just live.
        private void EvictOld(DateTime latestTime)
        {
            while (tradeQueue.Count > 0)
            {
                TradeRecord oldest = tradeQueue.Peek();
                if ((latestTime - oldest.Time).TotalHours <= LookbackHours)
                    break;

                tradeQueue.Dequeue();

                double existing;
                if (volumeByPrice.TryGetValue(oldest.Bucket, out existing))
                {
                    double updated = existing - oldest.Volume;
                    if (updated <= EvictEpsilon)
                        volumeByPrice.Remove(oldest.Bucket);
                    else
                        volumeByPrice[oldest.Bucket] = updated;
                }
            }
        }

        // Same expanding-outward-from-POC algorithm as EGVolumeProfile.ComputeValueArea.
        private void ComputeValueArea(Dictionary<double, double> data, List<double> sortedBuckets, double pocPrice, double targetPercent, double totalVolume, out double low, out double high)
        {
            int pocIdx = sortedBuckets.IndexOf(pocPrice);
            int lowIdx = pocIdx;
            int highIdx = pocIdx;
            double target = totalVolume * Math.Max(0, Math.Min(100, targetPercent)) / 100.0;
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

        // When Secondary Value Area From Primary is on, the Secondary Value Area's target
        // percentage is taken against the Primary Value Area's own volume and price range
        // (nesting it inside VA) instead of the whole profile - same as EGVolumeProfile.
        private void ComputeSecondaryValueArea(Dictionary<double, double> data, List<double> buckets, double pocPrice,
            double vaLow, double vaHigh, double totalVolume, out double svaLow, out double svaHigh)
        {
            if (!SecondaryValueAreaFromPrimary)
            {
                ComputeValueArea(data, buckets, pocPrice, SecondaryValueAreaPercent, totalVolume, out svaLow, out svaHigh);
                return;
            }

            List<double> vaBuckets = buckets.Where(p => p >= vaLow && p <= vaHigh).ToList();
            double vaTotalVolume = vaBuckets.Sum(p => data[p]);
            ComputeValueArea(data, vaBuckets, pocPrice, SecondaryValueAreaPercent, vaTotalVolume, out svaLow, out svaHigh);
        }

        // Isolates the top Node Count local-maximum PEAKS (a row whose volume is >= both
        // neighbors, edge rows only needing to beat their one neighbor) rather than the top
        // Node Count individual rows by raw volume - the naive row-ranking approach tends to
        // just clump every slot around whichever single peak is tallest, since that peak's
        // immediate neighbors are also high-volume. Ranking peaks instead naturally spreads
        // selection across genuinely distinct bumps in the profile.
        //
        // rankedPeaks (rank 0 = highest peak volume) is what HVN#/extended-line ordering uses,
        // same as before. clusterMembers is every row belonging to any accepted peak's
        // contiguous band - used for row highlighting - built by expanding outward from each
        // peak while volume stays at/above HVN Cluster Expansion (%) of that peak's own volume,
        // which is what turns a single peak row into the tapered band a real HVN zone should
        // look like (naturally stops at a valley, since a valley's volume necessarily falls
        // below that threshold before the next peak's slope climbs back up).
        private static void ComputeHighVolumeNodeClusters(Dictionary<double, double> data, List<double> buckets, int peakCount, double expansionPercent, int minSpacingRows,
            out List<double> rankedPeaks, out HashSet<double> clusterMembers)
        {
            rankedPeaks = new List<double>();
            clusterMembers = new HashSet<double>();

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

            // Min HVN Spacing (rows) is expressed in rows, converted to a price distance using
            // the profile's own row size (the gap between any two adjacent buckets) so two
            // nearby bumps on the same underlying peak don't both get accepted as separate nodes.
            double rowSize = buckets.Count >= 2 ? Math.Abs(buckets[1] - buckets[0]) : 0;
            double minSpacingPrice = rowSize * Math.Max(0, minSpacingRows);

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

                clusterMembers.Add(peak);

                for (int i = peakIdx - 1; i >= 0; i--)
                {
                    if (data[buckets[i]] < floor) break;
                    clusterMembers.Add(buckets[i]);
                }

                for (int i = peakIdx + 1; i < buckets.Count; i++)
                {
                    if (data[buckets[i]] < floor) break;
                    clusterMembers.Add(buckets[i]);
                }
            }
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            if (Bars == null || ChartControl == null || IsInHitTest)
                return;

            int lastBarIndex = ChartBars.ToIndex;
            if (lastBarIndex < 0)
                return;

            if (volumeByPrice.Count == 0)
                return;

            if (profileBrushDx == null || valueAreaBrushDx == null || secondaryValueAreaBrushDx == null || pocBrushDx == null || highVolumeNodeBrushDx == null)
                return;

            double tickSize = Instrument.MasterInstrument.TickSize;
            double rowSize = tickSize * Math.Max(1, TickAggregation);

            // nearEdgeX is where the longest (highest-volume) bar's tip can reach - closest to
            // the actual bars. anchorFarX is the fixed pivot (0-volume edge) every row's bar
            // grows left FROM, out past the profile's own max width - this is what makes the
            // profile print toward the bars instead of away from them. Profile Anchor Chart Edge
            // pins anchorFarX to the chart panel's own right edge instead of the last bar's index
            // - a fixed screen position, so the profile stays visible when scrolling left - same
            // mechanism as EGVolumeProfile's Most Right alignment / EGInterestProfile's Chart Edge
            // anchor. Right Offset (px) is meaningless in that mode since there's no bar to offset
            // from, and it incidentally pushes the label column (anchorFarX + Profile-Label Offset
            // (px)) past the panel's render bounds, so NT8 clips the price labels out.
            float maxBarWidth = (float)(ProfileWidthPercent / 100.0 * ChartPanel.W);
            float anchorFarX;
            if (ProfileAnchor == EGRollingVolumeProfileAnchorMode.ChartEdge)
            {
                anchorFarX = ChartPanel.X + ChartPanel.W;
            }
            else
            {
                float nearEdgeX = chartControl.GetXByBarIndex(ChartBars, lastBarIndex) + RightOffsetPixels;
                anchorFarX = nearEdgeX + maxBarWidth;
            }

            // Every extended line (Boundary/POC/VA/SVA/HVN) shares the same span: from the start
            // of the rolling Lookback (hrs) window out to anchorFarX, the profile's own pivot -
            // never past it, and never the plain ChartPanel right edge (that overshot past the
            // pivot into the label column, which looked like the line stretching the wrong way).
            // Each queued trade already carries the primary-bar index it belongs to (tracked at
            // ingest time via the same CurrentBars[0]-based technique EGFootprintLadder uses), so
            // the oldest trade still in the window (tradeQueue's own front, guaranteed non-empty
            // here since volumeByPrice is non-empty) gives that boundary directly.
            int lookbackStartBar = tradeQueue.Peek().BarIndex;
            float lookbackStartX = chartControl.GetXByBarIndex(ChartBars, Math.Max(ChartBars.FromIndex, lookbackStartBar));

            List<double> buckets = volumeByPrice.Keys.OrderBy(p => p).ToList();
            double totalVolume = buckets.Sum(p => volumeByPrice[p]);
            if (totalVolume <= 0)
                return;

            double maxVolume = buckets.Max(p => volumeByPrice[p]);
            double pocPrice = buckets.OrderByDescending(p => volumeByPrice[p]).First();

            double vaLow, vaHigh;
            ComputeValueArea(volumeByPrice, buckets, pocPrice, ValueAreaPercent, totalVolume, out vaLow, out vaHigh);

            double svaLow, svaHigh;
            ComputeSecondaryValueArea(volumeByPrice, buckets, pocPrice, vaLow, vaHigh, totalVolume, out svaLow, out svaHigh);

            List<double> rankedHighVolumeNodes;
            HashSet<double> highVolumeNodeClusterMembers;
            ComputeHighVolumeNodeClusters(volumeByPrice, buckets, HighVolumeNodeCount, HighVolumeNodeClusterExpansionPercent, HighVolumeNodeMinSpacingRows,
                out rankedHighVolumeNodes, out highVolumeNodeClusterMembers);
            HashSet<double> highVolumeNodeSet = ShowHighVolumeNodes ? highVolumeNodeClusterMembers : null;

            foreach (double price in buckets)
            {
                if (price < chartScale.MinValue || price > chartScale.MaxValue)
                    continue;

                double vol = volumeByPrice[price];
                float y = chartScale.GetYByValue(price);
                float rowTop = chartScale.GetYByValue(price + rowSize / 2.0);
                float rowBottom = chartScale.GetYByValue(price - rowSize / 2.0);
                float rowHeight = Math.Max(1f, Math.Abs(rowBottom - rowTop) - 1f);
                float width = (float)(vol / maxVolume * maxBarWidth);

                bool inSecondaryVA = ShowSecondaryValueArea && price >= svaLow && price <= svaHigh;
                bool inVA = ShowValueArea && price >= vaLow && price <= vaHigh;
                bool isHighVolumeNode = highVolumeNodeSet != null && highVolumeNodeSet.Contains(price);
                bool isPoc = ShowPOC && price == pocPrice;

                SharpDX.Direct2D1.Brush brush = profileBrushDx;
                double opacityPercent = ProfileOpacity;
                if (inVA) { brush = valueAreaBrushDx; opacityPercent = ValueAreaOpacity; }
                if (inSecondaryVA) { brush = secondaryValueAreaBrushDx; opacityPercent = SecondaryValueAreaOpacity; }
                if (isHighVolumeNode) { brush = highVolumeNodeBrushDx; opacityPercent = HighVolumeNodeOpacity; }
                if (isPoc) { brush = pocBrushDx; opacityPercent = POCOpacity; }

                brush.Opacity = (float)(opacityPercent / 100.0);

                float barLeft = anchorFarX - width;
                var rect = new SharpDX.RectangleF(barLeft, y - rowHeight / 2f, width, rowHeight);
                RenderTarget.FillRectangle(rect, brush);
            }

            if (ShowBoundaryLine)
            {
                DrawExtendedLine(chartScale, buckets[buckets.Count - 1], lookbackStartX, anchorFarX, profileBrushDx, BoundaryLineWidthPixels, BoundaryLineOpacity, boundaryLineStroke);
                DrawExtendedLine(chartScale, buckets[0], lookbackStartX, anchorFarX, profileBrushDx, BoundaryLineWidthPixels, BoundaryLineOpacity, boundaryLineStroke);
            }

            if (ShowPOCLine)
                DrawExtendedLine(chartScale, pocPrice, lookbackStartX, anchorFarX, pocBrushDx, POCLineWidthPixels, POCLineOpacity, pocLineStroke);

            if (ExtendValueAreaLine || ExtendSecondaryValueAreaLine)
                RenderValueAreaLines(chartScale, vaLow, vaHigh, svaLow, svaHigh, lookbackStartX, anchorFarX);

            if (ExtendHighVolumeNodeLines)
                RenderHighVolumeNodeLines(chartScale, rankedHighVolumeNodes, lookbackStartX, anchorFarX, rowSize);

            DrawPriceLabels(chartScale, anchorFarX, buckets, vaLow, vaHigh, svaLow, svaHigh, pocPrice, rankedHighVolumeNodes);
        }

        // Pure geometry - a horizontal line for VAH/VAL (and SVAH/SVAL) from the start of the
        // rolling Lookback (hrs) window out to anchorFarX. No text is drawn here; the
        // corresponding "Display ... Labels" checkbox controls that level's price tag instead
        // (see DrawPriceLabels), so each level has exactly one label rather than one on the line
        // plus a second one in the tag column.
        private void RenderValueAreaLines(ChartScale chartScale, double vaLow, double vaHigh, double svaLow, double svaHigh, float lineFarX, float lineNearX)
        {
            if (ExtendValueAreaLine)
            {
                DrawExtendedLine(chartScale, vaHigh, lineFarX, lineNearX, valueAreaBrushDx, ValueAreaLineWidthPixels, ValueAreaLineOpacity, valueAreaLineStroke);
                DrawExtendedLine(chartScale, vaLow, lineFarX, lineNearX, valueAreaBrushDx, ValueAreaLineWidthPixels, ValueAreaLineOpacity, valueAreaLineStroke);
            }

            if (ExtendSecondaryValueAreaLine)
            {
                DrawExtendedLine(chartScale, svaHigh, lineFarX, lineNearX, secondaryValueAreaBrushDx, SecondaryValueAreaLineWidthPixels, SecondaryValueAreaLineOpacity, secondaryValueAreaLineStroke);
                DrawExtendedLine(chartScale, svaLow, lineFarX, lineNearX, secondaryValueAreaBrushDx, SecondaryValueAreaLineWidthPixels, SecondaryValueAreaLineOpacity, secondaryValueAreaLineStroke);
            }
        }

        // Pure geometry for the top High Volume Node Count rows, ranked by raw volume. See
        // RenderValueAreaLines - HVN# text lives only in DrawPriceLabels now. Unlike the other
        // extended lines, this one has no fixed Width/Style property - its thickness dynamically
        // matches that row's own bar height (same rowTop/rowBottom -> chartScale math the main
        // render loop uses), so it reads as a continuation of the bar rather than an arbitrary
        // thin line, and it's always solid (no stroke object needed).
        private void RenderHighVolumeNodeLines(ChartScale chartScale, List<double> rankedPrices, float lineFarX, float lineNearX, double rowSize)
        {
            for (int i = 0; i < rankedPrices.Count; i++)
            {
                double price = rankedPrices[i];
                if (price < chartScale.MinValue || price > chartScale.MaxValue) continue;

                float rowTop = chartScale.GetYByValue(price + rowSize / 2.0);
                float rowBottom = chartScale.GetYByValue(price - rowSize / 2.0);
                float rowHeight = Math.Max(1f, Math.Abs(rowBottom - rowTop) - 1f);

                DrawExtendedLine(chartScale, price, lineFarX, lineNearX, highVolumeNodeBrushDx, rowHeight, HighVolumeNodeLineOpacity, null);
            }
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

        // Price tags sit in one fixed column at anchorFarX + Profile-Label Offset (px) - the same
        // pivot the bars grow left from - so they always clear the profile regardless of any
        // row's own volume. Plain colored text, no background/border. Each tag is gated ONLY by
        // its own "Display ... Labels" checkbox - never by the corresponding line toggle, which
        // draws independent geometry (see RenderValueAreaLines/RenderHighVolumeNodeLines/the
        // Boundary and POC line blocks in OnRender). All labels share one Label Font Family/Size
        // (Labels group) built once per render call; only the brush (and so the color) differs
        // per level.
        //
        // HIGH/LOW/POC/VAH/VAL/SVAH/SVAL are all exact members of the same rounded bucket grid,
        // so two of them can land on the identical price (e.g. a narrow profile where VAH and
        // SVAH coincide) - drawing both independently would stack one on top of the other.
        // Grouping by exact price before drawing combines any such group into a single
        // "NAME1 / NAME2 xxxx.xx" line instead, with names ordered POC > Secondary Value Area >
        // Value Area > Profile Boundaries and the whole label colored by whichever of those
        // ranks highest in the group. HVN labels are intentionally left out of this merge - they
        // are ranked peaks rather than fixed structural levels, so coinciding with one of the
        // above is treated as a separate, unmerged label.
        private void DrawPriceLabels(ChartScale chartScale, float anchorFarX, List<double> buckets, double vaLow, double vaHigh, double svaLow, double svaHigh, double pocPrice, List<double> rankedHighVolumeNodes)
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

                if (ShowPOC && ShowPOCLabels)
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
                    for (int i = 0; i < rankedHighVolumeNodes.Count; i++)
                        DrawPriceLabel(chartScale, labelX, rankedHighVolumeNodes[i], "HVN" + (i + 1), highVolumeNodeBrushDx, labelFormat);
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

        private static SharpDX.DirectWrite.TextFormat BuildLabelTextFormat(string fontFamily, double fontSize)
        {
            SimpleFont font = new SimpleFont(fontFamily, fontSize);
            SharpDX.DirectWrite.TextFormat format = font.ToDirectWriteTextFormat();
            format.TextAlignment = SharpDX.DirectWrite.TextAlignment.Leading;
            format.ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center;
            return format;
        }

        // Plain price tag text at a fixed X column - no background/border. textBrush is one of
        // the cached row-color brushes; its Opacity is reset to fully opaque here since the main
        // render loop above leaves it at whatever per-row/line opacity it last painted with.
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

        [NinjaScriptProperty]
        [Range(1, 100000)]
        [Display(Name = "Tick Aggregation", Description = "Number of ticks grouped into each profile row (row size = tick size x this value)", GroupName = "Settings", Order = 1)]
        public int TickAggregation { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 100.0)]
        [Display(Name = "Lookback (hrs)", Description = "Rolling time window, in hours, of trades included in the profile; older trades age out automatically as new ones arrive", GroupName = "Settings", Order = 2)]
        public double LookbackHours { get; set; }

        [XmlIgnore]
        [Display(Name = "Profile Anchor", Description = "Current Bar pins the profile to the right of the last bar (Bar-profile Right Offset (px) applies); scrolls out of view when you scroll the chart left. Chart Edge pins the profile to the chart panel's own right edge instead - a fixed screen position, always visible regardless of scroll - same mechanism as EGVolumeProfile's Most Right alignment; Bar-profile Right Offset (px) is ignored, and price labels typically fall outside the visible panel", GroupName = "Settings", Order = 3)]
        public EGRollingVolumeProfileAnchorMode ProfileAnchor { get; set; }

        [Browsable(false)]
        public string ProfileAnchorSerializable
        {
            get { return ProfileAnchor.ToString(); }
            set { ProfileAnchor = (EGRollingVolumeProfileAnchorMode)Enum.Parse(typeof(EGRollingVolumeProfileAnchorMode), value); }
        }

        [NinjaScriptProperty]
        [Range(0, 500)]
        [Display(Name = "Bar-profile Right Offset (px)", Description = "Horizontal pixel gap from the last bar to where the nearest (longest) profile row can reach; the profile grows leftward, toward the bars, from its own fixed pivot further right. Only applies when Profile Anchor is Current Bar", GroupName = "Settings", Order = 4)]
        public int RightOffsetPixels { get; set; }

        [NinjaScriptProperty]
        [Range(0, 200)]
        [Display(Name = "Profile-Label Offset (px)", Description = "Horizontal pixel gap from the profile's own pivot (its right edge) to the price labels", GroupName = "Settings", Order = 5)]
        public int ProfileLabelOffsetPixels { get; set; }

        // ----- Profile -----

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Profile Width (%)", Description = "Max bar length as a percentage of the chart panel width", GroupName = "Profile", Order = 1)]
        public double ProfileWidthPercent { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Profile Opacity", Description = "Opacity of profile bars outside the Value Area(s)", GroupName = "Profile", Order = 2)]
        public double ProfileOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Display Boundary Line", Description = "Draw the profile's own HIGH and LOW rows as horizontal lines from the start of the rolling Lookback (hrs) window out to the profile's own pivot", GroupName = "Profile", Order = 3)]
        public bool ShowBoundaryLine { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Boundary Line Width (px)", Description = "Width in pixels of the extended Boundary lines", GroupName = "Profile", Order = 4)]
        public int BoundaryLineWidthPixels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Boundary Line Style", Description = "Dash style of the extended Boundary lines", GroupName = "Profile", Order = 5)]
        public DashStyleHelper BoundaryLineStyle { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Boundary Line Opacity", Description = "Opacity of the extended Boundary lines", GroupName = "Profile", Order = 6)]
        public double BoundaryLineOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Display Boundary Labels", Description = "Show the HIGH/LOW price tags at the profile's own top and bottom rows; independent of Display Boundary Line", GroupName = "Profile", Order = 7)]
        public bool ShowBoundaryLabels { get; set; }

        // ----- Value Area -----

        [NinjaScriptProperty]
        [Display(Name = "Display Value Area", Description = "Highlight rows inside the Value Area", GroupName = "Value Area", Order = 1)]
        public bool ShowValueArea { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Value Area (%)", Description = "Percentage of the rolling window's volume contained in the Value Area", GroupName = "Value Area", Order = 2)]
        public double ValueAreaPercent { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Value Area Opacity", Description = "Opacity of Value Area rows", GroupName = "Value Area", Order = 3)]
        public double ValueAreaOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Extend Value Area Line", Description = "Draw the Value Area High and Low as horizontal lines from the start of the rolling Lookback (hrs) window out to the profile's own pivot", GroupName = "Value Area", Order = 4)]
        public bool ExtendValueAreaLine { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Value Area Line Width (px)", Description = "Width in pixels of the extended Value Area lines", GroupName = "Value Area", Order = 5)]
        public int ValueAreaLineWidthPixels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Value Area Line Style", Description = "Dash style of the extended Value Area lines", GroupName = "Value Area", Order = 6)]
        public DashStyleHelper ValueAreaLineStyle { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Value Area Line Opacity", Description = "Opacity of the extended Value Area lines", GroupName = "Value Area", Order = 7)]
        public double ValueAreaLineOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Display Value Area Labels", Description = "Show the VAH/VAL price tags (requires Display Value Area); independent of Extend Value Area Line", GroupName = "Value Area", Order = 8)]
        public bool ShowValueAreaLabels { get; set; }

        // ----- Secondary Value Area -----

        [NinjaScriptProperty]
        [Display(Name = "Display Secondary Value Area", Description = "Highlight rows inside the Secondary Value Area", GroupName = "Secondary Value Area", Order = 1)]
        public bool ShowSecondaryValueArea { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Secondary Value Area (%)", Description = "Percentage of volume contained in the Secondary Value Area", GroupName = "Secondary Value Area", Order = 2)]
        public double SecondaryValueAreaPercent { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Secondary Value Area From Primary", Description = "When enabled, Secondary Value Area (%) is taken against the Primary Value Area's own volume and price range instead of the whole profile's", GroupName = "Secondary Value Area", Order = 3)]
        public bool SecondaryValueAreaFromPrimary { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Secondary Value Area Opacity", Description = "Opacity of Secondary Value Area rows", GroupName = "Secondary Value Area", Order = 4)]
        public double SecondaryValueAreaOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Extend Secondary Value Area Line", Description = "Draw the Secondary Value Area High and Low as horizontal lines from the start of the rolling Lookback (hrs) window out to the profile's own pivot", GroupName = "Secondary Value Area", Order = 5)]
        public bool ExtendSecondaryValueAreaLine { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Secondary Value Area Line Width (px)", Description = "Width in pixels of the extended Secondary Value Area lines", GroupName = "Secondary Value Area", Order = 6)]
        public int SecondaryValueAreaLineWidthPixels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Secondary Value Area Line Style", Description = "Dash style of the extended Secondary Value Area lines", GroupName = "Secondary Value Area", Order = 7)]
        public DashStyleHelper SecondaryValueAreaLineStyle { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Secondary Value Area Line Opacity", Description = "Opacity of the extended Secondary Value Area lines", GroupName = "Secondary Value Area", Order = 8)]
        public double SecondaryValueAreaLineOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Display Secondary Value Area Labels", Description = "Show the SVAH/SVAL price tags (requires Display Secondary Value Area); independent of Extend Secondary Value Area Line", GroupName = "Secondary Value Area", Order = 9)]
        public bool ShowSecondaryValueAreaLabels { get; set; }

        // ----- Point Of Control -----

        [NinjaScriptProperty]
        [Display(Name = "Display POC", Description = "Color the Point of Control row differently from the rest of the profile", GroupName = "Point Of Control", Order = 1)]
        public bool ShowPOC { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "POC Opacity", Description = "Opacity of the Point of Control row", GroupName = "Point Of Control", Order = 2)]
        public double POCOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Display POC Line", Description = "Draw a line at the POC price, from the start of the rolling Lookback (hrs) window out to the profile's own pivot; independent of Display POC", GroupName = "Point Of Control", Order = 3)]
        public bool ShowPOCLine { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "POC Line Width (px)", Description = "Width in pixels of the POC line", GroupName = "Point Of Control", Order = 4)]
        public int POCLineWidthPixels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "POC Line Style", Description = "Dash style of the POC line", GroupName = "Point Of Control", Order = 5)]
        public DashStyleHelper POCLineStyle { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "POC Line Opacity", Description = "Opacity of the POC line", GroupName = "Point Of Control", Order = 6)]
        public double POCLineOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Display POC Labels", Description = "Show the POC price tag (requires Display POC); independent of Display POC Line", GroupName = "Point Of Control", Order = 7)]
        public bool ShowPOCLabels { get; set; }

        // ----- High Volume Nodes -----

        [NinjaScriptProperty]
        [Display(Name = "Display High Volume Nodes", Description = "Isolate the Node Count highest-volume peak clusters (at the current Tick Aggregation row size) with High Volume Node Color", GroupName = "High Volume Nodes", Order = 1)]
        public bool ShowHighVolumeNodes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Node Count", Description = "Number of highest-volume peak clusters isolated (Display High Volume Nodes) and/or extended (Extend High Volume Node Lines / Display High Volume Node Labels) - a peak is a row whose volume is a local maximum, not simply the single highest rows", GroupName = "High Volume Nodes", Order = 2)]
        public int HighVolumeNodeCount { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "HVN Cluster Expansion (%)", Description = "From each isolated peak, include neighboring rows outward in both directions as long as their volume stays at or above this percentage of the peak's own volume - turns a single peak row into the tapered band a real High Volume Node zone should look like", GroupName = "High Volume Nodes", Order = 3)]
        public double HighVolumeNodeClusterExpansionPercent { get; set; }

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name = "Min HVN Spacing (rows)", Description = "Minimum number of rows required between two isolated peaks, so two nearby bumps on the same underlying peak don't both count as separate nodes", GroupName = "High Volume Nodes", Order = 4)]
        public int HighVolumeNodeMinSpacingRows { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "High Volume Node Opacity", Description = "Opacity of isolated High Volume Node rows", GroupName = "High Volume Nodes", Order = 5)]
        public double HighVolumeNodeOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Extend High Volume Node Lines", Description = "Draw a line for each of the top Node Count peaks from the start of the rolling Lookback (hrs) window out to the profile's own pivot - thickness dynamically matches that row's own bar height rather than a fixed width", GroupName = "High Volume Nodes", Order = 6)]
        public bool ExtendHighVolumeNodeLines { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "High Volume Node Line Opacity", Description = "Opacity of the extended High Volume Node lines", GroupName = "High Volume Nodes", Order = 7)]
        public double HighVolumeNodeLineOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Display High Volume Node Labels", Description = "Show the HVN1/HVN2/... price tags (rank 1 = highest peak volume; requires Display High Volume Nodes); independent of Extend High Volume Node Lines", GroupName = "High Volume Nodes", Order = 8)]
        public bool ShowHighVolumeNodeLabels { get; set; }

        // ----- Labels -----

        [NinjaScriptProperty]
        [TypeConverter(typeof(EGRollingVolumeProfileFontFamilyConverter))]
        [Display(Name = "Label Font Family", Description = "Font family shared by every price tag label (HIGH/LOW, POC, VAH/VAL, SVAH/SVAL, HVN#)", GroupName = "Labels", Order = 1)]
        public string LabelFontFamily { get; set; }

        [NinjaScriptProperty]
        [TypeConverter(typeof(EGRollingVolumeProfileFontSizeConverter))]
        [Display(Name = "Label Font Size", Description = "Font size shared by every price tag label (HIGH/LOW, POC, VAH/VAL, SVAH/SVAL, HVN#)", GroupName = "Labels", Order = 2)]
        public double LabelFontSize { get; set; }

        // ----- Colors -----

        private System.Windows.Media.Brush profileColor;
        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Profile", Description = "Color of profile bars outside the Value Area(s), and the Boundary line", GroupName = "Colors", Order = 1)]
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

        private System.Windows.Media.Brush valueAreaColor;
        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Value Area", Description = "Color of Value Area rows and extended Value Area lines", GroupName = "Colors", Order = 2)]
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
        [Display(Name = "Secondary Value Area", Description = "Color of Secondary Value Area rows and extended Secondary Value Area lines", GroupName = "Colors", Order = 3)]
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
        [Display(Name = "POC", Description = "Color of the Point of Control row and its line", GroupName = "Colors", Order = 4)]
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
        [Display(Name = "High Volume Node", Description = "Color of isolated High Volume Node rows and their extended lines", GroupName = "Colors", Order = 5)]
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

    public enum EGRollingVolumeProfileAnchorMode { CurrentBar, ChartEdge }

    public class EGRollingVolumeProfileFontFamilyConverter : TypeConverter
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

    public class EGRollingVolumeProfileFontSizeConverter : TypeConverter
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
		private EducatedGambling.EGRollingVolumeProfile[] cacheEGRollingVolumeProfile;
		public EducatedGambling.EGRollingVolumeProfile EGRollingVolumeProfile(int tickAggregation, double lookbackHours, int rightOffsetPixels, int profileLabelOffsetPixels, double profileWidthPercent, double profileOpacity, bool showBoundaryLine, int boundaryLineWidthPixels, DashStyleHelper boundaryLineStyle, double boundaryLineOpacity, bool showBoundaryLabels, bool showValueArea, double valueAreaPercent, double valueAreaOpacity, bool extendValueAreaLine, int valueAreaLineWidthPixels, DashStyleHelper valueAreaLineStyle, double valueAreaLineOpacity, bool showValueAreaLabels, bool showSecondaryValueArea, double secondaryValueAreaPercent, bool secondaryValueAreaFromPrimary, double secondaryValueAreaOpacity, bool extendSecondaryValueAreaLine, int secondaryValueAreaLineWidthPixels, DashStyleHelper secondaryValueAreaLineStyle, double secondaryValueAreaLineOpacity, bool showSecondaryValueAreaLabels, bool showPOC, double pOCOpacity, bool showPOCLine, int pOCLineWidthPixels, DashStyleHelper pOCLineStyle, double pOCLineOpacity, bool showPOCLabels, bool showHighVolumeNodes, int highVolumeNodeCount, double highVolumeNodeClusterExpansionPercent, int highVolumeNodeMinSpacingRows, double highVolumeNodeOpacity, bool extendHighVolumeNodeLines, double highVolumeNodeLineOpacity, bool showHighVolumeNodeLabels, string labelFontFamily, double labelFontSize, System.Windows.Media.Brush profileColor, System.Windows.Media.Brush valueAreaColor, System.Windows.Media.Brush secondaryValueAreaColor, System.Windows.Media.Brush pOCColor, System.Windows.Media.Brush highVolumeNodeColor)
		{
			return EGRollingVolumeProfile(Input, tickAggregation, lookbackHours, rightOffsetPixels, profileLabelOffsetPixels, profileWidthPercent, profileOpacity, showBoundaryLine, boundaryLineWidthPixels, boundaryLineStyle, boundaryLineOpacity, showBoundaryLabels, showValueArea, valueAreaPercent, valueAreaOpacity, extendValueAreaLine, valueAreaLineWidthPixels, valueAreaLineStyle, valueAreaLineOpacity, showValueAreaLabels, showSecondaryValueArea, secondaryValueAreaPercent, secondaryValueAreaFromPrimary, secondaryValueAreaOpacity, extendSecondaryValueAreaLine, secondaryValueAreaLineWidthPixels, secondaryValueAreaLineStyle, secondaryValueAreaLineOpacity, showSecondaryValueAreaLabels, showPOC, pOCOpacity, showPOCLine, pOCLineWidthPixels, pOCLineStyle, pOCLineOpacity, showPOCLabels, showHighVolumeNodes, highVolumeNodeCount, highVolumeNodeClusterExpansionPercent, highVolumeNodeMinSpacingRows, highVolumeNodeOpacity, extendHighVolumeNodeLines, highVolumeNodeLineOpacity, showHighVolumeNodeLabels, labelFontFamily, labelFontSize, profileColor, valueAreaColor, secondaryValueAreaColor, pOCColor, highVolumeNodeColor);
		}

		public EducatedGambling.EGRollingVolumeProfile EGRollingVolumeProfile(ISeries<double> input, int tickAggregation, double lookbackHours, int rightOffsetPixels, int profileLabelOffsetPixels, double profileWidthPercent, double profileOpacity, bool showBoundaryLine, int boundaryLineWidthPixels, DashStyleHelper boundaryLineStyle, double boundaryLineOpacity, bool showBoundaryLabels, bool showValueArea, double valueAreaPercent, double valueAreaOpacity, bool extendValueAreaLine, int valueAreaLineWidthPixels, DashStyleHelper valueAreaLineStyle, double valueAreaLineOpacity, bool showValueAreaLabels, bool showSecondaryValueArea, double secondaryValueAreaPercent, bool secondaryValueAreaFromPrimary, double secondaryValueAreaOpacity, bool extendSecondaryValueAreaLine, int secondaryValueAreaLineWidthPixels, DashStyleHelper secondaryValueAreaLineStyle, double secondaryValueAreaLineOpacity, bool showSecondaryValueAreaLabels, bool showPOC, double pOCOpacity, bool showPOCLine, int pOCLineWidthPixels, DashStyleHelper pOCLineStyle, double pOCLineOpacity, bool showPOCLabels, bool showHighVolumeNodes, int highVolumeNodeCount, double highVolumeNodeClusterExpansionPercent, int highVolumeNodeMinSpacingRows, double highVolumeNodeOpacity, bool extendHighVolumeNodeLines, double highVolumeNodeLineOpacity, bool showHighVolumeNodeLabels, string labelFontFamily, double labelFontSize, System.Windows.Media.Brush profileColor, System.Windows.Media.Brush valueAreaColor, System.Windows.Media.Brush secondaryValueAreaColor, System.Windows.Media.Brush pOCColor, System.Windows.Media.Brush highVolumeNodeColor)
		{
			if (cacheEGRollingVolumeProfile != null)
				for (int idx = 0; idx < cacheEGRollingVolumeProfile.Length; idx++)
					if (cacheEGRollingVolumeProfile[idx] != null && cacheEGRollingVolumeProfile[idx].TickAggregation == tickAggregation && cacheEGRollingVolumeProfile[idx].LookbackHours == lookbackHours && cacheEGRollingVolumeProfile[idx].RightOffsetPixels == rightOffsetPixels && cacheEGRollingVolumeProfile[idx].ProfileLabelOffsetPixels == profileLabelOffsetPixels && cacheEGRollingVolumeProfile[idx].ProfileWidthPercent == profileWidthPercent && cacheEGRollingVolumeProfile[idx].ProfileOpacity == profileOpacity && cacheEGRollingVolumeProfile[idx].ShowBoundaryLine == showBoundaryLine && cacheEGRollingVolumeProfile[idx].BoundaryLineWidthPixels == boundaryLineWidthPixels && cacheEGRollingVolumeProfile[idx].BoundaryLineStyle == boundaryLineStyle && cacheEGRollingVolumeProfile[idx].BoundaryLineOpacity == boundaryLineOpacity && cacheEGRollingVolumeProfile[idx].ShowBoundaryLabels == showBoundaryLabels && cacheEGRollingVolumeProfile[idx].ShowValueArea == showValueArea && cacheEGRollingVolumeProfile[idx].ValueAreaPercent == valueAreaPercent && cacheEGRollingVolumeProfile[idx].ValueAreaOpacity == valueAreaOpacity && cacheEGRollingVolumeProfile[idx].ExtendValueAreaLine == extendValueAreaLine && cacheEGRollingVolumeProfile[idx].ValueAreaLineWidthPixels == valueAreaLineWidthPixels && cacheEGRollingVolumeProfile[idx].ValueAreaLineStyle == valueAreaLineStyle && cacheEGRollingVolumeProfile[idx].ValueAreaLineOpacity == valueAreaLineOpacity && cacheEGRollingVolumeProfile[idx].ShowValueAreaLabels == showValueAreaLabels && cacheEGRollingVolumeProfile[idx].ShowSecondaryValueArea == showSecondaryValueArea && cacheEGRollingVolumeProfile[idx].SecondaryValueAreaPercent == secondaryValueAreaPercent && cacheEGRollingVolumeProfile[idx].SecondaryValueAreaFromPrimary == secondaryValueAreaFromPrimary && cacheEGRollingVolumeProfile[idx].SecondaryValueAreaOpacity == secondaryValueAreaOpacity && cacheEGRollingVolumeProfile[idx].ExtendSecondaryValueAreaLine == extendSecondaryValueAreaLine && cacheEGRollingVolumeProfile[idx].SecondaryValueAreaLineWidthPixels == secondaryValueAreaLineWidthPixels && cacheEGRollingVolumeProfile[idx].SecondaryValueAreaLineStyle == secondaryValueAreaLineStyle && cacheEGRollingVolumeProfile[idx].SecondaryValueAreaLineOpacity == secondaryValueAreaLineOpacity && cacheEGRollingVolumeProfile[idx].ShowSecondaryValueAreaLabels == showSecondaryValueAreaLabels && cacheEGRollingVolumeProfile[idx].ShowPOC == showPOC && cacheEGRollingVolumeProfile[idx].POCOpacity == pOCOpacity && cacheEGRollingVolumeProfile[idx].ShowPOCLine == showPOCLine && cacheEGRollingVolumeProfile[idx].POCLineWidthPixels == pOCLineWidthPixels && cacheEGRollingVolumeProfile[idx].POCLineStyle == pOCLineStyle && cacheEGRollingVolumeProfile[idx].POCLineOpacity == pOCLineOpacity && cacheEGRollingVolumeProfile[idx].ShowPOCLabels == showPOCLabels && cacheEGRollingVolumeProfile[idx].ShowHighVolumeNodes == showHighVolumeNodes && cacheEGRollingVolumeProfile[idx].HighVolumeNodeCount == highVolumeNodeCount && cacheEGRollingVolumeProfile[idx].HighVolumeNodeClusterExpansionPercent == highVolumeNodeClusterExpansionPercent && cacheEGRollingVolumeProfile[idx].HighVolumeNodeMinSpacingRows == highVolumeNodeMinSpacingRows && cacheEGRollingVolumeProfile[idx].HighVolumeNodeOpacity == highVolumeNodeOpacity && cacheEGRollingVolumeProfile[idx].ExtendHighVolumeNodeLines == extendHighVolumeNodeLines && cacheEGRollingVolumeProfile[idx].HighVolumeNodeLineOpacity == highVolumeNodeLineOpacity && cacheEGRollingVolumeProfile[idx].ShowHighVolumeNodeLabels == showHighVolumeNodeLabels && cacheEGRollingVolumeProfile[idx].LabelFontFamily == labelFontFamily && cacheEGRollingVolumeProfile[idx].LabelFontSize == labelFontSize && cacheEGRollingVolumeProfile[idx].ProfileColor == profileColor && cacheEGRollingVolumeProfile[idx].ValueAreaColor == valueAreaColor && cacheEGRollingVolumeProfile[idx].SecondaryValueAreaColor == secondaryValueAreaColor && cacheEGRollingVolumeProfile[idx].POCColor == pOCColor && cacheEGRollingVolumeProfile[idx].HighVolumeNodeColor == highVolumeNodeColor && cacheEGRollingVolumeProfile[idx].EqualsInput(input))
						return cacheEGRollingVolumeProfile[idx];
			return CacheIndicator<EducatedGambling.EGRollingVolumeProfile>(new EducatedGambling.EGRollingVolumeProfile(){ TickAggregation = tickAggregation, LookbackHours = lookbackHours, RightOffsetPixels = rightOffsetPixels, ProfileLabelOffsetPixels = profileLabelOffsetPixels, ProfileWidthPercent = profileWidthPercent, ProfileOpacity = profileOpacity, ShowBoundaryLine = showBoundaryLine, BoundaryLineWidthPixels = boundaryLineWidthPixels, BoundaryLineStyle = boundaryLineStyle, BoundaryLineOpacity = boundaryLineOpacity, ShowBoundaryLabels = showBoundaryLabels, ShowValueArea = showValueArea, ValueAreaPercent = valueAreaPercent, ValueAreaOpacity = valueAreaOpacity, ExtendValueAreaLine = extendValueAreaLine, ValueAreaLineWidthPixels = valueAreaLineWidthPixels, ValueAreaLineStyle = valueAreaLineStyle, ValueAreaLineOpacity = valueAreaLineOpacity, ShowValueAreaLabels = showValueAreaLabels, ShowSecondaryValueArea = showSecondaryValueArea, SecondaryValueAreaPercent = secondaryValueAreaPercent, SecondaryValueAreaFromPrimary = secondaryValueAreaFromPrimary, SecondaryValueAreaOpacity = secondaryValueAreaOpacity, ExtendSecondaryValueAreaLine = extendSecondaryValueAreaLine, SecondaryValueAreaLineWidthPixels = secondaryValueAreaLineWidthPixels, SecondaryValueAreaLineStyle = secondaryValueAreaLineStyle, SecondaryValueAreaLineOpacity = secondaryValueAreaLineOpacity, ShowSecondaryValueAreaLabels = showSecondaryValueAreaLabels, ShowPOC = showPOC, POCOpacity = pOCOpacity, ShowPOCLine = showPOCLine, POCLineWidthPixels = pOCLineWidthPixels, POCLineStyle = pOCLineStyle, POCLineOpacity = pOCLineOpacity, ShowPOCLabels = showPOCLabels, ShowHighVolumeNodes = showHighVolumeNodes, HighVolumeNodeCount = highVolumeNodeCount, HighVolumeNodeClusterExpansionPercent = highVolumeNodeClusterExpansionPercent, HighVolumeNodeMinSpacingRows = highVolumeNodeMinSpacingRows, HighVolumeNodeOpacity = highVolumeNodeOpacity, ExtendHighVolumeNodeLines = extendHighVolumeNodeLines, HighVolumeNodeLineOpacity = highVolumeNodeLineOpacity, ShowHighVolumeNodeLabels = showHighVolumeNodeLabels, LabelFontFamily = labelFontFamily, LabelFontSize = labelFontSize, ProfileColor = profileColor, ValueAreaColor = valueAreaColor, SecondaryValueAreaColor = secondaryValueAreaColor, POCColor = pOCColor, HighVolumeNodeColor = highVolumeNodeColor }, input, ref cacheEGRollingVolumeProfile);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.EducatedGambling.EGRollingVolumeProfile EGRollingVolumeProfile(int tickAggregation, double lookbackHours, int rightOffsetPixels, int profileLabelOffsetPixels, double profileWidthPercent, double profileOpacity, bool showBoundaryLine, int boundaryLineWidthPixels, DashStyleHelper boundaryLineStyle, double boundaryLineOpacity, bool showBoundaryLabels, bool showValueArea, double valueAreaPercent, double valueAreaOpacity, bool extendValueAreaLine, int valueAreaLineWidthPixels, DashStyleHelper valueAreaLineStyle, double valueAreaLineOpacity, bool showValueAreaLabels, bool showSecondaryValueArea, double secondaryValueAreaPercent, bool secondaryValueAreaFromPrimary, double secondaryValueAreaOpacity, bool extendSecondaryValueAreaLine, int secondaryValueAreaLineWidthPixels, DashStyleHelper secondaryValueAreaLineStyle, double secondaryValueAreaLineOpacity, bool showSecondaryValueAreaLabels, bool showPOC, double pOCOpacity, bool showPOCLine, int pOCLineWidthPixels, DashStyleHelper pOCLineStyle, double pOCLineOpacity, bool showPOCLabels, bool showHighVolumeNodes, int highVolumeNodeCount, double highVolumeNodeClusterExpansionPercent, int highVolumeNodeMinSpacingRows, double highVolumeNodeOpacity, bool extendHighVolumeNodeLines, double highVolumeNodeLineOpacity, bool showHighVolumeNodeLabels, string labelFontFamily, double labelFontSize, System.Windows.Media.Brush profileColor, System.Windows.Media.Brush valueAreaColor, System.Windows.Media.Brush secondaryValueAreaColor, System.Windows.Media.Brush pOCColor, System.Windows.Media.Brush highVolumeNodeColor)
		{
			return indicator.EGRollingVolumeProfile(Input, tickAggregation, lookbackHours, rightOffsetPixels, profileLabelOffsetPixels, profileWidthPercent, profileOpacity, showBoundaryLine, boundaryLineWidthPixels, boundaryLineStyle, boundaryLineOpacity, showBoundaryLabels, showValueArea, valueAreaPercent, valueAreaOpacity, extendValueAreaLine, valueAreaLineWidthPixels, valueAreaLineStyle, valueAreaLineOpacity, showValueAreaLabels, showSecondaryValueArea, secondaryValueAreaPercent, secondaryValueAreaFromPrimary, secondaryValueAreaOpacity, extendSecondaryValueAreaLine, secondaryValueAreaLineWidthPixels, secondaryValueAreaLineStyle, secondaryValueAreaLineOpacity, showSecondaryValueAreaLabels, showPOC, pOCOpacity, showPOCLine, pOCLineWidthPixels, pOCLineStyle, pOCLineOpacity, showPOCLabels, showHighVolumeNodes, highVolumeNodeCount, highVolumeNodeClusterExpansionPercent, highVolumeNodeMinSpacingRows, highVolumeNodeOpacity, extendHighVolumeNodeLines, highVolumeNodeLineOpacity, showHighVolumeNodeLabels, labelFontFamily, labelFontSize, profileColor, valueAreaColor, secondaryValueAreaColor, pOCColor, highVolumeNodeColor);
		}

		public Indicators.EducatedGambling.EGRollingVolumeProfile EGRollingVolumeProfile(ISeries<double> input , int tickAggregation, double lookbackHours, int rightOffsetPixels, int profileLabelOffsetPixels, double profileWidthPercent, double profileOpacity, bool showBoundaryLine, int boundaryLineWidthPixels, DashStyleHelper boundaryLineStyle, double boundaryLineOpacity, bool showBoundaryLabels, bool showValueArea, double valueAreaPercent, double valueAreaOpacity, bool extendValueAreaLine, int valueAreaLineWidthPixels, DashStyleHelper valueAreaLineStyle, double valueAreaLineOpacity, bool showValueAreaLabels, bool showSecondaryValueArea, double secondaryValueAreaPercent, bool secondaryValueAreaFromPrimary, double secondaryValueAreaOpacity, bool extendSecondaryValueAreaLine, int secondaryValueAreaLineWidthPixels, DashStyleHelper secondaryValueAreaLineStyle, double secondaryValueAreaLineOpacity, bool showSecondaryValueAreaLabels, bool showPOC, double pOCOpacity, bool showPOCLine, int pOCLineWidthPixels, DashStyleHelper pOCLineStyle, double pOCLineOpacity, bool showPOCLabels, bool showHighVolumeNodes, int highVolumeNodeCount, double highVolumeNodeClusterExpansionPercent, int highVolumeNodeMinSpacingRows, double highVolumeNodeOpacity, bool extendHighVolumeNodeLines, double highVolumeNodeLineOpacity, bool showHighVolumeNodeLabels, string labelFontFamily, double labelFontSize, System.Windows.Media.Brush profileColor, System.Windows.Media.Brush valueAreaColor, System.Windows.Media.Brush secondaryValueAreaColor, System.Windows.Media.Brush pOCColor, System.Windows.Media.Brush highVolumeNodeColor)
		{
			return indicator.EGRollingVolumeProfile(input, tickAggregation, lookbackHours, rightOffsetPixels, profileLabelOffsetPixels, profileWidthPercent, profileOpacity, showBoundaryLine, boundaryLineWidthPixels, boundaryLineStyle, boundaryLineOpacity, showBoundaryLabels, showValueArea, valueAreaPercent, valueAreaOpacity, extendValueAreaLine, valueAreaLineWidthPixels, valueAreaLineStyle, valueAreaLineOpacity, showValueAreaLabels, showSecondaryValueArea, secondaryValueAreaPercent, secondaryValueAreaFromPrimary, secondaryValueAreaOpacity, extendSecondaryValueAreaLine, secondaryValueAreaLineWidthPixels, secondaryValueAreaLineStyle, secondaryValueAreaLineOpacity, showSecondaryValueAreaLabels, showPOC, pOCOpacity, showPOCLine, pOCLineWidthPixels, pOCLineStyle, pOCLineOpacity, showPOCLabels, showHighVolumeNodes, highVolumeNodeCount, highVolumeNodeClusterExpansionPercent, highVolumeNodeMinSpacingRows, highVolumeNodeOpacity, extendHighVolumeNodeLines, highVolumeNodeLineOpacity, showHighVolumeNodeLabels, labelFontFamily, labelFontSize, profileColor, valueAreaColor, secondaryValueAreaColor, pOCColor, highVolumeNodeColor);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.EducatedGambling.EGRollingVolumeProfile EGRollingVolumeProfile(int tickAggregation, double lookbackHours, int rightOffsetPixels, int profileLabelOffsetPixels, double profileWidthPercent, double profileOpacity, bool showBoundaryLine, int boundaryLineWidthPixels, DashStyleHelper boundaryLineStyle, double boundaryLineOpacity, bool showBoundaryLabels, bool showValueArea, double valueAreaPercent, double valueAreaOpacity, bool extendValueAreaLine, int valueAreaLineWidthPixels, DashStyleHelper valueAreaLineStyle, double valueAreaLineOpacity, bool showValueAreaLabels, bool showSecondaryValueArea, double secondaryValueAreaPercent, bool secondaryValueAreaFromPrimary, double secondaryValueAreaOpacity, bool extendSecondaryValueAreaLine, int secondaryValueAreaLineWidthPixels, DashStyleHelper secondaryValueAreaLineStyle, double secondaryValueAreaLineOpacity, bool showSecondaryValueAreaLabels, bool showPOC, double pOCOpacity, bool showPOCLine, int pOCLineWidthPixels, DashStyleHelper pOCLineStyle, double pOCLineOpacity, bool showPOCLabels, bool showHighVolumeNodes, int highVolumeNodeCount, double highVolumeNodeClusterExpansionPercent, int highVolumeNodeMinSpacingRows, double highVolumeNodeOpacity, bool extendHighVolumeNodeLines, double highVolumeNodeLineOpacity, bool showHighVolumeNodeLabels, string labelFontFamily, double labelFontSize, System.Windows.Media.Brush profileColor, System.Windows.Media.Brush valueAreaColor, System.Windows.Media.Brush secondaryValueAreaColor, System.Windows.Media.Brush pOCColor, System.Windows.Media.Brush highVolumeNodeColor)
		{
			return indicator.EGRollingVolumeProfile(Input, tickAggregation, lookbackHours, rightOffsetPixels, profileLabelOffsetPixels, profileWidthPercent, profileOpacity, showBoundaryLine, boundaryLineWidthPixels, boundaryLineStyle, boundaryLineOpacity, showBoundaryLabels, showValueArea, valueAreaPercent, valueAreaOpacity, extendValueAreaLine, valueAreaLineWidthPixels, valueAreaLineStyle, valueAreaLineOpacity, showValueAreaLabels, showSecondaryValueArea, secondaryValueAreaPercent, secondaryValueAreaFromPrimary, secondaryValueAreaOpacity, extendSecondaryValueAreaLine, secondaryValueAreaLineWidthPixels, secondaryValueAreaLineStyle, secondaryValueAreaLineOpacity, showSecondaryValueAreaLabels, showPOC, pOCOpacity, showPOCLine, pOCLineWidthPixels, pOCLineStyle, pOCLineOpacity, showPOCLabels, showHighVolumeNodes, highVolumeNodeCount, highVolumeNodeClusterExpansionPercent, highVolumeNodeMinSpacingRows, highVolumeNodeOpacity, extendHighVolumeNodeLines, highVolumeNodeLineOpacity, showHighVolumeNodeLabels, labelFontFamily, labelFontSize, profileColor, valueAreaColor, secondaryValueAreaColor, pOCColor, highVolumeNodeColor);
		}

		public Indicators.EducatedGambling.EGRollingVolumeProfile EGRollingVolumeProfile(ISeries<double> input , int tickAggregation, double lookbackHours, int rightOffsetPixels, int profileLabelOffsetPixels, double profileWidthPercent, double profileOpacity, bool showBoundaryLine, int boundaryLineWidthPixels, DashStyleHelper boundaryLineStyle, double boundaryLineOpacity, bool showBoundaryLabels, bool showValueArea, double valueAreaPercent, double valueAreaOpacity, bool extendValueAreaLine, int valueAreaLineWidthPixels, DashStyleHelper valueAreaLineStyle, double valueAreaLineOpacity, bool showValueAreaLabels, bool showSecondaryValueArea, double secondaryValueAreaPercent, bool secondaryValueAreaFromPrimary, double secondaryValueAreaOpacity, bool extendSecondaryValueAreaLine, int secondaryValueAreaLineWidthPixels, DashStyleHelper secondaryValueAreaLineStyle, double secondaryValueAreaLineOpacity, bool showSecondaryValueAreaLabels, bool showPOC, double pOCOpacity, bool showPOCLine, int pOCLineWidthPixels, DashStyleHelper pOCLineStyle, double pOCLineOpacity, bool showPOCLabels, bool showHighVolumeNodes, int highVolumeNodeCount, double highVolumeNodeClusterExpansionPercent, int highVolumeNodeMinSpacingRows, double highVolumeNodeOpacity, bool extendHighVolumeNodeLines, double highVolumeNodeLineOpacity, bool showHighVolumeNodeLabels, string labelFontFamily, double labelFontSize, System.Windows.Media.Brush profileColor, System.Windows.Media.Brush valueAreaColor, System.Windows.Media.Brush secondaryValueAreaColor, System.Windows.Media.Brush pOCColor, System.Windows.Media.Brush highVolumeNodeColor)
		{
			return indicator.EGRollingVolumeProfile(input, tickAggregation, lookbackHours, rightOffsetPixels, profileLabelOffsetPixels, profileWidthPercent, profileOpacity, showBoundaryLine, boundaryLineWidthPixels, boundaryLineStyle, boundaryLineOpacity, showBoundaryLabels, showValueArea, valueAreaPercent, valueAreaOpacity, extendValueAreaLine, valueAreaLineWidthPixels, valueAreaLineStyle, valueAreaLineOpacity, showValueAreaLabels, showSecondaryValueArea, secondaryValueAreaPercent, secondaryValueAreaFromPrimary, secondaryValueAreaOpacity, extendSecondaryValueAreaLine, secondaryValueAreaLineWidthPixels, secondaryValueAreaLineStyle, secondaryValueAreaLineOpacity, showSecondaryValueAreaLabels, showPOC, pOCOpacity, showPOCLine, pOCLineWidthPixels, pOCLineStyle, pOCLineOpacity, showPOCLabels, showHighVolumeNodes, highVolumeNodeCount, highVolumeNodeClusterExpansionPercent, highVolumeNodeMinSpacingRows, highVolumeNodeOpacity, extendHighVolumeNodeLines, highVolumeNodeLineOpacity, showHighVolumeNodeLabels, labelFontFamily, labelFontSize, profileColor, valueAreaColor, secondaryValueAreaColor, pOCColor, highVolumeNodeColor);
		}
	}
}

#endregion
