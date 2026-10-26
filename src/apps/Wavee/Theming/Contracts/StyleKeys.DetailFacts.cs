// ── Theming/Contracts/StyleKeys.DetailFacts.cs — the facts bento's style key (themes line p6) ────────────────────────────────────
//
// The facts bento (the insights panel's body) is a STYLE, not a view: its core components keep their hooks, settles, lens verbs and
// tooltips and call the pictures of the style while they render (see `DetailFactsStyle`). They read it through `ListStyleLookup`.

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class StyleKeys
{
    /// <summary>The facts bento's pictures: the section, its head, the hero figure, the charts' styles, the artist row, the disclosure,
    /// the blend's legend and bar, the pill and the prose (<see cref="DetailFactsStyle"/>). Read by <c>User.FactsPanel</c>'s components.</summary>
    public static readonly StyleKey<DetailFactsStyle> DetailFacts = new("Detail.Facts");
}
