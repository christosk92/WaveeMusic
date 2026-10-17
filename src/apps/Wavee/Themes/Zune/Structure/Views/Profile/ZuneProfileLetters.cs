// ── Themes/Zune/Structure/Views/Profile/ZuneProfileLetters.cs — the profile list's letters: the header glyph, the sticky overlay, the A-Z strip (P5 Z7) ──
//
// Zune draws the letters plateless: a light 22 glyph in the tertiary ink (the board's library artist column, `Z-Library-Artists`). The letters'
// ARITHMETIC (`LibraryLetters`: the bands, the extents, the sticky letter and the header positions) stays core: the host reads it.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;
using Wavee.Theming.Roles;
using Z = Wavee.Themes.Zune.ZuneProfileRules;

namespace Wavee.Themes.Zune;

/// <summary>The letter pictures of the profile lists.</summary>
internal static class ZuneProfileLetters
{
    static readonly string[] s_letters = ["A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M", "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z"];

    /// <summary>Letter index (0 = "#", 1..26 = A..Z: <see cref="LibraryLetters.Of"/>'s space) to its glyph.</summary>
    public static string LetterText(int letter) => letter <= 0 || letter > s_letters.Length ? "#" : s_letters[letter - 1];

    /// <summary>The header's glyph: the light display 22, tertiary, a letter (not a name: never re-cased).</summary>
    public static Element Glyph(string text) => new TextEl(text)
    {
        Size = Z.LetterSize, LineHeight = Z.LetterLine, Weight = Z.LetterWeight, FontFamily = R.Type.DisplayFace, Color = Tok.TextTertiary,
        MaxLines = 1, Wrap = TextWrap.NoWrap,
    };

    /// <summary>The same glyph over a BOUND text (a recycled navigator header reads its letter out of its slot: the library's).</summary>
    public static Element Glyph(Prop<string> text) => new TextEl(text)
    {
        Size = Z.LetterSize, LineHeight = Z.LetterLine, Weight = Z.LetterWeight, FontFamily = R.Type.DisplayFace, Color = Tok.TextTertiary,
        MaxLines = 1, Wrap = TextWrap.NoWrap,
    };

    /// <summary>The pinned twin of the inline letter header: a ZStack overlay over the list, pushed by the distance the page derives from the scroll
    /// geometry (a bound transform, so a scroll frame never re-renders it). Always mounted; before any letter is under the top it is simply transparent.</summary>
    public static Element StickyLetter(IReadSignal<int> letter, IReadSignal<float> push) => new BoxEl
    {
        Key = "zune-people-sticky",
        Height = LibraryLetters.HeaderExtent, Direction = 0, AlignItems = FlexAlign.End, HitTestVisible = false,
        AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
        Padding = new Edges4(Spacing.S, 0f, Spacing.S, Spacing.XS),
        Opacity = Prop.Of(() => letter.Value >= 0 ? 1f : 0f),
        Transform = Prop.Of(() => Affine2D.Translation(0f, push.Value)),
        Children =
        [
            new TextEl(Prop.Of(() => LetterText(letter.Value)))
            {
                Size = Z.LetterSize, LineHeight = Z.LetterLine, Weight = Z.LetterWeight, FontFamily = R.Type.DisplayFace, Color = Tok.TextTertiary,
                MaxLines = 1, Wrap = TextWrap.NoWrap,
            },
        ],
    };

    /// <summary>The A-Z strip on the list's right edge: 27 rows of 18 x 13. A present letter is full ink and the one under the viewport top is the accent at
    /// 700 (two stacked runs: <c>Weight</c> is not bindable); an absent letter sits at 30 % and its tap NO-OPS, because <paramref name="jump"/> resolves
    /// through the letters' header index and finds nothing.</summary>
    public static Element JumpStrip(IReadSignal<uint> present, IReadSignal<int> current, Action<int> jump)
    {
        var rows = new Element[LibraryLetters.Count];
        for (int l = 0; l < rows.Length; l++)
        {
            int letter = l;
            Func<bool> here = () => (present.Value & (1u << letter)) != 0;
            Func<bool> now = () => current.Value == letter;
            string text = LetterText(letter);
            rows[l] = new BoxEl
            {
                Width = Z.StripRowW, Height = Z.StripRowH, ZStack = true, Corners = CornerRadius4.All(Z.StripRowRadius),
                // 27 letters must not become 27 tab stops: the strip is a pointer affordance beside a typeahead list.
                Role = AutomationRole.Button, TabStop = false, Cursor = CursorId.Hand, HoverFill = Tok.FillSubtleSecondary,
                Opacity = Prop.Of(() => here() ? 1f : Z.AbsentOpacity), OnClick = () => jump(letter),
                Children =
                [
                    StripInk(text, Z.InkWeight, Prop.Of(() => now() ? ColorF.Transparent : Tok.TextTertiary)),
                    StripInk(text, Z.InkActiveWeight, Prop.Of(() => now() ? Tok.AccentTextPrimary : ColorF.Transparent)),
                ],
            };
        }
        return new BoxEl
        {
            Key = "zune-people-strip",
            Direction = 1, Width = Z.StripW, Shrink = 0f, Justify = FlexJustify.Center,
            Padding = new Edges4(0f, Spacing.XS, 0f, Spacing.XS), Children = rows,
        };
    }

    static TextEl StripInk(string text, ushort weight, Prop<ColorF> ink) => R.Type.MicroMeta(text) with
    {
        Weight = weight, Color = ink, AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Center, Case = TextCase.AsWritten,
    };
}
