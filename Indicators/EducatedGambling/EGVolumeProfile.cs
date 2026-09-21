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
using SharpDX;
using SharpDX.Direct2D1;
#endregion

namespace NinjaTrader.NinjaScript.Indicators.EducatedGambling
{
    [CategoryOrder("Setting", 1)]
    [CategoryOrder("Profile", 2)]
    [CategoryOrder("Value Area", 3)]
    [CategoryOrder("Secondary Value Area", 4)]
    [CategoryOrder("Buy/Sell", 5)]
    [CategoryOrder("Levels of Interest", 6)]
    [CategoryOrder("Colors", 7)]
    public class EGVolumeProfile : Indicator
    {
        private struct LargeOrderEvent
        {
            public double Size;
            public bool IsBuy;
        }

        private class SessionProfileData
        {
            public int StartBar;
            public int EndBar = -1;
            public readonly Dictionary<double, double> Volume = new Dictionary<double, double>();
            public readonly Dictionary<double, double> SellVolume = new Dictionary<double, double>();
            public readonly Dictionary<double, double> BuyVolume = new Dictionary<double, double>();
            public readonly Dictionary<double, List<LargeOrderEvent>> LargeOrders = new Dictionary<double, List<LargeOrderEvent>>();
            public double MaxLargeOrderSize;
            public readonly Queue<double> BvcPriceChanges = new Queue<double>();
            public double CurrentBarSellVolume;
            public double CurrentBarBuyVolume;
        }

        private readonly List<SessionProfileData> sessions = new List<SessionProfileData>();

        private SessionProfileData CurrentSession
        {
            get { return sessions.Count > 0 ? sessions[sessions.Count - 1] : null; }
        }

        // These track the market's actual Bid/Ask quote prices (not the aggressor-side
        // classification, which uses Buy/Sell terminology below).
        private double currentBid;
        private double currentAsk;
        private int lastGaltonProcessedBar = -1;

        private SessionIterator profileSessionIterator;
        private DateTime cacheSessionEnd = Core.Globals.MinDate;

        private SharpDX.Direct2D1.Brush profileBrushDx;
        private SharpDX.Direct2D1.Brush valueAreaBrushDx;
        private SharpDX.Direct2D1.Brush secondaryValueAreaBrushDx;
        private SharpDX.Direct2D1.Brush pocBrushDx;
        private SharpDX.Direct2D1.Brush sellBrushDx;
        private SharpDX.Direct2D1.Brush buyBrushDx;
        private SharpDX.Direct2D1.Brush largeSellBrushDx;
        private SharpDX.Direct2D1.Brush largeBuyBrushDx;
        private NinjaTrader.Gui.Stroke valueAreaLineStroke;
        private NinjaTrader.Gui.Stroke secondaryValueAreaLineStroke;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "EGVolumeProfile";
                Description = "Volume profile with POC, Value Area, Secondary Value Area, and Buy/Sell aggressor classification (tick-based or Galton), built from hidden per-tick series. Can display one profile per session across the loaded history.";
                Calculate = Calculate.OnEachTick;
                IsOverlay = true;
                DisplayInDataBox = false;
                PaintPriceMarkers = false;
                ScaleJustification = NinjaTrader.Gui.Chart.ScaleJustification.Right;
                IsSuspendedWhileInactive = false;

                TradingHoursTemplate = NinjaTrader.Data.TradingHours.SystemDefault ?? "";
                SessionsToDisplay = 1;
                TickAggregation = 10;
                DisplayMode = EGVolumeProfileDisplayMode.Standard;
                ViewMode = EGVolumeProfileViewMode.Volume;
                ClassificationMethod = EGVolumeProfileClassificationMethod.TickBased;
                GaltonSource = EGVolumeProfileGaltonSource.BVC;
                BVCLookback = 20;
                IsolateDominantSide = false;
                GradientPrint = EGVolumeProfileGradientPrint.Off;
                ProfileAlignment = EGVolumeProfileAlignment.Left;

                ShowPOC = true;
                POCOpacity = 100;
                ProfileWidthPercent = 30;
                ProfileOpacity = 70;

                ShowValueArea = true;
                ValueAreaPercent = 70;
                ValueAreaOpacity = 85;
                ExtendValueAreaLine = false;
                ValueAreaLineWidthPixels = 2;
                ValueAreaLineStyle = DashStyleHelper.Solid;
                ValueAreaLineOpacity = 100;
                ShowValueAreaLabels = false;
                ValueAreaLabelFontFamily = "Arial";
                ValueAreaLabelFontSize = 11;

                ShowSecondaryValueArea = false;
                SecondaryValueAreaPercent = 40;
                SecondaryValueAreaOpacity = 40;
                ExtendSecondaryValueAreaLine = false;
                SecondaryValueAreaLineWidthPixels = 2;
                SecondaryValueAreaLineStyle = DashStyleHelper.Solid;
                SecondaryValueAreaLineOpacity = 100;
                ShowSecondaryValueAreaLabels = false;
                SecondaryValueAreaLabelFontFamily = "Arial";
                SecondaryValueAreaLabelFontSize = 11;

                BuySellLayout = EGVolumeProfileBuySellLayout.Overlay;

                ShowLargeOrders = false;
                LargeOrderThreshold = 50;
                LargeOrderStyle = EGVolumeProfileLargeOrderStyle.Dots;
                LargeOrderMaxPerRow = 20;
                LargeOrderSize = 8;
                LargeOrderSpacing = 4;
                ShowImbalance = false;
                ImbalanceThresholdPercent = 70;

                ProfileColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 70, 130, 180));
                ValueAreaColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 255, 165, 0));
                SecondaryValueAreaColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 135, 206, 235));
                POCColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 220, 20, 60));
                SellColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 220, 50, 50));
                BuyColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 50, 180, 90));
                LargeSellColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 240, 128, 128));
                LargeBuyColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 144, 238, 144));
            }
            else if (State == State.Configure)
            {
                AddDataSeries(Instrument.FullName, BarsPeriodType.Tick, 1, MarketDataType.Last);
                AddDataSeries(Instrument.FullName, BarsPeriodType.Tick, 1, MarketDataType.Bid);
                AddDataSeries(Instrument.FullName, BarsPeriodType.Tick, 1, MarketDataType.Ask);
            }
            else if (State == State.DataLoaded)
            {
                profileSessionIterator = new SessionIterator(ResolveTradingHours());
            }
            else if (State == State.Terminated)
            {
                DisposeDxBrushes();
            }
        }

        private NinjaTrader.Data.TradingHours ResolveTradingHours()
        {
            if (!string.IsNullOrEmpty(TradingHoursTemplate))
            {
                NinjaTrader.Data.TradingHours th = NinjaTrader.Data.TradingHours.Get(TradingHoursTemplate);
                if (th != null) return th;
            }
            return Bars.TradingHours;
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
            if (sellBrushDx != null) { sellBrushDx.Dispose(); sellBrushDx = null; }
            if (buyBrushDx != null) { buyBrushDx.Dispose(); buyBrushDx = null; }
            if (largeSellBrushDx != null) { largeSellBrushDx.Dispose(); largeSellBrushDx = null; }
            if (largeBuyBrushDx != null) { largeBuyBrushDx.Dispose(); largeBuyBrushDx = null; }
        }

        public override void OnRenderTargetChanged()
        {
            DisposeDxBrushes();

            if (RenderTarget == null) return;

            if (ProfileColor != null) profileBrushDx = ProfileColor.ToDxBrush(RenderTarget);
            if (ValueAreaColor != null) valueAreaBrushDx = ValueAreaColor.ToDxBrush(RenderTarget);
            if (SecondaryValueAreaColor != null) secondaryValueAreaBrushDx = SecondaryValueAreaColor.ToDxBrush(RenderTarget);
            if (POCColor != null) pocBrushDx = POCColor.ToDxBrush(RenderTarget);
            if (SellColor != null) sellBrushDx = SellColor.ToDxBrush(RenderTarget);
            if (BuyColor != null) buyBrushDx = BuyColor.ToDxBrush(RenderTarget);
            if (LargeSellColor != null) largeSellBrushDx = LargeSellColor.ToDxBrush(RenderTarget);
            if (LargeBuyColor != null) largeBuyBrushDx = LargeBuyColor.ToDxBrush(RenderTarget);

            valueAreaLineStroke = new NinjaTrader.Gui.Stroke(ValueAreaColor ?? System.Windows.Media.Brushes.Orange, ValueAreaLineStyle, ValueAreaLineWidthPixels);
            valueAreaLineStroke.RenderTarget = RenderTarget;

            secondaryValueAreaLineStroke = new NinjaTrader.Gui.Stroke(SecondaryValueAreaColor ?? System.Windows.Media.Brushes.LightBlue, SecondaryValueAreaLineStyle, SecondaryValueAreaLineWidthPixels);
            secondaryValueAreaLineStroke.RenderTarget = RenderTarget;
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress == 0)
            {
                if (ClassificationMethod == EGVolumeProfileClassificationMethod.Galton && CurrentBar != lastGaltonProcessedBar)
                {
                    lastGaltonProcessedBar = CurrentBar;
                    ProcessGaltonBar();
                }

                if (Time[0] > cacheSessionEnd)
                {
                    profileSessionIterator.GetNextSession(Time[0], true);
                    cacheSessionEnd = profileSessionIterator.ActualSessionEnd;

                    if (sessions.Count > 0)
                        sessions[sessions.Count - 1].EndBar = CurrentBar - 1;

                    sessions.Add(new SessionProfileData { StartBar = CurrentBar });

                    int maxKeep = Math.Max(1, SessionsToDisplay);
                    while (sessions.Count > maxKeep) sessions.RemoveAt(0);
                }
                return;
            }

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

            if (BarsInProgress != 1) return;

            SessionProfileData session = CurrentSession;
            if (session == null) return;

            double tickSize = Instrument.MasterInstrument.TickSize;
            double rowSize = tickSize * Math.Max(1, TickAggregation);
            double price = Close[0];
            double bucket = Instrument.MasterInstrument.RoundToTickSize(Math.Floor(price / rowSize) * rowSize);
            double vol = Volume[0];

            // Quote rule: a print at or above the current ask means a buyer crossed the spread
            // (buy-initiated); at or below the current bid means a seller crossed it (sell-initiated).
            bool isBuy = currentAsk > 0 && price >= currentAsk;
            bool isSell = !isBuy && currentBid > 0 && price <= currentBid;

            if (ShowLargeOrders && vol >= LargeOrderThreshold && (isBuy || isSell))
            {
                List<LargeOrderEvent> list;
                if (!session.LargeOrders.TryGetValue(bucket, out list))
                {
                    list = new List<LargeOrderEvent>();
                    session.LargeOrders[bucket] = list;
                }

                // Safety cap on stacked markers per price level: once at the cap, new large
                // orders at this price stop being tracked, existing ones stay.
                if (list.Count < LargeOrderMaxPerRow)
                {
                    list.Add(new LargeOrderEvent { Size = vol, IsBuy = isBuy });
                    if (vol > session.MaxLargeOrderSize) session.MaxLargeOrderSize = vol;
                }
            }

            if (ClassificationMethod == EGVolumeProfileClassificationMethod.Galton)
            {
                if (isBuy) session.CurrentBarBuyVolume += vol;
                else if (isSell) session.CurrentBarSellVolume += vol;
                return;
            }

            double existing;
            session.Volume[bucket] = (session.Volume.TryGetValue(bucket, out existing) ? existing : 0) + vol;

            if (isBuy)
                session.BuyVolume[bucket] = (session.BuyVolume.TryGetValue(bucket, out existing) ? existing : 0) + vol;
            else if (isSell)
                session.SellVolume[bucket] = (session.SellVolume.TryGetValue(bucket, out existing) ? existing : 0) + vol;
        }

        private void ProcessGaltonBar()
        {
            if (CurrentBar < 1) return;

            SessionProfileData session = CurrentSession;
            if (session == null) return;

            double closedClose = Close[1];
            double closedHigh = High[1];
            double closedLow = Low[1];
            double closedVolume = Volume[1];

            double buyTotal;
            double sellTotal;

            if (GaltonSource == EGVolumeProfileGaltonSource.TickAggregate)
            {
                buyTotal = session.CurrentBarBuyVolume;
                sellTotal = session.CurrentBarSellVolume;
            }
            else
            {
                double priceChange = CurrentBar >= 2 ? closedClose - Close[2] : 0;
                session.BvcPriceChanges.Enqueue(priceChange);
                while (session.BvcPriceChanges.Count > Math.Max(2, BVCLookback)) session.BvcPriceChanges.Dequeue();

                double sigma = GetStdDev(session.BvcPriceChanges);
                double z = sigma > 0 ? priceChange / sigma : 0;
                double buyFraction = NormalCdf(z);

                buyTotal = closedVolume * buyFraction;
                sellTotal = closedVolume * (1.0 - buyFraction);
            }

            session.CurrentBarSellVolume = 0;
            session.CurrentBarBuyVolume = 0;

            if (buyTotal + sellTotal <= 0) return;

            double tickSize = Instrument.MasterInstrument.TickSize;
            double rowSize = tickSize * Math.Max(1, TickAggregation);

            Dictionary<double, double> weights = ComputeGaltonWeights(closedClose, closedLow, closedHigh, rowSize);

            double existing;
            foreach (KeyValuePair<double, double> kv in weights)
            {
                double bucket = kv.Key;
                double w = kv.Value;

                session.Volume[bucket] = (session.Volume.TryGetValue(bucket, out existing) ? existing : 0) + w * (buyTotal + sellTotal);
                session.BuyVolume[bucket] = (session.BuyVolume.TryGetValue(bucket, out existing) ? existing : 0) + w * buyTotal;
                session.SellVolume[bucket] = (session.SellVolume.TryGetValue(bucket, out existing) ? existing : 0) + w * sellTotal;
            }
        }

        private Dictionary<double, double> ComputeGaltonWeights(double closePrice, double lowPrice, double highPrice, double rowSize)
        {
            var weights = new Dictionary<double, double>();

            double closeBucket = Instrument.MasterInstrument.RoundToTickSize(Math.Round(closePrice / rowSize) * rowSize);
            double lowBucket = Instrument.MasterInstrument.RoundToTickSize(Math.Round(lowPrice / rowSize) * rowSize);
            double highBucket = Instrument.MasterInstrument.RoundToTickSize(Math.Round(highPrice / rowSize) * rowSize);

            int nUp = Math.Max(0, (int)Math.Round((highBucket - closeBucket) / rowSize));
            int nDown = Math.Max(0, (int)Math.Round((closeBucket - lowBucket) / rowSize));

            AddGaltonHalf(weights, closeBucket, rowSize, nUp, 1);
            AddGaltonHalf(weights, closeBucket, rowSize, nDown, -1);

            return weights;
        }

        private void AddGaltonHalf(Dictionary<double, double> weights, double closeBucket, double rowSize, int n, int direction)
        {
            double sigma = Math.Max(1.0, n / 2.5);
            var raw = new double[n + 1];
            double sum = 0;

            for (int k = 0; k <= n; k++)
            {
                raw[k] = Math.Exp(-(k * k) / (2.0 * sigma * sigma));
                sum += raw[k];
            }

            for (int k = 0; k <= n; k++)
            {
                double bucket = Instrument.MasterInstrument.RoundToTickSize(closeBucket + direction * k * rowSize);
                double w = (raw[k] / sum) * 0.5;
                double existing;
                weights[bucket] = (weights.TryGetValue(bucket, out existing) ? existing : 0) + w;
            }
        }

        private static double GetStdDev(Queue<double> values)
        {
            if (values.Count < 2) return 0;
            double mean = values.Average();
            double sumSq = values.Sum(v => (v - mean) * (v - mean));
            return Math.Sqrt(sumSq / (values.Count - 1));
        }

        private static double NormalCdf(double z)
        {
            return 0.5 * (1.0 + Erf(z / Math.Sqrt(2.0)));
        }

        private static double Erf(double x)
        {
            double sign = x < 0 ? -1.0 : 1.0;
            x = Math.Abs(x);
            const double a1 = 0.254829592, a2 = -0.284496736, a3 = 1.421413741, a4 = -1.453152027, a5 = 1.061405429, p = 0.3275911;
            double t = 1.0 / (1.0 + p * x);
            double y = 1.0 - (((((a5 * t + a4) * t) + a3) * t + a2) * t + a1) * t * Math.Exp(-x * x);
            return sign * y;
        }

        private void ComputeValueArea(Dictionary<double, double> data, List<double> sortedBuckets, double pocPrice, double targetPercent, double totalVolume, out double low, out double high)
        {
            if (sortedBuckets.Count == 0 || totalVolume <= 0)
            {
                low = pocPrice;
                high = pocPrice;
                return;
            }

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

        private static void BuildIsolatedSides(Dictionary<double, double> sell, Dictionary<double, double> buy,
            out Dictionary<double, double> isolatedSell, out Dictionary<double, double> isolatedBuy)
        {
            isolatedSell = new Dictionary<double, double>();
            isolatedBuy = new Dictionary<double, double>();

            var prices = new HashSet<double>(sell.Keys);
            prices.UnionWith(buy.Keys);

            foreach (double price in prices)
            {
                double s, b;
                sell.TryGetValue(price, out s);
                buy.TryGetValue(price, out b);

                // Every price row gets an entry on both sides (zero on the losing side) rather
                // than omitting it, so Outline mode's envelope actually dips to zero width at
                // that row instead of drawing a straight line across the gap to the next
                // surviving row.
                isolatedSell[price] = s > b ? s : 0;
                isolatedBuy[price] = b > s ? b : 0;
            }
        }

        // Off returns full intensity (no gradient effect); Volume scales by this row's volume
        // relative to the profile's own max row; Delta scales by this row's Buy/Sell imbalance
        // magnitude relative to the MOST imbalanced row actually present in this profile (not
        // against a theoretical 100%-one-sided max, which real order flow rarely reaches at any
        // single price - normalizing against it left nearly every row clustered near the opacity
        // floor with no visible contrast). Read from the session's raw Buy/Sell data regardless
        // of which `data` dictionary is actually being rendered (Volume/Buy/Sell/isolated).
        private static double RawDeltaImbalance(SessionProfileData session, double price)
        {
            double s, b;
            session.SellVolume.TryGetValue(price, out s);
            session.BuyVolume.TryGetValue(price, out b);
            double total = b + s;
            return total > 0 ? Math.Abs(b - s) / total : 0;
        }

        private double ComputeGradientIntensity(SessionProfileData session, double price, double vol, double maxVolume, double maxDeltaImbalance)
        {
            if (GradientPrint == EGVolumeProfileGradientPrint.Volume)
                return maxVolume > 0 ? vol / maxVolume : 0;

            if (GradientPrint == EGVolumeProfileGradientPrint.Delta)
                return maxDeltaImbalance > 0 ? RawDeltaImbalance(session, price) / maxDeltaImbalance : 0;

            return 1.0;
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);

            if (Bars == null || ChartControl == null || IsInHitTest) return;
            if (sessions.Count == 0) return;
            if (profileBrushDx == null || valueAreaBrushDx == null || secondaryValueAreaBrushDx == null || pocBrushDx == null) return;
            if (sellBrushDx == null || buyBrushDx == null || largeSellBrushDx == null || largeBuyBrushDx == null) return;

            double tickSize = Instrument.MasterInstrument.TickSize;
            double rowSize = tickSize * Math.Max(1, TickAggregation);

            int sessionsToShow = Math.Max(1, SessionsToDisplay);
            int startIdx = Math.Max(0, sessions.Count - sessionsToShow);
            bool rightAlign = ProfileAlignment == EGVolumeProfileAlignment.Right;

            for (int i = startIdx; i < sessions.Count; i++)
            {
                SessionProfileData session = sessions[i];
                int effectiveEndBar = session.EndBar >= 0 ? session.EndBar : CurrentBar;

                if (effectiveEndBar < ChartBars.FromIndex || session.StartBar > ChartBars.ToIndex) continue;

                int anchorBar = rightAlign
                    ? Math.Min(ChartBars.ToIndex, effectiveEndBar)
                    : Math.Max(ChartBars.FromIndex, session.StartBar);
                float anchorX = chartControl.GetXByBarIndex(ChartBars, anchorBar);

                RenderSessionProfile(chartScale, session, anchorX, rightAlign, rowSize);
                RenderLevelsOfInterest(chartScale, session, anchorX, rightAlign);

                if (ExtendValueAreaLine || ExtendSecondaryValueAreaLine)
                {
                    float sessionStartX = chartControl.GetXByBarIndex(ChartBars, session.StartBar);
                    float sessionEndX = chartControl.GetXByBarIndex(ChartBars, effectiveEndBar);
                    RenderValueAreaLines(chartScale, session, sessionStartX, sessionEndX);
                }
            }
        }

        private void RenderSessionProfile(ChartScale chartScale, SessionProfileData session, float anchorX, bool rightAlign, double rowSize)
        {
            if (ViewMode == EGVolumeProfileViewMode.Sell)
            {
                RenderProfile(chartScale, session.SellVolume, sellBrushDx, anchorX, rightAlign, rowSize, session);
            }
            else if (ViewMode == EGVolumeProfileViewMode.Buy)
            {
                RenderProfile(chartScale, session.BuyVolume, buyBrushDx, anchorX, rightAlign, rowSize, session);
            }
            else if (ViewMode == EGVolumeProfileViewMode.BuySell)
            {
                Dictionary<double, double> sellData = session.SellVolume;
                Dictionary<double, double> buyData = session.BuyVolume;

                if (IsolateDominantSide)
                    BuildIsolatedSides(session.SellVolume, session.BuyVolume, out sellData, out buyData);

                if (BuySellLayout == EGVolumeProfileBuySellLayout.Mirror)
                {
                    float fullMaxWidth = (float)(ProfileWidthPercent / 100.0 * ChartPanel.W);
                    float halfMaxWidth = fullMaxWidth / 2f;
                    float centerX = rightAlign ? anchorX - halfMaxWidth : anchorX + halfMaxWidth;

                    // Sell always grows toward the left of center, Buy always toward the right,
                    // independent of Profile Alignment (which only moves the anchor/center position).
                    RenderProfile(chartScale, sellData, sellBrushDx, centerX, true, rowSize, session, halfMaxWidth);
                    RenderProfile(chartScale, buyData, buyBrushDx, centerX, false, rowSize, session, halfMaxWidth);
                }
                else
                {
                    RenderProfile(chartScale, sellData, sellBrushDx, anchorX, rightAlign, rowSize, session);
                    RenderProfile(chartScale, buyData, buyBrushDx, anchorX, rightAlign, rowSize, session);
                }
            }
            else
            {
                RenderProfile(chartScale, session.Volume, profileBrushDx, anchorX, rightAlign, rowSize, session);
            }
        }

        // Value Area / Secondary Value Area lines are computed from the session's total Volume
        // profile (not per Buy/Sell side), since VA/SVA colors are single global colors and the
        // classic Value Area concept is defined over total volume - so the lines stay identical
        // regardless of which View Mode is currently selected. They span the session's own bar
        // range (start to end/current), independent of Profile Alignment.
        private void RenderValueAreaLines(ChartScale chartScale, SessionProfileData session, float sessionStartX, float sessionEndX)
        {
            if (session.Volume.Count == 0) return;

            List<double> buckets = session.Volume.Keys.OrderBy(k => k).ToList();
            double totalVolume = session.Volume.Values.Sum();
            double pocPrice = session.Volume.OrderByDescending(kv => kv.Value).First().Key;

            double vaLow, vaHigh, svaLow, svaHigh;
            ComputeValueArea(session.Volume, buckets, pocPrice, ValueAreaPercent, totalVolume, out vaLow, out vaHigh);
            ComputeValueArea(session.Volume, buckets, pocPrice, SecondaryValueAreaPercent, totalVolume, out svaLow, out svaHigh);

            SharpDX.DirectWrite.TextFormat vaLabelFormat = null;
            if (ExtendValueAreaLine && ShowValueAreaLabels)
            {
                SimpleFont font = new SimpleFont(ValueAreaLabelFontFamily, ValueAreaLabelFontSize);
                vaLabelFormat = font.ToDirectWriteTextFormat();
                vaLabelFormat.TextAlignment = SharpDX.DirectWrite.TextAlignment.Leading;
                vaLabelFormat.ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center;
            }

            SharpDX.DirectWrite.TextFormat svaLabelFormat = null;
            if (ExtendSecondaryValueAreaLine && ShowSecondaryValueAreaLabels)
            {
                SimpleFont font = new SimpleFont(SecondaryValueAreaLabelFontFamily, SecondaryValueAreaLabelFontSize);
                svaLabelFormat = font.ToDirectWriteTextFormat();
                svaLabelFormat.TextAlignment = SharpDX.DirectWrite.TextAlignment.Leading;
                svaLabelFormat.ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center;
            }

            if (ExtendValueAreaLine)
            {
                DrawExtendedLine(chartScale, vaHigh, sessionStartX, sessionEndX, valueAreaBrushDx, ValueAreaLineWidthPixels, ValueAreaLineOpacity, valueAreaLineStroke, ShowValueAreaLabels, "VAH", vaLabelFormat);
                DrawExtendedLine(chartScale, vaLow, sessionStartX, sessionEndX, valueAreaBrushDx, ValueAreaLineWidthPixels, ValueAreaLineOpacity, valueAreaLineStroke, ShowValueAreaLabels, "VAL", vaLabelFormat);
            }

            if (ExtendSecondaryValueAreaLine)
            {
                DrawExtendedLine(chartScale, svaHigh, sessionStartX, sessionEndX, secondaryValueAreaBrushDx, SecondaryValueAreaLineWidthPixels, SecondaryValueAreaLineOpacity, secondaryValueAreaLineStroke, ShowSecondaryValueAreaLabels, "SVAH", svaLabelFormat);
                DrawExtendedLine(chartScale, svaLow, sessionStartX, sessionEndX, secondaryValueAreaBrushDx, SecondaryValueAreaLineWidthPixels, SecondaryValueAreaLineOpacity, secondaryValueAreaLineStroke, ShowSecondaryValueAreaLabels, "SVAL", svaLabelFormat);
            }

            if (vaLabelFormat != null) vaLabelFormat.Dispose();
            if (svaLabelFormat != null) svaLabelFormat.Dispose();
        }

        private void DrawExtendedLine(ChartScale chartScale, double price, float x0, float x1, SharpDX.Direct2D1.Brush brush, int widthPixels, double opacityPercent,
            NinjaTrader.Gui.Stroke stroke, bool showLabel, string labelText, SharpDX.DirectWrite.TextFormat labelFormat)
        {
            if (brush == null) return;
            if (price < chartScale.MinValue || price > chartScale.MaxValue) return;

            float y = chartScale.GetYByValue(price);
            brush.Opacity = (float)(Math.Max(0, Math.Min(100, opacityPercent)) / 100.0);
            RenderTarget.DrawLine(new SharpDX.Vector2(x0, y), new SharpDX.Vector2(x1, y), brush, widthPixels, stroke == null ? null : stroke.StrokeStyle);

            if (showLabel && labelFormat != null)
            {
                var labelRect = new SharpDX.RectangleF(x1 + 4f, y - 8f, 60f, 16f);
                RenderTarget.DrawText(labelText, labelFormat, labelRect, brush, SharpDX.Direct2D1.DrawTextOptions.None);
            }
        }

        // Large-order and imbalance markers are properties of a price level itself, so they're
        // drawn once per session off the same anchor point used by the primary profile, rather
        // than being tied to whichever ViewMode/Layout combination is currently selected.
        private void RenderLevelsOfInterest(ChartScale chartScale, SessionProfileData session, float anchorX, bool rightAlign)
        {
            if (ShowLargeOrders && session.LargeOrders.Count > 0)
            {
                // In Mirror layout, markers must originate from the same center point the
                // Buy/Sell bars themselves split from (not the session's Left/Right anchor), with
                // Sell markers always stacking toward the left of center and Buy markers toward
                // the right - independent of Profile Alignment, matching
                // RenderSessionProfile's Mirror branch.
                bool mirrorBuySell = ViewMode == EGVolumeProfileViewMode.BuySell && BuySellLayout == EGVolumeProfileBuySellLayout.Mirror;

                float originX = anchorX;
                if (mirrorBuySell)
                {
                    float fullMaxWidth = (float)(ProfileWidthPercent / 100.0 * ChartPanel.W);
                    float halfMaxWidth = fullMaxWidth / 2f;
                    originX = rightAlign ? anchorX - halfMaxWidth : anchorX + halfMaxWidth;
                }

                float dotRadius = LargeOrderSize / 2f;
                float dotStep = LargeOrderSize + LargeOrderSpacing;
                float overlayDir = rightAlign ? -1f : 1f;
                const float originGap = 5f;
                const float minDotOpacity = 0.35f;

                foreach (KeyValuePair<double, List<LargeOrderEvent>> kv in session.LargeOrders)
                {
                    double price = kv.Key;
                    if (price < chartScale.MinValue || price > chartScale.MaxValue) continue;

                    float y = chartScale.GetYByValue(price);
                    List<LargeOrderEvent> events = kv.Value;

                    int sellStackIndex = 0;
                    int buyStackIndex = 0;

                    for (int i = 0; i < events.Count; i++)
                    {
                        LargeOrderEvent evt = events[i];
                        SharpDX.Direct2D1.Brush markerBrush = evt.IsBuy ? largeBuyBrushDx : largeSellBrushDx;

                        // Gradient: smallest qualifying print still visible at a floor opacity,
                        // the single biggest large order seen this session renders at full opacity.
                        float sizeRatio = session.MaxLargeOrderSize > 0
                            ? (float)(evt.Size / session.MaxLargeOrderSize)
                            : 1f;
                        markerBrush.Opacity = minDotOpacity + (1f - minDotOpacity) * sizeRatio;

                        float dir;
                        int stackIndex;
                        if (mirrorBuySell)
                        {
                            dir = evt.IsBuy ? 1f : -1f;
                            stackIndex = evt.IsBuy ? buyStackIndex++ : sellStackIndex++;
                        }
                        else
                        {
                            dir = overlayDir;
                            stackIndex = i;
                        }

                        float markerCenterX = originX + dir * (originGap + dotRadius + stackIndex * dotStep);

                        if (LargeOrderStyle == EGVolumeProfileLargeOrderStyle.Squares)
                        {
                            var square = new SharpDX.RectangleF(markerCenterX - dotRadius, y - dotRadius, LargeOrderSize, LargeOrderSize);
                            RenderTarget.FillRectangle(square, markerBrush);
                        }
                        else
                        {
                            var dot = new SharpDX.Direct2D1.Ellipse(new SharpDX.Vector2(markerCenterX, y), dotRadius, dotRadius);
                            RenderTarget.FillEllipse(dot, markerBrush);
                        }
                    }
                }
            }

            if (ShowImbalance)
            {
                var prices = new HashSet<double>(session.SellVolume.Keys);
                prices.UnionWith(session.BuyVolume.Keys);

                foreach (double price in prices)
                {
                    if (price < chartScale.MinValue || price > chartScale.MaxValue) continue;

                    double s, b;
                    session.SellVolume.TryGetValue(price, out s);
                    session.BuyVolume.TryGetValue(price, out b);
                    double total = b + s;
                    if (total <= 0) continue;

                    double dominantShare = Math.Max(b, s) / total * 100.0;
                    if (dominantShare < ImbalanceThresholdPercent) continue;

                    SharpDX.Direct2D1.Brush brush = b > s ? buyBrushDx : sellBrushDx;
                    brush.Opacity = 1f;

                    float y = chartScale.GetYByValue(price);
                    const float length = 14f;
                    float x1 = rightAlign ? anchorX + length : anchorX - length;
                    RenderTarget.DrawLine(new SharpDX.Vector2(anchorX, y), new SharpDX.Vector2(x1, y), brush, 2.5f);
                }
            }
        }

        private void RenderProfile(ChartScale chartScale, Dictionary<double, double> data, SharpDX.Direct2D1.Brush baseBrushDx,
            float anchorX, bool rightAlign, double rowSize, SessionProfileData session, float? maxBarWidthOverride = null)
        {
            if (data.Count == 0) return;

            double maxVolume = data.Values.Max();
            if (maxVolume <= 0) return;

            List<double> buckets = data.Keys.OrderBy(k => k).ToList();
            double totalVolume = data.Values.Sum();
            double pocPrice = data.OrderByDescending(kv => kv.Value).First().Key;

            double vaLow, vaHigh, svaLow, svaHigh;
            ComputeValueArea(data, buckets, pocPrice, ValueAreaPercent, totalVolume, out vaLow, out vaHigh);
            ComputeValueArea(data, buckets, pocPrice, SecondaryValueAreaPercent, totalVolume, out svaLow, out svaHigh);

            float maxBarWidth = maxBarWidthOverride ?? (float)(ProfileWidthPercent / 100.0 * ChartPanel.W);

            if (DisplayMode == EGVolumeProfileDisplayMode.Outline)
            {
                RenderOutline(data, chartScale, buckets, pocPrice, vaLow, vaHigh, svaLow, svaHigh, rowSize, maxVolume, anchorX, maxBarWidth, rightAlign, baseBrushDx);
                return;
            }

            const double gradientFloor = 0.2;

            double maxDeltaImbalance = 0;
            if (GradientPrint == EGVolumeProfileGradientPrint.Delta)
            {
                foreach (double price in buckets)
                {
                    double raw = RawDeltaImbalance(session, price);
                    if (raw > maxDeltaImbalance) maxDeltaImbalance = raw;
                }
            }

            foreach (double price in buckets)
            {
                if (price < chartScale.MinValue || price > chartScale.MaxValue) continue;

                double vol = data[price];
                float y = chartScale.GetYByValue(price);
                float rowTop = chartScale.GetYByValue(price + rowSize / 2.0);
                float rowBottom = chartScale.GetYByValue(price - rowSize / 2.0);
                float rowHeight = Math.Max(1f, Math.Abs(rowBottom - rowTop) - 1f);
                float width = (float)(vol / maxVolume * maxBarWidth);

                bool inSecondaryVA = ShowSecondaryValueArea && price >= svaLow && price <= svaHigh;
                bool inVA = ShowValueArea && price >= vaLow && price <= vaHigh;
                bool isPoc = ShowPOC && price == pocPrice;

                SharpDX.Direct2D1.Brush brush = baseBrushDx;
                double baseOpacityPercent = ProfileOpacity;
                if (inVA) { brush = valueAreaBrushDx; baseOpacityPercent = ValueAreaOpacity; }
                if (inSecondaryVA) { brush = secondaryValueAreaBrushDx; baseOpacityPercent = SecondaryValueAreaOpacity; }
                if (isPoc) { brush = pocBrushDx; baseOpacityPercent = POCOpacity; }

                float rowOpacity = (float)(baseOpacityPercent / 100.0);
                if (GradientPrint != EGVolumeProfileGradientPrint.Off)
                {
                    double intensity = ComputeGradientIntensity(session, price, vol, maxVolume, maxDeltaImbalance);
                    rowOpacity *= (float)(gradientFloor + (1.0 - gradientFloor) * intensity);
                }
                brush.Opacity = rowOpacity;

                float barX = rightAlign ? anchorX - width : anchorX;
                var barRect = new SharpDX.RectangleF(barX, y - rowHeight / 2f, width, rowHeight);
                RenderTarget.FillRectangle(barRect, brush);
            }
        }

        private void RenderOutline(Dictionary<double, double> data, ChartScale chartScale, List<double> buckets, double pocPrice,
            double vaLow, double vaHigh, double svaLow, double svaHigh,
            double rowSize, double maxVolume, float anchorX, float maxBarWidth, bool rightAlign, SharpDX.Direct2D1.Brush baseBrushDx)
        {
            using (SharpDX.Direct2D1.PathGeometry baseGeometry = BuildEnvelopeGeometry(data, buckets, chartScale, maxVolume, anchorX, maxBarWidth, rightAlign))
            {
                baseBrushDx.Opacity = (float)(ProfileOpacity / 100.0);
                RenderTarget.FillGeometry(baseGeometry, baseBrushDx);
            }

            if (ShowValueArea)
            {
                List<double> vaBuckets = buckets.Where(p => p >= vaLow && p <= vaHigh).ToList();
                using (SharpDX.Direct2D1.PathGeometry vaGeometry = BuildEnvelopeGeometry(data, vaBuckets, chartScale, maxVolume, anchorX, maxBarWidth, rightAlign))
                {
                    valueAreaBrushDx.Opacity = (float)(ValueAreaOpacity / 100.0);
                    RenderTarget.FillGeometry(vaGeometry, valueAreaBrushDx);
                }
            }

            if (ShowSecondaryValueArea)
            {
                List<double> svaBuckets = buckets.Where(p => p >= svaLow && p <= svaHigh).ToList();
                using (SharpDX.Direct2D1.PathGeometry svaGeometry = BuildEnvelopeGeometry(data, svaBuckets, chartScale, maxVolume, anchorX, maxBarWidth, rightAlign))
                {
                    secondaryValueAreaBrushDx.Opacity = (float)(SecondaryValueAreaOpacity / 100.0);
                    RenderTarget.FillGeometry(svaGeometry, secondaryValueAreaBrushDx);
                }
            }

            DrawSegmentedOutline(data, chartScale, buckets, vaLow, vaHigh, svaLow, svaHigh, maxVolume, anchorX, maxBarWidth, rightAlign, baseBrushDx);

            if (ShowPOC)
            {
                double pocVol;
                data.TryGetValue(pocPrice, out pocVol);
                float y = chartScale.GetYByValue(pocPrice);
                float rowTop = chartScale.GetYByValue(pocPrice + rowSize / 2.0);
                float rowBottom = chartScale.GetYByValue(pocPrice - rowSize / 2.0);
                float rowHeight = Math.Max(1f, Math.Abs(rowBottom - rowTop) - 1f);
                float width = (float)(pocVol / maxVolume * maxBarWidth);
                float barX = rightAlign ? anchorX - width : anchorX;

                pocBrushDx.Opacity = (float)(POCOpacity / 100.0);
                RenderTarget.FillRectangle(new SharpDX.RectangleF(barX, y - rowHeight / 2f, width, rowHeight), pocBrushDx);
            }
        }

        private void DrawSegmentedOutline(Dictionary<double, double> data, ChartScale chartScale, List<double> buckets,
            double vaLow, double vaHigh, double svaLow, double svaHigh,
            double maxVolume, float anchorX, float maxBarWidth, bool rightAlign, SharpDX.Direct2D1.Brush baseBrushDx)
        {
            List<double> descending = buckets.OrderByDescending(p => p).ToList();
            if (descending.Count == 0) return;

            SharpDX.Vector2 prevPoint = default(SharpDX.Vector2);
            bool havePrev = false;

            foreach (double price in descending)
            {
                double vol;
                data.TryGetValue(price, out vol);
                float y = chartScale.GetYByValue(price);
                float width = (float)(vol / maxVolume * maxBarWidth);
                float tipX = rightAlign ? anchorX - width : anchorX + width;
                var point = new SharpDX.Vector2(tipX, y);

                if (havePrev)
                {
                    bool inSecondaryVA = ShowSecondaryValueArea && price >= svaLow && price <= svaHigh;
                    bool inVA = ShowValueArea && price >= vaLow && price <= vaHigh;

                    SharpDX.Direct2D1.Brush segmentBrushDx = baseBrushDx;
                    if (inVA) segmentBrushDx = valueAreaBrushDx;
                    if (inSecondaryVA) segmentBrushDx = secondaryValueAreaBrushDx;

                    segmentBrushDx.Opacity = 1f;
                    RenderTarget.DrawLine(prevPoint, point, segmentBrushDx, 1.5f);
                }

                prevPoint = point;
                havePrev = true;
            }
        }

        private SharpDX.Direct2D1.PathGeometry BuildEnvelopeGeometry(Dictionary<double, double> data, List<double> bucketsAscending, ChartScale chartScale,
            double maxVolume, float anchorX, float maxBarWidth, bool rightAlign)
        {
            var geometry = new SharpDX.Direct2D1.PathGeometry(RenderTarget.Factory);

            using (SharpDX.Direct2D1.GeometrySink sink = geometry.Open())
            {
                if (bucketsAscending.Count == 0)
                {
                    sink.Close();
                    return geometry;
                }

                List<double> descending = bucketsAscending.OrderByDescending(p => p).ToList();
                var points = new List<SharpDX.Vector2>(descending.Count);

                foreach (double price in descending)
                {
                    double vol;
                    data.TryGetValue(price, out vol);
                    float y = chartScale.GetYByValue(price);
                    float width = (float)(vol / maxVolume * maxBarWidth);
                    float tipX = rightAlign ? anchorX - width : anchorX + width;
                    points.Add(new SharpDX.Vector2(tipX, y));
                }

                sink.BeginFigure(new SharpDX.Vector2(anchorX, points[0].Y), SharpDX.Direct2D1.FigureBegin.Filled);
                foreach (SharpDX.Vector2 pt in points) sink.AddLine(pt);
                sink.AddLine(new SharpDX.Vector2(anchorX, points[points.Count - 1].Y));
                sink.EndFigure(SharpDX.Direct2D1.FigureEnd.Closed);
                sink.Close();
            }

            return geometry;
        }

        #region Properties

        // ----- Setting -----

        [NinjaScriptProperty]
        [TypeConverter(typeof(EGVolumeProfileTradingHoursConverter))]
        [Display(Name = "Trading Hours", Description = "Trading hours template used to determine session boundaries", GroupName = "Setting", Order = 1)]
        public string TradingHoursTemplate { get; set; }

        [NinjaScriptProperty]
        [Range(1, 60)]
        [Display(Name = "Sessions", Description = "Number of most-recent sessions to display, each as its own separate profile", GroupName = "Setting", Order = 2)]
        public int SessionsToDisplay { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100000)]
        [Display(Name = "Tick Aggregation", Description = "Number of ticks combined into a single profile row", GroupName = "Setting", Order = 3)]
        public int TickAggregation { get; set; }

        [XmlIgnore]
        [Display(Name = "Display Mode", Description = "Standard draws individual bars per row; Outline draws a single filled, outlined silhouette of the profile", GroupName = "Setting", Order = 4)]
        public EGVolumeProfileDisplayMode DisplayMode { get; set; }

        [Browsable(false)]
        public string DisplayModeSerializable
        {
            get { return DisplayMode.ToString(); }
            set { DisplayMode = (EGVolumeProfileDisplayMode)Enum.Parse(typeof(EGVolumeProfileDisplayMode), value); }
        }

        [XmlIgnore]
        [Display(Name = "View Mode", Description = "Volume shows total traded volume; Buy/Sell shows both aggressor-side profiles at once; Buy and Sell show only that side", GroupName = "Setting", Order = 5)]
        public EGVolumeProfileViewMode ViewMode { get; set; }

        [Browsable(false)]
        public string ViewModeSerializable
        {
            get { return ViewMode.ToString(); }
            set { ViewMode = (EGVolumeProfileViewMode)Enum.Parse(typeof(EGVolumeProfileViewMode), value); }
        }

        [XmlIgnore]
        [Display(Name = "Classification Method", Description = "Tick-Based classifies each real trade print as buy- or sell-initiated via the quote rule (hit the ask = buy, hit the bid = sell); Galton estimates each bar's Buy/Sell totals and spreads them across that bar's Low-High range using a Galton-board-style binomial curve centered at Close", GroupName = "Setting", Order = 6)]
        public EGVolumeProfileClassificationMethod ClassificationMethod { get; set; }

        [Browsable(false)]
        public string ClassificationMethodSerializable
        {
            get { return ClassificationMethod.ToString(); }
            set { ClassificationMethod = (EGVolumeProfileClassificationMethod)Enum.Parse(typeof(EGVolumeProfileClassificationMethod), value); }
        }

        [XmlIgnore]
        [Display(Name = "Galton Source", Description = "Only used when Classification Method is Galton. BVC estimates each bar's Buy/Sell split from its close-to-close price change normalized by volatility (no tick data needed); Tick Aggregate sums the tick-based Buy/Sell classification per bar instead of per price, then re-spreads it with the Galton curve", GroupName = "Setting", Order = 7)]
        public EGVolumeProfileGaltonSource GaltonSource { get; set; }

        [Browsable(false)]
        public string GaltonSourceSerializable
        {
            get { return GaltonSource.ToString(); }
            set { GaltonSource = (EGVolumeProfileGaltonSource)Enum.Parse(typeof(EGVolumeProfileGaltonSource), value); }
        }

        [NinjaScriptProperty]
        [Range(2, 500)]
        [Display(Name = "BVC Lookback", Description = "Number of bars used to compute the price-change volatility in the BVC formula", GroupName = "Setting", Order = 8)]
        public int BVCLookback { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Isolate Dominant Side", Description = "When View Mode is Buy/Sell, only draws the side with more volume at each price row; the smaller side is zeroed out at that row so the two sides never both extend at the same level", GroupName = "Setting", Order = 9)]
        public bool IsolateDominantSide { get; set; }

        [XmlIgnore]
        [Display(Name = "Gradient Print", Description = "Off disables gradient shading; Volume shades each row's opacity by its volume relative to the profile's own max row; Delta shades by that row's Buy/Sell imbalance magnitude, read from session data regardless of View Mode. Only applies when Display Mode is Standard", GroupName = "Setting", Order = 10)]
        public EGVolumeProfileGradientPrint GradientPrint { get; set; }

        [Browsable(false)]
        public string GradientPrintSerializable
        {
            get { return GradientPrint.ToString(); }
            set { GradientPrint = (EGVolumeProfileGradientPrint)Enum.Parse(typeof(EGVolumeProfileGradientPrint), value); }
        }

        // ----- Profile -----

        [NinjaScriptProperty]
        [Display(Name = "Display POC", Description = "Color the Point of Control row differently from the rest of the profile", GroupName = "Profile", Order = 1)]
        public bool ShowPOC { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "POC Opacity", Description = "Opacity of the Point of Control row", GroupName = "Profile", Order = 2)]
        public double POCOpacity { get; set; }

        [XmlIgnore]
        [Display(Name = "Profile Alignment", Description = "Left anchors each profile at its own session start and extends it rightward; Right anchors it at its own session end and extends it leftward", GroupName = "Profile", Order = 3)]
        public EGVolumeProfileAlignment ProfileAlignment { get; set; }

        [Browsable(false)]
        public string ProfileAlignmentSerializable
        {
            get { return ProfileAlignment.ToString(); }
            set { ProfileAlignment = (EGVolumeProfileAlignment)Enum.Parse(typeof(EGVolumeProfileAlignment), value); }
        }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Profile Width (%)", Description = "Max bar length as a percentage of the chart panel width", GroupName = "Profile", Order = 4)]
        public double ProfileWidthPercent { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Profile Opacity", Description = "Opacity of profile bars outside the Value Area(s)", GroupName = "Profile", Order = 5)]
        public double ProfileOpacity { get; set; }

        // ----- Value Area -----

        [NinjaScriptProperty]
        [Display(Name = "Display Value Area", Description = "Highlight rows inside the Value Area", GroupName = "Value Area", Order = 1)]
        public bool ShowValueArea { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Value Area (%)", Description = "Percentage of session volume contained in the Value Area", GroupName = "Value Area", Order = 2)]
        public double ValueAreaPercent { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Value Area Opacity", Description = "Opacity of Value Area rows", GroupName = "Value Area", Order = 3)]
        public double ValueAreaOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Extend Value Area Line", Description = "Draw the Value Area High and Low as horizontal lines spanning the full session, computed from the session's total volume regardless of View Mode", GroupName = "Value Area", Order = 4)]
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
        [Display(Name = "Value Area Line Opacity", Description = "Opacity of the extended Value Area lines (and their labels)", GroupName = "Value Area", Order = 7)]
        public double ValueAreaLineOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Display Value Area Labels", Description = "Show VAH/VAL text labels at the session end of the extended Value Area lines", GroupName = "Value Area", Order = 8)]
        public bool ShowValueAreaLabels { get; set; }

        [NinjaScriptProperty]
        [TypeConverter(typeof(EGVolumeProfileFontFamilyConverter))]
        [Display(Name = "Value Area Label Font Family", Description = "Font family of the VAH/VAL labels", GroupName = "Value Area", Order = 9)]
        public string ValueAreaLabelFontFamily { get; set; }

        [NinjaScriptProperty]
        [TypeConverter(typeof(EGVolumeProfileFontSizeConverter))]
        [Display(Name = "Value Area Label Font Size", Description = "Font size of the VAH/VAL labels", GroupName = "Value Area", Order = 10)]
        public double ValueAreaLabelFontSize { get; set; }

        // ----- Secondary Value Area -----

        [NinjaScriptProperty]
        [Display(Name = "Display Secondary Value Area", Description = "Highlight rows inside the Secondary Value Area", GroupName = "Secondary Value Area", Order = 1)]
        public bool ShowSecondaryValueArea { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Secondary Value Area (%)", Description = "Percentage of session volume contained in the Secondary Value Area", GroupName = "Secondary Value Area", Order = 2)]
        public double SecondaryValueAreaPercent { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Secondary Value Area Opacity", Description = "Opacity of Secondary Value Area rows", GroupName = "Secondary Value Area", Order = 3)]
        public double SecondaryValueAreaOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Extend Secondary Value Area Line", Description = "Draw the Secondary Value Area High and Low as horizontal lines spanning the full session, computed from the session's total volume regardless of View Mode", GroupName = "Secondary Value Area", Order = 4)]
        public bool ExtendSecondaryValueAreaLine { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Secondary Value Area Line Width (px)", Description = "Width in pixels of the extended Secondary Value Area lines", GroupName = "Secondary Value Area", Order = 5)]
        public int SecondaryValueAreaLineWidthPixels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Secondary Value Area Line Style", Description = "Dash style of the extended Secondary Value Area lines", GroupName = "Secondary Value Area", Order = 6)]
        public DashStyleHelper SecondaryValueAreaLineStyle { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Secondary Value Area Line Opacity", Description = "Opacity of the extended Secondary Value Area lines (and their labels)", GroupName = "Secondary Value Area", Order = 7)]
        public double SecondaryValueAreaLineOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Display Secondary Value Area Labels", Description = "Show SVAH/SVAL text labels at the session end of the extended Secondary Value Area lines", GroupName = "Secondary Value Area", Order = 8)]
        public bool ShowSecondaryValueAreaLabels { get; set; }

        [NinjaScriptProperty]
        [TypeConverter(typeof(EGVolumeProfileFontFamilyConverter))]
        [Display(Name = "Secondary Value Area Label Font Family", Description = "Font family of the SVAH/SVAL labels", GroupName = "Secondary Value Area", Order = 9)]
        public string SecondaryValueAreaLabelFontFamily { get; set; }

        [NinjaScriptProperty]
        [TypeConverter(typeof(EGVolumeProfileFontSizeConverter))]
        [Display(Name = "Secondary Value Area Label Font Size", Description = "Font size of the SVAH/SVAL labels", GroupName = "Secondary Value Area", Order = 10)]
        public double SecondaryValueAreaLabelFontSize { get; set; }

        // ----- Buy/Sell -----

        [XmlIgnore]
        [Display(Name = "Buy/Sell Layout", Description = "Overlay draws both profiles from the same anchor, overlapping, each up to the full Profile Width (%); Mirror splits Profile Width (%) in half at its center, Sell always toward the left and Buy always toward the right, regardless of Profile Alignment", GroupName = "Buy/Sell", Order = 1)]
        public EGVolumeProfileBuySellLayout BuySellLayout { get; set; }

        [Browsable(false)]
        public string BuySellLayoutSerializable
        {
            get { return BuySellLayout.ToString(); }
            set { BuySellLayout = (EGVolumeProfileBuySellLayout)Enum.Parse(typeof(EGVolumeProfileBuySellLayout), value); }
        }

        // ----- Levels of Interest -----

        [NinjaScriptProperty]
        [Display(Name = "Show Large Orders", Description = "Mark price rows with a marker for each individual trade print at or above Large Order Threshold, colored by aggressor side", GroupName = "Levels of Interest", Order = 1)]
        public bool ShowLargeOrders { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100000)]
        [Display(Name = "Large Order Threshold", Description = "Minimum size of a single trade print to count as a large order", GroupName = "Levels of Interest", Order = 2)]
        public int LargeOrderThreshold { get; set; }

        [XmlIgnore]
        [Display(Name = "Large Order Style", Description = "Shape used to mark large orders: Dots or Squares", GroupName = "Levels of Interest", Order = 3)]
        public EGVolumeProfileLargeOrderStyle LargeOrderStyle { get; set; }

        [Browsable(false)]
        public string LargeOrderStyleSerializable
        {
            get { return LargeOrderStyle.ToString(); }
            set { LargeOrderStyle = (EGVolumeProfileLargeOrderStyle)Enum.Parse(typeof(EGVolumeProfileLargeOrderStyle), value); }
        }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "Large Order Max Per Row", Description = "Maximum number of large-order markers stacked at a single price level before additional ones stop being tracked", GroupName = "Levels of Interest", Order = 4)]
        public int LargeOrderMaxPerRow { get; set; }

        [NinjaScriptProperty]
        [Range(2, 40)]
        [Display(Name = "Large Order Size (px)", Description = "Diameter (Dots) or side length (Squares) in pixels of each large-order marker", GroupName = "Levels of Interest", Order = 5)]
        public int LargeOrderSize { get; set; }

        [NinjaScriptProperty]
        [Range(0, 40)]
        [Display(Name = "Large Order Spacing (px)", Description = "Horizontal gap between stacked large-order markers at the same price level", GroupName = "Levels of Interest", Order = 6)]
        public int LargeOrderSpacing { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Imbalance", Description = "Mark price rows where one side (Buy or Sell) holds at least Imbalance Threshold (%) of that row's combined Buy+Sell volume", GroupName = "Levels of Interest", Order = 7)]
        public bool ShowImbalance { get; set; }

        [NinjaScriptProperty]
        [Range(50, 100)]
        [Display(Name = "Imbalance Threshold (%)", Description = "Minimum share of a row's combined Buy+Sell volume held by the dominant side to mark it as imbalanced", GroupName = "Levels of Interest", Order = 8)]
        public double ImbalanceThresholdPercent { get; set; }

        // ----- Colors -----

        private System.Windows.Media.Brush profileColor;
        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Profile", Description = "Color of profile bars outside the Value Area(s)", GroupName = "Colors", Order = 1)]
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
        [Display(Name = "POC", Description = "Color of the Point of Control row", GroupName = "Colors", Order = 4)]
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

        private System.Windows.Media.Brush sellColor;
        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Sell", Description = "Color of the sell-initiated profile (hit the bid) in Sell and Buy/Sell view modes", GroupName = "Colors", Order = 5)]
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

        private System.Windows.Media.Brush buyColor;
        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Buy", Description = "Color of the buy-initiated profile (hit the ask) in Buy and Buy/Sell view modes", GroupName = "Colors", Order = 6)]
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

        private System.Windows.Media.Brush largeSellColor;
        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Large Sell", Description = "Color of large-order markers that were sell-initiated (hit the bid)", GroupName = "Colors", Order = 7)]
        public System.Windows.Media.Brush LargeSellColor
        {
            get { return largeSellColor; }
            set { largeSellColor = Frz(value); }
        }

        [Browsable(false)]
        public string LargeSellColorSerializable
        {
            get { return Serialize.BrushToString(LargeSellColor); }
            set { LargeSellColor = Serialize.StringToBrush(value); }
        }

        private System.Windows.Media.Brush largeBuyColor;
        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Large Buy", Description = "Color of large-order markers that were buy-initiated (hit the ask)", GroupName = "Colors", Order = 8)]
        public System.Windows.Media.Brush LargeBuyColor
        {
            get { return largeBuyColor; }
            set { largeBuyColor = Frz(value); }
        }

        [Browsable(false)]
        public string LargeBuyColorSerializable
        {
            get { return Serialize.BrushToString(LargeBuyColor); }
            set { LargeBuyColor = Serialize.StringToBrush(value); }
        }

        #endregion
    }

    public enum EGVolumeProfileAlignment { Left, Right }

    public enum EGVolumeProfileDisplayMode { Standard, Outline }

    public enum EGVolumeProfileViewMode { Volume, BuySell, Sell, Buy }

    public enum EGVolumeProfileBuySellLayout { Overlay, Mirror }

    public enum EGVolumeProfileClassificationMethod { TickBased, Galton }

    public enum EGVolumeProfileGaltonSource { BVC, TickAggregate }

    public enum EGVolumeProfileLargeOrderStyle { Dots, Squares }

    public enum EGVolumeProfileGradientPrint { Off, Volume, Delta }

    public class EGVolumeProfileTradingHoursConverter : TypeConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext c) => true;
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext c) => false;

        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext c)
        {
            List<string> names = NinjaTrader.Data.TradingHours.All
                .Select(th => th.Name)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new StandardValuesCollection(names);
        }
    }

    public class EGVolumeProfileFontFamilyConverter : TypeConverter
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

    public class EGVolumeProfileFontSizeConverter : TypeConverter
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
		private EducatedGambling.EGVolumeProfile[] cacheEGVolumeProfile;
		public EducatedGambling.EGVolumeProfile EGVolumeProfile(string tradingHoursTemplate, int sessionsToDisplay, int tickAggregation, int bVCLookback, bool isolateDominantSide, bool showPOC, double pOCOpacity, double profileWidthPercent, double profileOpacity, bool showValueArea, double valueAreaPercent, double valueAreaOpacity, bool extendValueAreaLine, int valueAreaLineWidthPixels, DashStyleHelper valueAreaLineStyle, double valueAreaLineOpacity, bool showValueAreaLabels, string valueAreaLabelFontFamily, double valueAreaLabelFontSize, bool showSecondaryValueArea, double secondaryValueAreaPercent, double secondaryValueAreaOpacity, bool extendSecondaryValueAreaLine, int secondaryValueAreaLineWidthPixels, DashStyleHelper secondaryValueAreaLineStyle, double secondaryValueAreaLineOpacity, bool showSecondaryValueAreaLabels, string secondaryValueAreaLabelFontFamily, double secondaryValueAreaLabelFontSize, bool showLargeOrders, int largeOrderThreshold, int largeOrderMaxPerRow, int largeOrderSize, int largeOrderSpacing, bool showImbalance, double imbalanceThresholdPercent, System.Windows.Media.Brush profileColor, System.Windows.Media.Brush valueAreaColor, System.Windows.Media.Brush secondaryValueAreaColor, System.Windows.Media.Brush pOCColor, System.Windows.Media.Brush sellColor, System.Windows.Media.Brush buyColor, System.Windows.Media.Brush largeSellColor, System.Windows.Media.Brush largeBuyColor)
		{
			return EGVolumeProfile(Input, tradingHoursTemplate, sessionsToDisplay, tickAggregation, bVCLookback, isolateDominantSide, showPOC, pOCOpacity, profileWidthPercent, profileOpacity, showValueArea, valueAreaPercent, valueAreaOpacity, extendValueAreaLine, valueAreaLineWidthPixels, valueAreaLineStyle, valueAreaLineOpacity, showValueAreaLabels, valueAreaLabelFontFamily, valueAreaLabelFontSize, showSecondaryValueArea, secondaryValueAreaPercent, secondaryValueAreaOpacity, extendSecondaryValueAreaLine, secondaryValueAreaLineWidthPixels, secondaryValueAreaLineStyle, secondaryValueAreaLineOpacity, showSecondaryValueAreaLabels, secondaryValueAreaLabelFontFamily, secondaryValueAreaLabelFontSize, showLargeOrders, largeOrderThreshold, largeOrderMaxPerRow, largeOrderSize, largeOrderSpacing, showImbalance, imbalanceThresholdPercent, profileColor, valueAreaColor, secondaryValueAreaColor, pOCColor, sellColor, buyColor, largeSellColor, largeBuyColor);
		}

		public EducatedGambling.EGVolumeProfile EGVolumeProfile(ISeries<double> input, string tradingHoursTemplate, int sessionsToDisplay, int tickAggregation, int bVCLookback, bool isolateDominantSide, bool showPOC, double pOCOpacity, double profileWidthPercent, double profileOpacity, bool showValueArea, double valueAreaPercent, double valueAreaOpacity, bool extendValueAreaLine, int valueAreaLineWidthPixels, DashStyleHelper valueAreaLineStyle, double valueAreaLineOpacity, bool showValueAreaLabels, string valueAreaLabelFontFamily, double valueAreaLabelFontSize, bool showSecondaryValueArea, double secondaryValueAreaPercent, double secondaryValueAreaOpacity, bool extendSecondaryValueAreaLine, int secondaryValueAreaLineWidthPixels, DashStyleHelper secondaryValueAreaLineStyle, double secondaryValueAreaLineOpacity, bool showSecondaryValueAreaLabels, string secondaryValueAreaLabelFontFamily, double secondaryValueAreaLabelFontSize, bool showLargeOrders, int largeOrderThreshold, int largeOrderMaxPerRow, int largeOrderSize, int largeOrderSpacing, bool showImbalance, double imbalanceThresholdPercent, System.Windows.Media.Brush profileColor, System.Windows.Media.Brush valueAreaColor, System.Windows.Media.Brush secondaryValueAreaColor, System.Windows.Media.Brush pOCColor, System.Windows.Media.Brush sellColor, System.Windows.Media.Brush buyColor, System.Windows.Media.Brush largeSellColor, System.Windows.Media.Brush largeBuyColor)
		{
			if (cacheEGVolumeProfile != null)
				for (int idx = 0; idx < cacheEGVolumeProfile.Length; idx++)
					if (cacheEGVolumeProfile[idx] != null && cacheEGVolumeProfile[idx].TradingHoursTemplate == tradingHoursTemplate && cacheEGVolumeProfile[idx].SessionsToDisplay == sessionsToDisplay && cacheEGVolumeProfile[idx].TickAggregation == tickAggregation && cacheEGVolumeProfile[idx].BVCLookback == bVCLookback && cacheEGVolumeProfile[idx].IsolateDominantSide == isolateDominantSide && cacheEGVolumeProfile[idx].ShowPOC == showPOC && cacheEGVolumeProfile[idx].POCOpacity == pOCOpacity && cacheEGVolumeProfile[idx].ProfileWidthPercent == profileWidthPercent && cacheEGVolumeProfile[idx].ProfileOpacity == profileOpacity && cacheEGVolumeProfile[idx].ShowValueArea == showValueArea && cacheEGVolumeProfile[idx].ValueAreaPercent == valueAreaPercent && cacheEGVolumeProfile[idx].ValueAreaOpacity == valueAreaOpacity && cacheEGVolumeProfile[idx].ExtendValueAreaLine == extendValueAreaLine && cacheEGVolumeProfile[idx].ValueAreaLineWidthPixels == valueAreaLineWidthPixels && cacheEGVolumeProfile[idx].ValueAreaLineStyle == valueAreaLineStyle && cacheEGVolumeProfile[idx].ValueAreaLineOpacity == valueAreaLineOpacity && cacheEGVolumeProfile[idx].ShowValueAreaLabels == showValueAreaLabels && cacheEGVolumeProfile[idx].ValueAreaLabelFontFamily == valueAreaLabelFontFamily && cacheEGVolumeProfile[idx].ValueAreaLabelFontSize == valueAreaLabelFontSize && cacheEGVolumeProfile[idx].ShowSecondaryValueArea == showSecondaryValueArea && cacheEGVolumeProfile[idx].SecondaryValueAreaPercent == secondaryValueAreaPercent && cacheEGVolumeProfile[idx].SecondaryValueAreaOpacity == secondaryValueAreaOpacity && cacheEGVolumeProfile[idx].ExtendSecondaryValueAreaLine == extendSecondaryValueAreaLine && cacheEGVolumeProfile[idx].SecondaryValueAreaLineWidthPixels == secondaryValueAreaLineWidthPixels && cacheEGVolumeProfile[idx].SecondaryValueAreaLineStyle == secondaryValueAreaLineStyle && cacheEGVolumeProfile[idx].SecondaryValueAreaLineOpacity == secondaryValueAreaLineOpacity && cacheEGVolumeProfile[idx].ShowSecondaryValueAreaLabels == showSecondaryValueAreaLabels && cacheEGVolumeProfile[idx].SecondaryValueAreaLabelFontFamily == secondaryValueAreaLabelFontFamily && cacheEGVolumeProfile[idx].SecondaryValueAreaLabelFontSize == secondaryValueAreaLabelFontSize && cacheEGVolumeProfile[idx].ShowLargeOrders == showLargeOrders && cacheEGVolumeProfile[idx].LargeOrderThreshold == largeOrderThreshold && cacheEGVolumeProfile[idx].LargeOrderMaxPerRow == largeOrderMaxPerRow && cacheEGVolumeProfile[idx].LargeOrderSize == largeOrderSize && cacheEGVolumeProfile[idx].LargeOrderSpacing == largeOrderSpacing && cacheEGVolumeProfile[idx].ShowImbalance == showImbalance && cacheEGVolumeProfile[idx].ImbalanceThresholdPercent == imbalanceThresholdPercent && cacheEGVolumeProfile[idx].ProfileColor == profileColor && cacheEGVolumeProfile[idx].ValueAreaColor == valueAreaColor && cacheEGVolumeProfile[idx].SecondaryValueAreaColor == secondaryValueAreaColor && cacheEGVolumeProfile[idx].POCColor == pOCColor && cacheEGVolumeProfile[idx].SellColor == sellColor && cacheEGVolumeProfile[idx].BuyColor == buyColor && cacheEGVolumeProfile[idx].LargeSellColor == largeSellColor && cacheEGVolumeProfile[idx].LargeBuyColor == largeBuyColor && cacheEGVolumeProfile[idx].EqualsInput(input))
						return cacheEGVolumeProfile[idx];
			return CacheIndicator<EducatedGambling.EGVolumeProfile>(new EducatedGambling.EGVolumeProfile(){ TradingHoursTemplate = tradingHoursTemplate, SessionsToDisplay = sessionsToDisplay, TickAggregation = tickAggregation, BVCLookback = bVCLookback, IsolateDominantSide = isolateDominantSide, ShowPOC = showPOC, POCOpacity = pOCOpacity, ProfileWidthPercent = profileWidthPercent, ProfileOpacity = profileOpacity, ShowValueArea = showValueArea, ValueAreaPercent = valueAreaPercent, ValueAreaOpacity = valueAreaOpacity, ExtendValueAreaLine = extendValueAreaLine, ValueAreaLineWidthPixels = valueAreaLineWidthPixels, ValueAreaLineStyle = valueAreaLineStyle, ValueAreaLineOpacity = valueAreaLineOpacity, ShowValueAreaLabels = showValueAreaLabels, ValueAreaLabelFontFamily = valueAreaLabelFontFamily, ValueAreaLabelFontSize = valueAreaLabelFontSize, ShowSecondaryValueArea = showSecondaryValueArea, SecondaryValueAreaPercent = secondaryValueAreaPercent, SecondaryValueAreaOpacity = secondaryValueAreaOpacity, ExtendSecondaryValueAreaLine = extendSecondaryValueAreaLine, SecondaryValueAreaLineWidthPixels = secondaryValueAreaLineWidthPixels, SecondaryValueAreaLineStyle = secondaryValueAreaLineStyle, SecondaryValueAreaLineOpacity = secondaryValueAreaLineOpacity, ShowSecondaryValueAreaLabels = showSecondaryValueAreaLabels, SecondaryValueAreaLabelFontFamily = secondaryValueAreaLabelFontFamily, SecondaryValueAreaLabelFontSize = secondaryValueAreaLabelFontSize, ShowLargeOrders = showLargeOrders, LargeOrderThreshold = largeOrderThreshold, LargeOrderMaxPerRow = largeOrderMaxPerRow, LargeOrderSize = largeOrderSize, LargeOrderSpacing = largeOrderSpacing, ShowImbalance = showImbalance, ImbalanceThresholdPercent = imbalanceThresholdPercent, ProfileColor = profileColor, ValueAreaColor = valueAreaColor, SecondaryValueAreaColor = secondaryValueAreaColor, POCColor = pOCColor, SellColor = sellColor, BuyColor = buyColor, LargeSellColor = largeSellColor, LargeBuyColor = largeBuyColor }, input, ref cacheEGVolumeProfile);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.EducatedGambling.EGVolumeProfile EGVolumeProfile(string tradingHoursTemplate, int sessionsToDisplay, int tickAggregation, int bVCLookback, bool isolateDominantSide, bool showPOC, double pOCOpacity, double profileWidthPercent, double profileOpacity, bool showValueArea, double valueAreaPercent, double valueAreaOpacity, bool extendValueAreaLine, int valueAreaLineWidthPixels, DashStyleHelper valueAreaLineStyle, double valueAreaLineOpacity, bool showValueAreaLabels, string valueAreaLabelFontFamily, double valueAreaLabelFontSize, bool showSecondaryValueArea, double secondaryValueAreaPercent, double secondaryValueAreaOpacity, bool extendSecondaryValueAreaLine, int secondaryValueAreaLineWidthPixels, DashStyleHelper secondaryValueAreaLineStyle, double secondaryValueAreaLineOpacity, bool showSecondaryValueAreaLabels, string secondaryValueAreaLabelFontFamily, double secondaryValueAreaLabelFontSize, bool showLargeOrders, int largeOrderThreshold, int largeOrderMaxPerRow, int largeOrderSize, int largeOrderSpacing, bool showImbalance, double imbalanceThresholdPercent, System.Windows.Media.Brush profileColor, System.Windows.Media.Brush valueAreaColor, System.Windows.Media.Brush secondaryValueAreaColor, System.Windows.Media.Brush pOCColor, System.Windows.Media.Brush sellColor, System.Windows.Media.Brush buyColor, System.Windows.Media.Brush largeSellColor, System.Windows.Media.Brush largeBuyColor)
		{
			return indicator.EGVolumeProfile(Input, tradingHoursTemplate, sessionsToDisplay, tickAggregation, bVCLookback, isolateDominantSide, showPOC, pOCOpacity, profileWidthPercent, profileOpacity, showValueArea, valueAreaPercent, valueAreaOpacity, extendValueAreaLine, valueAreaLineWidthPixels, valueAreaLineStyle, valueAreaLineOpacity, showValueAreaLabels, valueAreaLabelFontFamily, valueAreaLabelFontSize, showSecondaryValueArea, secondaryValueAreaPercent, secondaryValueAreaOpacity, extendSecondaryValueAreaLine, secondaryValueAreaLineWidthPixels, secondaryValueAreaLineStyle, secondaryValueAreaLineOpacity, showSecondaryValueAreaLabels, secondaryValueAreaLabelFontFamily, secondaryValueAreaLabelFontSize, showLargeOrders, largeOrderThreshold, largeOrderMaxPerRow, largeOrderSize, largeOrderSpacing, showImbalance, imbalanceThresholdPercent, profileColor, valueAreaColor, secondaryValueAreaColor, pOCColor, sellColor, buyColor, largeSellColor, largeBuyColor);
		}

		public Indicators.EducatedGambling.EGVolumeProfile EGVolumeProfile(ISeries<double> input , string tradingHoursTemplate, int sessionsToDisplay, int tickAggregation, int bVCLookback, bool isolateDominantSide, bool showPOC, double pOCOpacity, double profileWidthPercent, double profileOpacity, bool showValueArea, double valueAreaPercent, double valueAreaOpacity, bool extendValueAreaLine, int valueAreaLineWidthPixels, DashStyleHelper valueAreaLineStyle, double valueAreaLineOpacity, bool showValueAreaLabels, string valueAreaLabelFontFamily, double valueAreaLabelFontSize, bool showSecondaryValueArea, double secondaryValueAreaPercent, double secondaryValueAreaOpacity, bool extendSecondaryValueAreaLine, int secondaryValueAreaLineWidthPixels, DashStyleHelper secondaryValueAreaLineStyle, double secondaryValueAreaLineOpacity, bool showSecondaryValueAreaLabels, string secondaryValueAreaLabelFontFamily, double secondaryValueAreaLabelFontSize, bool showLargeOrders, int largeOrderThreshold, int largeOrderMaxPerRow, int largeOrderSize, int largeOrderSpacing, bool showImbalance, double imbalanceThresholdPercent, System.Windows.Media.Brush profileColor, System.Windows.Media.Brush valueAreaColor, System.Windows.Media.Brush secondaryValueAreaColor, System.Windows.Media.Brush pOCColor, System.Windows.Media.Brush sellColor, System.Windows.Media.Brush buyColor, System.Windows.Media.Brush largeSellColor, System.Windows.Media.Brush largeBuyColor)
		{
			return indicator.EGVolumeProfile(input, tradingHoursTemplate, sessionsToDisplay, tickAggregation, bVCLookback, isolateDominantSide, showPOC, pOCOpacity, profileWidthPercent, profileOpacity, showValueArea, valueAreaPercent, valueAreaOpacity, extendValueAreaLine, valueAreaLineWidthPixels, valueAreaLineStyle, valueAreaLineOpacity, showValueAreaLabels, valueAreaLabelFontFamily, valueAreaLabelFontSize, showSecondaryValueArea, secondaryValueAreaPercent, secondaryValueAreaOpacity, extendSecondaryValueAreaLine, secondaryValueAreaLineWidthPixels, secondaryValueAreaLineStyle, secondaryValueAreaLineOpacity, showSecondaryValueAreaLabels, secondaryValueAreaLabelFontFamily, secondaryValueAreaLabelFontSize, showLargeOrders, largeOrderThreshold, largeOrderMaxPerRow, largeOrderSize, largeOrderSpacing, showImbalance, imbalanceThresholdPercent, profileColor, valueAreaColor, secondaryValueAreaColor, pOCColor, sellColor, buyColor, largeSellColor, largeBuyColor);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.EducatedGambling.EGVolumeProfile EGVolumeProfile(string tradingHoursTemplate, int sessionsToDisplay, int tickAggregation, int bVCLookback, bool isolateDominantSide, bool showPOC, double pOCOpacity, double profileWidthPercent, double profileOpacity, bool showValueArea, double valueAreaPercent, double valueAreaOpacity, bool extendValueAreaLine, int valueAreaLineWidthPixels, DashStyleHelper valueAreaLineStyle, double valueAreaLineOpacity, bool showValueAreaLabels, string valueAreaLabelFontFamily, double valueAreaLabelFontSize, bool showSecondaryValueArea, double secondaryValueAreaPercent, double secondaryValueAreaOpacity, bool extendSecondaryValueAreaLine, int secondaryValueAreaLineWidthPixels, DashStyleHelper secondaryValueAreaLineStyle, double secondaryValueAreaLineOpacity, bool showSecondaryValueAreaLabels, string secondaryValueAreaLabelFontFamily, double secondaryValueAreaLabelFontSize, bool showLargeOrders, int largeOrderThreshold, int largeOrderMaxPerRow, int largeOrderSize, int largeOrderSpacing, bool showImbalance, double imbalanceThresholdPercent, System.Windows.Media.Brush profileColor, System.Windows.Media.Brush valueAreaColor, System.Windows.Media.Brush secondaryValueAreaColor, System.Windows.Media.Brush pOCColor, System.Windows.Media.Brush sellColor, System.Windows.Media.Brush buyColor, System.Windows.Media.Brush largeSellColor, System.Windows.Media.Brush largeBuyColor)
		{
			return indicator.EGVolumeProfile(Input, tradingHoursTemplate, sessionsToDisplay, tickAggregation, bVCLookback, isolateDominantSide, showPOC, pOCOpacity, profileWidthPercent, profileOpacity, showValueArea, valueAreaPercent, valueAreaOpacity, extendValueAreaLine, valueAreaLineWidthPixels, valueAreaLineStyle, valueAreaLineOpacity, showValueAreaLabels, valueAreaLabelFontFamily, valueAreaLabelFontSize, showSecondaryValueArea, secondaryValueAreaPercent, secondaryValueAreaOpacity, extendSecondaryValueAreaLine, secondaryValueAreaLineWidthPixels, secondaryValueAreaLineStyle, secondaryValueAreaLineOpacity, showSecondaryValueAreaLabels, secondaryValueAreaLabelFontFamily, secondaryValueAreaLabelFontSize, showLargeOrders, largeOrderThreshold, largeOrderMaxPerRow, largeOrderSize, largeOrderSpacing, showImbalance, imbalanceThresholdPercent, profileColor, valueAreaColor, secondaryValueAreaColor, pOCColor, sellColor, buyColor, largeSellColor, largeBuyColor);
		}

		public Indicators.EducatedGambling.EGVolumeProfile EGVolumeProfile(ISeries<double> input , string tradingHoursTemplate, int sessionsToDisplay, int tickAggregation, int bVCLookback, bool isolateDominantSide, bool showPOC, double pOCOpacity, double profileWidthPercent, double profileOpacity, bool showValueArea, double valueAreaPercent, double valueAreaOpacity, bool extendValueAreaLine, int valueAreaLineWidthPixels, DashStyleHelper valueAreaLineStyle, double valueAreaLineOpacity, bool showValueAreaLabels, string valueAreaLabelFontFamily, double valueAreaLabelFontSize, bool showSecondaryValueArea, double secondaryValueAreaPercent, double secondaryValueAreaOpacity, bool extendSecondaryValueAreaLine, int secondaryValueAreaLineWidthPixels, DashStyleHelper secondaryValueAreaLineStyle, double secondaryValueAreaLineOpacity, bool showSecondaryValueAreaLabels, string secondaryValueAreaLabelFontFamily, double secondaryValueAreaLabelFontSize, bool showLargeOrders, int largeOrderThreshold, int largeOrderMaxPerRow, int largeOrderSize, int largeOrderSpacing, bool showImbalance, double imbalanceThresholdPercent, System.Windows.Media.Brush profileColor, System.Windows.Media.Brush valueAreaColor, System.Windows.Media.Brush secondaryValueAreaColor, System.Windows.Media.Brush pOCColor, System.Windows.Media.Brush sellColor, System.Windows.Media.Brush buyColor, System.Windows.Media.Brush largeSellColor, System.Windows.Media.Brush largeBuyColor)
		{
			return indicator.EGVolumeProfile(input, tradingHoursTemplate, sessionsToDisplay, tickAggregation, bVCLookback, isolateDominantSide, showPOC, pOCOpacity, profileWidthPercent, profileOpacity, showValueArea, valueAreaPercent, valueAreaOpacity, extendValueAreaLine, valueAreaLineWidthPixels, valueAreaLineStyle, valueAreaLineOpacity, showValueAreaLabels, valueAreaLabelFontFamily, valueAreaLabelFontSize, showSecondaryValueArea, secondaryValueAreaPercent, secondaryValueAreaOpacity, extendSecondaryValueAreaLine, secondaryValueAreaLineWidthPixels, secondaryValueAreaLineStyle, secondaryValueAreaLineOpacity, showSecondaryValueAreaLabels, secondaryValueAreaLabelFontFamily, secondaryValueAreaLabelFontSize, showLargeOrders, largeOrderThreshold, largeOrderMaxPerRow, largeOrderSize, largeOrderSpacing, showImbalance, imbalanceThresholdPercent, profileColor, valueAreaColor, secondaryValueAreaColor, pOCColor, sellColor, buyColor, largeSellColor, largeBuyColor);
		}
	}
}

#endregion
