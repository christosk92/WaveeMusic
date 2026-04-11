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

    // Shared band mapping (used by PushLevels before signal extraction)
    private readonly float[] _targetLevels = new float[VisibleBarCount];
    private readonly float[] _mappedLevels = new float[VisibleBarCount];
    private readonly float[] _spatialLevels = new float[VisibleBarCount];

    // Per-signal envelope arrays (target and rendered)
    private readonly float[] _vocalEnvelope = new float[VisibleBarCount];
    private readonly float[] _vocalEnvelopeRendered = new float[VisibleBarCount];
    private readonly float[] _bassEnvelope = new float[VisibleBarCount];
    private readonly float[] _bassEnvelopeRendered = new float[VisibleBarCount];
    private readonly float[] _airEnvelope = new float[VisibleBarCount];
    private readonly float[] _airEnvelopeRendered = new float[VisibleBarCount];

    // Previous-frame bands for transient detection (AirDetail)
    private readonly float[] _previousBands = new float[VisibleBarCount];

    // Per-signal scalar targets and rendered values
    private float _vocalTarget, _vocalRendered;
    private float _bassTarget, _bassRendered;
    private float _airTarget, _airRendered;

    // Per-signal phase accumulators
    private float _vocalPrimaryPhase, _vocalSecondaryPhase;
    private float _bassPrimaryPhase, _bassSecondaryPhase;
    private float _airPrimaryPhase, _airSecondaryPhase;

    private bool _isActive;
    private bool _isPending;
    private bool _isRenderingSubscribed;
    private int _framesSincePush = 999;
    private Color _cachedBarColor = Colors.Transparent;
    private Color _cachedTrackColor = Colors.Transparent;

    // Signal extraction weights (static, allocated once)
    private static readonly float[] BassWeights = { 0.4f, 0.5f, 0.7f, 0.9f, 1.0f, 1.0f, 0.85f, 0.7f };
    private static readonly float[] VocalWeights = { 0.3f, 0.5f, 0.7f, 0.85f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 0.9f, 0.7f, 0.4f };
    private static readonly float[] AirWeights = { 0.7f, 0.9f, 1.0f, 1.0f, 0.85f, 0.6f };

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

        if (!isActive && !_isPending)
            Reset();

        UpdateRenderingSubscription();
        VisualizerCanvas?.Invalidate();
    }

    public void SetPending(bool isPending)
    {
        _isPending = isPending;

        if (!isPending && !_isActive)
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

        ExtractSignals(_targetLevels);

        _framesSincePush = 0;
        VisualizerCanvas?.Invalidate();
    }

    private void ExtractSignals(float[] bands)
    {
        // --- BassBody: bands 0-7 (~50-350 Hz) ---
        float bassRaw = 0f;
        float bassWeightSum = 0f;
        for (int i = 0; i <= 7; i++)
        {
            bassRaw += bands[i] * BassWeights[i];
            bassWeightSum += BassWeights[i];
        }
        bassRaw /= bassWeightSum;
        _bassTarget = bassRaw;

        // Stretch bands 0-7 across 24-slot envelope
        for (int i = 0; i < VisibleBarCount; i++)
        {
            float t = i / (float)(VisibleBarCount - 1);
            float srcPos = t * 7f;
            int srcIdx = Math.Clamp((int)srcPos, 0, 6);
            float frac = srcPos - srcIdx;
            _bassEnvelope[i] = Lerp(bands[srcIdx], bands[Math.Min(srcIdx + 1, 7)], frac);
        }

        // --- VocalPresence: bands 7-18 (~250 Hz - 4.5 kHz) ---
        float vocalRaw = 0f;
        float vocalWeightSum = 0f;
        for (int i = 7; i <= 18; i++)
        {
            vocalRaw += bands[i] * VocalWeights[i - 7];
            vocalWeightSum += VocalWeights[i - 7];
        }
        vocalRaw /= vocalWeightSum;

        // Relative-energy suppression
        float bassEnergy = AverageRange(bands, 0, 6);
        float airEnergy = AverageRange(bands, 19, 5);
        float vocalEnergy = AverageRange(bands, 9, 9);
        float dominanceRatio = vocalEnergy / MathF.Max(0.01f, (bassEnergy + airEnergy) * 0.5f);
        float vocalBoost = Math.Clamp(dominanceRatio - 0.6f, 0f, 1.2f) / 1.2f;
        vocalRaw *= 0.7f + (vocalBoost * 0.3f);
        _vocalTarget = vocalRaw;

        // Stretch bands 7-18 across 24-slot envelope
        for (int i = 0; i < VisibleBarCount; i++)
        {
            float t = i / (float)(VisibleBarCount - 1);
            float srcPos = t * 11f;
            int srcIdx = Math.Clamp((int)srcPos, 0, 10);
            float frac = srcPos - srcIdx;
            _vocalEnvelope[i] = Lerp(bands[7 + srcIdx], bands[7 + Math.Min(srcIdx + 1, 11)], frac);
        }

        // --- AirDetail: bands 18-23 (~4.5-10 kHz) with transient emphasis ---
        float airRaw = 0f;
        float airWeightSum = 0f;
        for (int i = 18; i <= 23; i++)
        {
            airRaw += bands[i] * AirWeights[i - 18];
            airWeightSum += AirWeights[i - 18];
        }
        airRaw /= airWeightSum;

        // Transient emphasis from frame-to-frame spectral change
        float transientSum = 0f;
        for (int i = 18; i <= 23; i++)
        {
            float delta = MathF.Max(0f, bands[i] - _previousBands[i]);
            transientSum += delta;
        }
        float transientBoost = Math.Clamp(transientSum * 2.5f, 0f, 0.4f);
        airRaw = Math.Clamp(airRaw + transientBoost, 0f, 1f);
        _airTarget = airRaw;

        // Stretch bands 18-23 across 24-slot envelope
        for (int i = 0; i < VisibleBarCount; i++)
        {
            float t = i / (float)(VisibleBarCount - 1);
            float srcPos = t * 5f;
            int srcIdx = Math.Clamp((int)srcPos, 0, 4);
            float frac = srcPos - srcIdx;
            _airEnvelope[i] = Lerp(bands[18 + srcIdx], bands[18 + Math.Min(srcIdx + 1, 5)], frac);
        }

        // Store for next frame's transient detection
        Array.Copy(bands, _previousBands, VisibleBarCount);
    }

    public void Complete()
    {
        _framesSincePush = Math.Max(_framesSincePush, 3);
        VisualizerCanvas?.Invalidate();
    }

    public void Reset()
    {
        Array.Clear(_targetLevels);
        Array.Clear(_mappedLevels);
        Array.Clear(_spatialLevels);
        Array.Clear(_vocalEnvelope);
        Array.Clear(_vocalEnvelopeRendered);
        Array.Clear(_bassEnvelope);
        Array.Clear(_bassEnvelopeRendered);
        Array.Clear(_airEnvelope);
        Array.Clear(_airEnvelopeRendered);
        Array.Clear(_previousBands);
        _vocalTarget = 0f; _vocalRendered = 0f;
        _bassTarget = 0f; _bassRendered = 0f;
        _airTarget = 0f; _airRendered = 0f;
        _vocalPrimaryPhase = 0f; _vocalSecondaryPhase = 0f;
        _bassPrimaryPhase = 0f; _bassSecondaryPhase = 0f;
        _airPrimaryPhase = 0f; _airSecondaryPhase = 0f;
        _framesSincePush = 999;
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
        if (!IsLoaded || (!_isActive && !_isPending))
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
        if (!IsLoaded || Visibility != Visibility.Visible)
            return;

        _framesSincePush++;

        if (_isPending && !_isActive)
        {
            var phase = (float)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 4000) / 4000f;
            for (int i = 0; i < VisibleBarCount; i++)
            {
                var position = i / (float)Math.Max(1, VisibleBarCount - 1);
                var pulse = 0.5f + (0.5f * MathF.Sin((position * 4.6f * Tau) + (phase * Tau * 1.7f)));
                var sway = 0.5f + (0.5f * MathF.Sin((position * 1.8f * Tau) - (phase * Tau * 1.2f)));
                var level = 0.12f + (pulse * 0.06f) + (sway * 0.04f);
                _vocalEnvelope[i] = level;
                _bassEnvelope[i] = level * 0.82f;
                _airEnvelope[i] = level * 0.68f;
            }

            _vocalTarget = 0.18f;
            _bassTarget = 0.14f;
            _airTarget = 0.1f;
            _framesSincePush = 0;
        }
        else if (_framesSincePush > 2)
        {
            _vocalTarget *= 0.86f;
            _bassTarget *= 0.90f;
            _airTarget *= 0.78f;
            for (int i = 0; i < VisibleBarCount; i++)
            {
                _vocalEnvelope[i] *= 0.86f;
                _bassEnvelope[i] *= 0.90f;
                _airEnvelope[i] *= 0.78f;
            }
        }

        // Smooth scalar values with per-signal asymmetric alphas
        float vocalAlpha = _vocalTarget > _vocalRendered ? 0.48f : 0.25f;
        _vocalRendered += (_vocalTarget - _vocalRendered) * vocalAlpha;
        _vocalRendered = Math.Clamp(_vocalRendered, 0f, 1f);

        float bassAlpha = _bassTarget > _bassRendered ? 0.30f : 0.12f;
        _bassRendered += (_bassTarget - _bassRendered) * bassAlpha;
        _bassRendered = Math.Clamp(_bassRendered, 0f, 1f);

        float airAlpha = _airTarget > _airRendered ? 0.65f : 0.40f;
        _airRendered += (_airTarget - _airRendered) * airAlpha;
        _airRendered = Math.Clamp(_airRendered, 0f, 1f);

        // Smooth envelope arrays
        for (int i = 0; i < VisibleBarCount; i++)
        {
            float va = _vocalEnvelope[i] > _vocalEnvelopeRendered[i] ? 0.48f : 0.25f;
            _vocalEnvelopeRendered[i] += (_vocalEnvelope[i] - _vocalEnvelopeRendered[i]) * va;
            _vocalEnvelopeRendered[i] = Math.Clamp(_vocalEnvelopeRendered[i], 0f, 1f);

            float ba = _bassEnvelope[i] > _bassEnvelopeRendered[i] ? 0.30f : 0.12f;
            _bassEnvelopeRendered[i] += (_bassEnvelope[i] - _bassEnvelopeRendered[i]) * ba;
            _bassEnvelopeRendered[i] = Math.Clamp(_bassEnvelopeRendered[i], 0f, 1f);

            float aa = _airEnvelope[i] > _airEnvelopeRendered[i] ? 0.65f : 0.40f;
            _airEnvelopeRendered[i] += (_airEnvelope[i] - _airEnvelopeRendered[i]) * aa;
            _airEnvelopeRendered[i] = Math.Clamp(_airEnvelopeRendered[i], 0f, 1f);
        }

        // Per-signal phase accumulation
        float bassMotion = MathF.Max(0f, _bassRendered - 0.03f);
        _bassPrimaryPhase = WrapPhase(_bassPrimaryPhase + (bassMotion * 0.045f));
        _bassSecondaryPhase = WrapPhase(_bassSecondaryPhase + (bassMotion * 0.025f));

        float vocalMotion = MathF.Max(0f, _vocalRendered - 0.035f);
        _vocalPrimaryPhase = WrapPhase(_vocalPrimaryPhase + (vocalMotion * 0.072f));
        _vocalSecondaryPhase = WrapPhase(_vocalSecondaryPhase + (vocalMotion * 0.04f));

        float airMotion = MathF.Max(0f, _airRendered - 0.025f);
        _airPrimaryPhase = WrapPhase(_airPrimaryPhase + (airMotion * 0.11f));
        _airSecondaryPhase = WrapPhase(_airSecondaryPhase + (airMotion * 0.065f));

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

        var overallEnergy = (_vocalRendered + _bassRendered + _airRendered) / 3f;
        var safeAmplitude = MathF.Max(8f, MathF.Min(centerY - 10f, (height - centerY) - 10f));
        var targetAmplitude = MathF.Max(10f, height * (0.13f + (overallEnergy * 0.11f)));
        var maxAmplitude = MathF.Min(safeAmplitude, targetAmplitude);

        // Precompute per-signal cycle counts
        float vocalPrimaryCycles = 1.1f + (_vocalRendered * 0.8f);
        float vocalSecondaryCycles = 2.4f + (_vocalRendered * 1.0f);
        float vocalTertiaryCycles = 4.8f + (_vocalRendered * 1.2f);

        float bassPrimaryCycles = 0.8f + (_bassRendered * 0.6f);
        float bassSecondaryCycles = 1.6f + (_bassRendered * 0.7f);
        float bassTertiaryCycles = 3.2f + (_bassRendered * 0.9f);

        float airPrimaryCycles = 1.5f + (_airRendered * 1.3f);
        float airSecondaryCycles = 3.5f + (_airRendered * 1.8f);
        float airTertiaryCycles = 7.0f + (_airRendered * 2.5f);

        // Glow layers (back to front: air, bass, vocal)
        DrawSignalWaveLine(ds, sideInset, drawableWidth, centerY, maxAmplitude,
            _airRendered, airPrimaryCycles, airSecondaryCycles, airTertiaryCycles,
            _airPrimaryPhase, _airSecondaryPhase, 0.52f, _airEnvelopeRendered,
            WithAlpha(softGlowColor, 16), 3f);

        DrawSignalWaveLine(ds, sideInset, drawableWidth, centerY, maxAmplitude,
            _bassRendered, bassPrimaryCycles, bassSecondaryCycles, bassTertiaryCycles,
            _bassPrimaryPhase, _bassSecondaryPhase, 0.78f, _bassEnvelopeRendered,
            WithAlpha(softGlowColor, 24), 5f);

        DrawSignalWaveLine(ds, sideInset, drawableWidth, centerY, maxAmplitude,
            _vocalRendered, vocalPrimaryCycles, vocalSecondaryCycles, vocalTertiaryCycles,
            _vocalPrimaryPhase, _vocalSecondaryPhase, 1.0f, _vocalEnvelopeRendered,
            WithAlpha(softGlowColor, 34), 7.5f);

        // Horizon line
        ds.DrawLine(sideInset, centerY, sideInset + drawableWidth, centerY, horizonColor, 1f);

        // Signal lines (back to front: air, bass, vocal)
        DrawSignalWaveLine(ds, sideInset, drawableWidth, centerY, maxAmplitude,
            _airRendered, airPrimaryCycles, airSecondaryCycles, airTertiaryCycles,
            _airPrimaryPhase, _airSecondaryPhase, 0.52f, _airEnvelopeRendered,
            WithAlpha(Blend(baseColor, glowColor, 0.52f), 62), 0.9f);

        DrawSignalWaveLine(ds, sideInset, drawableWidth, centerY, maxAmplitude,
            _bassRendered, bassPrimaryCycles, bassSecondaryCycles, bassTertiaryCycles,
            _bassPrimaryPhase, _bassSecondaryPhase, 0.78f, _bassEnvelopeRendered,
            WithAlpha(Blend(baseColor, glowColor, 0.58f), 84), 1.2f);

        DrawSignalWaveLine(ds, sideInset, drawableWidth, centerY, maxAmplitude,
            _vocalRendered, vocalPrimaryCycles, vocalSecondaryCycles, vocalTertiaryCycles,
            _vocalPrimaryPhase, _vocalSecondaryPhase, 1.0f, _vocalEnvelopeRendered,
            WithAlpha(glowColor, 236), 1.82f);
    }

    private void DrawSignalWaveLine(
        Microsoft.Graphics.Canvas.CanvasDrawingSession ds,
        float startX,
        float width,
        float centerY,
        float amplitude,
        float signalLevel,
        float primaryCycles,
        float secondaryCycles,
        float tertiaryCycles,
        float primaryPhase,
        float secondaryPhase,
        float amplitudeScale,
        float[] envelope,
        Color color,
        float strokeWidth)
    {
        if (strokeWidth <= 0f || color.A == 0)
            return;

        var previousX = startX;
        var previousY = ComputeSignalWaveY(0f, centerY, amplitude, signalLevel,
            primaryCycles, secondaryCycles, tertiaryCycles,
            primaryPhase, secondaryPhase, amplitudeScale, envelope);

        for (int sampleIndex = 1; sampleIndex < WaveSampleCount; sampleIndex++)
        {
            var t = sampleIndex / (float)(WaveSampleCount - 1);
            var x = startX + (width * t);
            var y = ComputeSignalWaveY(t, centerY, amplitude, signalLevel,
                primaryCycles, secondaryCycles, tertiaryCycles,
                primaryPhase, secondaryPhase, amplitudeScale, envelope);
            ds.DrawLine(previousX, previousY, x, y, color, strokeWidth);
            previousX = x;
            previousY = y;
        }
    }

    private static float ComputeSignalWaveY(
        float t,
        float centerY,
        float amplitude,
        float signalLevel,
        float primaryCycles,
        float secondaryCycles,
        float tertiaryCycles,
        float primaryPhase,
        float secondaryPhase,
        float amplitudeScale,
        float[] envelope)
    {
        var sampledLevel = Math.Clamp(SampleCatmullRom(envelope, t), 0f, 1f);
        var ambientFloor = signalLevel <= 0.02f
            ? 0f
            : (0.002f + (signalLevel * 0.008f));
        var env = Math.Clamp((sampledLevel * 0.94f) + ambientFloor, 0f, 1f);
        var focus = MathF.Pow(Math.Clamp(MathF.Sin(t * MathF.PI), 0f, 1f), 0.78f);
        var liveEnvelope = MathF.Pow(env, 0.95f) * focus;

        var primary = MathF.Sin((t * primaryCycles * Tau) + primaryPhase);
        var secondary = 0.4f * MathF.Sin((t * secondaryCycles * Tau) - (secondaryPhase * 1.08f));
        var tertiary = 0.16f * MathF.Sin((t * tertiaryCycles * Tau) + (primaryPhase * 0.55f));
        var motion = MathF.Tanh((primary + secondary + tertiary) * 0.82f);

        var displacement = amplitude * amplitudeScale * liveEnvelope * motion
                           * (0.82f + (signalLevel * 0.12f));
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

    private static float Lerp(float a, float b, float t)
        => a + ((b - a) * t);

    private static float WrapPhase(float phase)
    {
        while (phase > Tau)
            phase -= Tau;

        return phase;
    }
}
