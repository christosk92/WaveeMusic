// ── Themes/Classic/Structure/Views/Pages/ClassicLetterKit.cs — the letters: the sticky overlay, the letter plate and the A–Z strip (themes plan 6.1, T10c2) ──
//
// The library navigator (`ClassicLibraryPage.Nav`) and the Following / Followers list pages share these three pieces and the letter glyphs. They lived in
// `Entities/User.UI.cs` until the last page moved (T10c2). The letters' ARITHMETIC (`LibraryLetters`: the bands, the extents, the sticky letter and the
// header positions) stays core: the hosts read it.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace Wavee.Themes.Classic;

/// <summary>The shared letter pictures.</summary>
internal static class ClassicLetterKit
{
    /// <summary>The ink/underline fade the A–Z strip shares with the rail words: the 83-ms WinUI BrushTransition.</summary>
    static FluentGpu.Animation.MotionTokenDef RailInkFade => FluentGpu.Animation.MotionTok.ControlFaster;

    static readonly string[] s_letters = ["A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M", "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z"];

    /// <summary>Letter index (0 = "#", 1..26 = A..Z — <see cref="LibraryLetters.Of"/>'s space) → its glyph.</summary>
    public static string LetterText(int letter) => letter <= 0 || letter > s_letters.Length ? "#" : s_letters[letter - 1];

    /// <summary>The pinned twin of the navigator's inline letter header (`ClassicLibraryPage.LetterHeader`): a ZStack overlay over the list, pushed by the distance the
    /// page derives from the scroll geometry (Recents' <c>StickyDayHeader</c> idiom — a bound transform, so a scroll frame
    /// never re-renders it). Always mounted; before any letter is under the top it is simply transparent, which is cheaper
    /// than collapsing it (a presence flip would relayout the overlay).</summary>
    public static Element StickyLetter(IReadSignal<int> letter, IReadSignal<float> push) => new BoxEl
    {
        // "nav:sticky" (§3.3 / A1): a stable key on the ROOT so the navigator host can place this overlay as a KEYED
        // sibling of the keyed list — a key on a component's single-child slot is inert (RC1), but this element is
        // returned straight into a `Children` array, where `ReconcileChildren` honors it.
        Key = "nav:sticky",
        Height = LibraryLetters.HeaderExtent, Direction = 0, AlignItems = FlexAlign.End, HitTestVisible = false,
        AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
        Padding = new Edges4(Spacing.S, 0f, Spacing.S, Spacing.XS),
        Opacity = Prop.Of(() => letter.Value >= 0 ? 1f : 0f),
        Transform = Prop.Of(() => Affine2D.Translation(0f, push.Value)),
        Children = [LetterPlate(Prop.Of(() => LetterText(letter.Value)))],
    };

    public static Element LetterPlate(Prop<string> text) => new BoxEl
    {
        Padding = new Edges4(ClassicLetterRules.PlatePadX, ClassicLetterRules.PlatePadY, ClassicLetterRules.PlatePadX, ClassicLetterRules.PlatePadY), Corners = CornerRadius4.All(ClassicLetterRules.PlateRadius), Fill = Tok.FillCardDefault,
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
        Children = [new TextEl(text) { Size = ClassicLetterRules.PlateSize, LineHeight = ClassicLetterRules.PlateLine, Weight = ClassicLetterRules.PlateWeight, CharSpacing = ClassicLetterRules.PlateSpacing, Color = Tok.TextTertiary }],
    };

    /// <summary>The A–Z strip on the navigator's right edge: 27 rows of 18 × 13. A present letter is full ink and the one
    /// under the viewport top is accent/700 (two stacked runs again — <c>Weight</c> is not bindable); an absent letter sits
    /// at 30 % and its tap NO-OPS, because <paramref name="jump"/> resolves through <c>LibraryLetters.HeaderFlat</c> and
    /// finds nothing. <paramref name="present"/> is the letters' bitmask, <paramref name="current"/> the sticky letter.</summary>
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
                Width = ClassicLetterRules.StripRowW, Height = ClassicLetterRules.StripRowH, ZStack = true, Corners = CornerRadius4.All(ClassicLetterRules.StripRowRadius),
                // 27 letters must not become 27 tab stops: the strip is a pointer affordance beside a typeahead list.
                Role = AutomationRole.Button, TabStop = false, Cursor = CursorId.Hand, HoverFill = Tok.FillSubtleSecondary,
                Opacity = Prop.Of(() => here() ? 1f : ClassicLetterRules.AbsentOpacity), Transition = RailInkFade, OnClick = () => jump(letter),
                Children =
                [
                    StripInk(text, ClassicLetterRules.InkWeight, Prop.Of(() => now() ? ColorF.Transparent : Tok.TextTertiary)),
                    StripInk(text, ClassicLetterRules.InkActiveWeight, Prop.Of(() => now() ? Tok.AccentTextPrimary : ColorF.Transparent)),
                ],
            };
        }
        return new BoxEl
        {
            // "nav:strip" (§3.3 / A1): same reasoning as StickyLetter's "nav:sticky" — a stable key on the ROOT so
            // the navigator host can place this strip as a KEYED sibling, not lose it to an inert single-child key.
            Key = "nav:strip",
            Direction = 1, Width = ClassicLetterRules.StripW, Shrink = 0f, Justify = FlexJustify.Center,
            Padding = new Edges4(0f, Spacing.XS, 0f, Spacing.XS), Children = rows,
        };
    }

    static TextEl StripInk(string text, ushort weight, Prop<ColorF> ink) => R.Type.MicroMeta(text) with
    {
        Weight = weight, Color = ink, BrushTransitionMs = Design.Motion.Fast,
        AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Center,
    };
}
