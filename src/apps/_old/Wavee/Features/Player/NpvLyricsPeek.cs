using System.Collections.Generic;
using System.Threading.Tasks;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Backend.Lyrics;
using Wavee.Core;

namespace Wavee;

/// <summary>Compact NPV lyrics reel: the sung line plus a faded peek at the next, ticking like
/// <see cref="FlipCountdown"/> (full-row slide, no blur). Hidden until a line-timed document is ready. Click opens
/// the lyrics rail. Owns its own fetch + 100 ms clock so the panel does not re-render on position ticks.</summary>
sealed class NpvLyricsPeek : Component
{
    const float RowH = 56f;
    const float SpineW = 3f;
    const float PeekOpacity = 0.38f;
    const float TickMs = 100f;

    static readonly EnterExit FlipIn = new(Dy: RowH, Opacity: 0f, Active: true);
    static readonly EnterExit FlipOut = new(Dy: -RowH, Opacity: 0f, Active: true);

    readonly record struct Pair(int Active, int Peek);

    readonly Signal<Pair> _pair = new(new(int.MinValue, int.MinValue));
    readonly Action _tick;
    PlaybackBridge? _b;
    LyricsDocument? _doc;
    ShellUi? _ui;

    public NpvLyricsPeek() => _tick = Tick;

    public override Element Render()
    {
        var b = UseContext(PlaybackBridge.Slot);
        var svc = UseContext(Services.Slot);
        var ui = UseContext(ShellUi.Slot);
        _b = b;
        _ui = ui;

        // A video is a DIFFERENT EDIT of the song, so the line timings (which belong to the audio edit) no longer point
        // at what is being heard — see LyricsSyncGate. The peek IS the synced line, so there is nothing honest to show:
        // it becomes the note instead, still opening the rail where the unsynced lyrics remain readable.
        bool syncOff = LyricsSyncGate.SyncSuppressed(b?.VideoActive() ?? false);

        var live = b?.Identity.Value.Track;
        string trackId = live?.Id ?? "";
        var docL = UseResource(
            ct => trackId.Length > 0 && svc?.Lyrics is { } lyrics
                ? lyrics.GetLyricsAsync(trackId, ct)
                : Task.FromResult<LyricsDocument?>(null),
            (LyricsDocument?)null, trackId).Loadable;
        var doc = docL.Value.Value;
        bool show = LyricsPeekClock.ShouldShow(doc);
        _doc = show && !syncOff ? doc : null;   // no document ⇒ the clock has nothing to advance

        // The 100ms clock is the sync. Stopping it is the suppression — not a render-time branch over a still-ticking
        // signal, which would keep waking the panel to compute a line it must not use.
        UseInterval(_tick, TickMs, enabled: show && !syncOff);
        UseLayoutEffect(() => { if (_doc is not null) Tick(); }, DepKey.From(show));

        var pair = _pair.Value;
        if (!show) return new BoxEl();
        if (syncOff) return UnsyncedNote(ui);
        if (pair.Active == int.MinValue)
        {
            var seed = LyricsPeekClock.ActiveAndPeek(doc, b?.PositionMs.Peek() ?? 0L);
            pair = new Pair(seed.Active, seed.Peek);
        }

        var lines = doc!.Lines;
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Stretch, Gap = Spacing.S,
            Height = RowH * 2f,
            Cursor = ui is null ? (CursorId?)null : CursorId.Hand,
            OnClick = ui is null ? null : OpenLyrics,
            Role = AutomationRole.Button,
            Focusable = ui is not null,
            Children =
            [
                new BoxEl
                {
                    Width = SpineW, Shrink = 0f,
                    Corners = CornerRadius4.All(1.5f),
                    Fill = Tok.AccentDefault,
                },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f,
                    Height = RowH * 2f, ClipToBounds = true,
                    Children =
                    [
                        Slot(pair.Active, lines, faded: false),
                        Slot(pair.Peek, lines, faded: true),
                    ],
                },
            ],
        };
    }

    /// <summary>The peek's stand-in while a video suppresses sync: says why the sung line is missing, and still opens
    /// the rail (the lyrics themselves are unchanged and stay readable there). Sized to ONE row rather than the reel's
    /// two, so the panel does not hold a half-empty gap.</summary>
    Element UnsyncedNote(ShellUi? ui) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
        Height = RowH,
        Cursor = ui is null ? (CursorId?)null : CursorId.Hand,
        OnClick = ui is null ? null : OpenLyrics,
        Role = AutomationRole.Button,
        Focusable = ui is not null,
        Children =
        [
            new BoxEl
            {
                Width = SpineW, Shrink = 0f, Height = RowH * 0.5f,
                Corners = CornerRadius4.All(1.5f),
                Fill = Tok.TextTertiary,
            },
            new TextEl(Loc.Get(Strings.Player.LyricsSyncUnavailableDuringVideo))
            {
                Size = 12f, Color = Tok.TextTertiary, Grow = 1f, Basis = 0f, MinWidth = 0f,
                Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            },
        ],
    };

    void OpenLyrics() => _ui?.Toggle(RailMode.Lyrics);

    void Tick()
    {
        var doc = _doc;
        if (doc is null)
        {
            _pair.SetIfChanged(new(-1, -1));
            return;
        }
        var next = LyricsPeekClock.ActiveAndPeek(doc, _b?.PositionMs.Peek() ?? 0L);
        _pair.SetIfChanged(new(next.Active, next.Peek));
    }

    static Element Slot(int index, IReadOnlyList<LyricLine> lines, bool faded)
    {
        Element inner = (uint)index < (uint)lines.Count
            ? new BoxEl
            {
                Key = "l:" + index.ToString(),
                Height = RowH, MinWidth = 0f, Grow = 1f, Basis = 0f,
                Direction = 1, Justify = FlexJustify.Center,
                Enter = FlipIn, Exit = FlipOut,
                Transition = MotionTok.ControlFast,
                Children =
                [
                    WaveeType.NpvLyric(lines[index].Text) with
                    {
                        Color = Tok.TextPrimary,
                        MaxLines = 2,
                        MinWidth = 0f,
                        Wrap = TextWrap.Wrap,
                        Trim = TextTrim.CharacterEllipsis,
                    },
                ],
            }
            : new BoxEl { Height = RowH };
        return new BoxEl
        {
            Height = RowH, ClipToBounds = true, MinWidth = 0f,
            Opacity = faded ? PeekOpacity : 1f,
            Children = [inner],
        };
    }
}
