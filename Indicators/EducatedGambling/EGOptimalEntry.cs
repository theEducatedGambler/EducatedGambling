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
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Indicators.EducatedGambling
{
    public class EGOptimalEntryFontFamilyTypeConverter : TypeConverter
    {
        private static readonly StandardValuesCollection _values = new StandardValuesCollection(
            Fonts.SystemFontFamilies
                .Select(f => f.Source)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList());

        public override bool CanConvertFrom(ITypeDescriptorContext c, Type t) => t == typeof(string) || base.CanConvertFrom(c, t);
        public override bool CanConvertTo(ITypeDescriptorContext c, Type t) => t == typeof(string) || base.CanConvertTo(c, t);
        public override bool GetStandardValuesSupported(ITypeDescriptorContext c) => true;
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext c) => false;
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext c) => _values;
    }

    public enum EGOptimalEntryRejoinMethod { HighLow, Close }

    [CategoryOrder("EMA Cloud",     1)]
    [CategoryOrder("TRAMA Line",    2)]
    [CategoryOrder("Entry Detection", 3)]
    [CategoryOrder("Entry Area", 4)]
    [CategoryOrder("Rejoin Detection", 5)]
    [CategoryOrder("Rejoin Area", 6)]
    // Flags an EMA Cloud (fast/slow EMA) crossover when the TRAMA line sits within N ticks of the
    // point where the two EMAs cross, then draws a short horizontal line to the right of that bar.
    // Fully self-contained: the EMAs and the TRAMA (LuxAlgo Trend Regularity Adaptive Moving Average,
    // CC BY-NC-SA 4.0, same engine as TRAMATimeFramingLine) are computed inline, no other indicators.
    // The cloud (EMA lines + fill) and the TRAMA line can each be shown or hidden independently;
    // detection runs either way.
    public class EGOptimalEntry : Indicator
    {
        private class EntryMarker
        {
            public int    Bar;      // signal bar (left edge of the entry rectangle)
            public bool   Bull;
            public double Anchor;   // price of the entry line (the B/S circle sits beyond it)
            public double Level = double.NaN;   // high/low level for the small circle, NaN = none
            public int    LevelBar = -1;        // bar index of the candle whose high/low sets the level
            public bool   Exceeded;             // high/low level further than the max distance: its line uses the exceeded color
            public string Tag;
        }

        private readonly List<EntryMarker> _markers = new List<EntryMarker>();

        private class RejoinStar
        {
            public int    Bar;     // candle the rejoin rectangle starts on
            public bool   Bull;
            public double Price;   // the rectangle's corner: its top for a bearish rejoin, its bottom for a bullish one
            public string Tag;     // matches the rejoin rectangle's tag
        }

        private readonly List<RejoinStar> _rejoinStars = new List<RejoinStar>();

        private class TramaCalculator
        {
            private readonly int _length;
            private readonly Queue<double> _flagWindow = new Queue<double>();
            private double _flagSum;
            private double _prevHighest;
            private double _prevLowest;
            private double _prevAma;
            private bool _hasPrev;

            public int Length => _length;

            public TramaCalculator(int length)
            {
                _length = length;
            }

            public double Update(double highest, double lowest, double src)
            {
                double hh = 0.0;
                double ll = 0.0;

                if (_hasPrev)
                {
                    hh = Math.Max(Math.Sign(highest - _prevHighest), 0);
                    ll = Math.Max(Math.Sign((lowest - _prevLowest) * -1.0), 0);
                }

                _prevHighest = highest;
                _prevLowest  = lowest;
                _hasPrev     = true;

                double flag = (hh > 0 || ll > 0) ? 1.0 : 0.0;
                _flagWindow.Enqueue(flag);
                _flagSum += flag;
                if (_flagWindow.Count > _length)
                    _flagSum -= _flagWindow.Dequeue();

                double ama;
                if (_flagWindow.Count < _length)
                {
                    ama = src;
                }
                else
                {
                    double smaFlag = _flagSum / _length;
                    double tc       = smaFlag * smaFlag;
                    ama = _prevAma + tc * (src - _prevAma);
                }

                _prevAma = ama;
                return ama;
            }
        }

        // BarsInProgress 1 = native higher-timeframe series, 2 = shared 1-minute feed for the
        // clock-anchored bucket mode. Both are always loaded so these indices never shift.
        private const int NativeBarsInProgressIndex       = 1;
        private const int SharedAnchorBarsInProgressIndex = 2;

        // Plot indices
        private const int PlotFast  = 0;
        private const int PlotSlow  = 1;
        private const int PlotTrama = 2;
        private const int PlotRejoin = 3;

        private Series<double> _fastEma;
        private Series<double> _slowEma;
        private Series<double> _rejoinEma;
        private readonly List<string> _rejoinTags = new List<string>();   // rejoin zones drawn for the latest entry
        private double _checkpointPrice;   // price of the last checkpoint: the entry, then each drawn rejoin area
        private int _trendDir;   // +1 after a bullish entry, -1 after a bearish one, 0 = none (reset when the EMAs cross)
        private int    _lastSignalBar = -1;
        private string _lastSignalTag;

        private TramaCalculator _calc;
        private double _tramaValue;
        private bool   _hasTrama;
        private double _tramaAtPrevBar = double.NaN;

        private System.Windows.Media.Brush _rejoinBullBorder;
        private System.Windows.Media.Brush _rejoinBearBorder;

        private SharpDX.Direct2D1.SolidColorBrush _bullBrush;
        private SharpDX.Direct2D1.SolidColorBrush _bearBrush;

        // Rolling high/low windows for each TRAMA source (native series, primary series, anchored buckets).
        private readonly Queue<double> _nativeHighWin  = new Queue<double>();
        private readonly Queue<double> _nativeLowWin   = new Queue<double>();
        private readonly Queue<double> _primaryHighWin = new Queue<double>();
        private readonly Queue<double> _primaryLowWin  = new Queue<double>();
        private readonly Queue<double> _bucketHighWin  = new Queue<double>();
        private readonly Queue<double> _bucketLowWin   = new Queue<double>();
        private DateTime _bucketStart = DateTime.MinValue;
        private double   _bucketHigh;
        private double   _bucketLow;
        private double   _bucketClose;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name        = "EGOptimalEntry";
                Description = "Marks entries where the TRAMA is within N ticks of an EMA cloud (fast/slow EMA) crossover: entry bar, B/S circle, high/low line, and Rejoin Area follow-up zones.";
                Calculate                = Calculate.OnBarClose;
                IsOverlay                = true;
                IsSuspendedWhileInactive = true;

                // EMA Cloud
                FastPeriod        = 8;
                SlowPeriod        = 21;
                ShowCloud         = true;
                FastEMAColor       = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0xFF, 0x99, 0x99)); FastEMAColor.Freeze();
                SlowEMAColor       = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0xB3, 0xFF, 0xB3)); SlowEMAColor.Freeze();
                EMALineWidth      = 1;
                EMALineOpacity    = 60;
                BullFillColor      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0xB3, 0xFF, 0xB3)); BullFillColor.Freeze();
                BearFillColor      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0xFF, 0x99, 0x99)); BearFillColor.Freeze();
                FillOpacity       = 10.0;

                // TRAMA Line
                TramaLength               = 20;
                UseCurrentTimeframe       = true;
                TimeframeMinutes          = 1;
                UseClockAnchoredTimeframe = true;
                ShowTrama                 = true;
                TramaColor                 = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0xE3, 0xE3, 0xE3)); TramaColor.Freeze();
                TramaWidth                = 1;
                TramaStyle                = DashStyleHelper.Dot;
                TramaOpacity              = 60;

                // Entry Detection
                NearTicks         = 60;
                ReplaceWithinBars = 15;
                DebugLog          = false;

                // Entry Bar
                RectWidthBars     = 7;
                EntryLineWidth    = 4;
                BullLineColor      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0x25, 0xD7, 0x25)); BullLineColor.Freeze();
                BearLineColor      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0xCC, 0x00, 0x00)); BearLineColor.Freeze();
                EntryLineOpacity  = 70;

                // High/Low Line
                ShowLevelLine      = true;
                LevelLengthBars    = 3;
                LevelLookback      = 10;
                LevelMaxTicks      = 60;
                LevelLineThickness = 4;
                LevelFarColor      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0xFF, 0xD7, 0x00)); LevelFarColor.Freeze();

                // Entry Marker
                ShowEntryMarker   = true;
                MarkerDiameter    = 20;
                MarkerGap         = 10;
                MarkerFontFamily  = "Segoe UI";
                MarkerFontSize    = 13;
                MarkerTextColor    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0x00, 0x00, 0x00)); MarkerTextColor.Freeze();

                // Entry Border (shared)
                EntryBorderWidth   = 1;
                EntryBorderColor   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0x00, 0x00, 0x00)); EntryBorderColor.Freeze();

                // Rejoin Area
                ShowRejoinArea         = true;
                RejoinWidthBars        = 6;
                MaxRejoinAreas         = 0;
                RejoinCheckpointPoints = 20;
                RejoinMethod           = EGOptimalEntryRejoinMethod.HighLow;
                RejoinBullColor        = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0x25, 0xD7, 0x25)); RejoinBullColor.Freeze();
                RejoinBearColor        = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0xCC, 0x00, 0x00)); RejoinBearColor.Freeze();
                RejoinOpacity          = 10;
                RejoinBorderWidth      = 1;
                RejoinBorderOpacity    = 70;
                ShowRejoinStar         = true;
                RejoinStarSize         = 16;
                RejoinStarOffset       = 7;
                RejoinStarOpacity      = 80;
                ShowRejoinEma          = true;
                RejoinEmaPeriod        = 50;
                RejoinEmaWidth         = 1;
                RejoinEmaStyle         = DashStyleHelper.Dot;
                RejoinEmaColor         = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0xE8, 0x97, 0x49)); RejoinEmaColor.Freeze();
                RejoinEmaOpacity       = 70;

                AddPlot(new Stroke(FastEMAColor, DashStyleHelper.Solid, EMALineWidth), PlotStyle.Line, "FastEMA");
                AddPlot(new Stroke(SlowEMAColor, DashStyleHelper.Solid, EMALineWidth), PlotStyle.Line, "SlowEMA");
                AddPlot(new Stroke(TramaColor, TramaStyle, TramaWidth), PlotStyle.Line, "TRAMA");
                AddPlot(new Stroke(RejoinEmaColor, RejoinEmaStyle, RejoinEmaWidth), PlotStyle.Line, "RejoinEMA");
            }
            else if (State == State.Configure)
            {
                AddDataSeries(Instrument.FullName, BarsPeriodType.Minute, TimeframeMinutes);
                AddDataSeries(Instrument.FullName, BarsPeriodType.Minute, 1);
            }
            else if (State == State.DataLoaded)
            {
                _fastEma = new Series<double>(this, MaximumBarsLookBack.Infinite);
                _slowEma = new Series<double>(this, MaximumBarsLookBack.Infinite);
                _rejoinEma = new Series<double>(this, MaximumBarsLookBack.Infinite);
                _trendDir  = 0;
                _rejoinTags.Clear();
                lock (_markers) _markers.Clear();
                lock (_rejoinStars) _rejoinStars.Clear();
                _lastSignalBar = -1;
                _lastSignalTag = null;

                _calc     = new TramaCalculator(TramaLength);
                _hasTrama = false;
                _tramaAtPrevBar = double.NaN;

                _nativeHighWin.Clear();
                _nativeLowWin.Clear();
                _primaryHighWin.Clear();
                _primaryLowWin.Clear();
                _bucketHighWin.Clear();
                _bucketLowWin.Clear();
                _bucketStart = DateTime.MinValue;

                _rejoinBullBorder = ApplyOpacity(RejoinBullColor, RejoinBorderOpacity);
                _rejoinBearBorder = ApplyOpacity(RejoinBearColor, RejoinBorderOpacity);

                Plots[PlotFast].Brush  = ApplyOpacity(FastEMAColor, EMALineOpacity);
                Plots[PlotFast].Width  = EMALineWidth;
                Plots[PlotSlow].Brush  = ApplyOpacity(SlowEMAColor, EMALineOpacity);
                Plots[PlotSlow].Width  = EMALineWidth;
                Plots[PlotTrama].Brush           = ApplyOpacity(TramaColor, TramaOpacity);
                Plots[PlotRejoin].Brush           = ApplyOpacity(RejoinEmaColor, RejoinEmaOpacity);
                Plots[PlotRejoin].Width           = RejoinEmaWidth;
                Plots[PlotRejoin].DashStyleHelper = RejoinEmaStyle;
                Plots[PlotTrama].Width           = TramaWidth;
                Plots[PlotTrama].DashStyleHelper = TramaStyle;
            }
            else if (State == State.Terminated)
            {
                DisposeFillResources();
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress == NativeBarsInProgressIndex)
            {
                if (!UseCurrentTimeframe && !UseClockAnchoredTimeframe)
                {
                    double hi, lo;
                    PushWindow(_nativeHighWin, _nativeLowWin, Highs[NativeBarsInProgressIndex][0], Lows[NativeBarsInProgressIndex][0], out hi, out lo);
                    _tramaValue = _calc.Update(hi, lo, Closes[NativeBarsInProgressIndex][0]);
                    _hasTrama   = true;
                }
                return;
            }

            if (BarsInProgress == SharedAnchorBarsInProgressIndex)
            {
                if (!UseCurrentTimeframe && UseClockAnchoredTimeframe)
                    UpdateAnchoredBucket();
                return;
            }

            // Primary series (BarsInProgress == 0)
            if (UseCurrentTimeframe)
            {
                double hi, lo;
                PushWindow(_primaryHighWin, _primaryLowWin, High[0], Low[0], out hi, out lo);
                _tramaValue = _calc.Update(hi, lo, Close[0]);
                _hasTrama   = true;
            }

            // TRAMA value on this bar and the previous one (for interpolating it at the crossover point)
            double tramaNow  = _tramaValue;
            double tramaPrev = double.IsNaN(_tramaAtPrevBar) ? tramaNow : _tramaAtPrevBar;
            _tramaAtPrevBar  = _hasTrama ? tramaNow : double.NaN;

            // Inline EMAs (seeded with the first input value, like NT8's EMA)
            if (CurrentBar == 0)
            {
                _fastEma[0] = Input[0];
                _slowEma[0] = Input[0];
                _rejoinEma[0] = Input[0];
            }
            else
            {
                _fastEma[0] = _fastEma[1] + (2.0 / (FastPeriod + 1)) * (Input[0] - _fastEma[1]);
                _slowEma[0] = _slowEma[1] + (2.0 / (SlowPeriod + 1)) * (Input[0] - _slowEma[1]);
                _rejoinEma[0] = _rejoinEma[1] + (2.0 / (RejoinEmaPeriod + 1)) * (Input[0] - _rejoinEma[1]);
            }

            // Plots (only when enabled; Reset leaves the value unplotted rather than 0)
            if (ShowCloud)
            {
                Values[PlotFast][0] = _fastEma[0];
                Values[PlotSlow][0] = _slowEma[0];
            }
            else
            {
                Values[PlotFast].Reset();
                Values[PlotSlow].Reset();
            }

            if (ShowRejoinEma)
                Values[PlotRejoin][0] = _rejoinEma[0];
            else
                Values[PlotRejoin].Reset();

            if (ShowTrama && _hasTrama)
                Values[PlotTrama][0] = _tramaValue;
            else
                Values[PlotTrama].Reset();

            if (CurrentBar <= Math.Max(FastPeriod, SlowPeriod)) return;
            if (!_hasTrama) return;

            double fastNow  = _fastEma[0];
            double slowNow  = _slowEma[0];
            double fastPrev = _fastEma[1];
            double slowPrev = _slowEma[1];

            bool fastAboveNow  = fastNow  > slowNow;
            bool fastAbovePrev = fastPrev > slowPrev;
            if (fastAboveNow == fastAbovePrev)
            {
                CheckRejoin(fastNow, slowNow);
                return;
            }
            _trendDir = 0;   // the cloud flipped: no active trend until a new primary entry fires
            if (DebugLog) Print(string.Format("[EGOptimalEntry] {0} cloud crossed -> trend reset (no active entry)", Time[0]));

            // Price level where the two EMAs cross, interpolated between the previous and current bar.
            double d0    = fastNow  - slowNow;
            double d1    = fastPrev - slowPrev;
            double denom = d1 - d0;
            double t     = Math.Abs(denom) > double.Epsilon ? d1 / denom : 0.0;
            if (t < 0.0) t = 0.0;
            else if (t > 1.0) t = 1.0;
            double crossPrice = fastPrev + t * (fastNow - fastPrev);

            // Compare the TRAMA at the same instant as the crossover (interpolated between bars, like crossPrice)
            double tramaAtCross = tramaPrev + t * (tramaNow - tramaPrev);
            double maxDistance = NearTicks * TickSize;
            double distance = Math.Abs(tramaAtCross - crossPrice);
            bool distanceOk = distance <= maxDistance;

            if (DebugLog)
                Print(string.Format("[EGOptimalEntry] {0} {1} cross | TRAMA dist {2:F1} ticks (limit {3:F1}) {4}",
                    Time[0], fastAboveNow ? "BULL" : "BEAR",
                    distance / TickSize, maxDistance / TickSize, distanceOk ? "ok" : "REJECTED"));

            if (!distanceOk) return;


            _trendDir = fastAboveNow ? 1 : -1;
            if (DebugLog) Print(string.Format("[EGOptimalEntry] {0} ENTRY {1} -> trend active", Time[0], fastAboveNow ? "BULL" : "BEAR"));
            _checkpointPrice = crossPrice;   // the entry is the first checkpoint

            string tag = "EGOptimalEntry_" + CurrentBar;

            // Chain suppression: a new signal within ReplaceWithinBars of the previous one replaces it
            if (ReplaceWithinBars > 0 && _lastSignalTag != null && CurrentBar - _lastSignalBar <= ReplaceWithinBars)
            {
                foreach (string rejoinTag in _rejoinTags)
                    RemoveDrawObject(rejoinTag);
                lock (_markers) _markers.RemoveAll(m => m.Tag == _lastSignalTag);
                lock (_rejoinStars) _rejoinStars.RemoveAll(st => _rejoinTags.Contains(st.Tag));
            }
            _rejoinTags.Clear();
            _lastSignalBar = CurrentBar;
            _lastSignalTag = tag;

            // High/low level: scanning back from the crossover candle (most recent first) over LevelLookback bars,
            // the first candle whose high (bear) / low (bull) is beyond the crossover price sets the level.
            double level    = double.NaN;
            int    levelBar = -1;
            bool   exceeded = false;
            if (ShowLevelLine)
            {
                int bars = Math.Min(LevelLookback, CurrentBar + 1);
                for (int i = 0; i < bars; i++)
                {
                    if (fastAboveNow ? Low[i] < crossPrice : High[i] > crossPrice)
                    {
                        level = fastAboveNow ? Low[i] : High[i];
                        levelBar = CurrentBar - i;
                        break;
                    }
                }
                // Level further from the crossover than the max distance: the high/low line uses the exceeded color
                exceeded = !double.IsNaN(level) && Math.Abs(level - crossPrice) > LevelMaxTicks * TickSize;
            }


            // The entry bar, the B/S circle and the high/low circle are all drawn in OnRender from this record
            lock (_markers)
                _markers.Add(new EntryMarker { Bar = CurrentBar, Bull = fastAboveNow, Anchor = crossPrice, Level = level, LevelBar = levelBar, Exceeded = exceeded, Tag = tag });
        }

        // Follow-up entry areas: during an active trend, a candle that closes outside the cloud on the
        // opposite side AND beyond the rejoin EMA (bear trend: above the cloud and below the EMA; bull trend:
        // below the cloud and above the EMA) gets a rectangle from its body extreme to its wick extreme.
        // Candles that traverse the whole cloud (high above the top and low below the bottom) are excluded, and the
        // whole candle must stay on the trend side of the rejoin EMA (bear: high <= EMA; bull: low >= EMA).
        private void CheckRejoin(double fastNow, double slowNow)
        {
            if (DebugLog)
                Print(string.Format("[EGOptimalEntry] {0} bar | trend {1} | open {2} high {3} low {4} close {5} | cloud {6:F2}-{7:F2}",
                    Time[0], _trendDir == 0 ? "none" : (_trendDir < 0 ? "BEAR" : "BULL"),
                    Open[0], High[0], Low[0], Close[0], Math.Min(fastNow, slowNow), Math.Max(fastNow, slowNow)));

            if (!ShowRejoinArea || _trendDir == 0) return;

            double cloudTop = Math.Max(fastNow, slowNow);
            double cloudBot = Math.Min(fastNow, slowNow);
            double ema      = _rejoinEma[0];
            double bodyLow  = Math.Min(Open[0], Close[0]);
            double bodyHigh = Math.Max(Open[0], Close[0]);
            bool   bear     = _trendDir < 0;

            // Outside the cloud against the trend (bear trend: closes above the cloud; bull trend: below it)
            bool outsideCloud = bear ? Close[0] > cloudTop : Close[0] < cloudBot;
            if (!outsideCloud) return;

            // Max areas reached
            bool maxReached = MaxRejoinAreas > 0 && _rejoinTags.Count >= MaxRejoinAreas;
            // A candle whose full range (high to low) spans the entire cloud is not a valid rejoin area
            bool traverses = High[0] >= cloudTop && Low[0] <= cloudBot;
            // Rejoin EMA rule. HighLow: the whole candle (wicks included) must stay on the trend side of the EMA.
            // Close: only the close has to be on the trend side, wicks may pierce the EMA.
            bool emaOk = RejoinMethod == EGOptimalEntryRejoinMethod.Close
                ? (bear ? Close[0] < ema : Close[0] > ema)
                : (bear ? High[0] <= ema : Low[0] >= ema);
            // Checkpoints: price must have progressed RejoinCheckpointPoints in the trend direction from the
            // previous checkpoint (the entry first, then each rejoin area drawn). 0 = off.
            bool checkpointOk = RejoinCheckpointPoints <= 0
                || (bear ? Close[0] <= _checkpointPrice - RejoinCheckpointPoints
                         : Close[0] >= _checkpointPrice + RejoinCheckpointPoints);

            bool valid = !maxReached && !traverses && emaOk && checkpointOk;

            if (DebugLog)
                Print(string.Format("[EGOptimalEntry] {0} {1} rejoin candidate | close {2} cloud {3}-{4} EMA {5:F2} (high {6}, low {7}) | max {8} | traverses cloud {9} | EMA {10} | checkpoint {11} (at {12}) -> {13}",
                    Time[0], bear ? "BEAR" : "BULL", Close[0], cloudBot, cloudTop, ema, High[0], Low[0],
                    maxReached ? "REACHED" : "ok", traverses ? "YES" : "no", emaOk ? "ok" : "CROSSED",
                    checkpointOk ? "ok" : "TOO CLOSE", _checkpointPrice, valid ? "DRAWN" : "rejected"));

            if (!valid) return;

            string rejoinTag = "EGOptimalEntry_Rejoin_" + CurrentBar;
            _checkpointPrice = Close[0];
            _rejoinTags.Add(rejoinTag);
            lock (_rejoinStars)
                _rejoinStars.Add(new RejoinStar { Bar = CurrentBar, Bull = !bear, Price = bear ? High[0] : Low[0], Tag = rejoinTag });
            var rect = bear
                ? Draw.Rectangle(this, rejoinTag, false, 0, High[0], -RejoinWidthBars, bodyLow,
                    _rejoinBearBorder, RejoinBearColor, RejoinOpacity)
                : Draw.Rectangle(this, rejoinTag, false, 0, bodyHigh, -RejoinWidthBars, Low[0],
                    _rejoinBullBorder, RejoinBullColor, RejoinOpacity);
            if (rect != null) rect.OutlineStroke.Width = RejoinBorderWidth;
        }

        private static System.Windows.Media.Brush ApplyOpacity(System.Windows.Media.Brush brush, double opacityPercent)
        {
            var solid = brush as System.Windows.Media.SolidColorBrush;
            if (solid == null) return brush;

            byte alpha = (byte)Math.Round(Math.Max(0, Math.Min(100, opacityPercent)) / 100.0 * 255);
            var result = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(alpha, solid.Color.R, solid.Color.G, solid.Color.B));
            result.Freeze();
            return result;
        }

        // Appends a high/low to a rolling window of TramaLength entries and returns the window's extremes.
        private void PushWindow(Queue<double> highWin, Queue<double> lowWin, double high, double low,
            out double highest, out double lowest)
        {
            highWin.Enqueue(high);
            if (highWin.Count > TramaLength) highWin.Dequeue();
            lowWin.Enqueue(low);
            if (lowWin.Count > TramaLength) lowWin.Dequeue();

            highest = double.MinValue;
            foreach (double h in highWin) if (h > highest) highest = h;
            lowest = double.MaxValue;
            foreach (double l in lowWin) if (l < lowest) lowest = l;
        }

        // Midnight-anchored TimeframeMinutes-wide bucket built from the shared 1-minute feed.
        private void UpdateAnchoredBucket()
        {
            DateTime barTime  = Times[SharedAnchorBarsInProgressIndex][0];
            double   barHigh  = Highs[SharedAnchorBarsInProgressIndex][0];
            double   barLow   = Lows[SharedAnchorBarsInProgressIndex][0];
            double   barClose = Closes[SharedAnchorBarsInProgressIndex][0];

            int bucketWidth = Math.Max(1, TimeframeMinutes);
            DateTime midnight = barTime.Date;
            int minutesSinceMidnight = (int)(barTime - midnight).TotalMinutes;
            int bucketStartMinutes = (minutesSinceMidnight / bucketWidth) * bucketWidth;
            DateTime bucketStart = midnight.AddMinutes(bucketStartMinutes);

            if (_bucketStart == DateTime.MinValue)
            {
                _bucketStart = bucketStart;
                _bucketHigh  = barHigh;
                _bucketLow   = barLow;
                _bucketClose = barClose;
                return;
            }

            if (bucketStart != _bucketStart)
            {
                double hi, lo;
                PushWindow(_bucketHighWin, _bucketLowWin, _bucketHigh, _bucketLow, out hi, out lo);
                _tramaValue = _calc.Update(hi, lo, _bucketClose);
                _hasTrama   = true;

                _bucketStart = bucketStart;
                _bucketHigh  = barHigh;
                _bucketLow   = barLow;
                _bucketClose = barClose;
            }
            else
            {
                _bucketHigh  = Math.Max(_bucketHigh, barHigh);
                _bucketLow   = Math.Min(_bucketLow, barLow);
                _bucketClose = barClose;
            }
        }

        // ---- Cloud fill rendering (same approach as EMACloud) ----
        public override void OnRenderTargetChanged()
        {
            DisposeFillResources();
            if (RenderTarget == null || RenderTarget.IsDisposed) return;

            float alpha = (float)Math.Max(0.0, Math.Min(100.0, FillOpacity)) / 100f;
            _bullBrush  = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, ToColor4(BullFillColor, alpha));
            _bearBrush  = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, ToColor4(BearFillColor, alpha));
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);
            DrawCloud(chartControl, chartScale);
            DrawEntryMarkers(chartControl, chartScale);
            DrawRejoinStars(chartControl, chartScale);
        }

        // Circle with B (buy) / S (sell), centered over the right half of the entry bar, sitting beyond the
        // outermost line drawn for the entry (below for buys, above for sells). X comes from the signal's bar
        // index and Y from its own price, so there is no second bar-index concept to disagree with.
        private void DrawEntryMarkers(ChartControl chartControl, ChartScale chartScale)
        {
            if (ChartBars == null || RenderTarget == null || RenderTarget.IsDisposed) return;

            EntryMarker[] snapshot;
            lock (_markers) snapshot = _markers.ToArray();
            if (snapshot.Length == 0) return;

            int   firstBar = ChartBars.FromIndex;
            int   lastBar  = ChartBars.ToIndex;
            float radius   = MarkerDiameter / 2f;
            float barDist  = (float)chartControl.Properties.BarDistance;

            // Smooth circle edges and text: turn anti-aliasing on for this pass, restore it afterwards
            SharpDX.Direct2D1.AntialiasMode      prevAa     = RenderTarget.AntialiasMode;
            SharpDX.Direct2D1.TextAntialiasMode  prevTextAa = RenderTarget.TextAntialiasMode;
            RenderTarget.AntialiasMode     = SharpDX.Direct2D1.AntialiasMode.PerPrimitive;
            RenderTarget.TextAntialiasMode = SharpDX.Direct2D1.TextAntialiasMode.Grayscale;

            try
            {
            using (var tf = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, MarkerFontFamily,
                SharpDX.DirectWrite.FontWeight.Bold, SharpDX.DirectWrite.FontStyle.Normal, MarkerFontSize))
            using (var textBrsh = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, ToColor4(MarkerTextColor, EntryLineOpacity / 100f)))
            using (var bullFill = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, ToColor4(BullLineColor, EntryLineOpacity / 100f)))
            using (var bearFill = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, ToColor4(BearLineColor, EntryLineOpacity / 100f)))
            using (var levelBull = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, ToColor4(BullLineColor, EntryLineOpacity / 100f)))
            using (var levelBear = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, ToColor4(BearLineColor, EntryLineOpacity / 100f)))
            using (var barBull   = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, ToColor4(BullLineColor, EntryLineOpacity / 100f)))
            using (var barBear   = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, ToColor4(BearLineColor, EntryLineOpacity / 100f)))
            using (var barFar    = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, ToColor4(LevelFarColor, EntryLineOpacity / 100f)))
            using (var barBorder = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, ToColor4(EntryBorderColor, EntryLineOpacity / 100f)))
            {
                tf.TextAlignment      = SharpDX.DirectWrite.TextAlignment.Center;
                tf.ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center;

                foreach (EntryMarker m in snapshot)
                {
                    if (m.Bar > lastBar || m.Bar + RectWidthBars < firstBar) continue;

                    float half  = RectWidthBars * barDist / 2f;                                   // half the entry bar's length
                    float barCx = (float)chartControl.GetXByBarIndex(ChartBars, m.Bar) + half;   // entry bar center
                    float cx    = barCx + half / 2f;                                              // B/S circle: center of the bar's right half
                    float y     = (float)chartScale.GetYByValue(m.Anchor);
                    float cy    = m.Bull ? y + MarkerGap + radius : y - MarkerGap - radius;

                    // Entry bar: a filled bar centered on the crossover price, fully enclosed by its border
                    float thick = EntryLineWidth;
                    var   bar   = new SharpDX.RectangleF(barCx - half, y - thick / 2f, half * 2f, thick);
                    RenderTarget.FillRectangle(bar, m.Bull ? barBull : barBear);
                    if (EntryBorderWidth > 0)
                        RenderTarget.DrawRectangle(bar, barBorder, EntryBorderWidth);   // shared border (bar + both circles)

                    if (ShowEntryMarker)
                    {
                        var circle = new SharpDX.Direct2D1.Ellipse(new SharpDX.Vector2(cx, cy), radius, radius);
                        RenderTarget.FillEllipse(circle, m.Bull ? bullFill : bearFill);
                        if (EntryBorderWidth > 0)
                            RenderTarget.DrawEllipse(circle, barBorder, EntryBorderWidth);
                        RenderTarget.DrawText(m.Bull ? "B" : "S", tf,
                            new SharpDX.RectangleF(cx - radius, cy - radius, MarkerDiameter, MarkerDiameter), textBrsh);
                    }

                    // High/low bar: same look as the entry bar with its own length, starting at the right edge of the
                    // candle that sets the level, at that candle's high (sell) / low (buy)
                    if (ShowLevelLine && !double.IsNaN(m.Level) && m.LevelBar >= firstBar && m.LevelBar <= lastBar)
                    {
                        float lx    = (float)chartControl.GetXByBarIndex(ChartBars, m.LevelBar) + barDist / 2f;
                        float ly    = (float)chartScale.GetYByValue(m.Level);
                        float llen  = LevelLengthBars * barDist;
                        var   lbar  = new SharpDX.RectangleF(lx, ly - LevelLineThickness / 2f, llen, LevelLineThickness);
                        RenderTarget.FillRectangle(lbar, m.Exceeded ? barFar : (m.Bull ? levelBull : levelBear));
                        if (EntryBorderWidth > 0)
                            RenderTarget.DrawRectangle(lbar, barBorder, EntryBorderWidth);
                    }
                }
            }
            }
            finally
            {
                RenderTarget.AntialiasMode     = prevAa;
                RenderTarget.TextAntialiasMode = prevTextAa;
            }
        }

        // Star on the left corner of each rejoin rectangle: top-left for bearish rejoins, bottom-left for bullish
        // ones, in the rejoin bull / bear color. X comes from the candle's bar index and Y from the corner's own
        // price, like the entry markers.
        private void DrawRejoinStars(ChartControl chartControl, ChartScale chartScale)
        {
            if (!ShowRejoinStar || !ShowRejoinArea || ChartBars == null || RenderTarget == null || RenderTarget.IsDisposed) return;

            RejoinStar[] snapshot;
            lock (_rejoinStars) snapshot = _rejoinStars.ToArray();
            if (snapshot.Length == 0) return;

            int   firstBar = ChartBars.FromIndex;
            int   lastBar  = ChartBars.ToIndex;
            float outerR   = RejoinStarSize / 2f;
            float innerR   = outerR * 0.4f;

            SharpDX.Direct2D1.AntialiasMode prevAa = RenderTarget.AntialiasMode;
            RenderTarget.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.PerPrimitive;
            try
            {
                using (var factory   = RenderTarget.Factory)
                using (var bullBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, ToColor4(RejoinBullColor, RejoinStarOpacity / 100f)))
                using (var bearBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, ToColor4(RejoinBearColor, RejoinStarOpacity / 100f)))
                {
                    foreach (RejoinStar st in snapshot)
                    {
                        if (st.Bar > lastBar || st.Bar + RejoinWidthBars < firstBar) continue;

                        float cx = (float)chartControl.GetXByBarIndex(ChartBars, st.Bar);
                        float cy = (float)chartScale.GetYByValue(st.Price);

                        // Diagonal offset away from the rectangle corner: left, and up for bearish / down for bullish
                        cx -= RejoinStarOffset;
                        cy += st.Bull ? RejoinStarOffset : -RejoinStarOffset;

                        using (var geo = new SharpDX.Direct2D1.PathGeometry(factory))
                        {
                            using (var sink = geo.Open())
                            {
                                // 5-point star: alternate outer / inner points, starting pointing straight up
                                for (int k = 0; k < 10; k++)
                                {
                                    double ang = -Math.PI / 2.0 + k * Math.PI / 5.0;
                                    float  r   = (k % 2 == 0) ? outerR : innerR;
                                    var    pt  = new SharpDX.Vector2(cx + r * (float)Math.Cos(ang), cy + r * (float)Math.Sin(ang));
                                    if (k == 0) sink.BeginFigure(pt, SharpDX.Direct2D1.FigureBegin.Filled);
                                    else        sink.AddLine(pt);
                                }
                                sink.EndFigure(SharpDX.Direct2D1.FigureEnd.Closed);
                                sink.Close();
                            }
                            RenderTarget.FillGeometry(geo, st.Bull ? bullBrush : bearBrush);
                        }
                    }
                }
            }
            finally
            {
                RenderTarget.AntialiasMode = prevAa;
            }
        }

        private void DrawCloud(ChartControl chartControl, ChartScale chartScale)
        {
            if (!ShowCloud) return;
            var rt = RenderTarget;
            if (rt == null || rt.IsDisposed)              return;
            if (_bullBrush == null || _bearBrush == null) return;
            if (_fastEma == null || _slowEma == null)     return;

            int firstBar = ChartBars.FromIndex;
            int lastBar  = Math.Min(ChartBars.ToIndex, BarsArray[0].Count - 1);
            if (lastBar - firstBar < 1) return;

            var pts = new List<(float x, float yFast, float ySlow)>(lastBar - firstBar + 1);
            for (int bar = firstBar; bar <= lastBar; bar++)
            {
                if (!_fastEma.IsValidDataPointAt(bar) || !_slowEma.IsValidDataPointAt(bar)) continue;
                double fast = _fastEma.GetValueAt(bar);
                double slow = _slowEma.GetValueAt(bar);
                if (double.IsNaN(fast) || double.IsNaN(slow)) continue;

                pts.Add((
                    (float)chartControl.GetXByBarIndex(ChartBars, bar),
                    (float)chartScale.GetYByValue(fast),
                    (float)chartScale.GetYByValue(slow)
                ));
            }

            if (pts.Count < 2) return;

            using (var factory = rt.Factory)
            using (var bullGeo = new SharpDX.Direct2D1.PathGeometry(factory))
            using (var bearGeo = new SharpDX.Direct2D1.PathGeometry(factory))
            {
                using (var bullSink = bullGeo.Open())
                using (var bearSink = bearGeo.Open())
                {
                    for (int i = 0; i < pts.Count - 1; i++)
                    {
                        var   a     = pts[i];
                        var   b     = pts[i + 1];
                        float da    = a.yFast - a.ySlow;
                        float db    = b.yFast - b.ySlow;
                        bool  aBull = da <= 0;   // fast above slow in pixel space (lower Y = higher price)
                        bool  bBull = db <= 0;

                        if (aBull == bBull)
                        {
                            var sink = aBull ? bullSink : bearSink;
                            sink.BeginFigure(new SharpDX.Vector2(a.x, a.yFast), SharpDX.Direct2D1.FigureBegin.Filled);
                            sink.AddLine(new SharpDX.Vector2(b.x, b.yFast));
                            sink.AddLine(new SharpDX.Vector2(b.x, b.ySlow));
                            sink.AddLine(new SharpDX.Vector2(a.x, a.ySlow));
                            sink.EndFigure(SharpDX.Direct2D1.FigureEnd.Closed);
                        }
                        else
                        {
                            // Lines cross — interpolate the crossing point and split into two triangles
                            float t  = da / (da - db);
                            float xC = a.x     + t * (b.x     - a.x);
                            float yC = a.yFast + t * (b.yFast - a.yFast);

                            var sinkA = aBull ? bullSink : bearSink;
                            sinkA.BeginFigure(new SharpDX.Vector2(a.x, a.yFast), SharpDX.Direct2D1.FigureBegin.Filled);
                            sinkA.AddLine(new SharpDX.Vector2(xC, yC));
                            sinkA.AddLine(new SharpDX.Vector2(a.x, a.ySlow));
                            sinkA.EndFigure(SharpDX.Direct2D1.FigureEnd.Closed);

                            var sinkB = bBull ? bullSink : bearSink;
                            sinkB.BeginFigure(new SharpDX.Vector2(xC, yC), SharpDX.Direct2D1.FigureBegin.Filled);
                            sinkB.AddLine(new SharpDX.Vector2(b.x, b.yFast));
                            sinkB.AddLine(new SharpDX.Vector2(b.x, b.ySlow));
                            sinkB.EndFigure(SharpDX.Direct2D1.FigureEnd.Closed);
                        }
                    }

                    bullSink.Close();
                    bearSink.Close();
                }

                rt.FillGeometry(bullGeo, _bullBrush);
                rt.FillGeometry(bearGeo, _bearBrush);
            }
        }

        private void DisposeFillResources()
        {
            if (_bullBrush != null) { _bullBrush.Dispose(); _bullBrush = null; }
            if (_bearBrush != null) { _bearBrush.Dispose(); _bearBrush = null; }
        }

        private static SharpDX.Color4 ToColor4(System.Windows.Media.Brush brush, float alpha)
        {
            var sb = brush as System.Windows.Media.SolidColorBrush;
            if (sb == null) return new SharpDX.Color4(1f, 1f, 1f, alpha);
            var c = sb.Color;
            return new SharpDX.Color4(c.R / 255f, c.G / 255f, c.B / 255f, alpha);
        }

        #region Properties

        // ---- EMA Cloud ----
        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Fast Period", GroupName = "EMA Cloud", Order = 1)]
        public int FastPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Slow Period", GroupName = "EMA Cloud", Order = 2)]
        public int SlowPeriod { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Cloud", Description = "Shows the fast/slow EMA lines and the fill between them. Crossover detection runs either way.", GroupName = "EMA Cloud", Order = 3)]
        public bool ShowCloud { get; set; }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Fast EMA Color", GroupName = "EMA Cloud", Order = 4)]
        public System.Windows.Media.Brush FastEMAColor { get; set; }

        [Browsable(false)]
        public string FastEMAColorSerializable
        {
            get { return Serialize.BrushToString(FastEMAColor); }
            set { FastEMAColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Slow EMA Color", GroupName = "EMA Cloud", Order = 5)]
        public System.Windows.Media.Brush SlowEMAColor { get; set; }

        [Browsable(false)]
        public string SlowEMAColorSerializable
        {
            get { return Serialize.BrushToString(SlowEMAColor); }
            set { SlowEMAColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "EMA Line Width", GroupName = "EMA Cloud", Order = 6)]
        public int EMALineWidth { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "EMA Line Opacity (%)", GroupName = "EMA Cloud", Order = 10)]
        public int EMALineOpacity { get; set; }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Bull Fill (Fast > Slow)", GroupName = "EMA Cloud", Order = 7)]
        public System.Windows.Media.Brush BullFillColor { get; set; }

        [Browsable(false)]
        public string BullFillColorSerializable
        {
            get { return Serialize.BrushToString(BullFillColor); }
            set { BullFillColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Bear Fill (Fast < Slow)", GroupName = "EMA Cloud", Order = 8)]
        public System.Windows.Media.Brush BearFillColor { get; set; }

        [Browsable(false)]
        public string BearFillColorSerializable
        {
            get { return Serialize.BrushToString(BearFillColor); }
            set { BearFillColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Range(0.0, 100.0)]
        [Display(Name = "Fill Opacity (%)", GroupName = "EMA Cloud", Order = 9)]
        public double FillOpacity { get; set; }

        // ---- TRAMA Line ----
        [NinjaScriptProperty]
        [Range(2, int.MaxValue)]
        [Display(Name = "Length", GroupName = "TRAMA Line", Order = 1)]
        public int TramaLength { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use Current Timeframe", Description = "When checked, the TRAMA is computed directly on the chart's own bars, ignoring Timeframe (Minutes)", GroupName = "TRAMA Line", Order = 2)]
        public bool UseCurrentTimeframe { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Timeframe (Minutes)", Description = "Bucket width in minutes for the TRAMA's own hidden series. Ignored when Use Current Timeframe is checked.", GroupName = "TRAMA Line", Order = 3)]
        public int TimeframeMinutes { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use Clock-Anchored Timeframe", Description = "Builds the TRAMA from a midnight-anchored bucket instead of NT8's native aggregation", GroupName = "TRAMA Line", Order = 4)]
        public bool UseClockAnchoredTimeframe { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show TRAMA Line", Description = "Plots the TRAMA line. Crossover detection runs either way.", GroupName = "TRAMA Line", Order = 5)]
        public bool ShowTrama { get; set; }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Line Color", GroupName = "TRAMA Line", Order = 6)]
        public System.Windows.Media.Brush TramaColor { get; set; }

        [Browsable(false)]
        public string TramaColorSerializable
        {
            get { return Serialize.BrushToString(TramaColor); }
            set { TramaColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Line Width", GroupName = "TRAMA Line", Order = 7)]
        public int TramaWidth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Line Style", GroupName = "TRAMA Line", Order = 8)]
        public DashStyleHelper TramaStyle { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Line Opacity (%)", GroupName = "TRAMA Line", Order = 9)]
        public int TramaOpacity { get; set; }

        // ---- Detection ----
        [NinjaScriptProperty]
        [Range(0, int.MaxValue)]
        [Display(Name = "Near Distance (Ticks)", Description = "Signal fires when the TRAMA is within this many ticks of the EMA crossover price", GroupName = "Entry Detection", Order = 1)]
        public int NearTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Debug Log", Description = "Prints every EMA crossover to the NinjaScript Output window with its TRAMA distance (ticks) and whether it was accepted or rejected. Use to tune the thresholds.", GroupName = "Entry Detection", Order = 3)]
        public bool DebugLog { get; set; }

        [NinjaScriptProperty]
        [Range(0, int.MaxValue)]
        [Display(Name = "Replace Older Within (Bars)", Description = "When a new signal appears within this many bars of the previous one, the previous rectangle is removed so only the most recent stays. Chains, so a run of signals each within this gap of the last leaves just the final one. 0 = off.", GroupName = "Entry Detection", Order = 2)]
        public int ReplaceWithinBars { get; set; }

        // ---- Entry Area ----
        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name = "Entry Bar Length (bars)", Description = "How many bars to the right the entry line extends from the crossover bar", GroupName = "Entry Area", Order = 1)]
        public int RectWidthBars { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Entry Area Opacity (%)", Description = "One opacity shared by everything in the entry area: the entry bar, the high/low line (including its exceeded color), the B/S marker (fill and letter) and the shared border. This includes the B/S letter.", GroupName = "Entry Area", Order = 19)]
        public int EntryLineOpacity { get; set; }

        [NinjaScriptProperty]
        [Range(0, 5)]
        [Display(Name = "Entry Border Width (px)", Description = "Border thickness in pixels, shared by the entry bar, the high/low line and the B/S circle; 0 = no border", GroupName = "Entry Area", Order = 17)]
        public int EntryBorderWidth { get; set; }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Entry Border Color", Description = "Shared by the entry bar, the high/low line and the B/S circle", GroupName = "Entry Area", Order = 18)]
        public System.Windows.Media.Brush EntryBorderColor { get; set; }

        [Browsable(false)]
        public string EntryBorderColorSerializable
        {
            get { return Serialize.BrushToString(EntryBorderColor); }
            set { EntryBorderColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Range(1, 30)]
        [Display(Name = "Entry Bar Thickness (px)", GroupName = "Entry Area", Order = 2)]
        public int EntryLineWidth { get; set; }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Entry Bar Bull Color", Description = "Fast EMA crossing above slow EMA", GroupName = "Entry Area", Order = 3)]
        public System.Windows.Media.Brush BullLineColor { get; set; }

        [Browsable(false)]
        public string BullLineColorSerializable
        {
            get { return Serialize.BrushToString(BullLineColor); }
            set { BullLineColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Entry Bar Bear Color", Description = "Fast EMA crossing below slow EMA", GroupName = "Entry Area", Order = 4)]
        public System.Windows.Media.Brush BearLineColor { get; set; }

        [Browsable(false)]
        public string BearLineColorSerializable
        {
            get { return Serialize.BrushToString(BearLineColor); }
            set { BearLineColor = Serialize.StringToBrush(value); }
        }


        // ---- Entry Area: High/Low Line ----
        [NinjaScriptProperty]
        [Display(Name = "Display High/Low Line", Description = "Draws a line at the high (sell) / low (buy) of the most recent candle beyond the crossover price within the lookback, starting at the right edge of that candle", GroupName = "Entry Area", Order = 5)]
        public bool ShowLevelLine { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "High/Low Line Lookback (bars)", Description = "How many bars back (including the crossover bar, most recent first) to search for the first candle whose high (bear) / low (bull) is beyond the rectangle edge", GroupName = "Entry Area", Order = 7)]
        public int LevelLookback { get; set; }

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name = "High/Low Line Length (bars)", Description = "How many bars to the right the high/low line extends from the candle it marks", GroupName = "Entry Area", Order = 6)]
        public int LevelLengthBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 30)]
        [Display(Name = "High/Low Line Thickness (px)", GroupName = "Entry Area", Order = 9)]
        public int LevelLineThickness { get; set; }

        [NinjaScriptProperty]
        [Range(0, int.MaxValue)]
        [Display(Name = "High/Low Line Max Distance (ticks)", Description = "If the high/low level is further than this from the crossover price, the high/low line is drawn in the High/Low Line Exceeded Color", GroupName = "Entry Area", Order = 8)]
        public int LevelMaxTicks { get; set; }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "High/Low Line Exceeded Color", Description = "Color of the high/low line when its level is further than High/Low Line Max Distance from the crossover price", GroupName = "Entry Area", Order = 10)]
        public System.Windows.Media.Brush LevelFarColor { get; set; }

        [Browsable(false)]
        public string LevelFarColorSerializable
        {
            get { return Serialize.BrushToString(LevelFarColor); }
            set { LevelFarColor = Serialize.StringToBrush(value); }
        }

        // ---- Entry Marker ----
        [NinjaScriptProperty]
        [Display(Name = "Display B/S Marker", Description = "Draws a circle with B (buy) or S (sell) over each entry, beyond its outermost line", GroupName = "Entry Area", Order = 11)]
        public bool ShowEntryMarker { get; set; }

        [NinjaScriptProperty]
        [Range(8, 80)]
        [Display(Name = "B/S Marker Diameter (px)", GroupName = "Entry Area", Order = 12)]
        public int MarkerDiameter { get; set; }

        [NinjaScriptProperty]
        [TypeConverter(typeof(EGOptimalEntryFontFamilyTypeConverter))]
        [Display(Name = "B/S Marker Font Family", Description = "Font used for the B / S letter (drawn bold)", GroupName = "Entry Area", Order = 14)]
        public string MarkerFontFamily { get; set; }

        [NinjaScriptProperty]
        [Range(6, 72)]
        [Display(Name = "B/S Marker Font Size", Description = "Size of the B / S letter in the marker circle", GroupName = "Entry Area", Order = 15)]
        public int MarkerFontSize { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "B/S Marker Gap (px)", Description = "Pixel gap between the outermost entry line and the circle", GroupName = "Entry Area", Order = 13)]
        public int MarkerGap { get; set; }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "B/S Marker Font Color", GroupName = "Entry Area", Order = 16)]
        public System.Windows.Media.Brush MarkerTextColor { get; set; }

        [Browsable(false)]
        public string MarkerTextColorSerializable
        {
            get { return Serialize.BrushToString(MarkerTextColor); }
            set { MarkerTextColor = Serialize.StringToBrush(value); }
        }

        // ---- Rejoin Area ----
        [NinjaScriptProperty]
        [Display(Name = "Display Rejoin Area", Description = "During an active trend (after a primary entry), marks candles that close outside the cloud against the trend and beyond the Rejoin EMA as potential follow-up entries", GroupName = "Rejoin Area", Order = 1)]
        public bool ShowRejoinArea { get; set; }

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name = "Rejoin Area Width (bars)", Description = "How many bars to the right the rejoin rectangle extends from the candle", GroupName = "Rejoin Area", Order = 2)]
        public int RejoinWidthBars { get; set; }

        [XmlIgnore]
        [Display(Name = "Rejoin Detection Method", Description = "High/Low: the whole candle (wicks included) must stay on the trend side of the Rejoin EMA. Close: only the close has to be, wicks may pierce the EMA.", GroupName = "Rejoin Detection", Order = 1)]
        public EGOptimalEntryRejoinMethod RejoinMethod { get; set; }

        [Browsable(false)]
        public string RejoinMethodSerializable
        {
            get { return RejoinMethod.ToString(); }
            set { RejoinMethod = (EGOptimalEntryRejoinMethod)Enum.Parse(typeof(EGOptimalEntryRejoinMethod), value); }
        }

        [NinjaScriptProperty]
        [Range(0, int.MaxValue)]
        [Display(Name = "Max Rejoin Areas", Description = "Maximum number of rejoin areas drawn per entry; once reached, further qualifying candles in that trend are ignored until the next entry. 0 = unlimited.", GroupName = "Rejoin Detection", Order = 4)]
        public int MaxRejoinAreas { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, double.MaxValue)]
        [Display(Name = "Rejoin Checkpoint Distance (Points)", Description = "Checkpoint spacing in price points. The entry is the first checkpoint; a rejoin area is only valid once the candle closes at least this far beyond the previous checkpoint in the trend direction, and then becomes the next checkpoint. 0 = off.", GroupName = "Rejoin Detection", Order = 3)]
        public double RejoinCheckpointPoints { get; set; }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Rejoin Area Bull Color", Description = "Rejoin rectangles after a bullish entry", GroupName = "Rejoin Area", Order = 3)]
        public System.Windows.Media.Brush RejoinBullColor { get; set; }

        [Browsable(false)]
        public string RejoinBullColorSerializable
        {
            get { return Serialize.BrushToString(RejoinBullColor); }
            set { RejoinBullColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Rejoin Area Bear Color", Description = "Rejoin rectangles after a bearish entry", GroupName = "Rejoin Area", Order = 4)]
        public System.Windows.Media.Brush RejoinBearColor { get; set; }

        [Browsable(false)]
        public string RejoinBearColorSerializable
        {
            get { return Serialize.BrushToString(RejoinBearColor); }
            set { RejoinBearColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Rejoin Area Opacity (%)", GroupName = "Rejoin Area", Order = 5)]
        public int RejoinOpacity { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Rejoin Area Border Width (px)", GroupName = "Rejoin Area", Order = 6)]
        public int RejoinBorderWidth { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Rejoin Area Border Opacity (%)", Description = "Opacity of the rejoin rectangle border (same color as the rejoin fill); 0 hides the border", GroupName = "Rejoin Area", Order = 7)]
        public int RejoinBorderOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Display Rejoin Star", Description = "Draws a star on the left corner of each rejoin rectangle: top-left for bearish, bottom-left for bullish, in the rejoin colors", GroupName = "Rejoin Area", Order = 8)]
        public bool ShowRejoinStar { get; set; }

        [NinjaScriptProperty]
        [Range(6, 60)]
        [Display(Name = "Rejoin Star Size (px)", GroupName = "Rejoin Area", Order = 9)]
        public int RejoinStarSize { get; set; }

        [NinjaScriptProperty]
        [Range(-60, 60)]
        [Display(Name = "Rejoin Star Diagonal Offset (px)", Description = "Moves the star diagonally away from the rectangle's left corner by this many pixels on both axes (left, and up for bearish / down for bullish). Negative values move it inward. 0 centers the star on the corner.", GroupName = "Rejoin Area", Order = 10)]
        public int RejoinStarOffset { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Rejoin Star Opacity (%)", GroupName = "Rejoin Area", Order = 11)]
        public int RejoinStarOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Display Rejoin EMA Line", GroupName = "Rejoin Area", Order = 12)]
        public bool ShowRejoinEma { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Rejoin EMA Period", GroupName = "Rejoin Detection", Order = 2)]
        public int RejoinEmaPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Rejoin EMA Line Width (px)", GroupName = "Rejoin Area", Order = 14)]
        public int RejoinEmaWidth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Rejoin EMA Line Style", GroupName = "Rejoin Area", Order = 15)]
        public DashStyleHelper RejoinEmaStyle { get; set; }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Rejoin EMA Line Color", GroupName = "Rejoin Area", Order = 13)]
        public System.Windows.Media.Brush RejoinEmaColor { get; set; }

        [Browsable(false)]
        public string RejoinEmaColorSerializable
        {
            get { return Serialize.BrushToString(RejoinEmaColor); }
            set { RejoinEmaColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Rejoin EMA Line Opacity (%)", GroupName = "Rejoin Area", Order = 16)]
        public int RejoinEmaOpacity { get; set; }

        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private EducatedGambling.EGOptimalEntry[] cacheEGOptimalEntry;
		public EducatedGambling.EGOptimalEntry EGOptimalEntry(int fastPeriod, int slowPeriod, bool showCloud, System.Windows.Media.Brush fastEMAColor, System.Windows.Media.Brush slowEMAColor, int eMALineWidth, int eMALineOpacity, System.Windows.Media.Brush bullFillColor, System.Windows.Media.Brush bearFillColor, double fillOpacity, int tramaLength, bool useCurrentTimeframe, int timeframeMinutes, bool useClockAnchoredTimeframe, bool showTrama, System.Windows.Media.Brush tramaColor, int tramaWidth, DashStyleHelper tramaStyle, int tramaOpacity, int nearTicks, bool debugLog, int replaceWithinBars, int rectWidthBars, int entryLineOpacity, int entryBorderWidth, System.Windows.Media.Brush entryBorderColor, int entryLineWidth, System.Windows.Media.Brush bullLineColor, System.Windows.Media.Brush bearLineColor, bool showLevelLine, int levelLookback, int levelLengthBars, int levelLineThickness, int levelMaxTicks, System.Windows.Media.Brush levelFarColor, bool showEntryMarker, int markerDiameter, string markerFontFamily, int markerFontSize, int markerGap, System.Windows.Media.Brush markerTextColor, bool showRejoinArea, int rejoinWidthBars, int maxRejoinAreas, double rejoinCheckpointPoints, System.Windows.Media.Brush rejoinBullColor, System.Windows.Media.Brush rejoinBearColor, int rejoinOpacity, int rejoinBorderWidth, int rejoinBorderOpacity, bool showRejoinStar, int rejoinStarSize, int rejoinStarOffset, int rejoinStarOpacity, bool showRejoinEma, int rejoinEmaPeriod, int rejoinEmaWidth, DashStyleHelper rejoinEmaStyle, System.Windows.Media.Brush rejoinEmaColor, int rejoinEmaOpacity)
		{
			return EGOptimalEntry(Input, fastPeriod, slowPeriod, showCloud, fastEMAColor, slowEMAColor, eMALineWidth, eMALineOpacity, bullFillColor, bearFillColor, fillOpacity, tramaLength, useCurrentTimeframe, timeframeMinutes, useClockAnchoredTimeframe, showTrama, tramaColor, tramaWidth, tramaStyle, tramaOpacity, nearTicks, debugLog, replaceWithinBars, rectWidthBars, entryLineOpacity, entryBorderWidth, entryBorderColor, entryLineWidth, bullLineColor, bearLineColor, showLevelLine, levelLookback, levelLengthBars, levelLineThickness, levelMaxTicks, levelFarColor, showEntryMarker, markerDiameter, markerFontFamily, markerFontSize, markerGap, markerTextColor, showRejoinArea, rejoinWidthBars, maxRejoinAreas, rejoinCheckpointPoints, rejoinBullColor, rejoinBearColor, rejoinOpacity, rejoinBorderWidth, rejoinBorderOpacity, showRejoinStar, rejoinStarSize, rejoinStarOffset, rejoinStarOpacity, showRejoinEma, rejoinEmaPeriod, rejoinEmaWidth, rejoinEmaStyle, rejoinEmaColor, rejoinEmaOpacity);
		}

		public EducatedGambling.EGOptimalEntry EGOptimalEntry(ISeries<double> input, int fastPeriod, int slowPeriod, bool showCloud, System.Windows.Media.Brush fastEMAColor, System.Windows.Media.Brush slowEMAColor, int eMALineWidth, int eMALineOpacity, System.Windows.Media.Brush bullFillColor, System.Windows.Media.Brush bearFillColor, double fillOpacity, int tramaLength, bool useCurrentTimeframe, int timeframeMinutes, bool useClockAnchoredTimeframe, bool showTrama, System.Windows.Media.Brush tramaColor, int tramaWidth, DashStyleHelper tramaStyle, int tramaOpacity, int nearTicks, bool debugLog, int replaceWithinBars, int rectWidthBars, int entryLineOpacity, int entryBorderWidth, System.Windows.Media.Brush entryBorderColor, int entryLineWidth, System.Windows.Media.Brush bullLineColor, System.Windows.Media.Brush bearLineColor, bool showLevelLine, int levelLookback, int levelLengthBars, int levelLineThickness, int levelMaxTicks, System.Windows.Media.Brush levelFarColor, bool showEntryMarker, int markerDiameter, string markerFontFamily, int markerFontSize, int markerGap, System.Windows.Media.Brush markerTextColor, bool showRejoinArea, int rejoinWidthBars, int maxRejoinAreas, double rejoinCheckpointPoints, System.Windows.Media.Brush rejoinBullColor, System.Windows.Media.Brush rejoinBearColor, int rejoinOpacity, int rejoinBorderWidth, int rejoinBorderOpacity, bool showRejoinStar, int rejoinStarSize, int rejoinStarOffset, int rejoinStarOpacity, bool showRejoinEma, int rejoinEmaPeriod, int rejoinEmaWidth, DashStyleHelper rejoinEmaStyle, System.Windows.Media.Brush rejoinEmaColor, int rejoinEmaOpacity)
		{
			if (cacheEGOptimalEntry != null)
				for (int idx = 0; idx < cacheEGOptimalEntry.Length; idx++)
					if (cacheEGOptimalEntry[idx] != null && cacheEGOptimalEntry[idx].FastPeriod == fastPeriod && cacheEGOptimalEntry[idx].SlowPeriod == slowPeriod && cacheEGOptimalEntry[idx].ShowCloud == showCloud && cacheEGOptimalEntry[idx].FastEMAColor == fastEMAColor && cacheEGOptimalEntry[idx].SlowEMAColor == slowEMAColor && cacheEGOptimalEntry[idx].EMALineWidth == eMALineWidth && cacheEGOptimalEntry[idx].EMALineOpacity == eMALineOpacity && cacheEGOptimalEntry[idx].BullFillColor == bullFillColor && cacheEGOptimalEntry[idx].BearFillColor == bearFillColor && cacheEGOptimalEntry[idx].FillOpacity == fillOpacity && cacheEGOptimalEntry[idx].TramaLength == tramaLength && cacheEGOptimalEntry[idx].UseCurrentTimeframe == useCurrentTimeframe && cacheEGOptimalEntry[idx].TimeframeMinutes == timeframeMinutes && cacheEGOptimalEntry[idx].UseClockAnchoredTimeframe == useClockAnchoredTimeframe && cacheEGOptimalEntry[idx].ShowTrama == showTrama && cacheEGOptimalEntry[idx].TramaColor == tramaColor && cacheEGOptimalEntry[idx].TramaWidth == tramaWidth && cacheEGOptimalEntry[idx].TramaStyle == tramaStyle && cacheEGOptimalEntry[idx].TramaOpacity == tramaOpacity && cacheEGOptimalEntry[idx].NearTicks == nearTicks && cacheEGOptimalEntry[idx].DebugLog == debugLog && cacheEGOptimalEntry[idx].ReplaceWithinBars == replaceWithinBars && cacheEGOptimalEntry[idx].RectWidthBars == rectWidthBars && cacheEGOptimalEntry[idx].EntryLineOpacity == entryLineOpacity && cacheEGOptimalEntry[idx].EntryBorderWidth == entryBorderWidth && cacheEGOptimalEntry[idx].EntryBorderColor == entryBorderColor && cacheEGOptimalEntry[idx].EntryLineWidth == entryLineWidth && cacheEGOptimalEntry[idx].BullLineColor == bullLineColor && cacheEGOptimalEntry[idx].BearLineColor == bearLineColor && cacheEGOptimalEntry[idx].ShowLevelLine == showLevelLine && cacheEGOptimalEntry[idx].LevelLookback == levelLookback && cacheEGOptimalEntry[idx].LevelLengthBars == levelLengthBars && cacheEGOptimalEntry[idx].LevelLineThickness == levelLineThickness && cacheEGOptimalEntry[idx].LevelMaxTicks == levelMaxTicks && cacheEGOptimalEntry[idx].LevelFarColor == levelFarColor && cacheEGOptimalEntry[idx].ShowEntryMarker == showEntryMarker && cacheEGOptimalEntry[idx].MarkerDiameter == markerDiameter && cacheEGOptimalEntry[idx].MarkerFontFamily == markerFontFamily && cacheEGOptimalEntry[idx].MarkerFontSize == markerFontSize && cacheEGOptimalEntry[idx].MarkerGap == markerGap && cacheEGOptimalEntry[idx].MarkerTextColor == markerTextColor && cacheEGOptimalEntry[idx].ShowRejoinArea == showRejoinArea && cacheEGOptimalEntry[idx].RejoinWidthBars == rejoinWidthBars && cacheEGOptimalEntry[idx].MaxRejoinAreas == maxRejoinAreas && cacheEGOptimalEntry[idx].RejoinCheckpointPoints == rejoinCheckpointPoints && cacheEGOptimalEntry[idx].RejoinBullColor == rejoinBullColor && cacheEGOptimalEntry[idx].RejoinBearColor == rejoinBearColor && cacheEGOptimalEntry[idx].RejoinOpacity == rejoinOpacity && cacheEGOptimalEntry[idx].RejoinBorderWidth == rejoinBorderWidth && cacheEGOptimalEntry[idx].RejoinBorderOpacity == rejoinBorderOpacity && cacheEGOptimalEntry[idx].ShowRejoinStar == showRejoinStar && cacheEGOptimalEntry[idx].RejoinStarSize == rejoinStarSize && cacheEGOptimalEntry[idx].RejoinStarOffset == rejoinStarOffset && cacheEGOptimalEntry[idx].RejoinStarOpacity == rejoinStarOpacity && cacheEGOptimalEntry[idx].ShowRejoinEma == showRejoinEma && cacheEGOptimalEntry[idx].RejoinEmaPeriod == rejoinEmaPeriod && cacheEGOptimalEntry[idx].RejoinEmaWidth == rejoinEmaWidth && cacheEGOptimalEntry[idx].RejoinEmaStyle == rejoinEmaStyle && cacheEGOptimalEntry[idx].RejoinEmaColor == rejoinEmaColor && cacheEGOptimalEntry[idx].RejoinEmaOpacity == rejoinEmaOpacity && cacheEGOptimalEntry[idx].EqualsInput(input))
						return cacheEGOptimalEntry[idx];
			return CacheIndicator<EducatedGambling.EGOptimalEntry>(new EducatedGambling.EGOptimalEntry(){ FastPeriod = fastPeriod, SlowPeriod = slowPeriod, ShowCloud = showCloud, FastEMAColor = fastEMAColor, SlowEMAColor = slowEMAColor, EMALineWidth = eMALineWidth, EMALineOpacity = eMALineOpacity, BullFillColor = bullFillColor, BearFillColor = bearFillColor, FillOpacity = fillOpacity, TramaLength = tramaLength, UseCurrentTimeframe = useCurrentTimeframe, TimeframeMinutes = timeframeMinutes, UseClockAnchoredTimeframe = useClockAnchoredTimeframe, ShowTrama = showTrama, TramaColor = tramaColor, TramaWidth = tramaWidth, TramaStyle = tramaStyle, TramaOpacity = tramaOpacity, NearTicks = nearTicks, DebugLog = debugLog, ReplaceWithinBars = replaceWithinBars, RectWidthBars = rectWidthBars, EntryLineOpacity = entryLineOpacity, EntryBorderWidth = entryBorderWidth, EntryBorderColor = entryBorderColor, EntryLineWidth = entryLineWidth, BullLineColor = bullLineColor, BearLineColor = bearLineColor, ShowLevelLine = showLevelLine, LevelLookback = levelLookback, LevelLengthBars = levelLengthBars, LevelLineThickness = levelLineThickness, LevelMaxTicks = levelMaxTicks, LevelFarColor = levelFarColor, ShowEntryMarker = showEntryMarker, MarkerDiameter = markerDiameter, MarkerFontFamily = markerFontFamily, MarkerFontSize = markerFontSize, MarkerGap = markerGap, MarkerTextColor = markerTextColor, ShowRejoinArea = showRejoinArea, RejoinWidthBars = rejoinWidthBars, MaxRejoinAreas = maxRejoinAreas, RejoinCheckpointPoints = rejoinCheckpointPoints, RejoinBullColor = rejoinBullColor, RejoinBearColor = rejoinBearColor, RejoinOpacity = rejoinOpacity, RejoinBorderWidth = rejoinBorderWidth, RejoinBorderOpacity = rejoinBorderOpacity, ShowRejoinStar = showRejoinStar, RejoinStarSize = rejoinStarSize, RejoinStarOffset = rejoinStarOffset, RejoinStarOpacity = rejoinStarOpacity, ShowRejoinEma = showRejoinEma, RejoinEmaPeriod = rejoinEmaPeriod, RejoinEmaWidth = rejoinEmaWidth, RejoinEmaStyle = rejoinEmaStyle, RejoinEmaColor = rejoinEmaColor, RejoinEmaOpacity = rejoinEmaOpacity }, input, ref cacheEGOptimalEntry);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.EducatedGambling.EGOptimalEntry EGOptimalEntry(int fastPeriod, int slowPeriod, bool showCloud, System.Windows.Media.Brush fastEMAColor, System.Windows.Media.Brush slowEMAColor, int eMALineWidth, int eMALineOpacity, System.Windows.Media.Brush bullFillColor, System.Windows.Media.Brush bearFillColor, double fillOpacity, int tramaLength, bool useCurrentTimeframe, int timeframeMinutes, bool useClockAnchoredTimeframe, bool showTrama, System.Windows.Media.Brush tramaColor, int tramaWidth, DashStyleHelper tramaStyle, int tramaOpacity, int nearTicks, bool debugLog, int replaceWithinBars, int rectWidthBars, int entryLineOpacity, int entryBorderWidth, System.Windows.Media.Brush entryBorderColor, int entryLineWidth, System.Windows.Media.Brush bullLineColor, System.Windows.Media.Brush bearLineColor, bool showLevelLine, int levelLookback, int levelLengthBars, int levelLineThickness, int levelMaxTicks, System.Windows.Media.Brush levelFarColor, bool showEntryMarker, int markerDiameter, string markerFontFamily, int markerFontSize, int markerGap, System.Windows.Media.Brush markerTextColor, bool showRejoinArea, int rejoinWidthBars, int maxRejoinAreas, double rejoinCheckpointPoints, System.Windows.Media.Brush rejoinBullColor, System.Windows.Media.Brush rejoinBearColor, int rejoinOpacity, int rejoinBorderWidth, int rejoinBorderOpacity, bool showRejoinStar, int rejoinStarSize, int rejoinStarOffset, int rejoinStarOpacity, bool showRejoinEma, int rejoinEmaPeriod, int rejoinEmaWidth, DashStyleHelper rejoinEmaStyle, System.Windows.Media.Brush rejoinEmaColor, int rejoinEmaOpacity)
		{
			return indicator.EGOptimalEntry(Input, fastPeriod, slowPeriod, showCloud, fastEMAColor, slowEMAColor, eMALineWidth, eMALineOpacity, bullFillColor, bearFillColor, fillOpacity, tramaLength, useCurrentTimeframe, timeframeMinutes, useClockAnchoredTimeframe, showTrama, tramaColor, tramaWidth, tramaStyle, tramaOpacity, nearTicks, debugLog, replaceWithinBars, rectWidthBars, entryLineOpacity, entryBorderWidth, entryBorderColor, entryLineWidth, bullLineColor, bearLineColor, showLevelLine, levelLookback, levelLengthBars, levelLineThickness, levelMaxTicks, levelFarColor, showEntryMarker, markerDiameter, markerFontFamily, markerFontSize, markerGap, markerTextColor, showRejoinArea, rejoinWidthBars, maxRejoinAreas, rejoinCheckpointPoints, rejoinBullColor, rejoinBearColor, rejoinOpacity, rejoinBorderWidth, rejoinBorderOpacity, showRejoinStar, rejoinStarSize, rejoinStarOffset, rejoinStarOpacity, showRejoinEma, rejoinEmaPeriod, rejoinEmaWidth, rejoinEmaStyle, rejoinEmaColor, rejoinEmaOpacity);
		}

		public Indicators.EducatedGambling.EGOptimalEntry EGOptimalEntry(ISeries<double> input , int fastPeriod, int slowPeriod, bool showCloud, System.Windows.Media.Brush fastEMAColor, System.Windows.Media.Brush slowEMAColor, int eMALineWidth, int eMALineOpacity, System.Windows.Media.Brush bullFillColor, System.Windows.Media.Brush bearFillColor, double fillOpacity, int tramaLength, bool useCurrentTimeframe, int timeframeMinutes, bool useClockAnchoredTimeframe, bool showTrama, System.Windows.Media.Brush tramaColor, int tramaWidth, DashStyleHelper tramaStyle, int tramaOpacity, int nearTicks, bool debugLog, int replaceWithinBars, int rectWidthBars, int entryLineOpacity, int entryBorderWidth, System.Windows.Media.Brush entryBorderColor, int entryLineWidth, System.Windows.Media.Brush bullLineColor, System.Windows.Media.Brush bearLineColor, bool showLevelLine, int levelLookback, int levelLengthBars, int levelLineThickness, int levelMaxTicks, System.Windows.Media.Brush levelFarColor, bool showEntryMarker, int markerDiameter, string markerFontFamily, int markerFontSize, int markerGap, System.Windows.Media.Brush markerTextColor, bool showRejoinArea, int rejoinWidthBars, int maxRejoinAreas, double rejoinCheckpointPoints, System.Windows.Media.Brush rejoinBullColor, System.Windows.Media.Brush rejoinBearColor, int rejoinOpacity, int rejoinBorderWidth, int rejoinBorderOpacity, bool showRejoinStar, int rejoinStarSize, int rejoinStarOffset, int rejoinStarOpacity, bool showRejoinEma, int rejoinEmaPeriod, int rejoinEmaWidth, DashStyleHelper rejoinEmaStyle, System.Windows.Media.Brush rejoinEmaColor, int rejoinEmaOpacity)
		{
			return indicator.EGOptimalEntry(input, fastPeriod, slowPeriod, showCloud, fastEMAColor, slowEMAColor, eMALineWidth, eMALineOpacity, bullFillColor, bearFillColor, fillOpacity, tramaLength, useCurrentTimeframe, timeframeMinutes, useClockAnchoredTimeframe, showTrama, tramaColor, tramaWidth, tramaStyle, tramaOpacity, nearTicks, debugLog, replaceWithinBars, rectWidthBars, entryLineOpacity, entryBorderWidth, entryBorderColor, entryLineWidth, bullLineColor, bearLineColor, showLevelLine, levelLookback, levelLengthBars, levelLineThickness, levelMaxTicks, levelFarColor, showEntryMarker, markerDiameter, markerFontFamily, markerFontSize, markerGap, markerTextColor, showRejoinArea, rejoinWidthBars, maxRejoinAreas, rejoinCheckpointPoints, rejoinBullColor, rejoinBearColor, rejoinOpacity, rejoinBorderWidth, rejoinBorderOpacity, showRejoinStar, rejoinStarSize, rejoinStarOffset, rejoinStarOpacity, showRejoinEma, rejoinEmaPeriod, rejoinEmaWidth, rejoinEmaStyle, rejoinEmaColor, rejoinEmaOpacity);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.EducatedGambling.EGOptimalEntry EGOptimalEntry(int fastPeriod, int slowPeriod, bool showCloud, System.Windows.Media.Brush fastEMAColor, System.Windows.Media.Brush slowEMAColor, int eMALineWidth, int eMALineOpacity, System.Windows.Media.Brush bullFillColor, System.Windows.Media.Brush bearFillColor, double fillOpacity, int tramaLength, bool useCurrentTimeframe, int timeframeMinutes, bool useClockAnchoredTimeframe, bool showTrama, System.Windows.Media.Brush tramaColor, int tramaWidth, DashStyleHelper tramaStyle, int tramaOpacity, int nearTicks, bool debugLog, int replaceWithinBars, int rectWidthBars, int entryLineOpacity, int entryBorderWidth, System.Windows.Media.Brush entryBorderColor, int entryLineWidth, System.Windows.Media.Brush bullLineColor, System.Windows.Media.Brush bearLineColor, bool showLevelLine, int levelLookback, int levelLengthBars, int levelLineThickness, int levelMaxTicks, System.Windows.Media.Brush levelFarColor, bool showEntryMarker, int markerDiameter, string markerFontFamily, int markerFontSize, int markerGap, System.Windows.Media.Brush markerTextColor, bool showRejoinArea, int rejoinWidthBars, int maxRejoinAreas, double rejoinCheckpointPoints, System.Windows.Media.Brush rejoinBullColor, System.Windows.Media.Brush rejoinBearColor, int rejoinOpacity, int rejoinBorderWidth, int rejoinBorderOpacity, bool showRejoinStar, int rejoinStarSize, int rejoinStarOffset, int rejoinStarOpacity, bool showRejoinEma, int rejoinEmaPeriod, int rejoinEmaWidth, DashStyleHelper rejoinEmaStyle, System.Windows.Media.Brush rejoinEmaColor, int rejoinEmaOpacity)
		{
			return indicator.EGOptimalEntry(Input, fastPeriod, slowPeriod, showCloud, fastEMAColor, slowEMAColor, eMALineWidth, eMALineOpacity, bullFillColor, bearFillColor, fillOpacity, tramaLength, useCurrentTimeframe, timeframeMinutes, useClockAnchoredTimeframe, showTrama, tramaColor, tramaWidth, tramaStyle, tramaOpacity, nearTicks, debugLog, replaceWithinBars, rectWidthBars, entryLineOpacity, entryBorderWidth, entryBorderColor, entryLineWidth, bullLineColor, bearLineColor, showLevelLine, levelLookback, levelLengthBars, levelLineThickness, levelMaxTicks, levelFarColor, showEntryMarker, markerDiameter, markerFontFamily, markerFontSize, markerGap, markerTextColor, showRejoinArea, rejoinWidthBars, maxRejoinAreas, rejoinCheckpointPoints, rejoinBullColor, rejoinBearColor, rejoinOpacity, rejoinBorderWidth, rejoinBorderOpacity, showRejoinStar, rejoinStarSize, rejoinStarOffset, rejoinStarOpacity, showRejoinEma, rejoinEmaPeriod, rejoinEmaWidth, rejoinEmaStyle, rejoinEmaColor, rejoinEmaOpacity);
		}

		public Indicators.EducatedGambling.EGOptimalEntry EGOptimalEntry(ISeries<double> input , int fastPeriod, int slowPeriod, bool showCloud, System.Windows.Media.Brush fastEMAColor, System.Windows.Media.Brush slowEMAColor, int eMALineWidth, int eMALineOpacity, System.Windows.Media.Brush bullFillColor, System.Windows.Media.Brush bearFillColor, double fillOpacity, int tramaLength, bool useCurrentTimeframe, int timeframeMinutes, bool useClockAnchoredTimeframe, bool showTrama, System.Windows.Media.Brush tramaColor, int tramaWidth, DashStyleHelper tramaStyle, int tramaOpacity, int nearTicks, bool debugLog, int replaceWithinBars, int rectWidthBars, int entryLineOpacity, int entryBorderWidth, System.Windows.Media.Brush entryBorderColor, int entryLineWidth, System.Windows.Media.Brush bullLineColor, System.Windows.Media.Brush bearLineColor, bool showLevelLine, int levelLookback, int levelLengthBars, int levelLineThickness, int levelMaxTicks, System.Windows.Media.Brush levelFarColor, bool showEntryMarker, int markerDiameter, string markerFontFamily, int markerFontSize, int markerGap, System.Windows.Media.Brush markerTextColor, bool showRejoinArea, int rejoinWidthBars, int maxRejoinAreas, double rejoinCheckpointPoints, System.Windows.Media.Brush rejoinBullColor, System.Windows.Media.Brush rejoinBearColor, int rejoinOpacity, int rejoinBorderWidth, int rejoinBorderOpacity, bool showRejoinStar, int rejoinStarSize, int rejoinStarOffset, int rejoinStarOpacity, bool showRejoinEma, int rejoinEmaPeriod, int rejoinEmaWidth, DashStyleHelper rejoinEmaStyle, System.Windows.Media.Brush rejoinEmaColor, int rejoinEmaOpacity)
		{
			return indicator.EGOptimalEntry(input, fastPeriod, slowPeriod, showCloud, fastEMAColor, slowEMAColor, eMALineWidth, eMALineOpacity, bullFillColor, bearFillColor, fillOpacity, tramaLength, useCurrentTimeframe, timeframeMinutes, useClockAnchoredTimeframe, showTrama, tramaColor, tramaWidth, tramaStyle, tramaOpacity, nearTicks, debugLog, replaceWithinBars, rectWidthBars, entryLineOpacity, entryBorderWidth, entryBorderColor, entryLineWidth, bullLineColor, bearLineColor, showLevelLine, levelLookback, levelLengthBars, levelLineThickness, levelMaxTicks, levelFarColor, showEntryMarker, markerDiameter, markerFontFamily, markerFontSize, markerGap, markerTextColor, showRejoinArea, rejoinWidthBars, maxRejoinAreas, rejoinCheckpointPoints, rejoinBullColor, rejoinBearColor, rejoinOpacity, rejoinBorderWidth, rejoinBorderOpacity, showRejoinStar, rejoinStarSize, rejoinStarOffset, rejoinStarOpacity, showRejoinEma, rejoinEmaPeriod, rejoinEmaWidth, rejoinEmaStyle, rejoinEmaColor, rejoinEmaOpacity);
		}
	}
}

#endregion
