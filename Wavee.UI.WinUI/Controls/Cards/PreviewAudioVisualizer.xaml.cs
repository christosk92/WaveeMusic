using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Microsoft.UI;

namespace Wavee.UI.WinUI.Controls.Cards;

public sealed partial class PreviewAudioVisualizer : UserControl
{
    public const int VisibleBarCount = 28;

    private readonly float[] _targetLevels = new float[VisibleBarCount];
    private readonly float[] _renderedLevels = new float[VisibleBarCount];
    private readonly Border[] _bars = new Border[VisibleBarCount];

    private bool _isActive;
    private bool _isRenderingSubscribed;
    private int _framesSincePush = 999;
    private float _idlePhase;
    private Color _cachedBarColor = Colors.Transparent;
    private Color _cachedTrackColor = Colors.Transparent;

    public static readonly DependencyProperty BarColorProperty =
        DependencyProperty.Register(nameof(BarColor), typeof(Color), typeof(PreviewAudioVisualizer),
            new PropertyMetadata(Colors.Transparent, OnBarColorChanged));

    public static readonly DependencyProperty TrackColorProperty =
        DependencyProperty.Register(nameof(TrackColor), typeof(Color), typeof(PreviewAudioVisualizer),
            new PropertyMetadata(Colors.Transparent, OnTrackColorChanged));

    public Color BarColor
    {
        get => (Color)GetValue(BarColorProperty);
        set => SetValue(BarColorProperty, value);
    }

    public Color TrackColor
    {
        get => (Color)GetValue(TrackColorProperty);
        set => SetValue(TrackColorProperty, value);
    }

    public PreviewAudioVisualizer()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        EnsureBarsCreated();
        Reset();
    }

    public void SetActive(bool isActive)
    {
        _isActive = isActive;

        if (!isActive)
            Reset();

        UpdateRenderingSubscription();
        ApplyBarVisuals();
    }

    public void PushLevels(ReadOnlySpan<float> levels)
    {
        if (levels.IsEmpty)
            return;

        for (int i = 0; i < VisibleBarCount; i++)
        {
            var barStart = i * levels.Length / (float)VisibleBarCount;
            var barEnd = (i + 1) * levels.Length / (float)VisibleBarCount;
            var sampleStart = Math.Clamp((int)MathF.Floor(barStart), 0, levels.Length - 1);
            var sampleEnd = Math.Clamp((int)MathF.Ceiling(barEnd), sampleStart + 1, levels.Length);

            var peak = 0f;
            var energy = 0f;
            for (int sampleIndex = sampleStart; sampleIndex < sampleEnd; sampleIndex++)
            {
                var value = Math.Clamp(levels[sampleIndex], 0f, 1f);
                peak = MathF.Max(peak, value);
                energy += value * value;
            }

            var sampleCount = Math.Max(1, sampleEnd - sampleStart);
            var rms = MathF.Sqrt(energy / sampleCount);
            var combined = (peak * 0.82f) + (rms * 0.18f);
            var boosted = MathF.Pow(Math.Clamp((combined - 0.015f) * 1.55f, 0f, 1f), 0.72f);
            _targetLevels[i] = Math.Clamp(boosted, 0f, 1f);
        }

        _framesSincePush = 0;
        ApplyBarVisuals();
    }

    public void Reset()
    {
        Array.Clear(_targetLevels);
        Array.Clear(_renderedLevels);
        _framesSincePush = 999;
        _idlePhase = 0;
        ApplyBarVisuals();
    }

    private static void OnBarColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (PreviewAudioVisualizer)d;
        self._cachedBarColor = (Color)e.NewValue;
        self.ApplyBarVisuals();
    }

    private static void OnTrackColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (PreviewAudioVisualizer)d;
        self._cachedTrackColor = (Color)e.NewValue;
        self.ApplyBarVisuals();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        EnsureBarsCreated();
        UpdateRenderingSubscription();
        ApplyBarVisuals();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        UnsubscribeRendering();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyBarVisuals();
    }

    private void EnsureBarsCreated()
    {
        if (BarHost == null || _bars[0] != null)
            return;

        BarHost.ColumnDefinitions.Clear();
        BarHost.Children.Clear();

        for (int i = 0; i < VisibleBarCount; i++)
        {
            BarHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var bar = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Bottom,
                MinHeight = 3,
                Height = 3,
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(Colors.Transparent),
                Opacity = 0
            };

            Grid.SetColumn(bar, i);
            BarHost.Children.Add(bar);
            _bars[i] = bar;
        }
    }

    private void UpdateRenderingSubscription()
    {
        if (!IsLoaded || !_isActive)
        {
            UnsubscribeRendering();
            return;
        }

        if (_isRenderingSubscribed)
            return;

        CompositionTarget.Rendering += CompositionTarget_Rendering;
        _isRenderingSubscribed = true;
    }

    private void UnsubscribeRendering()
    {
        if (!_isRenderingSubscribed)
            return;

        CompositionTarget.Rendering -= CompositionTarget_Rendering;
        _isRenderingSubscribed = false;
    }

    private void CompositionTarget_Rendering(object? sender, object e)
    {
        _framesSincePush++;
        _idlePhase += 0.085f;

        for (int i = 0; i < VisibleBarCount; i++)
        {
            var t = i / (float)(VisibleBarCount - 1);
            var edgeBias = 0.94f + (0.16f * MathF.Sin(t * MathF.PI));

            if (_framesSincePush > 2)
                _targetLevels[i] *= 0.9f;

            var idleCarrier =
                0.045f +
                (0.028f * MathF.Sin(_idlePhase + (t * 6.2f))) +
                (0.02f * MathF.Sin((_idlePhase * 1.45f) - (t * 4.6f)));
            var idleEnvelope = MathF.Max(0.012f, idleCarrier * edgeBias);
            var liveEnvelope = _targetLevels[i] * edgeBias;
            var targetEnvelope = MathF.Max(idleEnvelope, liveEnvelope);

            _renderedLevels[i] += (targetEnvelope - _renderedLevels[i]) * 0.38f;
            _renderedLevels[i] = Math.Clamp(_renderedLevels[i], 0f, 1f);
        }

        ApplyBarVisuals();
    }

    private void ApplyBarVisuals()
    {
        if (BarHost == null)
            return;

        EnsureBarsCreated();

        var activeColor = ResolveColor(_cachedBarColor, "HomeBaselineCardPreviewVisualizerForegroundColor", Colors.White);
        var baseColor = ResolveColor(_cachedTrackColor, "HomeBaselineCardPreviewVisualizerTrackColor", WithAlpha(activeColor, 72));
        if (baseColor.A == 0)
            baseColor = WithAlpha(activeColor, 72);

        var availableHeight = (float)Math.Max(8, BarHost.ActualHeight > 0 ? BarHost.ActualHeight : ActualHeight);
        var minHeight = MathF.Max(3f, availableHeight * 0.03f);
        var glowColor = Lighten(activeColor, 0.3f);

        for (int i = 0; i < VisibleBarCount; i++)
        {
            var bar = _bars[i];
            if (bar == null)
                continue;

            var level = Math.Clamp(_renderedLevels[i], 0f, 1f);
            var visualLevel = MathF.Min(1f, 0.02f + (MathF.Pow(level, 0.46f) * 1.2f));
            var barHeight = minHeight + ((availableHeight - minHeight) * visualLevel);
            bar.Height = barHeight;
            bar.Opacity = 0.18 + (visualLevel * 0.82f);
            bar.Background = new SolidColorBrush(Blend(baseColor, glowColor, 0.3f + (level * 0.7f)));
        }
    }

    private static Color ResolveColor(Color candidate, string resourceKey, Color fallback)
    {
        if (candidate.A > 0)
            return candidate;

        if (Application.Current?.Resources.TryGetValue(resourceKey, out var resource) == true)
        {
            if (resource is Color resourceColor)
                return resourceColor;

            if (resource is SolidColorBrush brush)
                return brush.Color;
        }

        return fallback;
    }

    private static Color WithAlpha(Color color, byte alpha)
        => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static Color Blend(Color from, Color to, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return Color.FromArgb(
            (byte)(from.A + ((to.A - from.A) * amount)),
            (byte)(from.R + ((to.R - from.R) * amount)),
            (byte)(from.G + ((to.G - from.G) * amount)),
            (byte)(from.B + ((to.B - from.B) * amount)));
    }

    private static Color Lighten(Color color, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return Color.FromArgb(
            color.A,
            (byte)(color.R + ((255 - color.R) * amount)),
            (byte)(color.G + ((255 - color.G) * amount)),
            (byte)(color.B + ((255 - color.B) * amount)));
    }
}
