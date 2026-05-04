using System;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Wavee.UI.WinUI.Controls.Diagnostics;

/// <summary>
/// Lightweight line chart for IPC diagnostics.
/// Draws a polyline from a rolling double[] buffer with threshold coloring,
/// gradient fill, and min/avg/max stats.
/// </summary>
public sealed partial class IpcLineChart : UserControl
{
    private double[] _data = Array.Empty<double>();
    private int _dataCount;
    private string _unit = "ms";

    // Thresholds for coloring (configurable per-chart)
    public double GoodThreshold { get; set; } = 2.0;
    public double WarnThreshold { get; set; } = 10.0;

    public IpcLineChart()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Redraw();
    }

    /// <summary>
    /// Updates the chart with new data. Call from the ViewModel refresh timer.
    /// </summary>
    public void Update(double[] data, int count, string unit = "ms")
    {
        _data = data;
        _dataCount = count;
        _unit = unit;
        Redraw();
    }

    private void Redraw()
    {
        ChartCanvas.Children.Clear();

        var w = ChartCanvas.ActualWidth;
        var h = ChartCanvas.ActualHeight;
        if (w <= 0 || h <= 0 || _dataCount == 0) return;

        // Compute stats
        double min = double.MaxValue, max = 0, sum = 0;
        for (int i = 0; i < _dataCount; i++)
        {
            var v = _data[i];
            if (v < min) min = v;
            if (v > max) max = v;
            sum += v;
        }
        var avg = sum / _dataCount;
        var latest = _data[(_dataCount - 1) % _data.Length];

        // Update stats text
        LatestText.Text = $"{latest:F1}{_unit}";
        MinText.Text = $"min {min:F1}";
        AvgText.Text = $"avg {avg:F1}";
        MaxText.Text = $"max {max:F1}";

        // Color the latest value
        LatestText.Foreground = latest <= GoodThreshold
            ? new SolidColorBrush(ParseHex("#4CAF50"))
            : latest <= WarnThreshold
                ? new SolidColorBrush(ParseHex("#FFC107"))
                : new SolidColorBrush(ParseHex("#F44336"));

        // Y scale — at least show the warn threshold
        var yMax = Math.Max(max * 1.2, WarnThreshold * 1.5);
        if (yMax <= 0) yMax = 1;

        // Padding
        const double padLeft = 0;
        const double padRight = 0;
        var chartW = w - padLeft - padRight;
        var chartH = h;

        // Build points
        var points = new PointCollection();
        var stepX = chartW / Math.Max(_dataCount - 1, 1);

        for (int i = 0; i < _dataCount; i++)
        {
            var x = padLeft + i * stepX;
            var y = chartH - (_data[i] / yMax * chartH);
            y = Math.Clamp(y, 0, chartH);
            points.Add(new Point(x, y));
        }

        // Draw gradient fill under the line
        var fillPoints = new PointCollection();
        fillPoints.Add(new Point(padLeft, chartH)); // bottom-left
        foreach (var pt in points) fillPoints.Add(pt);
        fillPoints.Add(new Point(padLeft + (_dataCount - 1) * stepX, chartH)); // bottom-right

        var fillPoly = new Polygon
        {
            Points = fillPoints,
            Fill = new SolidColorBrush(ParseHex("#204CAF50")),
            Stroke = null,
        };
        ChartCanvas.Children.Add(fillPoly);

        // Draw threshold lines
        DrawThresholdLine(chartH, yMax, w, GoodThreshold, "#304CAF50");
        DrawThresholdLine(chartH, yMax, w, WarnThreshold, "#30FFC107");

        // Draw the line
        var line = new Polyline
        {
            Points = points,
            Stroke = latest <= GoodThreshold
                ? new SolidColorBrush(ParseHex("#4CAF50"))
                : latest <= WarnThreshold
                    ? new SolidColorBrush(ParseHex("#FFC107"))
                    : new SolidColorBrush(ParseHex("#F44336")),
            StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Round,
        };
        ChartCanvas.Children.Add(line);

        // Y-axis labels
        AddLabel($"{yMax:F1}", 2, 0);
        AddLabel($"{yMax / 2:F1}", 2, chartH / 2 - 6);
        AddLabel("0", 2, chartH - 12);
    }

    private void DrawThresholdLine(double chartH, double yMax, double w, double threshold, string color)
    {
        if (threshold > yMax) return;
        var y = chartH - (threshold / yMax * chartH);
        var dashLine = new Line
        {
            X1 = 0, Y1 = y,
            X2 = w, Y2 = y,
            Stroke = new SolidColorBrush(ParseHex(color)),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 4, 4 },
        };
        ChartCanvas.Children.Add(dashLine);
    }

    private void AddLabel(string text, double x, double y)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 8,
            FontFamily = new FontFamily("Consolas"),
            Foreground = new SolidColorBrush(ParseHex("#60FFFFFF")),
            Opacity = 0.6,
        };
        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, y);
        ChartCanvas.Children.Add(tb);
    }

    private static Windows.UI.Color ParseHex(string hex)
    {
        hex = hex.TrimStart('#');
        return hex.Length switch
        {
            6 => Windows.UI.Color.FromArgb(255,
                byte.Parse(hex[..2], System.Globalization.NumberStyles.HexNumber),
                byte.Parse(hex[2..4], System.Globalization.NumberStyles.HexNumber),
                byte.Parse(hex[4..6], System.Globalization.NumberStyles.HexNumber)),
            8 => Windows.UI.Color.FromArgb(
                byte.Parse(hex[..2], System.Globalization.NumberStyles.HexNumber),
                byte.Parse(hex[2..4], System.Globalization.NumberStyles.HexNumber),
                byte.Parse(hex[4..6], System.Globalization.NumberStyles.HexNumber),
                byte.Parse(hex[6..8], System.Globalization.NumberStyles.HexNumber)),
            _ => Colors.White
        };
    }
}
