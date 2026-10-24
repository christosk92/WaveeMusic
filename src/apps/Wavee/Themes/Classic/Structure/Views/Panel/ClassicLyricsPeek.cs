// ── Themes/Classic/Structure/Views/Panel/ClassicLyricsPeek.cs — Classic's `Player.LyricsPeek` (themes plan 6.1, P3) ─────────────────
//
// From `Shell/Lyrics.UI.cs` (`PeekReel`): the now-playing body's two-row lyrics reel — the sung line over a faded peek at the next, on a
// 3-DIP accent spine — or, while a video plays, the one-row "no sync during video" note; both open the panel on lyrics. Absent (a bare
// zero-size box, no spine, no gap) unless the playing track has a timed document.
//
// The core hosts (`Lyrics.PeekHost`, `PeekReel`, [CoreHost]) keep the document, the 100 ms clock that runs only while the reel shows, and
// the active / peek pair, and decide the face. Each row is keyed by its line index, so a new sung line flips in from below while the old
// one flips out above (the reel's clip hides the travel).

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's lyrics peek.</summary>
internal static class ClassicLyricsPeek
{
    static readonly EnterExit FlipIn = new(Dy: ClassicHeroLayout.PeekRowH, Opacity: 0f, Active: true);
    static readonly EnterExit FlipOut = new(Dy: -ClassicHeroLayout.PeekRowH, Opacity: 0f, Active: true);

    /// <summary>The registered template of <see cref="ViewKeys.LyricsPeek"/>.</summary>
    public static Element Template(LyricsPeekProps props, in ViewContext ctx) => props.Face switch
    {
        LyricsPeekFace.Reel => Reel(props),
        LyricsPeekFace.Note => UnsyncedNote(props.OpenLyrics),
        _ => new BoxEl(),
    };

    static Element Reel(LyricsPeekProps props) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Stretch, Gap = Spacing.S,
        Height = ClassicHeroLayout.PeekReelH,
        Cursor = CursorId.Hand,
        OnClick = props.OpenLyrics,
        Role = AutomationRole.Button,
        Focusable = true,
        Children =
        [
            new BoxEl { Width = ClassicHeroLayout.PeekSpineW, Shrink = 0f, Corners = CornerRadius4.All(ClassicHeroLayout.PeekSpineRadius), Fill = Tok.AccentDefault },
            new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f,
                Height = ClassicHeroLayout.PeekReelH, ClipToBounds = true,
                Children = [Slot(props.Active, props.ActiveText, faded: false), Slot(props.Peek, props.PeekText, faded: true)],
            },
        ],
    };

    /// <summary>One 56-DIP row: the spine at tertiary, half height, and the sync note — still opening the rail.</summary>
    static Element UnsyncedNote(Action openLyrics) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
        Height = ClassicHeroLayout.PeekRowH,
        Cursor = CursorId.Hand,
        OnClick = openLyrics,
        Role = AutomationRole.Button,
        Focusable = true,
        Children =
        [
            new BoxEl { Width = ClassicHeroLayout.PeekSpineW, Shrink = 0f, Height = ClassicHeroLayout.PeekNoteSpineH, Corners = CornerRadius4.All(ClassicHeroLayout.PeekSpineRadius), Fill = Tok.TextTertiary },
            new TextEl(Loc.Get(Strings.Player.LyricsSyncUnavailableDuringVideo))
            {
                Size = ClassicHeroLayout.PeekNoteText, Color = Tok.TextTertiary, Grow = 1f, Basis = 0f, MinWidth = 0f,
                Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            },
        ],
    };

    static Element Slot(int index, string? text, bool faded)
    {
        Element inner = text is not null
            ? new BoxEl
            {
                Key = "l:" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Height = ClassicHeroLayout.PeekRowH, MinWidth = 0f, Grow = 1f, Basis = 0f,
                Direction = 1, Justify = FlexJustify.Center,
                Enter = FlipIn, Exit = FlipOut,
                Transition = MotionTok.ControlFast,
                Children =
                [
                    R.Type.NpvLyric(text) with
                    {
                        Color = Tok.TextPrimary, MaxLines = 2, MinWidth = 0f,
                        Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis,
                    },
                ],
            }
            : new BoxEl { Height = ClassicHeroLayout.PeekRowH };
        return new BoxEl
        {
            Height = ClassicHeroLayout.PeekRowH, ClipToBounds = true, MinWidth = 0f,
            Opacity = faded ? ClassicHeroLayout.PeekOpacity : 1f,
            Children = [inner],
        };
    }
}
