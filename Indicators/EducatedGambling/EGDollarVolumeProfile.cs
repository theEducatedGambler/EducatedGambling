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
using SharpDX.DirectWrite;
#endregion

namespace NinjaTrader.NinjaScript.Indicators.EducatedGambling
{
    [CategoryOrder("Filter", 1)]
    [CategoryOrder("Layout", 2)]
    [CategoryOrder("Bar Fill", 3)]
    [CategoryOrder("Dominant Levels", 4)]
    [CategoryOrder("Largest Volume", 5)]
    [CategoryOrder("Text", 6)]
    [CategoryOrder("Colors", 7)]
    public class EGDollarVolumeProfile : Indicator
    {
        private static readonly TimeSpan RthStart = new TimeSpan(9, 30, 0);
        private static readonly TimeSpan RthEnd = new TimeSpan(16, 0, 0);
        private static readonly TimeZoneInfo EasternTz = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

        private double currentBid;
        private double currentAsk;
        private double lastLastPrice;

        private readonly Dictionary<double, double> buyUsdByPrice = new Dictionary<double, double>();
        private readonly Dictionary<double, double> sellUsdByPrice = new Dictionary<double, double>();

        private SharpDX.Direct2D1.Brush buyBrushDx;
        private SharpDX.Direct2D1.Brush sellBrushDx;
        private SharpDX.Direct2D1.Brush volumeTextBrushDx;
        private SharpDX.Direct2D1.Brush priceTextBrushDx;
        private SharpDX.Direct2D1.Brush dominantTextBrushDx;
        private SharpDX.Direct2D1.Brush extendedDominantLineBarBrushDx;
        private NinjaTrader.Gui.Stroke extendedLineStrokeHelper;
        private SharpDX.Direct2D1.Brush largestVolumeTextBrushDx;
        private SharpDX.Direct2D1.Brush extendedLargestBuyVolumeLineBarBrushDx;
        private SharpDX.Direct2D1.Brush extendedLargestSellVolumeLineBarBrushDx;
        private NinjaTrader.Gui.Stroke extendedLargestBuyVolumeLineStrokeHelper;
        private NinjaTrader.Gui.Stroke extendedLargestSellVolumeLineStrokeHelper;

        private SharpDX.DirectWrite.TextFormat textFormat;
        private SharpDX.DirectWrite.TextFormat priceTextFormat;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "EGDollarVolumeProfile";
                Description = "Session dollar-volume profile split by aggressor side, from a hidden Last/Bid/Ask tick series.";
                Calculate = Calculate.OnEachTick;
                IsOverlay = true;
                DisplayInDataBox = false;
                PaintPriceMarkers = false;
                ScaleJustification = NinjaTrader.Gui.Chart.ScaleJustification.Right;
                IsSuspendedWhileInactive = false;

                SessionFilter = EGDollarVolumeProfileSessionFilter.RTH;
                BarFill = EGDollarVolumeProfileBarFillMode.Solid;
                GradientLevel = 5;
                PriceGroupingPoints = 5.75;
                BarLengthPixels = 250;
                BarHeightMode = EGDollarVolumeProfileBarHeightMode.Dynamic;
                FixedBarHeightPixels = 20;
                MaxLevelsToShow = 40;
                WallSpacingPixels = 4;
                TextSpacingPixels = 12;
                ShowUsdSuffix = true;
                HighlightDominantLevels = false;
                DominantLevelThreshold = 80;
                DominantColor = System.Windows.Media.Brushes.Gold;
                ExtendDominantLevel = false;
                ExtendMode = EGDollarVolumeProfileExtendMode.Bar;
                LineWidthPixels = 2;
                LineStyle = DashStyleHelper.Solid;
                ExtendedDominantLineBarColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(60, 255, 215, 0));
                HighlightLargestVolumeLevels = false;
                MaxLargestVolumeLevelsPerSide = 3;
                LargestVolumeColor = System.Windows.Media.Brushes.Cyan;
                ExtendLargestVolumeLevel = false;
                ExtendLargestVolumeMode = EGDollarVolumeProfileExtendMode.Bar;
                LargestVolumeLineWidthPixels = 2;
                LargestVolumeLineStyle = DashStyleHelper.Solid;
                LargestBuyVolumeExtendedLineBarColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(60, 0, 255, 255));
                LargestSellVolumeExtendedLineBarColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(60, 255, 140, 0));
                BuyColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(150, 0, 180, 120));
                SellColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(150, 200, 50, 50));
                PriceFontFamily = "Arial";
                PriceFontSize = 11;
                PriceTextColor = System.Windows.Media.Brushes.White;
                VolumeFontFamily = "Arial";
                VolumeFontSize = 11;
                VolumeTextColor = System.Windows.Media.Brushes.White;
            }
            else if (State == State.Configure)
            {
                AddDataSeries(Instrument.FullName, BarsPeriodType.Tick, 1, MarketDataType.Last);
                AddDataSeries(Instrument.FullName, BarsPeriodType.Tick, 1, MarketDataType.Bid);
                AddDataSeries(Instrument.FullName, BarsPeriodType.Tick, 1, MarketDataType.Ask);
            }
            else if (State == State.DataLoaded)
            {
                SimpleFont volumeFont = new SimpleFont(VolumeFontFamily, VolumeFontSize) { Bold = true };
                textFormat = volumeFont.ToDirectWriteTextFormat();
                textFormat.TextAlignment = SharpDX.DirectWrite.TextAlignment.Trailing;
                textFormat.ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center;
                textFormat.WordWrapping = SharpDX.DirectWrite.WordWrapping.NoWrap;

                SimpleFont priceFont = new SimpleFont(PriceFontFamily, PriceFontSize) { Bold = true };
                priceTextFormat = priceFont.ToDirectWriteTextFormat();
                priceTextFormat.TextAlignment = SharpDX.DirectWrite.TextAlignment.Center;
                priceTextFormat.ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center;
                priceTextFormat.WordWrapping = SharpDX.DirectWrite.WordWrapping.NoWrap;
            }
            else if (State == State.Terminated)
            {
                if (textFormat != null) { textFormat.Dispose(); textFormat = null; }
                if (priceTextFormat != null) { priceTextFormat.Dispose(); priceTextFormat = null; }
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
            if (buyBrushDx != null) { buyBrushDx.Dispose(); buyBrushDx = null; }
            if (sellBrushDx != null) { sellBrushDx.Dispose(); sellBrushDx = null; }
            if (volumeTextBrushDx != null) { volumeTextBrushDx.Dispose(); volumeTextBrushDx = null; }
            if (priceTextBrushDx != null) { priceTextBrushDx.Dispose(); priceTextBrushDx = null; }
            if (dominantTextBrushDx != null) { dominantTextBrushDx.Dispose(); dominantTextBrushDx = null; }
            if (extendedDominantLineBarBrushDx != null) { extendedDominantLineBarBrushDx.Dispose(); extendedDominantLineBarBrushDx = null; }
            if (largestVolumeTextBrushDx != null) { largestVolumeTextBrushDx.Dispose(); largestVolumeTextBrushDx = null; }
            if (extendedLargestBuyVolumeLineBarBrushDx != null) { extendedLargestBuyVolumeLineBarBrushDx.Dispose(); extendedLargestBuyVolumeLineBarBrushDx = null; }
            if (extendedLargestSellVolumeLineBarBrushDx != null) { extendedLargestSellVolumeLineBarBrushDx.Dispose(); extendedLargestSellVolumeLineBarBrushDx = null; }
        }

        public override void OnRenderTargetChanged()
        {
            DisposeDxBrushes();

            if (RenderTarget == null) return;

            if (BuyColor != null && SellColor != null)
            {
                buyBrushDx = BuyColor.ToDxBrush(RenderTarget);
                sellBrushDx = SellColor.ToDxBrush(RenderTarget);
            }

            if (VolumeTextColor != null) volumeTextBrushDx = VolumeTextColor.ToDxBrush(RenderTarget);
            if (PriceTextColor != null) priceTextBrushDx = PriceTextColor.ToDxBrush(RenderTarget);
            if (DominantColor != null) dominantTextBrushDx = DominantColor.ToDxBrush(RenderTarget);
            if (ExtendedDominantLineBarColor != null) extendedDominantLineBarBrushDx = ExtendedDominantLineBarColor.ToDxBrush(RenderTarget);
            if (LargestVolumeColor != null) largestVolumeTextBrushDx = LargestVolumeColor.ToDxBrush(RenderTarget);
            if (LargestBuyVolumeExtendedLineBarColor != null) extendedLargestBuyVolumeLineBarBrushDx = LargestBuyVolumeExtendedLineBarColor.ToDxBrush(RenderTarget);
            if (LargestSellVolumeExtendedLineBarColor != null) extendedLargestSellVolumeLineBarBrushDx = LargestSellVolumeExtendedLineBarColor.ToDxBrush(RenderTarget);

            extendedLineStrokeHelper = new NinjaTrader.Gui.Stroke(ExtendedDominantLineBarColor ?? System.Windows.Media.Brushes.Gold, LineStyle, LineWidthPixels);
            extendedLineStrokeHelper.RenderTarget = RenderTarget;

            extendedLargestBuyVolumeLineStrokeHelper = new NinjaTrader.Gui.Stroke(LargestBuyVolumeExtendedLineBarColor ?? System.Windows.Media.Brushes.Cyan, LargestVolumeLineStyle, LargestVolumeLineWidthPixels);
            extendedLargestBuyVolumeLineStrokeHelper.RenderTarget = RenderTarget;

            extendedLargestSellVolumeLineStrokeHelper = new NinjaTrader.Gui.Stroke(LargestSellVolumeExtendedLineBarColor ?? System.Windows.Media.Brushes.OrangeRed, LargestVolumeLineStyle, LargestVolumeLineWidthPixels);
            extendedLargestSellVolumeLineStrokeHelper.RenderTarget = RenderTarget;
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress == 0)
            {
                if (Bars.IsFirstBarOfSession)
                {
                    buyUsdByPrice.Clear();
                    sellUsdByPrice.Clear();
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

            double price = Close[0];
            double size = Volume[0];

            if (SessionFilter == EGDollarVolumeProfileSessionFilter.RTH && !IsInsideRth(Time[0]))
            {
                lastLastPrice = price;
                return;
            }

            bool? isBuy = null;
            if (currentAsk > 0 && price >= currentAsk) isBuy = true;
            else if (currentBid > 0 && price <= currentBid) isBuy = false;
            else if (lastLastPrice > 0)
            {
                if (price > lastLastPrice) isBuy = true;
                else if (price < lastLastPrice) isBuy = false;
            }

            lastLastPrice = price;

            if (isBuy == null) return;

            double usd = price * size * Instrument.MasterInstrument.PointValue;
            double bucket = GetBucket(price);

            Dictionary<double, double> target = isBuy.Value ? buyUsdByPrice : sellUsdByPrice;
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
            double width = PriceGroupingPoints > 0 ? PriceGroupingPoints : Instrument.MasterInstrument.TickSize;
            return Math.Floor(price / width) * width;
        }

        private float MeasureTextWidth(string text, SharpDX.DirectWrite.TextFormat format)
        {
            using (var layout = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, text, format, 2000f, 100f))
            {
                return layout.Metrics.Width;
            }
        }

        private string FormatUsd(double v)
        {
            string sign = v < 0 ? "-" : "";
            double abs = Math.Abs(v);
            string suffix = ShowUsdSuffix ? " USD" : "";
            if (abs >= 1_000_000) return sign + (abs / 1_000_000).ToString("0.00", CultureInfo.InvariantCulture) + "M" + suffix;
            if (abs >= 1_000) return sign + (abs / 1_000).ToString("0.00", CultureInfo.InvariantCulture) + "K" + suffix;
            return sign + abs.ToString("0.00", CultureInfo.InvariantCulture) + suffix;
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);

            if (buyUsdByPrice.Count == 0 && sellUsdByPrice.Count == 0) return;
            if (buyBrushDx == null || sellBrushDx == null || volumeTextBrushDx == null || textFormat == null) return;
            if (priceTextFormat == null || priceTextBrushDx == null) return;

            var buckets = new HashSet<double>(buyUsdByPrice.Keys);
            buckets.UnionWith(sellUsdByPrice.Keys);
            if (buckets.Count == 0) return;

            var levels = new List<Tuple<double, double, double>>();
            foreach (double p in buckets)
            {
                double buy;
                double sell;
                buyUsdByPrice.TryGetValue(p, out buy);
                sellUsdByPrice.TryGetValue(p, out sell);
                levels.Add(Tuple.Create(p, buy, sell));
            }

            double maxAbsNet = levels.Max(l => Math.Abs(l.Item2 - l.Item3));
            if (maxAbsNet <= 0) return;

            double maxBuyNet = levels.Where(l => l.Item2 - l.Item3 >= 0).Select(l => l.Item2 - l.Item3).DefaultIfEmpty(0).Max();
            double maxSellNet = levels.Where(l => l.Item2 - l.Item3 < 0).Select(l => l.Item3 - l.Item2).DefaultIfEmpty(0).Max();
            double fade = GradientLevel / 10.0;

            IEnumerable<Tuple<double, double, double>> toDrawQuery = levels.OrderByDescending(l => Math.Abs(l.Item2 - l.Item3));
            if (MaxLevelsToShow > 0) toDrawQuery = toDrawQuery.Take(MaxLevelsToShow);
            List<Tuple<double, double, double>> toDraw = toDrawQuery.ToList();

            var topBuyLevels = new HashSet<double>(toDraw.Where(l => l.Item2 - l.Item3 >= 0)
                .OrderByDescending(l => l.Item2 - l.Item3).Take(MaxLargestVolumeLevelsPerSide).Select(l => l.Item1));
            var topSellLevels = new HashSet<double>(toDraw.Where(l => l.Item2 - l.Item3 < 0)
                .OrderByDescending(l => l.Item3 - l.Item2).Take(MaxLargestVolumeLevelsPerSide).Select(l => l.Item1));

            float margin = WallSpacingPixels;
            float priceBoxWidth = 74f;
            float gap = TextSpacingPixels;
            float labelWidth = 150f;
            double groupSize = PriceGroupingPoints > 0 ? PriceGroupingPoints : Instrument.MasterInstrument.TickSize;

            float priceBoxRight = ChartPanel.X + ChartPanel.W - margin;
            float priceBoxLeft = priceBoxRight - priceBoxWidth;
            float barAreaRight = priceBoxLeft - gap;

            foreach (var lvl in toDraw)
            {
                double price = lvl.Item1;
                if (price < chartScale.MinValue || price > chartScale.MaxValue) continue;

                double buyUsd = lvl.Item2;
                double sellUsd = lvl.Item3;
                double netUsd = buyUsd - sellUsd;
                double totalUsd = buyUsd + sellUsd;
                double pct = totalUsd > 0 ? Math.Abs(netUsd) / totalUsd * 100.0 : 0.0;

                float y = chartScale.GetYByValue(price);
                float rowHeight;
                if (BarHeightMode == EGDollarVolumeProfileBarHeightMode.Fixed)
                {
                    rowHeight = FixedBarHeightPixels;
                }
                else
                {
                    float rowTop = chartScale.GetYByValue(price + groupSize / 2.0);
                    float rowBottom = chartScale.GetYByValue(price - groupSize / 2.0);
                    rowHeight = Math.Max(Math.Abs(rowBottom - rowTop), 6f);
                }
                float width = (float)(Math.Abs(netUsd) / maxAbsNet * BarLengthPixels);
                bool isDominantLevel = pct >= DominantLevelThreshold;
                bool isLargestVolumeLevel = netUsd >= 0 ? topBuyLevels.Contains(price) : topSellLevels.Contains(price);

                if (ExtendDominantLevel && extendedDominantLineBarBrushDx != null && isDominantLevel)
                {
                    if (ExtendMode == EGDollarVolumeProfileExtendMode.Bar)
                    {
                        var extendRect = new SharpDX.RectangleF(ChartPanel.X, y - rowHeight / 2f, barAreaRight - ChartPanel.X, rowHeight);
                        RenderTarget.FillRectangle(extendRect, extendedDominantLineBarBrushDx);
                    }
                    else
                    {
                        RenderTarget.DrawLine(new SharpDX.Vector2(ChartPanel.X, y), new SharpDX.Vector2(barAreaRight, y), extendedDominantLineBarBrushDx, LineWidthPixels, extendedLineStrokeHelper == null ? null : extendedLineStrokeHelper.StrokeStyle);
                    }
                }

                if (ExtendLargestVolumeLevel && isLargestVolumeLevel)
                {
                    var extendedLargestVolumeBrush = netUsd >= 0 ? extendedLargestBuyVolumeLineBarBrushDx : extendedLargestSellVolumeLineBarBrushDx;
                    var extendedLargestVolumeStrokeHelper = netUsd >= 0 ? extendedLargestBuyVolumeLineStrokeHelper : extendedLargestSellVolumeLineStrokeHelper;

                    if (extendedLargestVolumeBrush != null)
                    {
                        if (ExtendLargestVolumeMode == EGDollarVolumeProfileExtendMode.Bar)
                        {
                            var extendRect = new SharpDX.RectangleF(ChartPanel.X, y - rowHeight / 2f, barAreaRight - ChartPanel.X, rowHeight);
                            RenderTarget.FillRectangle(extendRect, extendedLargestVolumeBrush);
                        }
                        else
                        {
                            RenderTarget.DrawLine(new SharpDX.Vector2(ChartPanel.X, y), new SharpDX.Vector2(barAreaRight, y), extendedLargestVolumeBrush, LargestVolumeLineWidthPixels, extendedLargestVolumeStrokeHelper == null ? null : extendedLargestVolumeStrokeHelper.StrokeStyle);
                        }
                    }
                }

                var brush = netUsd >= 0 ? buyBrushDx : sellBrushDx;
                if (BarFill == EGDollarVolumeProfileBarFillMode.Gradient)
                {
                    double sideMax = netUsd >= 0 ? maxBuyNet : maxSellNet;
                    double intensity = sideMax > 0 ? Math.Abs(netUsd) / sideMax : 1.0;
                    brush.Opacity = (float)((1 - fade) + fade * intensity);
                }
                else
                {
                    brush.Opacity = 1f;
                }
                var barRect = new SharpDX.RectangleF(barAreaRight - width, y - rowHeight / 2f, width, rowHeight);
                RenderTarget.FillRectangle(barRect, brush);

                string pctText = pct.ToString("0.##", CultureInfo.InvariantCulture) + "%";
                float textBoxWidth = Math.Max(width, labelWidth);
                bool showDominantText = HighlightDominantLevels && dominantTextBrushDx != null && isDominantLevel;
                bool showLargestVolumeText = HighlightLargestVolumeLevels && largestVolumeTextBrushDx != null && isLargestVolumeLevel;

                if (showDominantText || showLargestVolumeText)
                {
                    string dollarPart = FormatUsd(netUsd) + " (";
                    var dollarBrush = showLargestVolumeText ? largestVolumeTextBrushDx : volumeTextBrushDx;
                    var pctBrush = showDominantText ? dominantTextBrushDx : volumeTextBrushDx;

                    float closeParenWidth = MeasureTextWidth(")", textFormat);
                    float pctWidth = MeasureTextWidth(pctText, textFormat);
                    float dollarWidth = MeasureTextWidth(dollarPart, textFormat);

                    float closeParenX = barAreaRight - closeParenWidth;
                    float pctX = closeParenX - pctWidth;
                    float dollarX = pctX - dollarWidth;

                    var closeParenRect = new SharpDX.RectangleF(closeParenX, y - rowHeight / 2f, closeParenWidth, rowHeight);
                    var pctRect = new SharpDX.RectangleF(pctX, y - rowHeight / 2f, pctWidth, rowHeight);
                    var dollarRect = new SharpDX.RectangleF(dollarX, y - rowHeight / 2f, dollarWidth, rowHeight);

                    RenderTarget.DrawText(dollarPart, textFormat, dollarRect, dollarBrush);
                    RenderTarget.DrawText(pctText, textFormat, pctRect, pctBrush);
                    RenderTarget.DrawText(")", textFormat, closeParenRect, volumeTextBrushDx);
                }
                else
                {
                    string label = FormatUsd(netUsd) + " (" + pctText + ")";
                    var textRect = new SharpDX.RectangleF(barAreaRight - textBoxWidth, y - rowHeight / 2f, textBoxWidth, rowHeight);
                    RenderTarget.DrawText(label, textFormat, textRect, volumeTextBrushDx);
                }

                var priceRect = new SharpDX.RectangleF(priceBoxLeft, y - rowHeight / 2f, priceBoxWidth, rowHeight);

                string priceLabel = Instrument.MasterInstrument.FormatPrice(price);
                RenderTarget.DrawText(priceLabel, priceTextFormat, priceRect, priceTextBrushDx);
            }
        }

        #region Properties

        // ----- Filter -----

        [XmlIgnore]
        [Display(Name = "Session", Description = "Restrict accumulation to Regular Trading Hours (RTH) or include the full extended session (ETH)", GroupName = "Filter", Order = 1)]
        public EGDollarVolumeProfileSessionFilter SessionFilter { get; set; }

        [Browsable(false)]
        public string SessionFilterSerializable
        {
            get { return SessionFilter.ToString(); }
            set { SessionFilter = (EGDollarVolumeProfileSessionFilter)Enum.Parse(typeof(EGDollarVolumeProfileSessionFilter), value); }
        }

        // ----- Layout -----

        [NinjaScriptProperty]
        [Range(0.0001, double.MaxValue)]
        [Display(Name = "Price Grouping (points)", Description = "Width in price points grouped into each level", GroupName = "Layout", Order = 1)]
        public double PriceGroupingPoints { get; set; }

        [NinjaScriptProperty]
        [Range(10, 2000)]
        [Display(Name = "Max Bar Length (px)", Description = "Max horizontal length in pixels for the largest bar", GroupName = "Layout", Order = 2)]
        public int BarLengthPixels { get; set; }

        [XmlIgnore]
        [Display(Name = "Bar Height Type", Description = "Dynamic sizes each row's height from Price Grouping (points) at the current chart zoom; Fixed uses a constant Fixed Bar Height (px) instead", GroupName = "Layout", Order = 3)]
        public EGDollarVolumeProfileBarHeightMode BarHeightMode { get; set; }

        [Browsable(false)]
        public string BarHeightModeSerializable
        {
            get { return BarHeightMode.ToString(); }
            set { BarHeightMode = (EGDollarVolumeProfileBarHeightMode)Enum.Parse(typeof(EGDollarVolumeProfileBarHeightMode), value); }
        }

        [NinjaScriptProperty]
        [Range(4, 100)]
        [Display(Name = "Fixed Bar Height (px)", Description = "Height in pixels of each price level's row when Bar Height Type is Fixed", GroupName = "Layout", Order = 4)]
        public int FixedBarHeightPixels { get; set; }

        [NinjaScriptProperty]
        [Range(0, 500)]
        [Display(Name = "Max Levels Shown (0 = all)", Description = "Max number of price levels rendered, ranked by net dollar volume; 0 shows all", GroupName = "Layout", Order = 5)]
        public int MaxLevelsToShow { get; set; }

        [NinjaScriptProperty]
        [Range(0, 200)]
        [Display(Name = "Chart Wall Spacing (px)", Description = "Horizontal gap in pixels between the ladder and the chart's right/scale edge", GroupName = "Layout", Order = 6)]
        public int WallSpacingPixels { get; set; }

        [NinjaScriptProperty]
        [Range(0, 200)]
        [Display(Name = "Price/Volume Text Spacing (px)", Description = "Horizontal gap in pixels between the price box and the volume bar/text", GroupName = "Layout", Order = 7)]
        public int TextSpacingPixels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show USD Suffix", Description = "Append \"USD\" to each dollar volume label", GroupName = "Layout", Order = 8)]
        public bool ShowUsdSuffix { get; set; }

        // ----- Bar Fill -----

        [XmlIgnore]
        [Display(Name = "Bar Fill", Description = "Solid keeps every bar at full opacity; Gradient scales each bar's opacity by its strength relative to the strongest bar on its side", GroupName = "Bar Fill", Order = 1)]
        public EGDollarVolumeProfileBarFillMode BarFill { get; set; }

        [Browsable(false)]
        public string BarFillSerializable
        {
            get { return BarFill.ToString(); }
            set { BarFill = (EGDollarVolumeProfileBarFillMode)Enum.Parse(typeof(EGDollarVolumeProfileBarFillMode), value); }
        }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Gradient Level", Description = "How strongly Gradient mode's opacity scales with relative strength", GroupName = "Bar Fill", Order = 2)]
        public int GradientLevel { get; set; }

        // ----- Dominant Levels -----

        [NinjaScriptProperty]
        [Display(Name = "Highlight Dominant Levels", Description = "Color the percentage portion of a level's label in Dominant Color once it reaches Dominant Level Threshold", GroupName = "Dominant Levels", Order = 1)]
        public bool HighlightDominantLevels { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Dominant Level Threshold (%)", Description = "Minimum imbalance percentage (|net| / total at that level) for a level to be considered dominant", GroupName = "Dominant Levels", Order = 2)]
        public int DominantLevelThreshold { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Extend Dominant Level", Description = "Draw a full-width extended line or bar behind dominant levels", GroupName = "Dominant Levels", Order = 3)]
        public bool ExtendDominantLevel { get; set; }

        [XmlIgnore]
        [Display(Name = "Extend Mode", Description = "Bar fills the full chart width behind the level; Line draws a single horizontal line instead", GroupName = "Dominant Levels", Order = 4)]
        public EGDollarVolumeProfileExtendMode ExtendMode { get; set; }

        [Browsable(false)]
        public string ExtendModeSerializable
        {
            get { return ExtendMode.ToString(); }
            set { ExtendMode = (EGDollarVolumeProfileExtendMode)Enum.Parse(typeof(EGDollarVolumeProfileExtendMode), value); }
        }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Line Width (px)", Description = "Width in pixels of the extended line when Extend Mode is Line", GroupName = "Dominant Levels", Order = 5)]
        public int LineWidthPixels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Line Style", Description = "Dash style of the extended line when Extend Mode is Line", GroupName = "Dominant Levels", Order = 6)]
        public DashStyleHelper LineStyle { get; set; }

        // ----- Largest Volume -----

        [NinjaScriptProperty]
        [Display(Name = "Highlight Largest Volume Levels", Description = "Color the dollar amount portion of a level's label in Largest Volume Color when it ranks among the largest per side", GroupName = "Largest Volume", Order = 1)]
        public bool HighlightLargestVolumeLevels { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Max Number Of Largest Volume Levels (per side)", Description = "Number of top levels, ranked by net dollar volume, flagged as largest-volume on each side (buy and sell independently)", GroupName = "Largest Volume", Order = 2)]
        public int MaxLargestVolumeLevelsPerSide { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Extend Largest Volume Level", Description = "Draw a full-width extended line or bar behind largest-volume levels, colored per side", GroupName = "Largest Volume", Order = 3)]
        public bool ExtendLargestVolumeLevel { get; set; }

        [XmlIgnore]
        [Display(Name = "Extend Largest Volume Mode", Description = "Bar fills the full chart width behind the level; Line draws a single horizontal line instead", GroupName = "Largest Volume", Order = 4)]
        public EGDollarVolumeProfileExtendMode ExtendLargestVolumeMode { get; set; }

        [Browsable(false)]
        public string ExtendLargestVolumeModeSerializable
        {
            get { return ExtendLargestVolumeMode.ToString(); }
            set { ExtendLargestVolumeMode = (EGDollarVolumeProfileExtendMode)Enum.Parse(typeof(EGDollarVolumeProfileExtendMode), value); }
        }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Largest Volume Line Width (px)", Description = "Width in pixels of the extended line when Extend Largest Volume Mode is Line", GroupName = "Largest Volume", Order = 5)]
        public int LargestVolumeLineWidthPixels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Largest Volume Line Style", Description = "Dash style of the extended line when Extend Largest Volume Mode is Line", GroupName = "Largest Volume", Order = 6)]
        public DashStyleHelper LargestVolumeLineStyle { get; set; }

        // ----- Text -----

        [NinjaScriptProperty]
        [TypeConverter(typeof(EGDollarVolumeProfileFontFamilyConverter))]
        [Display(Name = "Price Font Family", GroupName = "Text", Order = 1)]
        public string PriceFontFamily { get; set; }

        [NinjaScriptProperty]
        [TypeConverter(typeof(EGDollarVolumeProfileFontSizeConverter))]
        [Display(Name = "Price Font Size", GroupName = "Text", Order = 2)]
        public double PriceFontSize { get; set; }

        [NinjaScriptProperty]
        [TypeConverter(typeof(EGDollarVolumeProfileFontFamilyConverter))]
        [Display(Name = "Volume Font Family", GroupName = "Text", Order = 3)]
        public string VolumeFontFamily { get; set; }

        [NinjaScriptProperty]
        [TypeConverter(typeof(EGDollarVolumeProfileFontSizeConverter))]
        [Display(Name = "Volume Font Size", GroupName = "Text", Order = 4)]
        public double VolumeFontSize { get; set; }

        // ----- Colors -----

        private System.Windows.Media.Brush buyColor;
        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Net Buy Color", Description = "Bar color for levels where net dollar volume favors buyers", GroupName = "Colors", Order = 1)]
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
        [Display(Name = "Net Sell Color", Description = "Bar color for levels where net dollar volume favors sellers", GroupName = "Colors", Order = 2)]
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
        [Display(Name = "Dominant Color", Description = "Color used for the percentage portion of a dominant level's label", GroupName = "Colors", Order = 3)]
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

        private System.Windows.Media.Brush extendedDominantLineBarColor;
        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Extend Dominant Line/Bar Color", Description = "Color of the extended line/bar drawn behind dominant levels", GroupName = "Colors", Order = 4)]
        public System.Windows.Media.Brush ExtendedDominantLineBarColor
        {
            get { return extendedDominantLineBarColor; }
            set { extendedDominantLineBarColor = Frz(value); }
        }

        [Browsable(false)]
        public string ExtendedDominantLineBarColorSerializable
        {
            get { return Serialize.BrushToString(ExtendedDominantLineBarColor); }
            set { ExtendedDominantLineBarColor = Serialize.StringToBrush(value); }
        }

        private System.Windows.Media.Brush largestVolumeColor;
        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Largest Volume Color", Description = "Color used for the dollar amount portion of a largest-volume level's label", GroupName = "Colors", Order = 5)]
        public System.Windows.Media.Brush LargestVolumeColor
        {
            get { return largestVolumeColor; }
            set { largestVolumeColor = Frz(value); }
        }

        [Browsable(false)]
        public string LargestVolumeColorSerializable
        {
            get { return Serialize.BrushToString(LargestVolumeColor); }
            set { LargestVolumeColor = Serialize.StringToBrush(value); }
        }

        private System.Windows.Media.Brush largestBuyVolumeExtendedLineBarColor;
        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Largest Buy Volume Extended Line/Bar Color", Description = "Color of the extended line/bar drawn behind largest-volume buy-side levels", GroupName = "Colors", Order = 6)]
        public System.Windows.Media.Brush LargestBuyVolumeExtendedLineBarColor
        {
            get { return largestBuyVolumeExtendedLineBarColor; }
            set { largestBuyVolumeExtendedLineBarColor = Frz(value); }
        }

        [Browsable(false)]
        public string LargestBuyVolumeExtendedLineBarColorSerializable
        {
            get { return Serialize.BrushToString(LargestBuyVolumeExtendedLineBarColor); }
            set { LargestBuyVolumeExtendedLineBarColor = Serialize.StringToBrush(value); }
        }

        private System.Windows.Media.Brush largestSellVolumeExtendedLineBarColor;
        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Largest Sell Volume Extended Line/Bar Color", Description = "Color of the extended line/bar drawn behind largest-volume sell-side levels", GroupName = "Colors", Order = 7)]
        public System.Windows.Media.Brush LargestSellVolumeExtendedLineBarColor
        {
            get { return largestSellVolumeExtendedLineBarColor; }
            set { largestSellVolumeExtendedLineBarColor = Frz(value); }
        }

        [Browsable(false)]
        public string LargestSellVolumeExtendedLineBarColorSerializable
        {
            get { return Serialize.BrushToString(LargestSellVolumeExtendedLineBarColor); }
            set { LargestSellVolumeExtendedLineBarColor = Serialize.StringToBrush(value); }
        }

        private System.Windows.Media.Brush priceTextColor;
        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Price Text Color", Description = "Color of the price box text", GroupName = "Colors", Order = 8)]
        public System.Windows.Media.Brush PriceTextColor
        {
            get { return priceTextColor; }
            set { priceTextColor = Frz(value); }
        }

        [Browsable(false)]
        public string PriceTextColorSerializable
        {
            get { return Serialize.BrushToString(PriceTextColor); }
            set { PriceTextColor = Serialize.StringToBrush(value); }
        }

        private System.Windows.Media.Brush volumeTextColor;
        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Volume Text Color", Description = "Default color of the dollar volume / percentage label text", GroupName = "Colors", Order = 9)]
        public System.Windows.Media.Brush VolumeTextColor
        {
            get { return volumeTextColor; }
            set { volumeTextColor = Frz(value); }
        }

        [Browsable(false)]
        public string VolumeTextColorSerializable
        {
            get { return Serialize.BrushToString(VolumeTextColor); }
            set { VolumeTextColor = Serialize.StringToBrush(value); }
        }

        #endregion
    }

    public enum EGDollarVolumeProfileSessionFilter { RTH, ETH }

    public enum EGDollarVolumeProfileBarHeightMode { Dynamic, Fixed }

    public enum EGDollarVolumeProfileBarFillMode { Solid, Gradient }

    public enum EGDollarVolumeProfileExtendMode { Line, Bar }

    public class EGDollarVolumeProfileFontFamilyConverter : TypeConverter
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

    public class EGDollarVolumeProfileFontSizeConverter : TypeConverter
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
		private EducatedGambling.EGDollarVolumeProfile[] cacheEGDollarVolumeProfile;
		public EducatedGambling.EGDollarVolumeProfile EGDollarVolumeProfile(double priceGroupingPoints, int barLengthPixels, int fixedBarHeightPixels, int maxLevelsToShow, int wallSpacingPixels, int textSpacingPixels, bool showUsdSuffix, int gradientLevel, bool highlightDominantLevels, int dominantLevelThreshold, bool extendDominantLevel, int lineWidthPixels, DashStyleHelper lineStyle, bool highlightLargestVolumeLevels, int maxLargestVolumeLevelsPerSide, bool extendLargestVolumeLevel, int largestVolumeLineWidthPixels, DashStyleHelper largestVolumeLineStyle, string priceFontFamily, double priceFontSize, string volumeFontFamily, double volumeFontSize, System.Windows.Media.Brush buyColor, System.Windows.Media.Brush sellColor, System.Windows.Media.Brush dominantColor, System.Windows.Media.Brush extendedDominantLineBarColor, System.Windows.Media.Brush largestVolumeColor, System.Windows.Media.Brush largestBuyVolumeExtendedLineBarColor, System.Windows.Media.Brush largestSellVolumeExtendedLineBarColor, System.Windows.Media.Brush priceTextColor, System.Windows.Media.Brush volumeTextColor)
		{
			return EGDollarVolumeProfile(Input, priceGroupingPoints, barLengthPixels, fixedBarHeightPixels, maxLevelsToShow, wallSpacingPixels, textSpacingPixels, showUsdSuffix, gradientLevel, highlightDominantLevels, dominantLevelThreshold, extendDominantLevel, lineWidthPixels, lineStyle, highlightLargestVolumeLevels, maxLargestVolumeLevelsPerSide, extendLargestVolumeLevel, largestVolumeLineWidthPixels, largestVolumeLineStyle, priceFontFamily, priceFontSize, volumeFontFamily, volumeFontSize, buyColor, sellColor, dominantColor, extendedDominantLineBarColor, largestVolumeColor, largestBuyVolumeExtendedLineBarColor, largestSellVolumeExtendedLineBarColor, priceTextColor, volumeTextColor);
		}

		public EducatedGambling.EGDollarVolumeProfile EGDollarVolumeProfile(ISeries<double> input, double priceGroupingPoints, int barLengthPixels, int fixedBarHeightPixels, int maxLevelsToShow, int wallSpacingPixels, int textSpacingPixels, bool showUsdSuffix, int gradientLevel, bool highlightDominantLevels, int dominantLevelThreshold, bool extendDominantLevel, int lineWidthPixels, DashStyleHelper lineStyle, bool highlightLargestVolumeLevels, int maxLargestVolumeLevelsPerSide, bool extendLargestVolumeLevel, int largestVolumeLineWidthPixels, DashStyleHelper largestVolumeLineStyle, string priceFontFamily, double priceFontSize, string volumeFontFamily, double volumeFontSize, System.Windows.Media.Brush buyColor, System.Windows.Media.Brush sellColor, System.Windows.Media.Brush dominantColor, System.Windows.Media.Brush extendedDominantLineBarColor, System.Windows.Media.Brush largestVolumeColor, System.Windows.Media.Brush largestBuyVolumeExtendedLineBarColor, System.Windows.Media.Brush largestSellVolumeExtendedLineBarColor, System.Windows.Media.Brush priceTextColor, System.Windows.Media.Brush volumeTextColor)
		{
			if (cacheEGDollarVolumeProfile != null)
				for (int idx = 0; idx < cacheEGDollarVolumeProfile.Length; idx++)
					if (cacheEGDollarVolumeProfile[idx] != null && cacheEGDollarVolumeProfile[idx].PriceGroupingPoints == priceGroupingPoints && cacheEGDollarVolumeProfile[idx].BarLengthPixels == barLengthPixels && cacheEGDollarVolumeProfile[idx].FixedBarHeightPixels == fixedBarHeightPixels && cacheEGDollarVolumeProfile[idx].MaxLevelsToShow == maxLevelsToShow && cacheEGDollarVolumeProfile[idx].WallSpacingPixels == wallSpacingPixels && cacheEGDollarVolumeProfile[idx].TextSpacingPixels == textSpacingPixels && cacheEGDollarVolumeProfile[idx].ShowUsdSuffix == showUsdSuffix && cacheEGDollarVolumeProfile[idx].GradientLevel == gradientLevel && cacheEGDollarVolumeProfile[idx].HighlightDominantLevels == highlightDominantLevels && cacheEGDollarVolumeProfile[idx].DominantLevelThreshold == dominantLevelThreshold && cacheEGDollarVolumeProfile[idx].ExtendDominantLevel == extendDominantLevel && cacheEGDollarVolumeProfile[idx].LineWidthPixels == lineWidthPixels && cacheEGDollarVolumeProfile[idx].LineStyle == lineStyle && cacheEGDollarVolumeProfile[idx].HighlightLargestVolumeLevels == highlightLargestVolumeLevels && cacheEGDollarVolumeProfile[idx].MaxLargestVolumeLevelsPerSide == maxLargestVolumeLevelsPerSide && cacheEGDollarVolumeProfile[idx].ExtendLargestVolumeLevel == extendLargestVolumeLevel && cacheEGDollarVolumeProfile[idx].LargestVolumeLineWidthPixels == largestVolumeLineWidthPixels && cacheEGDollarVolumeProfile[idx].LargestVolumeLineStyle == largestVolumeLineStyle && cacheEGDollarVolumeProfile[idx].PriceFontFamily == priceFontFamily && cacheEGDollarVolumeProfile[idx].PriceFontSize == priceFontSize && cacheEGDollarVolumeProfile[idx].VolumeFontFamily == volumeFontFamily && cacheEGDollarVolumeProfile[idx].VolumeFontSize == volumeFontSize && cacheEGDollarVolumeProfile[idx].BuyColor == buyColor && cacheEGDollarVolumeProfile[idx].SellColor == sellColor && cacheEGDollarVolumeProfile[idx].DominantColor == dominantColor && cacheEGDollarVolumeProfile[idx].ExtendedDominantLineBarColor == extendedDominantLineBarColor && cacheEGDollarVolumeProfile[idx].LargestVolumeColor == largestVolumeColor && cacheEGDollarVolumeProfile[idx].LargestBuyVolumeExtendedLineBarColor == largestBuyVolumeExtendedLineBarColor && cacheEGDollarVolumeProfile[idx].LargestSellVolumeExtendedLineBarColor == largestSellVolumeExtendedLineBarColor && cacheEGDollarVolumeProfile[idx].PriceTextColor == priceTextColor && cacheEGDollarVolumeProfile[idx].VolumeTextColor == volumeTextColor && cacheEGDollarVolumeProfile[idx].EqualsInput(input))
						return cacheEGDollarVolumeProfile[idx];
			return CacheIndicator<EducatedGambling.EGDollarVolumeProfile>(new EducatedGambling.EGDollarVolumeProfile(){ PriceGroupingPoints = priceGroupingPoints, BarLengthPixels = barLengthPixels, FixedBarHeightPixels = fixedBarHeightPixels, MaxLevelsToShow = maxLevelsToShow, WallSpacingPixels = wallSpacingPixels, TextSpacingPixels = textSpacingPixels, ShowUsdSuffix = showUsdSuffix, GradientLevel = gradientLevel, HighlightDominantLevels = highlightDominantLevels, DominantLevelThreshold = dominantLevelThreshold, ExtendDominantLevel = extendDominantLevel, LineWidthPixels = lineWidthPixels, LineStyle = lineStyle, HighlightLargestVolumeLevels = highlightLargestVolumeLevels, MaxLargestVolumeLevelsPerSide = maxLargestVolumeLevelsPerSide, ExtendLargestVolumeLevel = extendLargestVolumeLevel, LargestVolumeLineWidthPixels = largestVolumeLineWidthPixels, LargestVolumeLineStyle = largestVolumeLineStyle, PriceFontFamily = priceFontFamily, PriceFontSize = priceFontSize, VolumeFontFamily = volumeFontFamily, VolumeFontSize = volumeFontSize, BuyColor = buyColor, SellColor = sellColor, DominantColor = dominantColor, ExtendedDominantLineBarColor = extendedDominantLineBarColor, LargestVolumeColor = largestVolumeColor, LargestBuyVolumeExtendedLineBarColor = largestBuyVolumeExtendedLineBarColor, LargestSellVolumeExtendedLineBarColor = largestSellVolumeExtendedLineBarColor, PriceTextColor = priceTextColor, VolumeTextColor = volumeTextColor }, input, ref cacheEGDollarVolumeProfile);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.EducatedGambling.EGDollarVolumeProfile EGDollarVolumeProfile(double priceGroupingPoints, int barLengthPixels, int fixedBarHeightPixels, int maxLevelsToShow, int wallSpacingPixels, int textSpacingPixels, bool showUsdSuffix, int gradientLevel, bool highlightDominantLevels, int dominantLevelThreshold, bool extendDominantLevel, int lineWidthPixels, DashStyleHelper lineStyle, bool highlightLargestVolumeLevels, int maxLargestVolumeLevelsPerSide, bool extendLargestVolumeLevel, int largestVolumeLineWidthPixels, DashStyleHelper largestVolumeLineStyle, string priceFontFamily, double priceFontSize, string volumeFontFamily, double volumeFontSize, System.Windows.Media.Brush buyColor, System.Windows.Media.Brush sellColor, System.Windows.Media.Brush dominantColor, System.Windows.Media.Brush extendedDominantLineBarColor, System.Windows.Media.Brush largestVolumeColor, System.Windows.Media.Brush largestBuyVolumeExtendedLineBarColor, System.Windows.Media.Brush largestSellVolumeExtendedLineBarColor, System.Windows.Media.Brush priceTextColor, System.Windows.Media.Brush volumeTextColor)
		{
			return indicator.EGDollarVolumeProfile(Input, priceGroupingPoints, barLengthPixels, fixedBarHeightPixels, maxLevelsToShow, wallSpacingPixels, textSpacingPixels, showUsdSuffix, gradientLevel, highlightDominantLevels, dominantLevelThreshold, extendDominantLevel, lineWidthPixels, lineStyle, highlightLargestVolumeLevels, maxLargestVolumeLevelsPerSide, extendLargestVolumeLevel, largestVolumeLineWidthPixels, largestVolumeLineStyle, priceFontFamily, priceFontSize, volumeFontFamily, volumeFontSize, buyColor, sellColor, dominantColor, extendedDominantLineBarColor, largestVolumeColor, largestBuyVolumeExtendedLineBarColor, largestSellVolumeExtendedLineBarColor, priceTextColor, volumeTextColor);
		}

		public Indicators.EducatedGambling.EGDollarVolumeProfile EGDollarVolumeProfile(ISeries<double> input , double priceGroupingPoints, int barLengthPixels, int fixedBarHeightPixels, int maxLevelsToShow, int wallSpacingPixels, int textSpacingPixels, bool showUsdSuffix, int gradientLevel, bool highlightDominantLevels, int dominantLevelThreshold, bool extendDominantLevel, int lineWidthPixels, DashStyleHelper lineStyle, bool highlightLargestVolumeLevels, int maxLargestVolumeLevelsPerSide, bool extendLargestVolumeLevel, int largestVolumeLineWidthPixels, DashStyleHelper largestVolumeLineStyle, string priceFontFamily, double priceFontSize, string volumeFontFamily, double volumeFontSize, System.Windows.Media.Brush buyColor, System.Windows.Media.Brush sellColor, System.Windows.Media.Brush dominantColor, System.Windows.Media.Brush extendedDominantLineBarColor, System.Windows.Media.Brush largestVolumeColor, System.Windows.Media.Brush largestBuyVolumeExtendedLineBarColor, System.Windows.Media.Brush largestSellVolumeExtendedLineBarColor, System.Windows.Media.Brush priceTextColor, System.Windows.Media.Brush volumeTextColor)
		{
			return indicator.EGDollarVolumeProfile(input, priceGroupingPoints, barLengthPixels, fixedBarHeightPixels, maxLevelsToShow, wallSpacingPixels, textSpacingPixels, showUsdSuffix, gradientLevel, highlightDominantLevels, dominantLevelThreshold, extendDominantLevel, lineWidthPixels, lineStyle, highlightLargestVolumeLevels, maxLargestVolumeLevelsPerSide, extendLargestVolumeLevel, largestVolumeLineWidthPixels, largestVolumeLineStyle, priceFontFamily, priceFontSize, volumeFontFamily, volumeFontSize, buyColor, sellColor, dominantColor, extendedDominantLineBarColor, largestVolumeColor, largestBuyVolumeExtendedLineBarColor, largestSellVolumeExtendedLineBarColor, priceTextColor, volumeTextColor);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.EducatedGambling.EGDollarVolumeProfile EGDollarVolumeProfile(double priceGroupingPoints, int barLengthPixels, int fixedBarHeightPixels, int maxLevelsToShow, int wallSpacingPixels, int textSpacingPixels, bool showUsdSuffix, int gradientLevel, bool highlightDominantLevels, int dominantLevelThreshold, bool extendDominantLevel, int lineWidthPixels, DashStyleHelper lineStyle, bool highlightLargestVolumeLevels, int maxLargestVolumeLevelsPerSide, bool extendLargestVolumeLevel, int largestVolumeLineWidthPixels, DashStyleHelper largestVolumeLineStyle, string priceFontFamily, double priceFontSize, string volumeFontFamily, double volumeFontSize, System.Windows.Media.Brush buyColor, System.Windows.Media.Brush sellColor, System.Windows.Media.Brush dominantColor, System.Windows.Media.Brush extendedDominantLineBarColor, System.Windows.Media.Brush largestVolumeColor, System.Windows.Media.Brush largestBuyVolumeExtendedLineBarColor, System.Windows.Media.Brush largestSellVolumeExtendedLineBarColor, System.Windows.Media.Brush priceTextColor, System.Windows.Media.Brush volumeTextColor)
		{
			return indicator.EGDollarVolumeProfile(Input, priceGroupingPoints, barLengthPixels, fixedBarHeightPixels, maxLevelsToShow, wallSpacingPixels, textSpacingPixels, showUsdSuffix, gradientLevel, highlightDominantLevels, dominantLevelThreshold, extendDominantLevel, lineWidthPixels, lineStyle, highlightLargestVolumeLevels, maxLargestVolumeLevelsPerSide, extendLargestVolumeLevel, largestVolumeLineWidthPixels, largestVolumeLineStyle, priceFontFamily, priceFontSize, volumeFontFamily, volumeFontSize, buyColor, sellColor, dominantColor, extendedDominantLineBarColor, largestVolumeColor, largestBuyVolumeExtendedLineBarColor, largestSellVolumeExtendedLineBarColor, priceTextColor, volumeTextColor);
		}

		public Indicators.EducatedGambling.EGDollarVolumeProfile EGDollarVolumeProfile(ISeries<double> input , double priceGroupingPoints, int barLengthPixels, int fixedBarHeightPixels, int maxLevelsToShow, int wallSpacingPixels, int textSpacingPixels, bool showUsdSuffix, int gradientLevel, bool highlightDominantLevels, int dominantLevelThreshold, bool extendDominantLevel, int lineWidthPixels, DashStyleHelper lineStyle, bool highlightLargestVolumeLevels, int maxLargestVolumeLevelsPerSide, bool extendLargestVolumeLevel, int largestVolumeLineWidthPixels, DashStyleHelper largestVolumeLineStyle, string priceFontFamily, double priceFontSize, string volumeFontFamily, double volumeFontSize, System.Windows.Media.Brush buyColor, System.Windows.Media.Brush sellColor, System.Windows.Media.Brush dominantColor, System.Windows.Media.Brush extendedDominantLineBarColor, System.Windows.Media.Brush largestVolumeColor, System.Windows.Media.Brush largestBuyVolumeExtendedLineBarColor, System.Windows.Media.Brush largestSellVolumeExtendedLineBarColor, System.Windows.Media.Brush priceTextColor, System.Windows.Media.Brush volumeTextColor)
		{
			return indicator.EGDollarVolumeProfile(input, priceGroupingPoints, barLengthPixels, fixedBarHeightPixels, maxLevelsToShow, wallSpacingPixels, textSpacingPixels, showUsdSuffix, gradientLevel, highlightDominantLevels, dominantLevelThreshold, extendDominantLevel, lineWidthPixels, lineStyle, highlightLargestVolumeLevels, maxLargestVolumeLevelsPerSide, extendLargestVolumeLevel, largestVolumeLineWidthPixels, largestVolumeLineStyle, priceFontFamily, priceFontSize, volumeFontFamily, volumeFontSize, buyColor, sellColor, dominantColor, extendedDominantLineBarColor, largestVolumeColor, largestBuyVolumeExtendedLineBarColor, largestSellVolumeExtendedLineBarColor, priceTextColor, volumeTextColor);
		}
	}
}

#endregion
