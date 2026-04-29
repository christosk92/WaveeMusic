using Wavee.Controls.Lyrics.Models;
using Wavee.Controls.Lyrics.Models.Lyrics;
using Wavee.Controls.Lyrics.Models.Settings;
using Microsoft.Graphics.Canvas.UI.Xaml;
using System;
using System.Collections.Generic;
using Windows.Foundation;
using Windows.UI;

namespace Wavee.Controls.Lyrics.Renderer
{
    // readonly struct passed `in` everywhere — zero-alloc per-frame. Holds a few
    // hundred bytes; renderers consume it via in-ref so we never copy it field-by-field.
    public readonly struct RenderContext
    {
        // Canvas
        public required ICanvasAnimatedControl Control { get; init; }
        public required Size CanvasSize { get; init; }

        // Time
        public required TimeSpan Elapsed { get; init; }
        public required double SongPositionMs { get; init; }
        public required double SongDurationMs { get; init; }

        // Layout
        public required double LyricsStartX { get; init; }
        public required double LyricsStartY { get; init; }
        public required double LyricsWidth { get; init; }
        public required double LyricsHeight { get; init; }
        public required double LyricsOpacity { get; init; }
        public required Rect AlbumArtRect { get; init; }

        // Scroll
        public required double CanvasScrollOffset { get; init; }
        public required double UserScrollOffset { get; init; }
        public required double PlayingLineTopOffsetFactor { get; init; }

        // Lines
        public required IList<RenderLyricsLine>? Lines { get; init; }
        public required int PrimaryPlayingLineIndex { get; init; }
        public required (int Start, int End) VisibleRange { get; init; }

        // Input
        public required int MouseHoverLineIndex { get; init; }
        public required bool IsMousePressing { get; init; }
        public required bool IsMouseScrolling { get; init; }

        // Config
        public required LyricsWindowStatus Settings { get; init; }
        public required NowPlayingPalette Palette { get; init; }

        // Audio
        public required float[]? SpectrumData { get; init; }
        public required int SpectrumBarCount { get; init; }
        public required float BassEnergy { get; init; }

        // Accent colors (post-transition)
        public required Color AccentColor1 { get; init; }
        public required Color AccentColor2 { get; init; }
        public required Color AccentColor3 { get; init; }
        public required Color AccentColor4 { get; init; }
        public required Color OverlayColor { get; init; }
        public required double OverlayOpacity { get; init; }

        // Dirty flags for this frame
        public required bool IsLayoutChanged { get; init; }
        public required bool IsPaletteChanged { get; init; }
        public required bool IsMouseScrollingChanged { get; init; }
        public required bool IsPlayingLineChanged { get; init; }
    }
}
