using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Wavee.UI.WinUI.ViewModels;
using Windows.Foundation;

namespace Wavee.UI.WinUI.Controls.Equalizer;

public sealed partial class EqualizerCurveControl : UserControl
{
    private const double PadLeft = 40;
    private const double PadRight = 16;
    private const double PadTop = 16;
    private const double PadBottom = 32;
    private const double NodeRadius = 7;
    private const double MinGain = -12.0;
    private const double MaxGain = 12.0;

    private readonly Ellipse[] _nodes = new Ellipse[10];
    private readonly Line[] _gridLinesV = new Line[10];
    private readonly List<TextBlock> _freqLabels = [];
    private int _draggingIndex = -1;

    public static readonly DependencyProperty BandsProperty =
        DependencyProperty.Register(nameof(Bands), typeof(ObservableCollection<EqualizerBandViewModel>),
            typeof(EqualizerCurveControl), new PropertyMetadata(null, OnBandsChanged));

    public ObservableCollection<EqualizerBandViewModel>? Bands
    {
        get => (ObservableCollection<EqualizerBandViewModel>?)GetValue(BandsProperty);
        set => SetValue(BandsProperty, value);
    }

    public EqualizerCurveControl()
    {
        InitializeComponent();
        CreateNodes();
    }

    private void CreateNodes()
    {
        for (var i = 0; i < 10; i++)
        {
            // Vertical grid line
            var gridLine = new Line
            {
                StrokeThickness = 1,
                StrokeDashArray = [2, 4],
                Opacity = 0.15
            };
            gridLine.SetValue(Canvas.ZIndexProperty, 0);
            _gridLinesV[i] = gridLine;
            EqCanvas.Children.Add(gridLine);

            // Node ellipse
            var node = new Ellipse
            {
                Width = NodeRadius * 2,
                Height = NodeRadius * 2,
                StrokeThickness = 2.5,
                Fill = new SolidColorBrush(Colors.White)
            };
            node.SetValue(Canvas.ZIndexProperty, 10);

            var index = i;
            node.PointerPressed += (_, e) => OnNodePointerPressed(index, e);
            node.PointerEntered += (_, _) =>
            {
                node.Width = (NodeRadius + 3) * 2;
                node.Height = (NodeRadius + 3) * 2;
                LayoutNode(index);
            };
            node.PointerExited += (_, _) =>
            {
                if (_draggingIndex != index)
                {
                    node.Width = NodeRadius * 2;
                    node.Height = NodeRadius * 2;
                    LayoutNode(index);
                }
            };

            _nodes[i] = node;
            EqCanvas.Children.Add(node);
        }

        EqCanvas.PointerMoved += OnCanvasPointerMoved;
        EqCanvas.PointerReleased += OnCanvasPointerReleased;
        EqCanvas.PointerCaptureLost += (_, _) => _draggingIndex = -1;
    }

    private static void OnBandsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (EqualizerCurveControl)d;

        if (e.OldValue is ObservableCollection<EqualizerBandViewModel> oldBands)
        {
            oldBands.CollectionChanged -= control.OnBandsCollectionChanged;
            foreach (var b in oldBands)
                b.PropertyChanged -= control.OnBandPropertyChanged;
        }

        if (e.NewValue is ObservableCollection<EqualizerBandViewModel> newBands)
        {
            newBands.CollectionChanged += control.OnBandsCollectionChanged;
            foreach (var b in newBands)
                b.PropertyChanged += control.OnBandPropertyChanged;
            control.FullRedraw();
        }
    }

    private void OnBandsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (Bands == null) return;
        foreach (var b in Bands) b.PropertyChanged -= OnBandPropertyChanged;
        foreach (var b in Bands) b.PropertyChanged += OnBandPropertyChanged;
        FullRedraw();
    }

    private void OnBandPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EqualizerBandViewModel.GainDb) or nameof(EqualizerBandViewModel.NormalizedGain))
            FullRedraw();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => FullRedraw();

    private double DrawWidth => Math.Max(1, ActualWidth - PadLeft - PadRight);
    private double DrawHeight => Math.Max(1, ActualHeight - PadTop - PadBottom);

    private Point BandToPoint(int index)
    {
        if (Bands == null || index >= Bands.Count)
            return new Point(PadLeft, PadTop + DrawHeight / 2);

        var x = PadLeft + (index / 9.0) * DrawWidth;
        var y = PadTop + (1.0 - Bands[index].NormalizedGain) * DrawHeight;
        return new Point(x, y);
    }

    private double YToGain(double y)
    {
        var normalized = 1.0 - ((y - PadTop) / DrawHeight);
        return Math.Clamp(normalized * (MaxGain - MinGain) + MinGain, MinGain, MaxGain);
    }

    private void FullRedraw()
    {
        if (Bands == null || Bands.Count == 0 || ActualWidth < 1 || ActualHeight < 1) return;

        ApplyAccentColors();
        LayoutDbLabels();
        LayoutBaseline();
        LayoutGridLines();
        LayoutFreqLabels();

        for (var i = 0; i < Math.Min(10, Bands.Count); i++)
            LayoutNode(i);

        UpdateCurve();
    }

    private void ApplyAccentColors()
    {
        SolidColorBrush accent;
        try { accent = (SolidColorBrush)Application.Current.Resources["App.Theme.AccentBrush"]; }
        catch (Exception ex) { Debug.WriteLine($"Failed to resolve AccentBrush: {ex.Message}"); accent = new SolidColorBrush(Colors.DodgerBlue); }

        CurvePath.Stroke = accent;
        FillPath.Fill = accent;

        var whiteBrush = new SolidColorBrush(Colors.White);
        foreach (var node in _nodes)
        {
            if (node == null) continue;
            node.Stroke = accent;
            node.Fill = whiteBrush;
        }

        var gridBrush = (SolidColorBrush)Application.Current.Resources["TextFillColorTertiaryBrush"];
        foreach (var line in _gridLinesV)
        {
            if (line != null) line.Stroke = gridBrush;
        }
    }

    private void LayoutDbLabels()
    {
        var h = DrawHeight;
        Canvas.SetLeft(LabelPlus12, 4); Canvas.SetTop(LabelPlus12, PadTop - 6);
        Canvas.SetLeft(LabelPlus6, 8);  Canvas.SetTop(LabelPlus6, PadTop + h * 0.25 - 6);
        Canvas.SetLeft(LabelZero, 4);   Canvas.SetTop(LabelZero, PadTop + h * 0.5 - 6);
        Canvas.SetLeft(LabelMinus6, 8); Canvas.SetTop(LabelMinus6, PadTop + h * 0.75 - 6);
        Canvas.SetLeft(LabelMinus12, 4);Canvas.SetTop(LabelMinus12, PadTop + h - 6);
    }

    private void LayoutBaseline()
    {
        var y = PadTop + DrawHeight * 0.5;
        BaselineLine.X1 = PadLeft;
        BaselineLine.Y1 = y;
        BaselineLine.X2 = PadLeft + DrawWidth;
        BaselineLine.Y2 = y;
    }

    private void LayoutGridLines()
    {
        for (var i = 0; i < 10; i++)
        {
            var x = PadLeft + (i / 9.0) * DrawWidth;
            _gridLinesV[i].X1 = x;
            _gridLinesV[i].Y1 = PadTop;
            _gridLinesV[i].X2 = x;
            _gridLinesV[i].Y2 = PadTop + DrawHeight;
        }
    }

    private void LayoutFreqLabels()
    {
        if (Bands == null) return;

        // Remove old labels
        foreach (var old in _freqLabels)
            EqCanvas.Children.Remove(old);
        _freqLabels.Clear();

        FreqLabelsPanel.Visibility = Visibility.Collapsed;

        var brush = (SolidColorBrush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        for (var i = 0; i < Math.Min(10, Bands.Count); i++)
        {
            var x = PadLeft + (i / 9.0) * DrawWidth;
            var label = new TextBlock
            {
                Text = Bands[i].FrequencyLabel,
                FontSize = 10,
                FontFamily = new FontFamily("Consolas"),
                Foreground = brush,
                TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center,
                Width = 36,
            };
            Canvas.SetLeft(label, x - 18);
            Canvas.SetTop(label, PadTop + DrawHeight + 6);
            EqCanvas.Children.Add(label);
            _freqLabels.Add(label);
        }
    }

    private void LayoutNode(int i)
    {
        if (Bands == null || i >= Bands.Count) return;
        var pt = BandToPoint(i);
        var r = _nodes[i].Width / 2;
        Canvas.SetLeft(_nodes[i], pt.X - r);
        Canvas.SetTop(_nodes[i], pt.Y - r);
    }

    private void UpdateCurve()
    {
        if (Bands == null || Bands.Count < 2) return;

        var count = Math.Min(10, Bands.Count);
        var points = new Point[count];
        for (var i = 0; i < count; i++)
            points[i] = BandToPoint(i);

        // Catmull-Rom spline → cubic Bezier segments
        var curveFigure = new PathFigure { StartPoint = points[0] };
        var fillFigure = new PathFigure { StartPoint = points[0] };

        for (var i = 0; i < count - 1; i++)
        {
            var p0 = i > 0 ? points[i - 1] : points[i];
            var p1 = points[i];
            var p2 = points[i + 1];
            var p3 = i + 2 < count ? points[i + 2] : points[i + 1];

            var cp1 = new Point(
                p1.X + (p2.X - p0.X) / 6.0,
                p1.Y + (p2.Y - p0.Y) / 6.0);
            var cp2 = new Point(
                p2.X - (p3.X - p1.X) / 6.0,
                p2.Y - (p3.Y - p1.Y) / 6.0);

            curveFigure.Segments.Add(new BezierSegment { Point1 = cp1, Point2 = cp2, Point3 = p2 });
            fillFigure.Segments.Add(new BezierSegment { Point1 = cp1, Point2 = cp2, Point3 = p2 });
        }

        var curveGeo = new PathGeometry();
        curveGeo.Figures.Add(curveFigure);
        CurvePath.Data = curveGeo;

        // Fill: close path down to baseline and back
        var baseY = PadTop + DrawHeight * 0.5;
        fillFigure.Segments.Add(new LineSegment { Point = new Point(points[count - 1].X, baseY) });
        fillFigure.Segments.Add(new LineSegment { Point = new Point(points[0].X, baseY) });
        fillFigure.IsClosed = true;

        var fillGeo = new PathGeometry();
        fillGeo.Figures.Add(fillFigure);
        FillPath.Data = fillGeo;
    }

    // ── Node drag interaction ──

    private void OnNodePointerPressed(int index, PointerRoutedEventArgs e)
    {
        _draggingIndex = index;
        EqCanvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnCanvasPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_draggingIndex < 0 || Bands == null || _draggingIndex >= Bands.Count) return;

        var pos = e.GetCurrentPoint(EqCanvas).Position;
        var gain = YToGain(pos.Y);
        gain = Math.Round(gain * 2) / 2.0; // snap to 0.5 dB

        Bands[_draggingIndex].GainDb = gain;
        e.Handled = true;
    }

    private void OnCanvasPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_draggingIndex >= 0)
        {
            _nodes[_draggingIndex].Width = NodeRadius * 2;
            _nodes[_draggingIndex].Height = NodeRadius * 2;
            LayoutNode(_draggingIndex);

            EqCanvas.ReleasePointerCapture(e.Pointer);
            _draggingIndex = -1;
            e.Handled = true;
        }
    }
}
