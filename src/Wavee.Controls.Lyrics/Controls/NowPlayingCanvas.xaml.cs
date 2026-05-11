// 2025/6/23 by Zhe Fang

using Wavee.Controls.Lyrics.Core;
using Wavee.Controls.Lyrics.Models;
using Wavee.Controls.Lyrics.Models.Lyrics;
using Wavee.Controls.Lyrics.Models.Settings;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using Windows.Foundation;
using Windows.UI;

namespace Wavee.Controls.Lyrics.Controls
{
    public sealed partial class NowPlayingCanvas : UserControl
    {
        private readonly LyricsEngine _engine = new();

        // Delegate computed properties to engine
        public TimeSpan SongPosition => _engine.SongPosition;
        public double CurrentCanvasYScroll => _engine.CurrentCanvasYScroll;
        public double ActualLyricsHeight => _engine.ActualLyricsHeight;
        public int CurrentHoveringLineIndex => _engine.CurrentHoveringLineIndex;

        // Bubble engine's SeekRequested event
        public event EventHandler<TimeSpan>? SeekRequested
        {
            add => _engine.SeekRequested += value;
            remove => _engine.SeekRequested -= value;
        }

        // Bubble engine's HoverActionInvoked event (e.g. "Explain line" lightbulb clicks)
        public event EventHandler<int>? HoverActionInvoked
        {
            add => _engine.HoverActionInvoked += value;
            remove => _engine.HoverActionInvoked -= value;
        }

        #region DependencyProperties

        public LyricsWindowStatus? LyricsWindowStatus
        {
            get { return (LyricsWindowStatus?)GetValue(LyricsWindowStatusProperty); }
            set { SetValue(LyricsWindowStatusProperty, value); }
        }

        public static readonly DependencyProperty LyricsWindowStatusProperty =
            DependencyProperty.Register(nameof(LyricsWindowStatus), typeof(LyricsWindowStatus), typeof(NowPlayingCanvas), new PropertyMetadata(null, OnDependencyPropertyChanged));

        public Rect AlbumArtRect
        {
            get { return (Rect)GetValue(AlbumArtRectProperty); }
            set { SetValue(AlbumArtRectProperty, value); }
        }

        public static readonly DependencyProperty AlbumArtRectProperty =
            DependencyProperty.Register(nameof(AlbumArtRect), typeof(Rect), typeof(NowPlayingCanvas), new PropertyMetadata(new Rect(), OnDependencyPropertyChanged));

        public double LyricsStartX
        {
            get { return (double)GetValue(LyricsStartXProperty); }
            set { SetValue(LyricsStartXProperty, value); }
        }

        public static readonly DependencyProperty LyricsStartXProperty =
            DependencyProperty.Register(nameof(LyricsStartX), typeof(double), typeof(NowPlayingCanvas), new PropertyMetadata(0.0, OnDependencyPropertyChanged));

        public double LyricsStartY
        {
            get { return (double)GetValue(LyricsStartYProperty); }
            set { SetValue(LyricsStartYProperty, value); }
        }

        public static readonly DependencyProperty LyricsStartYProperty =
            DependencyProperty.Register(nameof(LyricsStartY), typeof(double), typeof(NowPlayingCanvas), new PropertyMetadata(0.0, OnDependencyPropertyChanged));

        public double LyricsWidth
        {
            get { return (double)GetValue(LyricsWidthProperty); }
            set { SetValue(LyricsWidthProperty, value); }
        }

        public static readonly DependencyProperty LyricsWidthProperty =
            DependencyProperty.Register(nameof(LyricsWidth), typeof(double), typeof(NowPlayingCanvas), new PropertyMetadata(0.0, OnDependencyPropertyChanged));

        public double LyricsHeight
        {
            get { return (double)GetValue(LyricsHeightProperty); }
            set { SetValue(LyricsHeightProperty, value); }
        }

        public static readonly DependencyProperty LyricsHeightProperty =
            DependencyProperty.Register(nameof(LyricsHeight), typeof(double), typeof(NowPlayingCanvas), new PropertyMetadata(0.0, OnDependencyPropertyChanged));

        public double LyricsOpacity
        {
            get { return (double)GetValue(LyricsOpacityProperty); }
            set { SetValue(LyricsOpacityProperty, value); }
        }

        public static readonly DependencyProperty LyricsOpacityProperty =
            DependencyProperty.Register(nameof(LyricsOpacity), typeof(double), typeof(NowPlayingCanvas), new PropertyMetadata(0.0, OnDependencyPropertyChanged));

        public double MouseScrollOffset
        {
            get { return (double)GetValue(MouseScrollOffsetProperty); }
            set { SetValue(MouseScrollOffsetProperty, value); }
        }

        public static readonly DependencyProperty MouseScrollOffsetProperty =
            DependencyProperty.Register(nameof(MouseScrollOffset), typeof(double), typeof(NowPlayingCanvas), new PropertyMetadata(0.0, OnDependencyPropertyChanged));

        public Point MousePosition
        {
            get { return (Point)GetValue(MousePositionProperty); }
            set { SetValue(MousePositionProperty, value); }
        }

        public static readonly DependencyProperty MousePositionProperty =
            DependencyProperty.Register(nameof(MousePosition), typeof(Point), typeof(NowPlayingCanvas), new PropertyMetadata(new Point(0, 0), OnDependencyPropertyChanged));

        public bool IsMouseInLyricsArea
        {
            get { return (bool)GetValue(IsMouseInLyricsAreaProperty); }
            set { SetValue(IsMouseInLyricsAreaProperty, value); }
        }

        public static readonly DependencyProperty IsMouseInLyricsAreaProperty =
            DependencyProperty.Register(nameof(IsMouseInLyricsArea), typeof(bool), typeof(NowPlayingCanvas), new PropertyMetadata(false, OnDependencyPropertyChanged));

        public bool IsMousePressing
        {
            get { return (bool)GetValue(IsMousePressingProperty); }
            set { SetValue(IsMousePressingProperty, value); }
        }

        public static readonly DependencyProperty IsMousePressingProperty =
            DependencyProperty.Register(nameof(IsMousePressing), typeof(bool), typeof(NowPlayingCanvas), new PropertyMetadata(false, OnDependencyPropertyChanged));

        public bool IsMouseScrolling
        {
            get { return (bool)GetValue(IsMouseScrollingProperty); }
            set { SetValue(IsMouseScrollingProperty, value); }
        }

        public static readonly DependencyProperty IsMouseScrollingProperty =
            DependencyProperty.Register(nameof(IsMouseScrolling), typeof(bool), typeof(NowPlayingCanvas), new PropertyMetadata(false, OnDependencyPropertyChanged));

        public bool ShowHoverActionIcon
        {
            get { return (bool)GetValue(ShowHoverActionIconProperty); }
            set { SetValue(ShowHoverActionIconProperty, value); }
        }

        public static readonly DependencyProperty ShowHoverActionIconProperty =
            DependencyProperty.Register(nameof(ShowHoverActionIcon), typeof(bool), typeof(NowPlayingCanvas), new PropertyMetadata(false, OnDependencyPropertyChanged));

        public Color HoverActionIconAccentColor
        {
            get { return (Color)GetValue(HoverActionIconAccentColorProperty); }
            set { SetValue(HoverActionIconAccentColorProperty, value); }
        }

        public static readonly DependencyProperty HoverActionIconAccentColorProperty =
            DependencyProperty.Register(nameof(HoverActionIconAccentColor), typeof(Color), typeof(NowPlayingCanvas), new PropertyMetadata(Color.FromArgb(0xFF, 0xC8, 0x8B, 0xFF), OnDependencyPropertyChanged));

        #endregion

        public NowPlayingCanvas()
        {
            InitializeComponent();
        }

        public void FireSeekIfHovering() => _engine.FireSeekIfHovering();
        public int RefreshHoveringLineIndex() => _engine.RefreshHoveringLineIndex();
        public bool TryRefreshHoveringLine(out int lineIndex, out Rect bounds)
            => _engine.TryRefreshHoveringLine(out lineIndex, out bounds);

        // Hit-test the in-canvas hover-action icon (e.g. "Explain line" lightbulb).
        // Callers should invoke this before FireSeekIfHovering on pointer-press so an
        // icon click doesn't also trigger seek-to-line.
        public bool IsPointOnHoverActionIcon(Point canvasPoint) => _engine.IsPointOnHoverActionIcon(canvasPoint);
        public bool TryInvokeHoverActionAt(Point canvasPoint) => _engine.TryInvokeHoverActionAt(canvasPoint);

        public void SetClearColor(Color color)
        {
            _engine.SetClearColor(color);
            if (Canvas != null)
                Canvas.ClearColor = color;
        }

        private static void OnDependencyPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not NowPlayingCanvas canvas) return;
            var engine = canvas._engine;

            if (e.Property == LyricsWindowStatusProperty)
                engine.SetSettings((LyricsWindowStatus)e.NewValue);
            else if (e.Property == AlbumArtRectProperty)
                engine.SetAlbumArtRect((Rect)e.NewValue);
            else if (e.Property == LyricsStartXProperty)
                engine.SetLyricsStartX(Convert.ToDouble(e.NewValue));
            else if (e.Property == LyricsStartYProperty)
                engine.SetLyricsStartY(Convert.ToDouble(e.NewValue));
            else if (e.Property == LyricsWidthProperty)
                engine.SetLyricsWidth(Convert.ToDouble(e.NewValue));
            else if (e.Property == LyricsHeightProperty)
                engine.SetLyricsHeight(Convert.ToDouble(e.NewValue));
            else if (e.Property == LyricsOpacityProperty)
                engine.SetLyricsOpacity(Convert.ToDouble(e.NewValue));
            else if (e.Property == MouseScrollOffsetProperty)
                engine.SetMouseScrollOffset(Convert.ToDouble(e.NewValue));
            else if (e.Property == MousePositionProperty)
                engine.SetMousePosition((Point)e.NewValue);
            else if (e.Property == IsMouseInLyricsAreaProperty)
                engine.SetIsMouseInLyricsArea((bool)e.NewValue);
            else if (e.Property == IsMousePressingProperty)
                engine.SetIsMousePressing((bool)e.NewValue);
            else if (e.Property == IsMouseScrollingProperty)
                engine.SetIsMouseScrolling((bool)e.NewValue, (bool)e.OldValue);
            else if (e.Property == ShowHoverActionIconProperty)
                engine.ShowHoverActionIcon = (bool)e.NewValue;
            else if (e.Property == HoverActionIconAccentColorProperty)
                engine.HoverActionIconAccentColor = (Color)e.NewValue;
        }

        // Canvas event handlers — delegate to engine
        private void Canvas_Draw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
            => _engine.Draw(sender, args);

        private void Canvas_Update(ICanvasAnimatedControl sender, CanvasAnimatedUpdateEventArgs args)
            => _engine.Update(sender, args);

        private void Canvas_CreateResources(CanvasAnimatedControl sender, CanvasCreateResourcesEventArgs args)
            => _engine.CreateResources(sender, args);

        public void SetRenderingActive(bool isActive)
        {
            if (Canvas == null) return;
            Canvas.Paused = !isActive;
            _engine.SetIsActive(isActive);
        }

        private void UserControl_Unloaded(object sender, RoutedEventArgs e)
        {
            Canvas.Draw -= Canvas_Draw;
            Canvas.Update -= Canvas_Update;
            Canvas.CreateResources -= Canvas_CreateResources;

            Canvas.Paused = true;
            Canvas.RemoveFromVisualTree();
            Canvas = null;

            _engine.Dispose();
        }

        // Public API — passthrough to engine
        public void SetLyricsData(LyricsData? lyricsData) => _engine.SetLyricsData(lyricsData);
        public void SetSongInfo(SongInfo songInfo) => _engine.SetSongInfo(songInfo);
        public void SetPosition(TimeSpan position) => _engine.SetPosition(position);
        public void SetIsPlaying(bool isPlaying) => _engine.SetIsPlaying(isPlaying);
        public void SetAlbumArtBytes(byte[]? imageBytes) => _engine.SetAlbumArtBytes(imageBytes);
        public void SetPositionOffset(int positionOffsetMs) => _engine.SetPositionOffset(positionOffsetMs);
        public void SetNowPlayingPalette(NowPlayingPalette nowPlayingPalette) => _engine.SetNowPlayingPalette(nowPlayingPalette);
    }
}
