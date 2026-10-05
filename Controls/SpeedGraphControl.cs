using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace DriveTester.Controls;

public class SpeedGraphControl : FrameworkElement
{
    public static readonly DependencyProperty SpeedPointsProperty = DependencyProperty.Register(
        nameof(SpeedPoints),
        typeof(IReadOnlyList<double>),
        typeof(SpeedGraphControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? SpeedPoints
    {
        get => (IReadOnlyList<double>?)GetValue(SpeedPointsProperty);
        set => SetValue(SpeedPointsProperty, value);
    }

    private static readonly Pen GridPen = new(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 1.0);
    private static readonly Pen LinePen = new(new SolidColorBrush(Color.FromRgb(56, 189, 248)), 2.0); // Sky blue
    private static readonly Brush FillBrush = new LinearGradientBrush(
        Color.FromArgb(100, 56, 189, 248),
        Color.FromArgb(5, 56, 189, 248),
        new Point(0, 0),
        new Point(0, 1));
    private static readonly Typeface TextTypeface = new("Segoe UI");

    public void Redraw()
    {
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 10 || h <= 10) return;

        // Draw background
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(15, 23, 42)), null, new Rect(0, 0, w, h));

        var points = SpeedPoints;
        double maxSpeed = 100.0; // minimum scale 100 MB/s

        if (points != null && points.Count > 0)
        {
            double observedMax = points.Max();
            if (observedMax > maxSpeed)
            {
                // Round up to nearest 100 MB/s
                maxSpeed = Math.Ceiling(observedMax / 50.0) * 50.0;
            }
        }

        // Draw horizontal grid lines (4 lines)
        int gridDivisions = 4;
        for (int i = 0; i <= gridDivisions; i++)
        {
            double y = h - (i * (h - 20) / gridDivisions) - 10;
            dc.DrawLine(GridPen, new Point(0, y), new Point(w, y));

            double speedVal = (i * maxSpeed) / gridDivisions;
            var text = new FormattedText(
                $"{speedVal:F0} MB/s",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                TextTypeface,
                9.0,
                new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
                1.0);
            dc.DrawText(text, new Point(w - text.Width - 6, y - text.Height - 1));
        }

        if (points == null || points.Count < 2)
        {
            var noDataText = new FormattedText(
                "Speed throughput graph will appear during test execution...",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                TextTypeface,
                11.0,
                new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
                1.0);
            dc.DrawText(noDataText, new Point((w - noDataText.Width) / 2, (h - noDataText.Height) / 2));
            return;
        }

        // Build path geometry for speed curve
        var streamGeometry = new StreamGeometry();
        var fillGeometry = new StreamGeometry();

        double stepX = w / Math.Max(points.Count - 1, 1);
        double bottomY = h - 10;

        using (var lineCtx = streamGeometry.Open())
        using (var fillCtx = fillGeometry.Open())
        {
            double startY = bottomY - ((points[0] / maxSpeed) * (h - 20));
            var startPt = new Point(0, Math.Clamp(startY, 10, bottomY));

            lineCtx.BeginFigure(startPt, false, false);
            fillCtx.BeginFigure(new Point(0, bottomY), true, true);
            fillCtx.LineTo(startPt, true, false);

            for (int i = 1; i < points.Count; i++)
            {
                double px = i * stepX;
                double py = bottomY - ((points[i] / maxSpeed) * (h - 20));
                var pt = new Point(px, Math.Clamp(py, 10, bottomY));

                lineCtx.LineTo(pt, true, false);
                fillCtx.LineTo(pt, true, false);
            }

            fillCtx.LineTo(new Point((points.Count - 1) * stepX, bottomY), true, false);
        }

        streamGeometry.Freeze();
        fillGeometry.Freeze();

        dc.DrawGeometry(FillBrush, null, fillGeometry);
        dc.DrawGeometry(null, LinePen, streamGeometry);
    }
}
