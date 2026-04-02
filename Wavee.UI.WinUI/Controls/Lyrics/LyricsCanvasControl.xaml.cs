using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Wavee.UI.WinUI.Controls.Lyrics.Helpers;
using Wavee.UI.WinUI.Controls.Lyrics.Models;
using Wavee.UI.WinUI.Controls.Lyrics.Renderer;
using Wavee.UI.WinUI.ViewModels;
using Windows.Foundation;
using Windows.UI;

namespace Wavee.UI.WinUI.Controls.Lyrics;

public sealed partial class LyricsCanvasControl : UserControl
{
    private LyricsViewModel? _vm;

    // ── BetterLyrics render infrastructure ──
    private List<RenderLyricsLine>? _renderLines;
    private readonly LyricsSynchronizer _synchronizer = new();
    private readonly LyricsAnimator _animator = new();
    private readonly LyricsRenderer _renderer = new();
    private readonly LyricsEffectSettings _effectSettings = new();
    private readonly LyricsStyleSettings _styleSettings = new();
    private NowPlayingPalette _palette = NowPlayingPalette.Default;

    // ── Canvas scroll transition ──
    private ValueTransition<double> _canvasYScrollTransition;

    // ── State ──
    private int _primaryPlayingLineIndex = -1;
    private int _prevPlayingLineIndex = -1;
    private int _startVisibleIndex;
    private int _endVisibleIndex;
    private double _targetYScrollOffset;
    private bool _needsLayout;
    private bool _isPaused = true;
    private volatile bool _isPlaying;
    private bool _isPrimaryPlayingLineChanged;
    private bool _isLayoutChanged;
    private bool _isArtThemeColorsChanged;

    // ── Render-thread time tracking ──
    private double _songPositionMs;
    private long _syncPositionTicks;
    private volatile bool _positionSyncNeeded;

    // ── Canvas dimensions ──
    private double _canvasWidth;
    private double _canvasHeight;
    private double _lyricsWidth;
    private double _lyricsHeight;
    private const double LyricsXPadding = 20;

    // ── Timer for syncing position from UI thread ──
    private DispatcherTimer? _syncTimer;

    public LyricsCanvasControl()
    {
        InitializeComponent();

        _canvasYScrollTransition = new ValueTransition<double>(
            0,
            EasingHelper.GetInterpolatorByEasingType<double>(
                _effectSettings.LyricsScrollEasingType,
                _effectSettings.LyricsScrollEasingMode),
            _effectSettings.LyricsScrollDuration / 1000.0);

        _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _syncTimer.Tick += OnSyncTimerTick;

        Unloaded += (_, _) =>
        {
            _syncTimer?.Stop();
            _syncTimer = null;
        };
    }

    public void SetViewModel(LyricsViewModel vm)
    {
        if (_vm != null)
        {
            _vm.ActiveLineChanged -= OnActiveLineChanged;
            _vm.LyricsLoaded -= OnLyricsLoaded;
        }

        _vm = vm;
        _vm.ActiveLineChanged += OnActiveLineChanged;
        _vm.LyricsLoaded += OnLyricsLoaded;
    }

    public void SetPaused(bool paused)
    {
        _isPaused = paused;
        _isPlaying = !paused;

        if (Canvas != null)
            Canvas.Paused = paused;

        if (paused)
        {
            _syncTimer?.Stop();
        }
        else
        {
            SyncPositionFromViewModel();
            _syncTimer?.Start();
        }
    }

    /// <summary>
    /// Update the color palette from album art. Call from UI thread.
    /// </summary>
    public void UpdatePalette(NowPlayingPalette palette)
    {
        _palette = palette;
        _isArtThemeColorsChanged = true;
    }

    // ── UI thread sync ──

    private void OnSyncTimerTick(object? sender, object e)
    {
        if (_vm == null) return;
        _isPlaying = !_isPaused;
        SyncPositionFromViewModel();
    }

    private void SyncPositionFromViewModel()
    {
        if (_vm == null) return;
        var posMs = _vm.GetInterpolatedPositionMs();
        Interlocked.Exchange(ref _syncPositionTicks, (long)(posMs * 100.0));
        _positionSyncNeeded = true;
    }

    private void OnActiveLineChanged(int newIndex, int prevIndex)
    {
        SyncPositionFromViewModel();
    }

    private void OnLyricsLoaded()
    {
        BuildRenderLines();
        _needsLayout = true;
        _primaryPlayingLineIndex = -1;
        _prevPlayingLineIndex = -1;
        _songPositionMs = 0;
        _synchronizer.Reset();
        SyncPositionFromViewModel();
    }

    /// <summary>
    /// Bridge from ViewModel's LyricsLineItem list to BetterLyrics' RenderLyricsLine list.
    /// </summary>
    private void BuildRenderLines()
    {
        DisposeRenderLines();

        if (_vm == null || _vm.Lines.Count == 0)
        {
            _renderLines = null;
            return;
        }

        var lines = new List<RenderLyricsLine>(_vm.Lines.Count);

        for (int i = 0; i < _vm.Lines.Count; i++)
        {
            var item = _vm.Lines[i];
            var lyricsLine = new LyricsLine
            {
                PrimaryText = string.IsNullOrWhiteSpace(item.Words) ? "♪" : item.Words,
                StartMs = (int)item.StartTimeMs,
                EndMs = (int)item.EndTimeMs,
            };

            // Build syllables from word timings
            if (item.WordTimings is { Count: > 0 })
            {
                lyricsLine.IsPrimaryHasRealSyllableInfo = true;
                int textPos = 0;

                foreach (var word in item.WordTimings)
                {
                    var wordStart = lyricsLine.PrimaryText.IndexOf(word.Text, textPos, StringComparison.Ordinal);
                    if (wordStart < 0) wordStart = textPos;

                    var syllable = new BaseLyrics
                    {
                        Text = word.Text,
                        StartMs = (int)word.StartMs,
                        EndMs = (int)word.EndMs,
                        StartIndex = wordStart,
                    };
                    lyricsLine.PrimarySyllables.Add(syllable);
                    textPos = wordStart + word.Text.Length;
                }
            }
            else
            {
                // No word timings — single syllable for entire line
                lyricsLine.IsPrimaryHasRealSyllableInfo = false;
                lyricsLine.PrimarySyllables.Add(new BaseLyrics
                {
                    Text = lyricsLine.PrimaryText,
                    StartMs = (int)item.StartTimeMs,
                    EndMs = (int)item.EndTimeMs,
                    StartIndex = 0,
                });
            }

            lines.Add(new RenderLyricsLine(lyricsLine));
        }

        _renderLines = lines;
        LyricsLayoutManager.CalculateLanes(_renderLines);
    }

    // ── Win2D lifecycle ──

    private void Canvas_CreateResources(CanvasAnimatedControl sender, CanvasCreateResourcesEventArgs args)
    {
        _needsLayout = true;
    }

    private void Canvas_Update(ICanvasAnimatedControl sender, CanvasAnimatedUpdateEventArgs args)
    {
        if (_renderLines == null || _renderLines.Count == 0) return;

        _canvasWidth = sender.Size.Width;
        _canvasHeight = sender.Size.Height;
        _lyricsWidth = Math.Max(_canvasWidth - LyricsXPadding * 2, 100);
        _lyricsHeight = _canvasHeight;

        // Layout pass
        if (_needsLayout)
        {
            var animControl = sender as ICanvasAnimatedControl;
            if (animControl != null)
            {
                LyricsLayoutManager.MeasureAndArrange(
                    animControl, _renderLines,
                    _styleSettings, _effectSettings,
                    _canvasWidth, _canvasHeight,
                    _lyricsWidth, _lyricsHeight);
            }
            _needsLayout = false;
            _isLayoutChanged = true;
        }

        // Sync position from UI thread
        if (_positionSyncNeeded)
        {
            _songPositionMs = Interlocked.Read(ref _syncPositionTicks) / 100.0;
            _positionSyncNeeded = false;
        }

        // Advance song position every frame
        if (_isPlaying)
            _songPositionMs += args.Timing.ElapsedTime.TotalMilliseconds;

        // Find current playing line
        _primaryPlayingLineIndex = _synchronizer.GetCurrentLineIndex(_songPositionMs, _renderLines);
        _isPrimaryPlayingLineChanged = _primaryPlayingLineIndex != _prevPlayingLineIndex;

        if (_isPrimaryPlayingLineChanged)
            _prevPlayingLineIndex = _primaryPlayingLineIndex;

        // Calculate scroll offset
        var playingLineTopOffsetFactor = _styleSettings.PlayingLineTopOffset / 100.0;

        if (_isPrimaryPlayingLineChanged || _isLayoutChanged)
        {
            var newScrollOffset = LyricsLayoutManager.CalculateTargetScrollOffset(_renderLines, _primaryPlayingLineIndex);
            if (newScrollOffset.HasValue)
            {
                _targetYScrollOffset = newScrollOffset.Value;
                _canvasYScrollTransition.SetDuration(_effectSettings.LyricsScrollDuration / 1000.0);
                _canvasYScrollTransition.Start(_targetYScrollOffset);
            }
        }

        _canvasYScrollTransition.Update(args.Timing.ElapsedTime);

        // Calculate visible range
        var scrollOffset = _canvasYScrollTransition.Value;
        var (start, end) = LyricsLayoutManager.CalculateVisibleRange(
            _renderLines, scrollOffset, 0, _lyricsHeight, _canvasHeight, playingLineTopOffsetFactor);
        _startVisibleIndex = start;
        _endVisibleIndex = end;

        // Animate lines
        _animator.UpdateLines(
            _renderLines,
            _startVisibleIndex, _endVisibleIndex,
            _primaryPlayingLineIndex,
            _canvasHeight,
            _targetYScrollOffset,
            playingLineTopOffsetFactor,
            _styleSettings, _effectSettings,
            _canvasYScrollTransition,
            _palette,
            args.Timing.ElapsedTime,
            isMouseScrolling: false,
            isLayoutChanged: _isLayoutChanged,
            isPrimaryPlayingLineChanged: _isPrimaryPlayingLineChanged,
            isMouseScrollingChanged: false,
            isArtThemeColorsChanged: _isArtThemeColorsChanged,
            currentPositionMs: _songPositionMs);

        // Reset per-frame flags
        _isLayoutChanged = false;
        _isPrimaryPlayingLineChanged = false;
        _isArtThemeColorsChanged = false;
    }

    private void Canvas_Draw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
    {
        if (_renderLines == null || _renderLines.Count == 0) return;

        var playingLineTopOffsetFactor = _styleSettings.PlayingLineTopOffset / 100.0;
        var scrollOffset = _canvasYScrollTransition.Value;

        _renderer.Draw(
            sender,
            args.DrawingSession,
            _renderLines,
            mouseHoverLineIndex: -1,
            isMousePressing: false,
            _startVisibleIndex,
            _endVisibleIndex,
            lyricsX: LyricsXPadding,
            lyricsY: 0,
            lyricsWidth: _lyricsWidth,
            lyricsHeight: _lyricsHeight,
            userScrollOffset: scrollOffset,
            lyricsOpacity: 1.0,
            playingLineTopOffsetFactor: playingLineTopOffsetFactor,
            _effectSettings,
            _styleSettings,
            currentProgressMs: _songPositionMs);
    }

    // ── Click to seek ──

    private void Canvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_vm == null || _renderLines == null || _renderLines.Count == 0) return;

        var point = e.GetCurrentPoint((UIElement)sender);
        var mousePos = new Point(point.Position.X, point.Position.Y);
        var playingLineTopOffsetFactor = _styleSettings.PlayingLineTopOffset / 100.0;
        var scrollOffset = _canvasYScrollTransition.Value;

        var hitIndex = LyricsLayoutManager.FindMouseHoverLineIndex(
            _renderLines, true, mousePos, scrollOffset, _lyricsHeight, playingLineTopOffsetFactor);

        if (hitIndex >= 0 && hitIndex < _vm.Lines.Count)
            _vm.Lines[hitIndex].SeekCommand.Execute(null);
    }

    // ── Cleanup ──

    private void DisposeRenderLines()
    {
        if (_renderLines != null)
        {
            foreach (var line in _renderLines)
            {
                line.DisposeTextLayout();
                line.DisposeTextGeometry();
                line.DisposeCaches();
            }
            _renderLines = null;
        }
    }

    public void Dispose()
    {
        _syncTimer?.Stop();
        _syncTimer = null;

        if (_vm != null)
        {
            _vm.ActiveLineChanged -= OnActiveLineChanged;
            _vm.LyricsLoaded -= OnLyricsLoaded;
        }

        DisposeRenderLines();
    }
}
