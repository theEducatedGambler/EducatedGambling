#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
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
    [CategoryOrder("Bid/Ask", 5)]
    [CategoryOrder("Colors", 6)]
    public class EGVolumeProfile : Indicator
    {
        private readonly Dictionary<double, double> volumeByPrice = new Dictionary<double, double>();
        private readonly Dictionary<double, double> bidVolumeByPrice = new Dictionary<double, double>();
        private readonly Dictionary<double, double> askVolumeByPrice = new Dictionary<double, double>();
        private double currentBid;
        private double currentAsk;

        private readonly Queue<double> bvcPriceChanges = new Queue<double>();
        private double currentBarBidVolume;
        private double currentBarAskVolume;
        private int lastGaltonProcessedBar = -1;

        private SessionIterator profileSessionIterator;
        private DateTime cacheSessionEnd = Core.Globals.MinDate;
        private int sessionStartBar = -1;

        private SharpDX.Direct2D1.Brush profileBrushDx;
        private SharpDX.Direct2D1.Brush valueAreaBrushDx;
        private SharpDX.Direct2D1.Brush secondaryValueAreaBrushDx;
        private SharpDX.Direct2D1.Brush pocBrushDx;
        private SharpDX.Direct2D1.Brush bidBrushDx;
        private SharpDX.Direct2D1.Brush askBrushDx;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "EGVolumeProfile";
                Description = "Session volume profile with POC, Value Area, Secondary Value Area, and Bid/Ask classification (tick-based or Galton), built from hidden per-tick series.";
                Calculate = Calculate.OnEachTick;
                IsOverlay = true;
                DisplayInDataBox = false;
                PaintPriceMarkers = false;
                ScaleJustification = NinjaTrader.Gui.Chart.ScaleJustification.Right;
                IsSuspendedWhileInactive = false;

                TradingHoursTemplate = NinjaTrader.Data.TradingHours.SystemDefault ?? "";
                TickAggregation = 10;
                DisplayMode = EGVolumeProfileDisplayMode.Standard;
                ViewMode = EGVolumeProfileViewMode.Volume;
                ClassificationMethod = EGVolumeProfileClassificationMethod.TickBased;
                GaltonSource = EGVolumeProfileGaltonSource.BVC;
                BVCLookback = 20;
                ProfileAlignment = EGVolumeProfileAlignment.Left;

                ShowPOC = true;
                POCOpacity = 100;
                ProfileWidthPercent = 30;
                ProfileOpacity = 70;

                ShowValueArea = true;
                ValueAreaPercent = 70;
                ValueAreaOpacity = 85;

                ShowSecondaryValueArea = false;
                SecondaryValueAreaPercent = 40;
                SecondaryValueAreaOpacity = 40;

                BidAskLayout = EGVolumeProfileBidAskLayout.Overlay;

                ProfileColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 70, 130, 180));
                ValueAreaColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 255, 165, 0));
                SecondaryValueAreaColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 135, 206, 235));
                POCColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 220, 20, 60));
                BidColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 50, 180, 90));
                AskColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 220, 50, 50));
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
            if (bidBrushDx != null) { bidBrushDx.Dispose(); bidBrushDx = null; }
            if (askBrushDx != null) { askBrushDx.Dispose(); askBrushDx = null; }
        }

        public override void OnRenderTargetChanged()
        {
            DisposeDxBrushes();

            if (RenderTarget == null) return;

            if (ProfileColor != null) profileBrushDx = ProfileColor.ToDxBrush(RenderTarget);
            if (ValueAreaColor != null) valueAreaBrushDx = ValueAreaColor.ToDxBrush(RenderTarget);
            if (SecondaryValueAreaColor != null) secondaryValueAreaBrushDx = SecondaryValueAreaColor.ToDxBrush(RenderTarget);
            if (POCColor != null) pocBrushDx = POCColor.ToDxBrush(RenderTarget);
            if (BidColor != null) bidBrushDx = BidColor.ToDxBrush(RenderTarget);
            if (AskColor != null) askBrushDx = AskColor.ToDxBrush(RenderTarget);
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
                    sessionStartBar = CurrentBar;
                    volumeByPrice.Clear();
                    bidVolumeByPrice.Clear();
                    askVolumeByPrice.Clear();
                    currentBarBidVolume = 0;
                    currentBarAskVolume = 0;
                    bvcPriceChanges.Clear();
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

            double tickSize = Instrument.MasterInstrument.TickSize;
            double rowSize = tickSize * Math.Max(1, TickAggregation);
            double price = Close[0];
            double bucket = Instrument.MasterInstrument.RoundToTickSize(Math.Floor(price / rowSize) * rowSize);
            double vol = Volume[0];

            bool isAsk = currentAsk > 0 && price >= currentAsk;
            bool isBid = !isAsk && currentBid > 0 && price <= currentBid;

            if (ClassificationMethod == EGVolumeProfileClassificationMethod.Galton)
            {
                if (isAsk) currentBarAskVolume += vol;
                else if (isBid) currentBarBidVolume += vol;
                return;
            }

            double existing;
            volumeByPrice[bucket] = (volumeByPrice.TryGetValue(bucket, out existing) ? existing : 0) + vol;

            if (isAsk)
                askVolumeByPrice[bucket] = (askVolumeByPrice.TryGetValue(bucket, out existing) ? existing : 0) + vol;
            else if (isBid)
                bidVolumeByPrice[bucket] = (bidVolumeByPrice.TryGetValue(bucket, out existing) ? existing : 0) + vol;
        }

        private void ProcessGaltonBar()
        {
            if (CurrentBar < 1) return;

            double closedClose = Close[1];
            double closedHigh = High[1];
            double closedLow = Low[1];
            double closedVolume = Volume[1];

            double askTotal;
            double bidTotal;

            if (GaltonSource == EGVolumeProfileGaltonSource.TickAggregate)
            {
                askTotal = currentBarAskVolume;
                bidTotal = currentBarBidVolume;
            }
            else
            {
                double priceChange = CurrentBar >= 2 ? closedClose - Close[2] : 0;
                bvcPriceChanges.Enqueue(priceChange);
                while (bvcPriceChanges.Count > Math.Max(2, BVCLookback)) bvcPriceChanges.Dequeue();

                double sigma = GetStdDev(bvcPriceChanges);
                double z = sigma > 0 ? priceChange / sigma : 0;
                double buyFraction = NormalCdf(z);

                askTotal = closedVolume * buyFraction;
                bidTotal = closedVolume * (1.0 - buyFraction);
            }

            currentBarBidVolume = 0;
            currentBarAskVolume = 0;

            if (askTotal + bidTotal <= 0) return;

            double tickSize = Instrument.MasterInstrument.TickSize;
            double rowSize = tickSize * Math.Max(1, TickAggregation);

            Dictionary<double, double> weights = ComputeGaltonWeights(closedClose, closedLow, closedHigh, rowSize);

            double existing;
            foreach (KeyValuePair<double, double> kv in weights)
            {
                double bucket = kv.Key;
                double w = kv.Value;

                volumeByPrice[bucket] = (volumeByPrice.TryGetValue(bucket, out existing) ? existing : 0) + w * (askTotal + bidTotal);
                askVolumeByPrice[bucket] = (askVolumeByPrice.TryGetValue(bucket, out existing) ? existing : 0) + w * askTotal;
                bidVolumeByPrice[bucket] = (bidVolumeByPrice.TryGetValue(bucket, out existing) ? existing : 0) + w * bidTotal;
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

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);

            if (Bars == null || ChartControl == null || IsInHitTest) return;
            if (sessionStartBar < 0 || sessionStartBar > ChartBars.ToIndex) return;
            if (profileBrushDx == null || valueAreaBrushDx == null || secondaryValueAreaBrushDx == null || pocBrushDx == null) return;
            if (bidBrushDx == null || askBrushDx == null) return;

            double tickSize = Instrument.MasterInstrument.TickSize;
            double rowSize = tickSize * Math.Max(1, TickAggregation);

            int anchorBar = ProfileAlignment == EGVolumeProfileAlignment.Right
                ? Math.Min(ChartBars.ToIndex, CurrentBar)
                : Math.Max(ChartBars.FromIndex, sessionStartBar);
            float anchorX = chartControl.GetXByBarIndex(ChartBars, anchorBar);
            bool rightAlign = ProfileAlignment == EGVolumeProfileAlignment.Right;

            if (ViewMode == EGVolumeProfileViewMode.Bid)
            {
                RenderProfile(chartScale, bidVolumeByPrice, bidBrushDx, anchorX, rightAlign, rowSize);
            }
            else if (ViewMode == EGVolumeProfileViewMode.Ask)
            {
                RenderProfile(chartScale, askVolumeByPrice, askBrushDx, anchorX, rightAlign, rowSize);
            }
            else if (ViewMode == EGVolumeProfileViewMode.BidAsk)
            {
                if (BidAskLayout == EGVolumeProfileBidAskLayout.Mirror)
                {
                    float fullMaxWidth = (float)(ProfileWidthPercent / 100.0 * ChartPanel.W);
                    float halfMaxWidth = fullMaxWidth / 2f;
                    float centerX = rightAlign ? anchorX - halfMaxWidth : anchorX + halfMaxWidth;

                    // Bid always grows toward the left of center, Ask always toward the right,
                    // independent of Profile Alignment (which only moves the anchor/center position).
                    RenderProfile(chartScale, bidVolumeByPrice, bidBrushDx, centerX, true, rowSize, halfMaxWidth);
                    RenderProfile(chartScale, askVolumeByPrice, askBrushDx, centerX, false, rowSize, halfMaxWidth);
                }
                else
                {
                    RenderProfile(chartScale, bidVolumeByPrice, bidBrushDx, anchorX, rightAlign, rowSize);
                    RenderProfile(chartScale, askVolumeByPrice, askBrushDx, anchorX, rightAlign, rowSize);
                }
            }
            else
            {
                RenderProfile(chartScale, volumeByPrice, profileBrushDx, anchorX, rightAlign, rowSize);
            }
        }

        private void RenderProfile(ChartScale chartScale, Dictionary<double, double> data, SharpDX.Direct2D1.Brush baseBrushDx,
            float anchorX, bool rightAlign, double rowSize, float? maxBarWidthOverride = null)
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

            baseBrushDx.Opacity = (float)(ProfileOpacity / 100.0);
            valueAreaBrushDx.Opacity = (float)(ValueAreaOpacity / 100.0);
            secondaryValueAreaBrushDx.Opacity = (float)(SecondaryValueAreaOpacity / 100.0);
            pocBrushDx.Opacity = (float)(POCOpacity / 100.0);

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
                if (inVA) brush = valueAreaBrushDx;
                if (inSecondaryVA) brush = secondaryValueAreaBrushDx;
                if (isPoc) brush = pocBrushDx;

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
        [Range(1, 100000)]
        [Display(Name = "Tick Aggregation", Description = "Number of ticks combined into a single profile row", GroupName = "Setting", Order = 2)]
        public int TickAggregation { get; set; }

        [XmlIgnore]
        [Display(Name = "Display Mode", Description = "Standard draws individual bars per row; Outline draws a single filled, outlined silhouette of the profile", GroupName = "Setting", Order = 3)]
        public EGVolumeProfileDisplayMode DisplayMode { get; set; }

        [Browsable(false)]
        public string DisplayModeSerializable
        {
            get { return DisplayMode.ToString(); }
            set { DisplayMode = (EGVolumeProfileDisplayMode)Enum.Parse(typeof(EGVolumeProfileDisplayMode), value); }
        }

        [XmlIgnore]
        [Display(Name = "View Mode", Description = "Volume shows total traded volume; Bid/Ask shows both aggressor-side profiles at once; Bid and Ask show only that side", GroupName = "Setting", Order = 4)]
        public EGVolumeProfileViewMode ViewMode { get; set; }

        [Browsable(false)]
        public string ViewModeSerializable
        {
            get { return ViewMode.ToString(); }
            set { ViewMode = (EGVolumeProfileViewMode)Enum.Parse(typeof(EGVolumeProfileViewMode), value); }
        }

        [XmlIgnore]
        [Display(Name = "Classification Method", Description = "Tick-Based classifies each real trade print via the Bid/Ask quote rule; Galton estimates each bar's Buy/Sell totals and spreads them across that bar's Low-High range using a Galton-board-style binomial curve centered at Close", GroupName = "Setting", Order = 5)]
        public EGVolumeProfileClassificationMethod ClassificationMethod { get; set; }

        [Browsable(false)]
        public string ClassificationMethodSerializable
        {
            get { return ClassificationMethod.ToString(); }
            set { ClassificationMethod = (EGVolumeProfileClassificationMethod)Enum.Parse(typeof(EGVolumeProfileClassificationMethod), value); }
        }

        [XmlIgnore]
        [Display(Name = "Galton Source", Description = "Only used when Classification Method is Galton. BVC estimates each bar's Buy/Sell split from its close-to-close price change normalized by volatility (no tick data needed); Tick Aggregate sums the tick-based Bid/Ask classification per bar instead of per price, then re-spreads it with the Galton curve", GroupName = "Setting", Order = 6)]
        public EGVolumeProfileGaltonSource GaltonSource { get; set; }

        [Browsable(false)]
        public string GaltonSourceSerializable
        {
            get { return GaltonSource.ToString(); }
            set { GaltonSource = (EGVolumeProfileGaltonSource)Enum.Parse(typeof(EGVolumeProfileGaltonSource), value); }
        }

        [NinjaScriptProperty]
        [Range(2, 500)]
        [Display(Name = "BVC Lookback", Description = "Number of bars used to compute the price-change volatility in the BVC formula", GroupName = "Setting", Order = 7)]
        public int BVCLookback { get; set; }

        // ----- Profile -----

        [NinjaScriptProperty]
        [Display(Name = "Display POC", Description = "Color the Point of Control row differently from the rest of the profile", GroupName = "Profile", Order = 1)]
        public bool ShowPOC { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "POC Opacity", Description = "Opacity of the Point of Control row", GroupName = "Profile", Order = 2)]
        public double POCOpacity { get; set; }

        [XmlIgnore]
        [Display(Name = "Profile Alignment", Description = "Left anchors the profile at session start and extends it rightward; Right anchors it at the current bar and extends it leftward", GroupName = "Profile", Order = 3)]
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

        // ----- Bid/Ask -----

        [XmlIgnore]
        [Display(Name = "Bid/Ask Layout", Description = "Overlay draws both profiles from the same anchor, overlapping, each up to the full Profile Width (%); Mirror splits Profile Width (%) in half at its center, Bid always toward the left and Ask always toward the right, regardless of Profile Alignment", GroupName = "Bid/Ask", Order = 1)]
        public EGVolumeProfileBidAskLayout BidAskLayout { get; set; }

        [Browsable(false)]
        public string BidAskLayoutSerializable
        {
            get { return BidAskLayout.ToString(); }
            set { BidAskLayout = (EGVolumeProfileBidAskLayout)Enum.Parse(typeof(EGVolumeProfileBidAskLayout), value); }
        }

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
        [Display(Name = "Value Area", Description = "Color of Value Area rows", GroupName = "Colors", Order = 2)]
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
        [Display(Name = "Secondary Value Area", Description = "Color of Secondary Value Area rows", GroupName = "Colors", Order = 3)]
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

        private System.Windows.Media.Brush bidColor;
        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Bid", Description = "Color of the Bid-side (sell-initiated) profile in Bid and Bid/Ask view modes", GroupName = "Colors", Order = 5)]
        public System.Windows.Media.Brush BidColor
        {
            get { return bidColor; }
            set { bidColor = Frz(value); }
        }

        [Browsable(false)]
        public string BidColorSerializable
        {
            get { return Serialize.BrushToString(BidColor); }
            set { BidColor = Serialize.StringToBrush(value); }
        }

        private System.Windows.Media.Brush askColor;
        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Ask", Description = "Color of the Ask-side (buy-initiated) profile in Ask and Bid/Ask view modes", GroupName = "Colors", Order = 6)]
        public System.Windows.Media.Brush AskColor
        {
            get { return askColor; }
            set { askColor = Frz(value); }
        }

        [Browsable(false)]
        public string AskColorSerializable
        {
            get { return Serialize.BrushToString(AskColor); }
            set { AskColor = Serialize.StringToBrush(value); }
        }

        #endregion
    }

    public enum EGVolumeProfileAlignment { Left, Right }

    public enum EGVolumeProfileDisplayMode { Standard, Outline }

    public enum EGVolumeProfileViewMode { Volume, BidAsk, Bid, Ask }

    public enum EGVolumeProfileBidAskLayout { Overlay, Mirror }

    public enum EGVolumeProfileClassificationMethod { TickBased, Galton }

    public enum EGVolumeProfileGaltonSource { BVC, TickAggregate }

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
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private EducatedGambling.EGVolumeProfile[] cacheEGVolumeProfile;
		public EducatedGambling.EGVolumeProfile EGVolumeProfile(string tradingHoursTemplate, int tickAggregation, int bVCLookback, bool showPOC, double pOCOpacity, double profileWidthPercent, double profileOpacity, bool showValueArea, double valueAreaPercent, double valueAreaOpacity, bool showSecondaryValueArea, double secondaryValueAreaPercent, double secondaryValueAreaOpacity, System.Windows.Media.Brush profileColor, System.Windows.Media.Brush valueAreaColor, System.Windows.Media.Brush secondaryValueAreaColor, System.Windows.Media.Brush pOCColor, System.Windows.Media.Brush bidColor, System.Windows.Media.Brush askColor)
		{
			return EGVolumeProfile(Input, tradingHoursTemplate, tickAggregation, bVCLookback, showPOC, pOCOpacity, profileWidthPercent, profileOpacity, showValueArea, valueAreaPercent, valueAreaOpacity, showSecondaryValueArea, secondaryValueAreaPercent, secondaryValueAreaOpacity, profileColor, valueAreaColor, secondaryValueAreaColor, pOCColor, bidColor, askColor);
		}

		public EducatedGambling.EGVolumeProfile EGVolumeProfile(ISeries<double> input, string tradingHoursTemplate, int tickAggregation, int bVCLookback, bool showPOC, double pOCOpacity, double profileWidthPercent, double profileOpacity, bool showValueArea, double valueAreaPercent, double valueAreaOpacity, bool showSecondaryValueArea, double secondaryValueAreaPercent, double secondaryValueAreaOpacity, System.Windows.Media.Brush profileColor, System.Windows.Media.Brush valueAreaColor, System.Windows.Media.Brush secondaryValueAreaColor, System.Windows.Media.Brush pOCColor, System.Windows.Media.Brush bidColor, System.Windows.Media.Brush askColor)
		{
			if (cacheEGVolumeProfile != null)
				for (int idx = 0; idx < cacheEGVolumeProfile.Length; idx++)
					if (cacheEGVolumeProfile[idx] != null && cacheEGVolumeProfile[idx].TradingHoursTemplate == tradingHoursTemplate && cacheEGVolumeProfile[idx].TickAggregation == tickAggregation && cacheEGVolumeProfile[idx].BVCLookback == bVCLookback && cacheEGVolumeProfile[idx].ShowPOC == showPOC && cacheEGVolumeProfile[idx].POCOpacity == pOCOpacity && cacheEGVolumeProfile[idx].ProfileWidthPercent == profileWidthPercent && cacheEGVolumeProfile[idx].ProfileOpacity == profileOpacity && cacheEGVolumeProfile[idx].ShowValueArea == showValueArea && cacheEGVolumeProfile[idx].ValueAreaPercent == valueAreaPercent && cacheEGVolumeProfile[idx].ValueAreaOpacity == valueAreaOpacity && cacheEGVolumeProfile[idx].ShowSecondaryValueArea == showSecondaryValueArea && cacheEGVolumeProfile[idx].SecondaryValueAreaPercent == secondaryValueAreaPercent && cacheEGVolumeProfile[idx].SecondaryValueAreaOpacity == secondaryValueAreaOpacity && cacheEGVolumeProfile[idx].ProfileColor == profileColor && cacheEGVolumeProfile[idx].ValueAreaColor == valueAreaColor && cacheEGVolumeProfile[idx].SecondaryValueAreaColor == secondaryValueAreaColor && cacheEGVolumeProfile[idx].POCColor == pOCColor && cacheEGVolumeProfile[idx].BidColor == bidColor && cacheEGVolumeProfile[idx].AskColor == askColor && cacheEGVolumeProfile[idx].EqualsInput(input))
						return cacheEGVolumeProfile[idx];
			return CacheIndicator<EducatedGambling.EGVolumeProfile>(new EducatedGambling.EGVolumeProfile(){ TradingHoursTemplate = tradingHoursTemplate, TickAggregation = tickAggregation, BVCLookback = bVCLookback, ShowPOC = showPOC, POCOpacity = pOCOpacity, ProfileWidthPercent = profileWidthPercent, ProfileOpacity = profileOpacity, ShowValueArea = showValueArea, ValueAreaPercent = valueAreaPercent, ValueAreaOpacity = valueAreaOpacity, ShowSecondaryValueArea = showSecondaryValueArea, SecondaryValueAreaPercent = secondaryValueAreaPercent, SecondaryValueAreaOpacity = secondaryValueAreaOpacity, ProfileColor = profileColor, ValueAreaColor = valueAreaColor, SecondaryValueAreaColor = secondaryValueAreaColor, POCColor = pOCColor, BidColor = bidColor, AskColor = askColor }, input, ref cacheEGVolumeProfile);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.EducatedGambling.EGVolumeProfile EGVolumeProfile(string tradingHoursTemplate, int tickAggregation, int bVCLookback, bool showPOC, double pOCOpacity, double profileWidthPercent, double profileOpacity, bool showValueArea, double valueAreaPercent, double valueAreaOpacity, bool showSecondaryValueArea, double secondaryValueAreaPercent, double secondaryValueAreaOpacity, System.Windows.Media.Brush profileColor, System.Windows.Media.Brush valueAreaColor, System.Windows.Media.Brush secondaryValueAreaColor, System.Windows.Media.Brush pOCColor, System.Windows.Media.Brush bidColor, System.Windows.Media.Brush askColor)
		{
			return indicator.EGVolumeProfile(Input, tradingHoursTemplate, tickAggregation, bVCLookback, showPOC, pOCOpacity, profileWidthPercent, profileOpacity, showValueArea, valueAreaPercent, valueAreaOpacity, showSecondaryValueArea, secondaryValueAreaPercent, secondaryValueAreaOpacity, profileColor, valueAreaColor, secondaryValueAreaColor, pOCColor, bidColor, askColor);
		}

		public Indicators.EducatedGambling.EGVolumeProfile EGVolumeProfile(ISeries<double> input , string tradingHoursTemplate, int tickAggregation, int bVCLookback, bool showPOC, double pOCOpacity, double profileWidthPercent, double profileOpacity, bool showValueArea, double valueAreaPercent, double valueAreaOpacity, bool showSecondaryValueArea, double secondaryValueAreaPercent, double secondaryValueAreaOpacity, System.Windows.Media.Brush profileColor, System.Windows.Media.Brush valueAreaColor, System.Windows.Media.Brush secondaryValueAreaColor, System.Windows.Media.Brush pOCColor, System.Windows.Media.Brush bidColor, System.Windows.Media.Brush askColor)
		{
			return indicator.EGVolumeProfile(input, tradingHoursTemplate, tickAggregation, bVCLookback, showPOC, pOCOpacity, profileWidthPercent, profileOpacity, showValueArea, valueAreaPercent, valueAreaOpacity, showSecondaryValueArea, secondaryValueAreaPercent, secondaryValueAreaOpacity, profileColor, valueAreaColor, secondaryValueAreaColor, pOCColor, bidColor, askColor);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.EducatedGambling.EGVolumeProfile EGVolumeProfile(string tradingHoursTemplate, int tickAggregation, int bVCLookback, bool showPOC, double pOCOpacity, double profileWidthPercent, double profileOpacity, bool showValueArea, double valueAreaPercent, double valueAreaOpacity, bool showSecondaryValueArea, double secondaryValueAreaPercent, double secondaryValueAreaOpacity, System.Windows.Media.Brush profileColor, System.Windows.Media.Brush valueAreaColor, System.Windows.Media.Brush secondaryValueAreaColor, System.Windows.Media.Brush pOCColor, System.Windows.Media.Brush bidColor, System.Windows.Media.Brush askColor)
		{
			return indicator.EGVolumeProfile(Input, tradingHoursTemplate, tickAggregation, bVCLookback, showPOC, pOCOpacity, profileWidthPercent, profileOpacity, showValueArea, valueAreaPercent, valueAreaOpacity, showSecondaryValueArea, secondaryValueAreaPercent, secondaryValueAreaOpacity, profileColor, valueAreaColor, secondaryValueAreaColor, pOCColor, bidColor, askColor);
		}

		public Indicators.EducatedGambling.EGVolumeProfile EGVolumeProfile(ISeries<double> input , string tradingHoursTemplate, int tickAggregation, int bVCLookback, bool showPOC, double pOCOpacity, double profileWidthPercent, double profileOpacity, bool showValueArea, double valueAreaPercent, double valueAreaOpacity, bool showSecondaryValueArea, double secondaryValueAreaPercent, double secondaryValueAreaOpacity, System.Windows.Media.Brush profileColor, System.Windows.Media.Brush valueAreaColor, System.Windows.Media.Brush secondaryValueAreaColor, System.Windows.Media.Brush pOCColor, System.Windows.Media.Brush bidColor, System.Windows.Media.Brush askColor)
		{
			return indicator.EGVolumeProfile(input, tradingHoursTemplate, tickAggregation, bVCLookback, showPOC, pOCOpacity, profileWidthPercent, profileOpacity, showValueArea, valueAreaPercent, valueAreaOpacity, showSecondaryValueArea, secondaryValueAreaPercent, secondaryValueAreaOpacity, profileColor, valueAreaColor, secondaryValueAreaColor, pOCColor, bidColor, askColor);
		}
	}
}

#endregion
