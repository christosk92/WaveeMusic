using System;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Microsoft.UI;

namespace Wavee.UI.WinUI.Controls.Cards;

public sealed partial class PreviewAudioVisualizer : UserControl
{
    private const float Tau = MathF.PI * 2f;
    public const int VisibleBarCount = 24;
    private const int WaveSampleCount = 120;

    private readonly float[] _targetLevels = new float[VisibleBarCount];
    private readonly float[] _renderedLevels = new float[VisibleBarCount];
    private readonly float[] _mappedLevels = new float[VisibleBarCount];
    private readonly float[] _spatialLevels = new float[VisibleBarCount];

    private bool _isActive;
    private bool _isRenderingSubscribed;
    private int _framesSincePush = 999;
    private Color _cachedBarColor = Colors.Transparent;
    private Color _cachedTrackColor = Colors.Transparent;
    private float _primaryPhase;
    private float _secondaryPhase;

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
        Reset();
    }

    public void SetActive(bool isActive)
    {
        _isActive = isActive;

        if (!isActive)
            Reset();

        UpdateRenderingSubscription();
        VisualizerCanvas?.Invalidate();
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
            var combined = (peak * 0.64f) + (rms * 0.36f);
            var boosted = MathF.Pow(Math.Clamp((combined - 0.028f) * 1.08f, 0f, 1f), 1.18f);
            _mappedLevels[i] = Math.Clamp(boosted, 0f, 1f);
        }

        for (int i = 0; i < VisibleBarCount; i++)
        {
            var left = i > 0 ? _mappedLevels[i - 1] : _mappedLevels[i];
            var center = _mappedLevels[i];
            var right = i + 1 < VisibleBarCount ? _mappedLevels[i + 1] : _mappedLevels[i];
            _spatialLevels[i] = (left * 0.24f) + (center * 0.52f) + (right * 0.24f);
        }

        for (int i = 0; i < VisibleBarCount; i++)
        {
            var left = i > 0 ? _spatialLevels[i - 1] : _spatialLevels[i];
            var center = _spatialLevels[i];
            var right = i + 1 < VisibleBarCount ? _spatialLevels[i + 1] : _spatialLevels[i];
            _targetLevels[i] = Math.Clamp((left * 0.14f) + (center * 0.72f) + (right * 0.14f), 0f, 1f);
        }

        _framesSincePush = 0;
        VisualizerCanvas?.Invalidate();
    }

    public void Reset()
    {
        Array.Clear(_targetLevels);
        Array.Clear(_renderedLevels);
        Array.Clear(_mappedLevels);
        Array.Clear(_spatialLevels);
        _framesSincePush = 999;
        _primaryPhase = 0f;
        _secondaryPhase = 0f;
        VisualizerCanvas?.Invalidate();
    }

    private static void OnBarColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (PreviewAudioVisualizer)d;
        self._cachedBarColor = (Color)e.NewValue;
        self.VisualizerCanvas?.Invalidate();
    }

    private static void OnTrackColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (PreviewAudioVisualizer)d;
        self._cachedTrackColor = (Color)e.NewValue;
        self.VisualizerCanvas?.Invalidate();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateRenderingSubscription();
        VisualizerCanvas?.Invalidate();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        UnsubscribeRendering();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        VisualizerCanvas?.Invalidate();
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
        var overallEnergy = 0f;

        for (int i = 0; i < VisibleBarCount; i++)
        {
            if (_framesSincePush > 2)
                _targetLevels[i] *= 0.84f;

            var targetEnvelope = _targetLevels[i];
            var smoothing = targetEnvelope > _renderedLevels[i] ? 0.52f : 0.3f;
            _renderedLevels[i] += (targetEnvelope - _renderedLevels[i]) * smoothing;
            _renderedLevels[i] = Math.Clamp(_renderedLevels[i], 0f, 1f);
            overallEnergy += _renderedLevels[i];
        }

        overallEnergy /= VisibleBarCount;
        var motionStep = MathF.Max(0f, overallEnergy - 0.01f);
        _primaryPhase = WrapPhase(_primaryPhase + (motionStep * 0.12f));
        _secondaryPhase = WrapPhase(_secondaryPhase + (motionStep * 0.068f));

        VisualizerCanvas?.Invalidate();
    }

    private void VisualizerCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var width = (float)sender.ActualWidth;
        var height = (float)sender.ActualHeight;
        if (width <= 0f || height <= 0f)
            return;

        var ds = args.DrawingSession;
        var activeColor = ResolveColor(_cachedBarColor, "HomeBaselineCardPreviewVisualizerForegroundColor", Colors.White);
        var baseColor = ResolveColor(_cachedTrackColor, "HomeBaselineCardPreviewVisualizerTrackColor", WithAlpha(activeColor, 56));
        if (baseColor.A == 0)
            baseColor = WithAlpha(activeColor, 56);

        var glowColor = Lighten(activeColor, 0.18f);
        var softGlowColor = Blend(baseColor, glowColor, 0.42f);
        var horizonColor = WithAlpha(Blend(baseColor, glowColor, 0.24f), 108);
        var centerY = height * 0.58f;
        var sideInset = MathF.Max(6f, width * 0.02f);
        var drawableWidth = MathF.Max(8f, width - (sideInset * 2f));
        var overallEnergy = AverageRange(_renderedLevels, 0, VisibleBarCount);
        var lowEnergy = AverageRange(_renderedLevels, 0, Math.Max(1, VisibleBarCount / 3));
        var midEnergy = AverageRange(_renderedLevels, VisibleBarCount / 4, Math.Max(1, VisibleBarCount / 2));
        var highEnergy = AverageRange(_renderedLevels, VisibleBarCount / 2, VisibleBarCount - (VisibleBarCount / 2));
        var maxAmplitude = MathF.Max(12f, height * (0.24f + (overallEnergy * 0.16f)));

        DrawWaveGlow(ds, sideInset, drawableWidth, centerY, maxAmplitude, overallEnergy, lowEnergy, midEnergy, highEnergy, 0f, 1.04f, WithAlpha(softGlowColor, 34), 7.5f);
        DrawWaveGlow(ds, sideInset, drawableWidth, centerY, maxAmplitude, overallEnergy, lowEnergy, midEnergy, highEnergy, 0.72f, 0.7f, WithAlpha(softGlowColor, 24), 4.75f);
        DrawWaveGlow(ds, sideInset, drawableWidth, centerY, maxAmplitude, overallEnergy, lowEnergy, midEnergy, highEnergy, -0.94f, -0.56f, WithAlpha(softGlowColor, 16), 3f);

        ds.DrawLine(sideInset, centerY, sideInset + drawableWidth, centerY, horizonColor, 1f);

        DrawWaveLine(ds, sideInset, drawableWidth, centerY, maxAmplitude, overallEnergy, lowEnergy, midEnergy, highEnergy, 0.72f, 0.7f, WithAlpha(Blend(baseColor, glowColor, 0.58f), 84), 1.08f);
        DrawWaveLine(ds, sideInset, drawableWidth, centerY, maxAmplitude, overallEnergy, lowEnergy, midEnergy, highEnergy, -0.94f, -0.56f, WithAlpha(Blend(baseColor, glowColor, 0.52f), 62), 1f);
        DrawWaveLine(ds, sideInset, drawableWidth, centerY, maxAmplitude, overallEnergy, lowEnergy, midEnergy, highEnergy, 0f, 1.04f, WithAlpha(glowColor, 236), 1.82f);
    }

    private void DrawWaveGlow(
        Microsoft.Graphics.Canvas.CanvasDrawingSession ds,
        float startX,
        float width,
        float centerY,
        float amplitude,
        float overallEnergy,
        float lowEnergy,
        float midEnergy,
        float highEnergy,
        float phaseOffset,
        float amplitudeScale,
        Color color,
        float strokeWidth)
    {
        if (strokeWidth <= 0f || color.A == 0)
            return;

        DrawWaveLine(ds, startX, width, centerY, amplitude, overallEnergy, lowEnergy, midEnergy, highEnergy, phaseOffset, amplitudeScale, color, strokeWidth);
    }

    private void DrawWaveLine(
        Microsoft.Graphics.Canvas.CanvasDrawingSession ds,
        float startX,
        float width,
        float centerY,
        float amplitude,
        float overallEnergy,
        float lowEnergy,
        float midEnergy,
        float highEnergy,
        float phaseOffset,
        float amplitudeScale,
        Color color,
        float strokeWidth)
    {
        var previousX = startX;
        var previousY = ComputeWaveY(0f, centerY, amplitude, overallEnergy, lowEnergy, midEnergy, highEnergy, phaseOffset, amplitudeScale);

        for (int sampleIndex = 1; sampleIndex < WaveSampleCount; sampleIndex++)
        {
            var t = sampleIndex / (float)(WaveSampleCount - 1);
            var x = startX + (width * t);
            var y = ComputeWaveY(t, centerY, amplitude, overallEnergy, lowEnergy, midEnergy, highEnergy, phaseOffset, amplitudeScale);
            ds.DrawLine(previousX, previousY, x, y, color, strokeWidth);
            previousX = x;
            previousY = y;
        }
    }

    private float ComputeWaveY(
        float t,
        float centerY,
        float amplitude,
        float overallEnergy,
        float lowEnergy,
        float midEnergy,
        float highEnergy,
        float phaseOffset,
        float amplitudeScale)
    {
        var sampledLevel = Math.Clamp(SampleCatmullRom(_renderedLevels, t), 0f, 1f);
        var ambientFloor = overallEnergy <= 0.02f
            ? 0f
            : (0.006f + (overallEnergy * 0.018f) + (t * 0.003f));
        var envelope = Math.Clamp((sampledLevel * 0.97f) + ambientFloor, 0f, 1f);
        var focus = MathF.Pow(Math.Clamp(MathF.Sin(t * MathF.PI), 0f, 1f), 0.78f);
        var liveEnvelope = MathF.Pow(envelope, 0.95f) * focus;
        var primaryCycles = 1.05f + (lowEnergy * 0.92f);
        var secondaryCycles = 2.2f + (midEnergy * 1.15f);
        var tertiaryCycles = 5.1f + (highEnergy * 1.7f);
        var primary = MathF.Sin((t * primaryCycles * Tau) + _primaryPhase + phaseOffset);
        var secondary = 0.4f * MathF.Sin((t * secondaryCycles * Tau) - (_secondaryPhase * 1.08f) + (phaseOffset * 0.68f));
        var tertiary = 0.16f * MathF.Sin((t * tertiaryCycles * Tau) + (_primaryPhase * 0.55f) - (phaseOffset * 0.42f));
        var motion = primary + secondary + tertiary;
        var displacement = amplitude * amplitudeScale * liveEnvelope * motion * (0.92f + (overallEnergy * 0.2f));
        return centerY - displacement;
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

    private static float AverageRange(float[] values, int start, int count)
    {
        if (values.Length == 0 || count <= 0)
            return 0f;

        start = Math.Clamp(start, 0, values.Length - 1);
        var end = Math.Clamp(start + count, start + 1, values.Length);
        var sum = 0f;
        for (int i = start; i < end; i++)
            sum += values[i];

        return sum / (end - start);
    }

    private static float SampleCatmullRom(float[] values, float t)
    {
        if (values.Length == 0)
            return 0f;

        if (values.Length == 1)
            return values[0];

        t = Math.Clamp(t, 0f, 1f);
        var position = t * (values.Length - 1);
        var index1 = (int)MathF.Floor(position);
        var index2 = Math.Min(values.Length - 1, index1 + 1);
        var index0 = Math.Max(0, index1 - 1);
        var index3 = Math.Min(values.Length - 1, index2 + 1);
        var localT = position - index1;
        var localT2 = localT * localT;
        var localT3 = localT2 * localT;
        var p0 = values[index0];
        var p1 = values[index1];
        var p2 = values[index2];
        var p3 = values[index3];

        var interpolated = 0.5f * (
            (2f * p1) +
            ((-p0 + p2) * localT) +
            ((2f * p0) - (5f * p1) + (4f * p2) - p3) * localT2 +
            ((-p0 + (3f * p1) - (3f * p2) + p3) * localT3));

        return Math.Clamp(interpolated, 0f, 1f);
    }

    private static float WrapPhase(float phase)
    {
        while (phase > Tau)
            phase -= Tau;

        return phase;
    }
}
