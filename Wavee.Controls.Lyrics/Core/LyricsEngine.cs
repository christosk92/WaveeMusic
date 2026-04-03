using Wavee.Controls.Lyrics.Enums;
using Wavee.Controls.Lyrics.Extensions;
using Wavee.Controls.Lyrics.Helper;
using Wavee.Controls.Lyrics.Helper.Lyrics;
using Wavee.Controls.Lyrics.Hooks;
using Wavee.Controls.Lyrics.Models;
using Wavee.Controls.Lyrics.Models.Lyrics;
using Wavee.Controls.Lyrics.Models.Settings;
using Wavee.Controls.Lyrics.Renderer;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.UI;

namespace Wavee.Controls.Lyrics.Core
{
    /// <summary>
    /// Owns all renderers, transitions, helpers, and state for the lyrics render loop.
    /// Built once by NowPlayingCanvas; the canvas delegates Create/Update/Draw/Dispose here.
    /// </summary>
    public sealed class LyricsEngine : IDisposable
    {
        // Canvas control reference (set in CreateResources, used by TriggerRelayout)
        private ICanvasAnimatedControl? _control;

        private readonly TranslationSettings _translationSettings = new();

        // --- Renderers ---
        private readonly LyricsRenderer _lyricsRenderer = new();
        private readonly CoverBackgroundRenderer _coverRenderer = new();
        private readonly EdgeFadeMaskRenderer _edgeFadeMaskRenderer = new();
        private readonly List<IBackgroundRenderer> _backgroundRenderers;
        private readonly PureColorBackgroundRenderer _pureColorRenderer = new();
        private readonly FluidBackgroundRenderer _fluidRenderer = new();
        private readonly SpectrumRenderer _spectrumRenderer = new();
        private readonly SnowRenderer _snowRenderer = new();
        private readonly FogRenderer _fogRenderer = new();
        private readonly RaindropRenderer _raindropRenderer = new();

        // --- Helpers ---
        private readonly LyricsSynchronizer _synchronizer = new();
        private readonly LyricsAnimator _animator = new();
        private readonly SpectrumAnalyzer _spectrumAnalyzer = new();

        // --- Transitions ---
        private readonly ValueTransition<Color> _immersiveBgColorTransition = new(
            initialValue: Colors.Transparent,
            defaultTotalDuration: 0.3f,
            interpolator: (from, to, progress) => Helper.ColorHelper.GetInterpolatedColor(progress, from, to)
        );
        private readonly ValueTransition<double> _immersiveBgOpacityTransition = new(
            initialValue: 1f,
            EasingHelper.GetInterpolatorByEasingType<double>(EasingType.Sine),
            defaultTotalDuration: 0.3f
        );
        private readonly ValueTransition<Color> _accentColor1Transition = new(
            initialValue: Colors.Transparent,
            defaultTotalDuration: 0.3f,
            interpolator: (from, to, progress) => Helper.ColorHelper.GetInterpolatedColor(progress, from, to)
        );
        private readonly ValueTransition<Color> _accentColor2Transition = new(
            initialValue: Colors.Transparent,
            defaultTotalDuration: 0.3f,
            interpolator: (from, to, progress) => Helper.ColorHelper.GetInterpolatedColor(progress, from, to)
        );
        private readonly ValueTransition<Color> _accentColor3Transition = new(
            initialValue: Colors.Transparent,
            defaultTotalDuration: 0.3f,
            interpolator: (from, to, progress) => Helper.ColorHelper.GetInterpolatedColor(progress, from, to)
        );
        private readonly ValueTransition<Color> _accentColor4Transition = new(
            initialValue: Colors.Transparent,
            defaultTotalDuration: 0.3f,
            interpolator: (from, to, progress) => Helper.ColorHelper.GetInterpolatedColor(progress, from, to)
        );
        private readonly ValueTransition<double> _canvasYScrollTransition = new(
            initialValue: 0f,
            EasingHelper.GetInterpolatorByEasingType<double>(EasingType.Sine),
            defaultTotalDuration: 0.3f
        );
        private readonly ValueTransition<double> _mouseYScrollTransition = new(
            initialValue: 0f,
            EasingHelper.GetInterpolatorByEasingType<double>(EasingType.Sine),
            defaultTotalDuration: 0.3f
        );

        private CompositionRenderer _compositionRenderer = new();
        private SpoutTextureHook _spoutHook = new();

        // --- Mutable state ---
        private TimeSpan _songPositionWithOffset;
        private TimeSpan _songPosition;

        private double _renderLyricsStartX;
        private double _renderLyricsStartY;
        private double _renderLyricsWidth;
        private double _renderLyricsHeight;
        private double _renderLyricsOpacity;

        private LyricsWindowStatus? _lyricsWindowStatus;
        private Rect _albumArtRect;
        private LyricsData? _lyricsData;
        private SongInfo _songInfo = new() { Album = "", Artist = "", Title = "", DurationMs = 0 };
        private byte[]? _albumArtBytes;
        private bool _isPlaying;
        private int _positionOffsetMs;

        private Point _mousePosition;
        private int _mouseHoverLineIndex = -1;
        private bool _isMouseInLyricsArea;
        private bool _isMousePressing;
        private bool _isMouseScrolling;

        private List<RenderLyricsLine>? _renderLyricsLines;
        private Color _clearColor = Colors.Transparent;

        private DirtyFlags _dirty = DirtyFlags.Layout;
        private RenderContext? _renderContext;

        private int _primaryPlayingLineIndex;
        private (int Start, int End) _visibleRange;
        private double _canvasTargetScrollOffset;

        // --- Read-only accessors (consumed by NowPlayingCanvas computed properties) ---
        public TimeSpan SongPosition => _songPosition;
        public double CurrentCanvasYScroll => _canvasYScrollTransition.Value;
        public double ActualLyricsHeight => LyricsLayoutManager.CalculateActualHeight(_renderLyricsLines);
        public int CurrentHoveringLineIndex => _mouseHoverLineIndex;

        // --- Events ---
        public event EventHandler<TimeSpan>? SeekRequested;

        public LyricsEngine()
        {
            _backgroundRenderers =
            [
                _pureColorRenderer,
                _coverRenderer,
                _fluidRenderer,
                _spectrumRenderer,
                _snowRenderer,
                _fogRenderer,
                _raindropRenderer,
            ];
        }

        // ================================================================
        // State setters — called by NowPlayingCanvas.OnDependencyPropertyChanged
        // ================================================================

        public void SetSettings(LyricsWindowStatus settings)
        {
            _lyricsWindowStatus = settings;
            _dirty |= DirtyFlags.Layout;
            UpdatePalette();
        }

        public void SetAlbumArtRect(Rect rect) => _albumArtRect = rect;

        public void SetLyricsStartX(double x) { _renderLyricsStartX = x; _dirty |= DirtyFlags.Layout; }
        public void SetLyricsStartY(double y) { _renderLyricsStartY = y; _dirty |= DirtyFlags.Layout; }
        public void SetLyricsWidth(double w) { _renderLyricsWidth = w; _dirty |= DirtyFlags.Layout; }
        public void SetLyricsHeight(double h) { _renderLyricsHeight = h; _dirty |= DirtyFlags.Layout; }
        public void SetLyricsOpacity(double o) { _renderLyricsOpacity = o; _dirty |= DirtyFlags.Layout; }

        public void SetMouseScrollOffset(double offset) => _mouseYScrollTransition.Start(offset);
        public void SetMousePosition(Point p) => _mousePosition = p;
        public void SetIsMouseInLyricsArea(bool v) => _isMouseInLyricsArea = v;
        public void SetIsMousePressing(bool v) => _isMousePressing = v;

        public void SetIsMouseScrolling(bool newValue, bool oldValue)
        {
            _isMouseScrolling = newValue;
            if (newValue != oldValue)
                _dirty |= DirtyFlags.MouseScrolling;
        }

        // ================================================================
        // Public API — called by consuming code via NowPlayingCanvas
        // ================================================================

        public void FireSeekIfHovering()
        {
            if (_mouseHoverLineIndex >= 0 && _renderLyricsLines != null
                && _mouseHoverLineIndex < _renderLyricsLines.Count)
            {
                var line = _renderLyricsLines[_mouseHoverLineIndex];
                SeekRequested?.Invoke(this, TimeSpan.FromMilliseconds(line.StartMs));
            }
        }

        public void SetLyricsData(LyricsData? lyricsData)
        {
            _lyricsData = lyricsData;
            _synchronizer.Reset();
            _dirty |= DirtyFlags.Layout;
        }

        public void SetSongInfo(SongInfo songInfo)
        {
            _songInfo = songInfo;
            ResetPlaybackState();
        }

        public void SetPosition(TimeSpan position)
        {
            _songPosition = position;
            _songPositionWithOffset = position + TimeSpan.FromMilliseconds(_positionOffsetMs);
        }

        public void SetIsPlaying(bool isPlaying) => _isPlaying = isPlaying;

        public void SetClearColor(Color color) => _clearColor = color;

        public void SetAlbumArtBytes(byte[]? imageBytes)
        {
            _albumArtBytes = imageBytes;
            _ = ReloadCoverBackgroundResourcesAsync();
        }

        public void SetPositionOffset(int positionOffsetMs)
        {
            _positionOffsetMs = positionOffsetMs;
            _songPositionWithOffset = _songPosition + TimeSpan.FromMilliseconds(_positionOffsetMs);
        }

        public void SetNowPlayingPalette(NowPlayingPalette nowPlayingPalette)
        {
            if (_lyricsWindowStatus == null) return;
            _lyricsWindowStatus.WindowPalette = nowPlayingPalette;
            UpdatePalette();
        }

        // ================================================================
        // Canvas callbacks
        // ================================================================

        public void CreateResources(CanvasAnimatedControl sender, CanvasCreateResourcesEventArgs args)
        {
            _control = sender;

            _compositionRenderer?.Dispose();

            var tasks = new Task[]
            {
                ReloadCoverBackgroundResourcesAsync()
            };
            args.TrackAsyncAction(Task.WhenAll(tasks).AsAsyncAction());

            foreach (var renderer in _backgroundRenderers)
                renderer.LoadResources(sender);

            InitSpectrumAnalyzer();
            InitSpoutHook(sender);

            _dirty |= DirtyFlags.Layout;
            TriggerRelayout();
        }

        public void Update(ICanvasAnimatedControl sender, CanvasAnimatedUpdateEventArgs args)
        {
            _control = sender;
            if (_lyricsWindowStatus == null) return;

            var lyricsBg = _lyricsWindowStatus.LyricsBackgroundSettings;
            var lyricsStyle = _lyricsWindowStatus.LyricsStyleSettings;
            var lyricsEffect = _lyricsWindowStatus.LyricsEffectSettings;
            TimeSpan elapsedTime = args.Timing.ElapsedTime;

            _accentColor1Transition.Update(elapsedTime);
            _accentColor2Transition.Update(elapsedTime);
            _accentColor3Transition.Update(elapsedTime);
            _accentColor4Transition.Update(elapsedTime);

            _immersiveBgOpacityTransition.Update(elapsedTime);
            _immersiveBgColorTransition.Update(elapsedTime);

            UpdatePlaybackState(elapsedTime);

            TriggerRelayout();

            #region UpdatePlayingLineIndex

            int primaryPlayingIndex = _synchronizer.GetCurrentLineIndex(_songPositionWithOffset.TotalMilliseconds, _renderLyricsLines);
            bool isPrimaryPlayingLineChanged = primaryPlayingIndex != _primaryPlayingLineIndex;
            _primaryPlayingLineIndex = primaryPlayingIndex;

            #endregion

            #region UpdateTargetScrollOffset

            if (isPrimaryPlayingLineChanged || (_dirty & DirtyFlags.Layout) != 0)
            {
                var targetScroll = LyricsLayoutManager.CalculateTargetScrollOffset(_renderLyricsLines, _primaryPlayingLineIndex);
                if (targetScroll.HasValue) _canvasTargetScrollOffset = targetScroll.Value;

                if ((_dirty & DirtyFlags.Layout) != 0)
                {
                    _canvasYScrollTransition.JumpTo(_canvasTargetScrollOffset);
                }
                else
                {
                    _canvasYScrollTransition.SetDurationMs(lyricsEffect.LyricsScrollDuration);
                    _canvasYScrollTransition.SetInterpolator(EasingHelper.GetInterpolatorByEasingType<double>(lyricsEffect.LyricsScrollEasingType, lyricsEffect.LyricsScrollEasingMode));
                    _canvasYScrollTransition.Start(_canvasTargetScrollOffset);
                }
            }
            _canvasYScrollTransition.Update(elapsedTime);

            #endregion

            _mouseYScrollTransition.Update(elapsedTime);

            _mouseHoverLineIndex = LyricsLayoutManager.FindMouseHoverLineIndex(
                _renderLyricsLines,
                _isMouseInLyricsArea,
                _mousePosition,
                _canvasYScrollTransition.Value + _mouseYScrollTransition.Value,
                _renderLyricsHeight,
                lyricsStyle.PlayingLineTopOffset / 100.0
            );

            _visibleRange = LyricsLayoutManager.CalculateVisibleRange(
                _renderLyricsLines,
                _canvasYScrollTransition.Value + _mouseYScrollTransition.Value,
                _renderLyricsStartY,
                _renderLyricsHeight,
                sender.Size.Height,
                lyricsStyle.PlayingLineTopOffset / 100.0
            );

            var maxRange = LyricsLayoutManager.CalculateMaxRange(_renderLyricsLines);

            _animator.UpdateLines(
                _renderLyricsLines,
                _isMouseScrolling ? maxRange.Start : _visibleRange.Start,
                _isMouseScrolling ? maxRange.End : _visibleRange.End,
                _primaryPlayingLineIndex,
                sender.Size.Height,
                _canvasTargetScrollOffset,
                lyricsStyle.PlayingLineTopOffset / 100.0,
                _lyricsWindowStatus.LyricsStyleSettings,
                _lyricsWindowStatus.LyricsEffectSettings,
                _canvasYScrollTransition,
                _lyricsWindowStatus.WindowPalette,
                elapsedTime,
                _isMouseScrolling,
                (_dirty & DirtyFlags.Layout) != 0,
                isPrimaryPlayingLineChanged,
                (_dirty & DirtyFlags.MouseScrolling) != 0,
                (_dirty & DirtyFlags.Palette) != 0,
                _songPositionWithOffset.TotalMilliseconds
            );

            // Capture dirty flags before clearing
            bool frameLayoutChanged = (_dirty & DirtyFlags.Layout) != 0;
            bool framePaletteChanged = (_dirty & DirtyFlags.Palette) != 0;
            bool frameMouseScrollingChanged = (_dirty & DirtyFlags.MouseScrolling) != 0;

            if (!_lyricsWindowStatus.ShowLyricsCard)
            {
                _lyricsRenderer.CalculateLyrics3DMatrix(
                    lyricsStyle: lyricsStyle,
                    lyricsEffect: lyricsEffect,
                    lyricsX: _renderLyricsStartX,
                    lyricsY: _renderLyricsStartY,
                    lyricsWidth: _renderLyricsWidth,
                    lyricsHeight: _renderLyricsHeight,
                    frameLayoutChanged
                );
            }

            _dirty = DirtyFlags.None;

            if (_spectrumAnalyzer.IsCapturing)
            {
                _spectrumAnalyzer.UpdateSmoothSpectrum();
            }

            if (_lyricsWindowStatus.IsEdgeFeatheringEnabled)
            {
                _edgeFadeMaskRenderer.Update(
                    sender,
                    (float)sender.Size.Width,
                    (float)sender.Size.Height,
                    _lyricsWindowStatus.EdgeFeatheringLeft,
                    _lyricsWindowStatus.EdgeFeatheringTop,
                    _lyricsWindowStatus.EdgeFeatheringRight,
                    _lyricsWindowStatus.EdgeFeatheringBottom
                );
            }

            // Compute overlay color/opacity for this frame
            Color frameOverlayColor;
            double frameOverlayOpacity;
            if (_lyricsWindowStatus.IsAdaptToEnvironment)
            {
                frameOverlayColor = _immersiveBgColorTransition.Value;
                frameOverlayOpacity = _immersiveBgOpacityTransition.Value * lyricsBg.PureColorOverlayOpacity / 100.0;
            }
            else
            {
                frameOverlayColor = _accentColor1Transition.Value;
                frameOverlayOpacity = lyricsBg.PureColorOverlayOpacity / 100.0;
            }

            // Build per-frame context
            _renderContext = new RenderContext
            {
                Control = sender,
                CanvasSize = sender.Size,
                Elapsed = elapsedTime,
                SongPositionMs = _songPositionWithOffset.TotalMilliseconds,
                SongDurationMs = _songInfo.DurationMs,
                LyricsStartX = _renderLyricsStartX,
                LyricsStartY = _renderLyricsStartY,
                LyricsWidth = _renderLyricsWidth,
                LyricsHeight = _renderLyricsHeight,
                LyricsOpacity = _renderLyricsOpacity,
                AlbumArtRect = _albumArtRect,
                CanvasScrollOffset = _canvasYScrollTransition.Value,
                UserScrollOffset = _mouseYScrollTransition.Value,
                PlayingLineTopOffsetFactor = lyricsStyle.PlayingLineTopOffset / 100.0,
                Lines = _renderLyricsLines,
                PrimaryPlayingLineIndex = _primaryPlayingLineIndex,
                VisibleRange = _visibleRange,
                MouseHoverLineIndex = _mouseHoverLineIndex,
                IsMousePressing = _isMousePressing,
                IsMouseScrolling = _isMouseScrolling,
                Settings = _lyricsWindowStatus,
                Palette = _lyricsWindowStatus.WindowPalette,
                SpectrumData = _spectrumAnalyzer.SmoothSpectrum,
                SpectrumBarCount = _spectrumAnalyzer.BarCount,
                BassEnergy = _spectrumAnalyzer.CurrentBassEnergy,
                AccentColor1 = _accentColor1Transition.Value,
                AccentColor2 = _accentColor2Transition.Value,
                AccentColor3 = _accentColor3Transition.Value,
                AccentColor4 = _accentColor4Transition.Value,
                OverlayColor = frameOverlayColor,
                OverlayOpacity = frameOverlayOpacity,
                IsLayoutChanged = frameLayoutChanged,
                IsPaletteChanged = framePaletteChanged,
                IsMouseScrollingChanged = frameMouseScrollingChanged,
                IsPlayingLineChanged = isPrimaryPlayingLineChanged,
            };

            // Update all background renderers via interface
            foreach (var renderer in _backgroundRenderers)
                renderer.Update(_renderContext);

            if (!_lyricsWindowStatus.ShowLyricsCard)
            {
                _lyricsRenderer.Update(_spectrumAnalyzer.CurrentBassEnergy, lyricsEffect.LyricsBreathingIntensity);
            }
        }

        public void Draw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
        {
            if (_lyricsWindowStatus == null || _renderContext == null) return;

            var ctx = _renderContext;
            var ds = args.DrawingSession;

            var lyricsStyle = ctx.Settings.LyricsStyleSettings;
            var albumStyle = ctx.Settings.AlbumArtLayoutSettings;
            var lyricsBg = ctx.Settings.LyricsBackgroundSettings;

            var bounds = new Rect(0, 0, sender.Size.Width, sender.Size.Height);

            if (ctx.Settings.IsSpoutOutputEnabled)
            {
                var finalTexture = _compositionRenderer.Render(
                     sender,
                     sender.Size,
                     sender.Dpi,
                     Colors.Transparent,
                     (ds) =>
                     {
                         DrawCoreWithEdgeFeatheringHandled(sender, ds, bounds, ctx, lyricsStyle, albumStyle, lyricsBg);
                     });

                ds.DrawImage(finalTexture);

                _spoutHook?.SendTexture(finalTexture);
            }
            else
            {
                DrawCoreWithEdgeFeatheringHandled(sender, ds, bounds, ctx, lyricsStyle, albumStyle, lyricsBg);
            }

            if (_lyricsWindowStatus.ShowDebugOverlay)
            {
                DrawDebugOverlay(sender, args, ds);
            }
        }

        // ================================================================
        // Private methods
        // ================================================================

        private void DrawCore(ICanvasAnimatedControl sender, CanvasDrawingSession ds,
            Rect bounds, RenderContext ctx,
            LyricsStyleSettings lyricsStyle, AlbumArtAreaStyleSettings albumStyle, LyricsBackgroundSettings lyricsBg)
        {
            ds.Clear(_clearColor);

            foreach (var renderer in _backgroundRenderers)
            {
                if (renderer.IsEnabled)
                    renderer.Draw(ds, ctx);
            }

            if (!ctx.Settings.ShowLyricsCard)
            {
                _lyricsRenderer.Draw(
                    control: sender,
                    ds: ds,
                    lines: ctx.Lines,
                    mouseHoverLineIndex: ctx.MouseHoverLineIndex,
                    isMousePressing: ctx.IsMousePressing,
                    startVisibleIndex: ctx.VisibleRange.Start,
                    endVisibleIndex: ctx.VisibleRange.End,
                    lyricsX: ctx.LyricsStartX,
                    lyricsY: ctx.LyricsStartY,
                    lyricsWidth: ctx.LyricsWidth,
                    lyricsHeight: ctx.LyricsHeight,
                    userScrollOffset: ctx.UserScrollOffset,
                    lyricsOpacity: ctx.LyricsOpacity,
                    playingLineTopOffsetFactor: ctx.PlayingLineTopOffsetFactor,
                    windowStatus: ctx.Settings,
                    currentProgressMs: ctx.SongPositionMs);
            }
        }

        private void DrawCoreWithEdgeFeatheringHandled(ICanvasAnimatedControl sender, CanvasDrawingSession ds,
            Rect bounds, RenderContext ctx,
            LyricsStyleSettings lyricsStyle, AlbumArtAreaStyleSettings albumStyle, LyricsBackgroundSettings lyricsBg)
        {
            if (ctx.Settings.IsEdgeFeatheringEnabled && _edgeFadeMaskRenderer.Brush != null)
            {
                using (ds.CreateLayer(_edgeFadeMaskRenderer.Brush))
                {
                    DrawCore(sender, ds, bounds, ctx, lyricsStyle, albumStyle, lyricsBg);
                }
            }
            else
            {
                DrawCore(sender, ds, bounds, ctx, lyricsStyle, albumStyle, lyricsBg);
            }
        }

        private void DrawDebugOverlay(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args, CanvasDrawingSession ds)
        {
            string debugText =
                $"Spout Sender : {_spoutHook?.SenderName ?? "Disabled"}\n" +
                $"FPS          : {(1.0 / args.Timing.ElapsedTime.TotalSeconds):00.0} (Avg: {args.Timing.UpdateCount / args.Timing.TotalTime.TotalSeconds:00.0})\n" +
                $"----------------------------------------\n" +
                $"Render Pos   : [{(int)_renderLyricsStartX}, {(int)_renderLyricsStartY}]\n" +
                $"Render Size  : [{(int)_renderLyricsWidth} x {(int)_renderLyricsHeight}]\n" +
                $"Actual Height: {LyricsLayoutManager.CalculateActualHeight(_renderLyricsLines)} px\n" +
                $"----------------------------------------\n" +
                $"Playing Line : #{_primaryPlayingLineIndex}\n" +
                $"Hover Line   : #{_mouseHoverLineIndex}\n" +
                $"Visible Range: [{_visibleRange.Start} -> {_visibleRange.End}]\n" +
                $"Total Lines  : {LyricsLayoutManager.CalculateMaxRange(_renderLyricsLines).End + 1}\n" +
                $"----------------------------------------\n" +
                $"Time         : {_songPosition:mm\\:ss} / {TimeSpan.FromMilliseconds(_songInfo.DurationMs):mm\\:ss}\n" +
                $"Y Offset     : {_canvasYScrollTransition.Value:0.00}\n" +
                $"User Scroll  : {_mouseYScrollTransition.Value:0.00}";

            using (var format = new Microsoft.Graphics.Canvas.Text.CanvasTextFormat
            {
                FontFamily = "Consolas",
                FontSize = 13,
                VerticalAlignment = Microsoft.Graphics.Canvas.Text.CanvasVerticalAlignment.Top,
                HorizontalAlignment = Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Left
            })
            using (var layout = new Microsoft.Graphics.Canvas.Text.CanvasTextLayout(ds, debugText, format, 2000f, 2000f))
            {
                var textBounds = layout.LayoutBounds;
                float padding = 12f;
                float margin = 12f;

                float boxWidth = (float)textBounds.Width + (padding * 2);
                float boxHeight = (float)textBounds.Height + (padding * 2);
                float canvasWidth = (float)sender.Size.Width;

                float xPos = canvasWidth - boxWidth - margin;
                float yPos = margin;

                var bgRect = new Rect(xPos, yPos, boxWidth, boxHeight);

                ds.FillRectangle(bgRect, Color.FromArgb(128, 10, 10, 10));
                ds.DrawRectangle(bgRect, Colors.Cyan, 1.0f);
                ds.DrawTextLayout(layout, new Vector2(xPos + padding, yPos + padding), Colors.GreenYellow);
            }
        }

        private void InitSpoutHook(CanvasAnimatedControl sender)
        {
            _spoutHook?.Dispose();
            _spoutHook = new SpoutTextureHook();
            _spoutHook.Initialize(sender.Device, $"BetterLyrics ({_lyricsWindowStatus?.GetHashCode()})");
        }

        private void InitSpectrumAnalyzer()
        {
            if (_lyricsWindowStatus == null) return;
            var lyricsBg = _lyricsWindowStatus.LyricsBackgroundSettings;

            _spectrumAnalyzer.BarCount = lyricsBg.SpectrumCount;
            _spectrumAnalyzer.Sensitivity = lyricsBg.SpectrumSensitivity;

            _spectrumAnalyzer.StartCapture();
        }

        private void DisposeSpectrumAnalyzer()
        {
            if (_spectrumAnalyzer.IsCapturing)
            {
                _spectrumAnalyzer.StopCapture();
            }
            _spectrumAnalyzer.Dispose();
        }

        private void TriggerRelayout()
        {
            if ((_dirty & DirtyFlags.Layout) == 0 || _lyricsWindowStatus == null || _control == null) return;

            DisposeRenderLyricsLines();
            _renderLyricsLines = _lyricsData?.LyricsLines.Select(x => new RenderLyricsLine(x)).ToList();

            if (_renderLyricsLines == null) return;

            LyricsLayoutManager.CalculateLanes(_renderLyricsLines);

            LyricsLayoutManager.MeasureAndArrange(
                resourceCreator: _control,
                lines: _renderLyricsLines,
                status: _lyricsWindowStatus,
                translationSettings: _translationSettings,
                canvasWidth: _control.Size.Width,
                canvasHeight: _control.Size.Height,
                lyricsWidth: _renderLyricsWidth,
                lyricsHeight: _renderLyricsHeight
            );
        }

        private void UpdatePlaybackState(TimeSpan elapsedTime)
        {
            if (_isPlaying)
            {
                _songPosition += elapsedTime;
                _songPositionWithOffset = _songPosition + TimeSpan.FromMilliseconds(_positionOffsetMs);
            }
        }

        private void ResetPlaybackState()
        {
            _songPosition = TimeSpan.Zero;
            _songPositionWithOffset = TimeSpan.FromMilliseconds(_positionOffsetMs);
            _synchronizer.Reset();
            _primaryPlayingLineIndex = 0;
        }

        private async Task ReloadCoverBackgroundResourcesAsync()
        {
            if (_control == null) return;

            try
            {
                var imageBytes = _albumArtBytes;
                if (imageBytes == null || imageBytes.Length == 0) return;

                using (var localMemoryStream = new InMemoryRandomAccessStream())
                {
                    using (var writer = new DataWriter(localMemoryStream.GetOutputStreamAt(0)))
                    {
                        writer.WriteBytes(imageBytes);
                        await writer.StoreAsync();
                    }

                    localMemoryStream.Seek(0);

                    if (_control == null) return;

                    CanvasBitmap bitmap = await CanvasBitmap.LoadAsync(_control, localMemoryStream);
                    _coverRenderer.SetCoverBitmap(bitmap);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ReloadCoverBackgroundResourcesAsync: {ex}");
            }
        }

        private void DisposeRenderLyricsLines()
        {
            if (_renderLyricsLines != null)
            {
                foreach (var item in _renderLyricsLines)
                {
                    item.DisposeTextGeometry();
                    item.DisposeTextLayout();
                    item.DisposeCaches();
                }
                _renderLyricsLines = null;
            }
        }

        private void UpdatePalette()
        {
            if (_lyricsWindowStatus == null) return;

            var palette = _lyricsWindowStatus.WindowPalette;
            _immersiveBgColorTransition.Start(palette.UnderlayColor);
            _accentColor1Transition.Start(palette.AccentColor1);
            _accentColor2Transition.Start(palette.AccentColor2);
            _accentColor3Transition.Start(palette.AccentColor3);
            _accentColor4Transition.Start(palette.AccentColor4);

            _dirty |= DirtyFlags.Palette;
        }

        public void Dispose()
        {
            foreach (var renderer in _backgroundRenderers)
                renderer.Dispose();

            DisposeRenderLyricsLines();
            DisposeSpectrumAnalyzer();

            _edgeFadeMaskRenderer.Dispose();

            _compositionRenderer?.Dispose();
            _spoutHook?.Dispose();
        }
    }
}
