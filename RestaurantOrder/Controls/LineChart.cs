using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RestaurantOrder.Controls;

public class LineChart : Control
{
    private List<BarPoint> _data = new();
    private string _valueFormat = "N0";
    private Brush _lineBrush = new SolidColorBrush(Color.FromRgb(0x08, 0x91, 0xB2));
    private Brush _areaBrush;

    static LineChart()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(LineChart), new FrameworkPropertyMetadata(typeof(LineChart)));
    }

    public LineChart()
    {
        SnapsToDevicePixels = true;
        SizeChanged += (_, _) => InvalidateVisual();
        _areaBrush = MakeAreaBrush(Color.FromRgb(0x06, 0xB6, 0xD4));
        Loaded += (_, _) =>
        {
            if (TryFindResource("BrandPrimaryBrush") is SolidColorBrush b)
            {
                _lineBrush = b;
                _areaBrush = MakeAreaBrush(b.Color);
            }
            InvalidateVisual();
        };
    }

    private static LinearGradientBrush MakeAreaBrush(Color baseColor)
    {
        var grad = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
        };
        grad.GradientStops.Add(new GradientStop(Color.FromArgb(60, baseColor.R, baseColor.G, baseColor.B), 0));
        grad.GradientStops.Add(new GradientStop(Color.FromArgb(0, baseColor.R, baseColor.G, baseColor.B), 1));
        grad.Freeze();
        return grad;
    }

    public Brush LineBrush
    {
        get => _lineBrush;
        set { _lineBrush = value; InvalidateVisual(); }
    }

    public string ValueFormat
    {
        get => _valueFormat;
        set { _valueFormat = value; InvalidateVisual(); }
    }

    public void SetData(IEnumerable<BarPoint> data)
    {
        _data = data?.ToList() ?? new List<BarPoint>();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0 || _data.Count == 0) return;

        const double leftPad = 42;
        const double rightPad = 12;
        const double topPad = 12;
        const double bottomPad = 28;
        var plotW = w - leftPad - rightPad;
        var plotH = h - topPad - bottomPad;
        if (plotW <= 0 || plotH <= 0) return;

        double maxV = _data.Max(p => p.Value);
        if (maxV <= 0) maxV = 1;
        double niceMax = NiceCeiling(maxV);

        var axisBrush = (TryFindResource("BrandBorderBrush") as Brush) ?? new SolidColorBrush(Color.FromRgb(0xD9, 0xE2, 0xEA));
        var labelBrush = (TryFindResource("BrandSubtleTextBrush") as Brush) ?? new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));

        const int yTicks = 4;
        var typeface = new Typeface("Segoe UI");
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        for (int i = 0; i <= yTicks; i++)
        {
            double y = topPad + plotH * (1 - (double)i / yTicks);
            dc.DrawLine(new Pen(axisBrush, 1), new Point(leftPad, y), new Point(leftPad + plotW, y));
            var v = niceMax * i / yTicks;
            var ft = new FormattedText(
                v.ToString(_valueFormat, CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                typeface, 10, labelBrush, dpi);
            dc.DrawText(ft, new Point(leftPad - ft.Width - 4, y - ft.Height / 2));
        }

        if (_data.Count == 1)
        {
            var p = _data[0];
            double y = topPad + plotH * (1 - p.Value / niceMax);
            dc.DrawEllipse(_lineBrush, null, new Point(leftPad + plotW / 2, y), 4, 4);
            var lbl = new FormattedText(p.Label ?? "", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, 10, labelBrush, dpi);
            dc.DrawText(lbl, new Point(leftPad + plotW / 2 - lbl.Width / 2, topPad + plotH + 6));
            return;
        }

        double step = plotW / (_data.Count - 1);
        var pts = new List<Point>();
        for (int i = 0; i < _data.Count; i++)
        {
            double x = leftPad + step * i;
            double y = topPad + plotH * (1 - _data[i].Value / niceMax);
            pts.Add(new Point(x, y));
        }

        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(pts[0].X, topPad + plotH), true, true);
            ctx.LineTo(pts[0], false, false);
            for (int i = 1; i < pts.Count; i++) ctx.LineTo(pts[i], false, false);
            ctx.LineTo(new Point(pts[^1].X, topPad + plotH), false, false);
        }
        area.Freeze();
        dc.DrawGeometry(_areaBrush, null, area);

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(pts[0], false, false);
            for (int i = 1; i < pts.Count; i++) ctx.LineTo(pts[i], true, false);
        }
        line.Freeze();
        dc.DrawGeometry(null, new Pen(_lineBrush, 2.5), line);

        for (int i = 0; i < pts.Count; i++)
        {
            dc.DrawEllipse(Brushes.White, new Pen(_lineBrush, 2), pts[i], 3.5, 3.5);
        }

        // X labels — show every Nth to avoid crowding
        int every = (int)Math.Ceiling((double)_data.Count / 8);
        for (int i = 0; i < _data.Count; i++)
        {
            if (i % every != 0 && i != _data.Count - 1) continue;
            var lbl = new FormattedText(_data[i].Label ?? "", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, 10, labelBrush, dpi);
            dc.DrawText(lbl, new Point(pts[i].X - lbl.Width / 2, topPad + plotH + 6));
        }
    }

    private static double NiceCeiling(double v)
    {
        if (v <= 0) return 1;
        var exp = Math.Floor(Math.Log10(v));
        var f = v / Math.Pow(10, exp);
        double nf = f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10;
        return nf * Math.Pow(10, exp);
    }
}
