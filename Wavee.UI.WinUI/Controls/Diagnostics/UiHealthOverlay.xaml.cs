using System;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.Controls.Diagnostics;

public sealed partial class UiHealthOverlay : UserControl
{
    private UiHealthMonitor? _monitor;
    private DispatcherQueueTimer? _refreshTimer;
    private bool _isExpanded;
    private readonly double[] _historyBuffer = new double[300];

    public UiHealthOverlay()
    {
        InitializeComponent();
    }

    internal void Attach(UiHealthMonitor monitor)
    {
        _monitor = monitor;

        _refreshTimer = DispatcherQueue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromMilliseconds(500);
        _refreshTimer.Tick += OnRefreshTick;
        // Don't auto-start — the overlay is created Visibility=Collapsed and the
        // refresh tick reads Process.Refresh() (a syscall) twice per second to
        // populate text bindings nobody can see. Tick lifecycle now follows
        // Visibility: Visible → Start, Collapsed → Stop.
        RegisterPropertyChangedCallback(VisibilityProperty, OnVisibilityChanged);
        if (Visibility == Visibility.Visible)
            _refreshTimer.Start();
    }

    public void Detach()
    {
        _refreshTimer?.Stop();
        if (_refreshTimer != null)
            _refreshTimer.Tick -= OnRefreshTick;
        _refreshTimer = null;
        _monitor = null;
    }

    private void OnVisibilityChanged(DependencyObject sender, DependencyProperty dp)
    {
        if (_refreshTimer == null) return;
        if (Visibility == Visibility.Visible)
            _refreshTimer.Start();
        else
            _refreshTimer.Stop();
    }

    private void OnRefreshTick(DispatcherQueueTimer sender, object args)
    {
        if (_monitor == null || Visibility != Visibility.Visible) return;

        var s = _monitor.CurrentStats;

        FpsText.Text = $"FPS(render): {s.Fps:F0}  avg: {s.AvgFrameMs:F1}ms";
        FrameText.Text = $"tick: {s.UiTickAvgMs:F1}ms  worst: {s.WorstRecentFrameMs:F0}ms  all-time: {s.WorstFrameMs:F0}ms";
        StallText.Text = $"stalls: {s.StallCount}  crits: {s.CriticalCount}  frames: {s.TotalFrames}";
        GcText.Text = $"GC: g0={s.GcGen0} g1={s.GcGen1} g2={s.GcGen2} g2-stall={s.Gen2DuringStalls}";
        MemoryText.Text = $"mem: managed={s.ManagedMb:F0}MB  ws={s.WorkingSetMb:F0}MB  private={s.PrivateMb:F0}MB";

        // Profiler top operations
        var profiler = Services.UiOperationProfiler.Instance;
        if (profiler != null)
        {
            var topOps = profiler.GetTopOperations(3);
            if (topOps.Count > 0)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var op in topOps)
                    sb.AppendLine($"{op.Name}: max={op.MaxMs:F0}ms avg={op.AvgMs:F0}ms n={op.Count}");
                var underruns = profiler.AudioUnderrunCount;
                if (underruns > 0)
                    sb.AppendLine($"audio underruns: {underruns}");
                ProfilerText.Text = sb.ToString().TrimEnd();
            }
            else
            {
                ProfilerText.Text = "";
            }
        }

        if (_isExpanded)
            DrawGraph();
    }

    private void ExpandButton_Click(object sender, RoutedEventArgs e)
    {
        _isExpanded = !_isExpanded;
        GraphCanvas.Visibility = _isExpanded ? Visibility.Visible : Visibility.Collapsed;
        GraphLegend.Visibility = _isExpanded ? Visibility.Visible : Visibility.Collapsed;
        ExpandButton.Content = _isExpanded ? "Hide" : "Graph";

        if (_isExpanded)
            DrawGraph();
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_monitor == null) return;

        var report = _monitor.GenerateReport();
        var dataPackage = new DataPackage();
        dataPackage.SetText(report);
        Clipboard.SetContent(dataPackage);

        // Brief visual feedback
        CopyButton.Content = "Done";
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.Tick += (_, _) => { CopyButton.Content = "Copy"; timer.Stop(); };
        timer.Start();
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        _monitor?.ResetStats();
    }

    private void DrawGraph()
    {
        if (_monitor == null) return;

        var count = _monitor.CopyHistory(_historyBuffer);
        if (count == 0) return;

        GraphCanvas.Children.Clear();

        var w = GraphCanvas.Width;
        var h = GraphCanvas.Height;

        // Find max for Y scale (clamp to at least 50ms for visibility)
        double maxMs = 50;
        for (int i = 0; i < count; i++)
            if (_historyBuffer[i] > maxMs) maxMs = _historyBuffer[i];

        // Round up to nice number
        maxMs = Math.Ceiling(maxMs / 50) * 50;

        // Draw threshold lines
        DrawHorizontalLine(16, maxMs, h, w, "#40808080");  // 16ms = 60fps
        DrawHorizontalLine(50, maxMs, h, w, "#40FFC107");  // 50ms warn
        if (maxMs > 100)
            DrawHorizontalLine(150, maxMs, h, w, "#40F44336"); // 150ms critical

        // Draw bars
        var barWidth = Math.Max(1, w / count);
        for (int i = 0; i < count; i++)
        {
            var ms = _historyBuffer[i];
            var barHeight = Math.Max(1, ms / maxMs * h);
            var x = i * barWidth;
            var y = h - barHeight;

            var color = ms switch
            {
                > 150 => "#F44336",
                > 50 => "#FFC107",
                > 16 => "#81C784",
                _ => "#4CAF50"
            };

            var rect = new Rectangle
            {
                Width = Math.Max(1, barWidth - 0.5),
                Height = barHeight,
                Fill = new SolidColorBrush(ParseColor(color)),
            };
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, y);
            GraphCanvas.Children.Add(rect);
        }

        // Y-axis labels
        AddLabel($"{maxMs:F0}ms", 0, 0);
        AddLabel($"{maxMs / 2:F0}ms", 0, h / 2 - 6);
        AddLabel("0ms", 0, h - 12);
    }

    private void DrawHorizontalLine(double ms, double maxMs, double h, double w, string color)
    {
        if (ms > maxMs) return;
        var y = h - (ms / maxMs * h);
        var line = new Rectangle
        {
            Width = w,
            Height = 1,
            Fill = new SolidColorBrush(ParseColor(color)),
        };
        Canvas.SetLeft(line, 0);
        Canvas.SetTop(line, y);
        GraphCanvas.Children.Add(line);
    }

    private void AddLabel(string text, double x, double y)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 8,
            FontFamily = new FontFamily("Consolas"),
            Foreground = new SolidColorBrush(ParseColor("#80FFFFFF")),
        };
        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, y);
        GraphCanvas.Children.Add(tb);
    }

    private static Windows.UI.Color ParseColor(string hex)
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
