using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace RestaurantOrder.Controls;

public class BarPoint
{
    public string Label { get; set; } = "";
    public double Value { get; set; }
}

public class BarChart : Control
{
    private List<BarPoint> _data = new();
    private string _valueFormat = "N0";
    private Brush _barBrush = new SolidColorBrush(Color.FromRgb(0x08, 0x91, 0xB2));

    static BarChart()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(BarChart), new FrameworkPropertyMetadata(typeof(BarChart)));
    }

    public BarChart()
    {
        SnapsToDevicePixels = true;
        ClipToBounds = false;
        SizeChanged += (_, _) => InvalidateVisual();
        Loaded += (_, _) =>
        {
            if (TryFindResource("BrandPrimaryBrush") is Brush b) _barBrush = b;
            InvalidateVisual();
        };
    }

    public Brush BarBrush
    {
        get => _barBrush;
        set { _barBrush = value; InvalidateVisual(); }
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

        const double leftPad = 36;
        const double rightPad = 8;
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

        // Horizontal grid lines + Y labels (5 ticks)
        const int yTicks = 4;
        for (int i = 0; i <= yTicks; i++)
        {
            double y = topPad + plotH * (1 - (double)i / yTicks);
            dc.DrawLine(new Pen(axisBrush, 1), new Point(leftPad, y), new Point(leftPad + plotW, y));

            var v = niceMax * i / yTicks;
            var ft = new FormattedText(
                v.ToString(_valueFormat, CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                10,
                labelBrush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(ft, new Point(leftPad - ft.Width - 4, y - ft.Height / 2));
        }

        double n = _data.Count;
        double slot = plotW / n;
        double barW = Math.Max(8, Math.Min(38, slot * 0.6));

        var typeface = new Typeface("Segoe UI");
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        for (int i = 0; i < _data.Count; i++)
        {
            var p = _data[i];
            double cx = leftPad + slot * i + slot / 2;
            double bh = plotH * (p.Value / niceMax);
            double y = topPad + plotH - bh;
            var rect = new Rect(cx - barW / 2, y, barW, bh);
            dc.DrawRoundedRectangle(_barBrush, null, rect, 4, 4);

            var lbl = new FormattedText(p.Label ?? "", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, 10, labelBrush, dpi);
            dc.DrawText(lbl, new Point(cx - lbl.Width / 2, topPad + plotH + 6));
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
