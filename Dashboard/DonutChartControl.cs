using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Arkadia.Dashboard;

/// <summary>
/// Lightweight donut/ring chart drawn entirely with Avalonia's DrawingContext.
/// No external charting library required.
/// </summary>
public sealed class DonutChartControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<DonutSegment>?> SegmentsProperty =
        AvaloniaProperty.Register<DonutChartControl, IReadOnlyList<DonutSegment>?>(nameof(Segments));

    /// <summary>Segments to render, in draw order (clockwise from the top).</summary>
    public IReadOnlyList<DonutSegment>? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public static readonly StyledProperty<double> RingThicknessProperty =
        AvaloniaProperty.Register<DonutChartControl, double>(nameof(RingThickness), 20.0);

    /// <summary>Width of the ring in device-independent pixels.</summary>
    public double RingThickness
    {
        get => GetValue(RingThicknessProperty);
        set => SetValue(RingThicknessProperty, value);
    }

    static DonutChartControl()
    {
        AffectsRender<DonutChartControl>(SegmentsProperty, RingThicknessProperty);
    }

    public override void Render(DrawingContext context)
    {
        var segments = Segments;
        if (segments is null || segments.Count == 0)
            return;

        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0)
            return;

        var cx      = Bounds.Width  / 2;
        var cy      = Bounds.Height / 2;
        var outerR  = size / 2 - 1; // 1px inset so the edge isn't clipped
        var innerR  = Math.Max(1, outerR - RingThickness);

        double total = 0;
        foreach (var s in segments)
            total += s.Value;
        if (total <= 0)
            return;

        const double gapDeg  = 2.5;  // gap between slices in degrees
        var          start   = -90.0; // begin at 12 o'clock

        foreach (var seg in segments)
        {
            var sweep = (seg.Value / total) * 360.0 - gapDeg;
            if (sweep > 0)
                DrawSlice(context, seg.Fill, cx, cy, outerR, innerR, start, sweep);
            start += (seg.Value / total) * 360.0;
        }
    }

    private static void DrawSlice(
        DrawingContext context, IBrush fill,
        double cx, double cy, double outerR, double innerR,
        double startDeg, double sweepDeg)
    {
        var startRad = ToRad(startDeg);
        var endRad   = ToRad(startDeg + sweepDeg);
        var large    = sweepDeg > 180;

        var p1 = Polar(cx, cy, outerR, startRad); // outer arc start
        var p2 = Polar(cx, cy, outerR, endRad);   // outer arc end
        var p3 = Polar(cx, cy, innerR, endRad);   // inner arc end
        var p4 = Polar(cx, cy, innerR, startRad); // inner arc start

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(p1, isFilled: true);
            ctx.ArcTo(p2, new Size(outerR, outerR), 0, large, SweepDirection.Clockwise);
            ctx.LineTo(p3);
            ctx.ArcTo(p4, new Size(innerR, innerR), 0, large, SweepDirection.CounterClockwise);
            ctx.EndFigure(true);
        }

        context.DrawGeometry(fill, null, geo);
    }

    private static double ToRad(double deg) => deg * Math.PI / 180.0;

    private static Point Polar(double cx, double cy, double r, double rad)
        => new(cx + r * Math.Cos(rad), cy + r * Math.Sin(rad));
}
