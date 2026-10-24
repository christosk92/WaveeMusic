// ── Themes/Common/CommonNotFound.cs — the not-found page, one copy for every theme (themes plan 6.5, T11c; tiers TC10) ───────────
//
// From `Shell.UI.cs`'s `NotFoundPage` (T4a moved it into Classic's content state; T11c makes it a Common block). Not a registered key of
// its own: the content state (`Shell.ContentState`, registered by each theme) draws its NotFound arm with this block, so a theme
// keeps its own page box and offline lane and shares the one not-found picture.
//
//   a centred column keyed `page-notfound:<route>|<arg>`: the destination's own glyph, the not-found line, and the one action that
//   always works (Go home). NOT "coming soon": say so.
//
// TIERS (TC10): the block keeps its call shape and its three children (the content state hands it a key, a glyph and the Go home verb, and no view context). The line wraps
// inside a narrow side room instead of running off a 300-wide window, and the action is a component that reads the tier through `CommonTierComponent` into a
// `CommonNotFoundLayout`: Go home is a 40-DIP target at Compact. A centred column needs no max width: it is as wide as its widest line.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Common;

/// <summary>The not-found page.</summary>
internal static class CommonNotFound
{
    /// <summary>Nothing claims the route: keep the destination's own glyph, say so, give the one action that always works.</summary>
    public static BoxEl Block(string key, string glyph, Action goHome) => new()
    {
        Key = key,
        Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1, Gap = Spacing.M,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = new Edges4(Spacing.PageNarrow, 0f, Spacing.PageNarrow, 0f),
        Children =
        [
            Icon(glyph, CommonNotFoundRules.GlyphSize, Tok.TextTertiary),
            R.Type.PageHero(Loc.Get(Strings.Nav.PageNotFound)) with { Wrap = TextWrap.Wrap, MinWidth = 0f },
            Embed.Comp(goHome, static () => new GoHomeAction()),
        ],
    };

    /// <summary>The Go home button at the tier's hit target.</summary>
    sealed class GoHomeAction : CommonTierComponent
    {
        public override Element Render()
        {
            var layout = CommonNotFoundLayout.For(ReadTier());
            return CommonTouch.Button(Loc.Get(Strings.Nav.GoHome), UseProps<Action>(), layout.Target);
        }
    }
}
