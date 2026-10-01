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
    [CategoryOrder("EMA Cloud",     1)]
    [CategoryOrder("TRAMA Line",    2)]
    [CategoryOrder("Entry Area", 3)]
    [CategoryOrder("Rejoin Area", 4)]
    // Flags an EMA Cloud (fast/slow EMA) crossover when the TRAMA line sits within N ticks of the
    // point where the two EMAs cross, then draws a short horizontal line to the right of that bar.
    // Fully self-contained: the EMAs and the TRAMA (LuxAlgo Trend Regularity Adaptive Moving Average,
    // CC BY-NC-SA 4.0, same engine as TRAMATimeFramingLine) are computed inline, no other indicators.
    // The cloud (EMA lines + fill) and the TRAMA line can each be shown or hidden independently;
    // detection runs either way.
    public class EGOptimalEntry : Indicator
    {
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

        private System.Windows.Media.Brush _bullLineBrush;
        private System.Windows.Media.Brush _bearLineBrush;
        private System.Windows.Media.Brush _levelFarBrush;

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
                Description = "Draws a line to the right when the TRAMA is within N ticks of an EMA cloud (fast/slow EMA) crossover.";
                Calculate                = Calculate.OnBarClose;
                IsOverlay                = true;
                IsSuspendedWhileInactive = true;

                FastPeriod = 9;
                SlowPeriod = 20;

                ShowCloud     = true;
                FastEMAColor  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0xB7, 0x08, 0x59)); FastEMAColor.Freeze();
                SlowEMAColor  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0x0C, 0x7F, 0xB6)); SlowEMAColor.Freeze();
                EMALineWidth  = 2;
                EMALineOpacity = 100;
                BullFillColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0x0C, 0x7F, 0xB6)); BullFillColor.Freeze();
                BearFillColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0xB7, 0x08, 0x59)); BearFillColor.Freeze();
                FillOpacity   = 20.0;

                TramaLength               = 20;
                UseCurrentTimeframe       = false;
                TimeframeMinutes          = 240;
                UseClockAnchoredTimeframe = false;
                ShowTrama                 = true;
                TramaColor                = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0xFF, 0x11, 0x00)); TramaColor.Freeze();
                TramaWidth                = 2;
                TramaOpacity              = 100;
                TramaStyle                = DashStyleHelper.Solid;

                NearTicks        = 10;
                DebugLog         = false;
                ReplaceWithinBars = 0;

                RectWidthBars   = 5;
                RectHeightTicks = 10;
                RectFillOpacity = 20;
                BullLineColor  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0x0C, 0x7F, 0xB6)); BullLineColor.Freeze();
                BearLineColor  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0xB7, 0x08, 0x59)); BearLineColor.Freeze();
                LineOpacity    = 100;

                ShowRejoinArea   = true;
                RejoinWidthBars  = 5;
                MaxRejoinAreas   = 0;
                RejoinCheckpointPoints = 50;
                RejoinBullColor  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0x0C, 0x7F, 0xB6)); RejoinBullColor.Freeze();
                RejoinBearColor  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0xB7, 0x08, 0x59)); RejoinBearColor.Freeze();
                RejoinOpacity    = 20;
                ShowRejoinEma    = true;
                RejoinEmaPeriod  = 50;
                RejoinEmaWidth   = 1;
                RejoinEmaStyle   = DashStyleHelper.Solid;
                RejoinEmaColor   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0xDC, 0xDC, 0xDC)); RejoinEmaColor.Freeze();
                RejoinEmaOpacity = 100;

                ShowLevelLine = true;
                LevelLineWidth      = 2;
                LevelLookback       = 10;
                LevelMaxTicks       = 30;
                ExceededLineWidth   = 2;
                ExceededLineStyle   = DashStyleHelper.Solid;
                LevelFarColor       = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0xFF, 0xD7, 0x00)); LevelFarColor.Freeze();
                LevelLineStyle      = DashStyleHelper.Solid;

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

                _bullLineBrush = ApplyOpacity(BullLineColor, LineOpacity);
                _bearLineBrush = ApplyOpacity(BearLineColor, LineOpacity);
                _levelFarBrush = ApplyOpacity(LevelFarColor, LineOpacity);

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
            _checkpointPrice = crossPrice;   // the entry is the first checkpoint

            string tag = "EGOptimalEntry_" + CurrentBar;

            // Chain suppression: a new signal within ReplaceWithinBars of the previous one replaces it
            if (ReplaceWithinBars > 0 && _lastSignalTag != null && CurrentBar - _lastSignalBar <= ReplaceWithinBars)
            {
                RemoveDrawObject(_lastSignalTag + "_Rect");
                RemoveDrawObject(_lastSignalTag + "_LevelLine");
                RemoveDrawObject(_lastSignalTag + "_FlushLine");
                foreach (string rejoinTag in _rejoinTags)
                    RemoveDrawObject(rejoinTag);
            }
            _rejoinTags.Clear();
            _lastSignalBar = CurrentBar;
            _lastSignalTag = tag;

            System.Windows.Media.Brush lineBrush = fastAboveNow ? _bullLineBrush : _bearLineBrush;

            double halfHeight = RectHeightTicks * TickSize / 2.0;
            Draw.Rectangle(this, tag + "_Rect", false,
                0,               crossPrice + halfHeight,
                -RectWidthBars,  crossPrice - halfHeight,
                System.Windows.Media.Brushes.Transparent, fastAboveNow ? BullLineColor : BearLineColor, RectFillOpacity);

            // Level line: scanning back from the crossover candle (most recent first) over LevelLookback bars,
            // the first candle whose high (bear) / low (bull) is beyond the rectangle edge sets the level.
            // If none qualifies, or the level is more than LevelMaxTicks from the edge, an extra line is
            // also drawn flush against the rectangle in the exceeded style.
            if (ShowLevelLine)
            {
                double edge  = fastAboveNow ? crossPrice - halfHeight : crossPrice + halfHeight;
                double level = double.NaN;
                int    bars  = Math.Min(LevelLookback, CurrentBar + 1);
                for (int i = 0; i < bars; i++)
                {
                    if (fastAboveNow ? Low[i] < edge : High[i] > edge)
                    {
                        level = fastAboveNow ? Low[i] : High[i];
                        break;
                    }
                }

                bool found = !double.IsNaN(level);
                if (found)
                    Draw.Line(this, tag + "_LevelLine", false, 0, level, -RectWidthBars, level,
                        lineBrush, LevelLineStyle, LevelLineWidth);

                if (!found || Math.Abs(level - edge) > LevelMaxTicks * TickSize)
                    Draw.Line(this, tag + "_FlushLine", false, 0, edge, -RectWidthBars, edge,
                        _levelFarBrush, ExceededLineStyle, ExceededLineWidth);
            }
        }

        // Follow-up entry areas: during an active trend, a candle that closes outside the cloud on the
        // opposite side AND beyond the rejoin EMA (bear trend: above the cloud and below the EMA; bull trend:
        // below the cloud and above the EMA) gets a borderless rectangle from its body extreme to its wick extreme.
        // Candles that traverse the whole cloud (high above the top and low below the bottom) are excluded, and the
        // whole candle must stay on the trend side of the rejoin EMA (bear: high <= EMA; bull: low >= EMA).
        private void CheckRejoin(double fastNow, double slowNow)
        {
            if (!ShowRejoinArea || _trendDir == 0) return;
            if (MaxRejoinAreas > 0 && _rejoinTags.Count >= MaxRejoinAreas) return;

            double cloudTop = Math.Max(fastNow, slowNow);
            double cloudBot = Math.Min(fastNow, slowNow);
            double ema      = _rejoinEma[0];
            double bodyLow  = Math.Min(Open[0], Close[0]);
            double bodyHigh = Math.Max(Open[0], Close[0]);

            // A candle whose full range (high to low) spans the entire cloud is not a valid rejoin area
            if (High[0] >= cloudTop && Low[0] <= cloudBot) return;

            string rejoinTag = "EGOptimalEntry_Rejoin_" + CurrentBar;
            // Checkpoints: a rejoin area is only valid once price has progressed RejoinCheckpointPoints in the trend
            // direction from the previous checkpoint (the entry first, then each rejoin area drawn). 0 = off.
            bool bearCheckpoint = RejoinCheckpointPoints <= 0 || Close[0] <= _checkpointPrice - RejoinCheckpointPoints;
            bool bullCheckpoint = RejoinCheckpointPoints <= 0 || Close[0] >= _checkpointPrice + RejoinCheckpointPoints;

            if (_trendDir < 0 && Close[0] > cloudTop && High[0] <= ema && bearCheckpoint)
            {
                _checkpointPrice = Close[0];
                _rejoinTags.Add(rejoinTag);
                Draw.Rectangle(this, rejoinTag, false,
                    0, High[0], -RejoinWidthBars, bodyLow,
                    System.Windows.Media.Brushes.Transparent, RejoinBearColor, RejoinOpacity);
            }
            else if (_trendDir > 0 && Close[0] < cloudBot && Low[0] >= ema && bullCheckpoint)
            {
                _checkpointPrice = Close[0];
                _rejoinTags.Add(rejoinTag);
                Draw.Rectangle(this, rejoinTag, false,
                    0, bodyHigh, -RejoinWidthBars, Low[0],
                    System.Windows.Media.Brushes.Transparent, RejoinBullColor, RejoinOpacity);
            }
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
        [Display(Name = "Near Distance (Ticks)", Description = "Signal fires when the TRAMA is within this many ticks of the EMA crossover price", GroupName = "Entry Area", Order = 1)]
        public int NearTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Debug Log", Description = "Prints every EMA crossover to the NinjaScript Output window with its TRAMA distance (ticks) and whether it was accepted or rejected. Use to tune the thresholds.", GroupName = "Entry Area", Order = 17)]
        public bool DebugLog { get; set; }

        [NinjaScriptProperty]
        [Range(0, int.MaxValue)]
        [Display(Name = "Replace Older Within (Bars)", Description = "When a new signal appears within this many bars of the previous one, the previous rectangle is removed so only the most recent stays. Chains, so a run of signals each within this gap of the last leaves just the final one. 0 = off.", GroupName = "Entry Area", Order = 2)]
        public int ReplaceWithinBars { get; set; }

        // ---- Entry Area ----
        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name = "Entry Area Width (bars)", Description = "How many bars to the right the rectangle extends from the crossover bar", GroupName = "Entry Area", Order = 3)]
        public int RectWidthBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Entry Area Height (ticks)", Description = "Total rectangle height in ticks, centered on the crossover price", GroupName = "Entry Area", Order = 4)]
        public int RectHeightTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Entry Area Opacity", GroupName = "Entry Area", Order = 7)]
        public int RectFillOpacity { get; set; }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Entry Bull Color", Description = "Fast EMA crossing above slow EMA", GroupName = "Entry Area", Order = 5)]
        public System.Windows.Media.Brush BullLineColor { get; set; }

        [Browsable(false)]
        public string BullLineColorSerializable
        {
            get { return Serialize.BrushToString(BullLineColor); }
            set { BullLineColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Entry Bear Color", Description = "Fast EMA crossing below slow EMA", GroupName = "Entry Area", Order = 6)]
        public System.Windows.Media.Brush BearLineColor { get; set; }

        [Browsable(false)]
        public string BearLineColorSerializable
        {
            get { return Serialize.BrushToString(BearLineColor); }
            set { BearLineColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Exceeded Line Width", GroupName = "Entry Area", Order = 15)]
        public int ExceededLineWidth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Exceeded Line Style", GroupName = "Entry Area", Order = 16)]
        public DashStyleHelper ExceededLineStyle { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "High/Low Line Opacity (%)", Description = "Opacity of the high/low line (bull, bear and far colors)", GroupName = "Entry Area", Order = 13)]
        public int LineOpacity { get; set; }


        // ---- Entry Area: High/Low Line ----
        [NinjaScriptProperty]
        [Display(Name = "Display High/Low Line", Description = "Draws a line at the most recent candle's low (bull cross) or high (bear cross) beyond the rectangle within the lookback, same length as the rectangle", GroupName = "Entry Area", Order = 8)]
        public bool ShowLevelLine { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "High/Low Line Width", GroupName = "Entry Area", Order = 11)]
        public int LevelLineWidth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "High/Low Line Style", GroupName = "Entry Area", Order = 12)]
        public DashStyleHelper LevelLineStyle { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "High/Low Line Lookback (bars)", Description = "How many bars back (including the crossover bar, most recent first) to search for the first candle whose high (bear) / low (bull) is beyond the rectangle edge", GroupName = "Entry Area", Order = 9)]
        public int LevelLookback { get; set; }

        [NinjaScriptProperty]
        [Range(0, int.MaxValue)]
        [Display(Name = "High/Low Line Max Distance (ticks)", Description = "If that low/high is further than this from the rectangle edge (or none is found), an extra line is also drawn flush against the rectangle in the Exceeded Line Color", GroupName = "Entry Area", Order = 10)]
        public int LevelMaxTicks { get; set; }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Exceeded Line Color", Description = "Color of the line drawn flush against the rectangle when the found low/high exceeds Max Distance (or none is found)", GroupName = "Entry Area", Order = 14)]
        public System.Windows.Media.Brush LevelFarColor { get; set; }

        [Browsable(false)]
        public string LevelFarColorSerializable
        {
            get { return Serialize.BrushToString(LevelFarColor); }
            set { LevelFarColor = Serialize.StringToBrush(value); }
        }

        // ---- Rejoin Area ----
        [NinjaScriptProperty]
        [Display(Name = "Display Rejoin Area", Description = "During an active trend (after a primary entry), marks candles that close outside the cloud against the trend and beyond the Rejoin EMA as potential follow-up entries", GroupName = "Rejoin Area", Order = 1)]
        public bool ShowRejoinArea { get; set; }

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name = "Rejoin Area Width (bars)", Description = "How many bars to the right the rejoin rectangle extends from the candle", GroupName = "Rejoin Area", Order = 2)]
        public int RejoinWidthBars { get; set; }

        [NinjaScriptProperty]
        [Range(0, int.MaxValue)]
        [Display(Name = "Max Rejoin Areas", Description = "Maximum number of rejoin areas drawn per entry; once reached, further qualifying candles in that trend are ignored until the next entry. 0 = unlimited.", GroupName = "Rejoin Area", Order = 3)]
        public int MaxRejoinAreas { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, double.MaxValue)]
        [Display(Name = "Rejoin Checkpoint Distance (Points)", Description = "Checkpoint spacing in price points. The entry is the first checkpoint; a rejoin area is only valid once the candle closes at least this far beyond the previous checkpoint in the trend direction, and then becomes the next checkpoint. 0 = off.", GroupName = "Rejoin Area", Order = 4)]
        public double RejoinCheckpointPoints { get; set; }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Rejoin Bull Color", Description = "Rejoin rectangles after a bullish entry", GroupName = "Rejoin Area", Order = 5)]
        public System.Windows.Media.Brush RejoinBullColor { get; set; }

        [Browsable(false)]
        public string RejoinBullColorSerializable
        {
            get { return Serialize.BrushToString(RejoinBullColor); }
            set { RejoinBullColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Rejoin Bear Color", Description = "Rejoin rectangles after a bearish entry", GroupName = "Rejoin Area", Order = 6)]
        public System.Windows.Media.Brush RejoinBearColor { get; set; }

        [Browsable(false)]
        public string RejoinBearColorSerializable
        {
            get { return Serialize.BrushToString(RejoinBearColor); }
            set { RejoinBearColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Rejoin Area Opacity", GroupName = "Rejoin Area", Order = 7)]
        public int RejoinOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Display Rejoin EMA Line", GroupName = "Rejoin Area", Order = 8)]
        public bool ShowRejoinEma { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Rejoin EMA Period", GroupName = "Rejoin Area", Order = 9)]
        public int RejoinEmaPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Rejoin EMA Line Width", GroupName = "Rejoin Area", Order = 11)]
        public int RejoinEmaWidth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Rejoin EMA Line Style", GroupName = "Rejoin Area", Order = 12)]
        public DashStyleHelper RejoinEmaStyle { get; set; }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Rejoin EMA Color", GroupName = "Rejoin Area", Order = 10)]
        public System.Windows.Media.Brush RejoinEmaColor { get; set; }

        [Browsable(false)]
        public string RejoinEmaColorSerializable
        {
            get { return Serialize.BrushToString(RejoinEmaColor); }
            set { RejoinEmaColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Rejoin EMA Line Opacity (%)", GroupName = "Rejoin Area", Order = 13)]
        public int RejoinEmaOpacity { get; set; }

        #endregion
    }
}
