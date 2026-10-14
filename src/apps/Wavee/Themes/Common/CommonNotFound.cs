// ── Themes/Common/CommonNotFound.cs — the not-found page, one copy for every theme (themes plan 6.5, T11c) ──────────────────────
//
// From `Shell.UI.cs`'s `NotFoundPage` (T4a moved it into Classic's content state; T11c makes it a Common block). Not a registered key of
// its own: the content state (`Shell.ContentState`, registered by each theme) draws its NotFound arm with this block, so a theme
// keeps its own page box and offline lane and shares the one not-found picture.
//
//   a centred column keyed `page-notfound:<route>|<arg>`: the destination's own glyph, the not-found line, and the one action that
//   always works (Go home). NOT "coming soon": say so.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
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
        Children =
        [
            Icon(glyph, CommonNotFoundRules.GlyphSize, Tok.TextTertiary),
            R.Type.PageHero(Loc.Get(Strings.Nav.PageNotFound)),
            Button.Standard(Loc.Get(Strings.Nav.GoHome), goHome),
        ],
    };
}
