// ── Themes/WinUi/Structure/Views/Detail/WinUiDetailShow.cs — WinUI's fixed show header (themes plan 6.2; P6 W6a) ──────────────────────────────────────
//
// A show or an episode page is not a track page: its list is the episodes, so there is no hero inside a table. Its header is the hero's composition drawn
// as a plain block above the episodes (board W-Show): the cover (168; 232 at Huge; 160 centred at Compact) beside the words, bottom-aligned: the eyebrow
// with the page's badges, the title, the publisher line, the rating, the meta, the played ledger and the action row (the page's Primary, then its
// Satellites). The page's own slots (`Detail.FrameSlots`) say what each row holds; this view only arranges them, on the tier's inset. W6b (the show and
// episode pages) refines the rows it owns.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

internal static partial class WinUiDetailFrame
{
    /// <summary>The registered template of <see cref="ViewKeys.DetailShowHeader"/>.</summary>
    public static Element ShowHeaderTemplate(DetailShowHeaderProps p, in ViewContext ctx)
    {
        var tiers = ctx.Tier();
        var tier = WinUiDetailTiers.Of(in tiers);
        var form = WinUiDetailTiers.HeroOf(in tiers);
        var spec = p.Spec;
        var id = spec.Identity;
        var slots = spec.Slots;
        float inset = tier.InsetX;

        bool episode = id.Kind == DetailKind.Episode;
        bool compact = tier.Tier == WidthTier.Compact;
        // W-Show-Compact: a show's Compact header keeps the cover BESIDE the words (128), as no album's does; an episode's stays stacked (W-Episode-Compact).
        if (compact && !episode)
            form = form with
            {
                Stacked = false, Cover = WinUiShowRules.CompactCover, Gap = WinUiShowRules.CompactCoverGap,
                TitleSize = WinUiShowRules.CompactTitle, TitleLine = WinUiShowRules.CompactTitleLine,
            };
        float measure = MathF.Max(0f, (float.IsNaN(p.PageWidth) || p.PageWidth <= 0f ? 0f : p.PageWidth) - 2f * inset - (form.Stacked ? 0f : form.Cover + form.Gap));
        float line = measure > 0f ? measure : float.NaN;
        var align = form.Stacked ? FlexAlign.Center : FlexAlign.Auto;
        var justify = form.Stacked ? FlexJustify.Center : FlexJustify.Start;

        var rows = new List<Element>(8);
        // The eyebrow: "Podcast" with the badges (W-Show); "Episode · <the show>" (W-Episode: the show's link rides the eyebrow).
        var eyebrow = new List<Element>(4);
        if (id.Eyebrow.Length > 0) eyebrow.Add(WinUiDetailKit.EyebrowRun(id.Eyebrow, form));
        if (episode && slots.Attribution is { } showLink)
        {
            if (eyebrow.Count > 0) eyebrow.Add(Dot(form.EyebrowSize, form.EyebrowLine));
            eyebrow.Add(showLink(line));
        }
        if (!episode && slots.Badges is { } badges) eyebrow.Add(badges());
        bool hasEyebrow = (form.Eyebrow || episode) && eyebrow.Count > 0;
        if (hasEyebrow)
            rows.Add(Block("hero-eyebrow", new BoxEl
            {
                Direction = 0, Gap = WinUiShowRules.FactsGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Justify = justify, Children = eyebrow.ToArray(),
            }, late: true));
        rows.Add(Block("hero-title", slots.Title is { } edit ? edit(form.TitleSize, form.TitleLine) : R.Type.DetailHero(id.Title) with
        {
            Size = form.TitleSize, LineHeight = form.TitleLine, Weight = 600, Color = Tok.TextPrimary,
            Wrap = TextWrap.WrapWholeWords, MaxLines = WinUiDetailRules.TitleMaxLines, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            AlignSelf = align,
        }));
        // The facts line (W-Show): the publisher · the rating · "14 episodes · weekly · since 2021", one wrapping line in Body. At Compact the publisher
        // takes its own line over the rest (W-Show-Compact).
        var facts = new List<Element>(6);
        Element MetaText(string text) => Ui.Body(text) with
        {
            Color = Tok.TextSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
        };
        if (!episode && slots.Attribution is { } attribution)
        {
            if (compact) rows.Add(Block("hero-attribution", attribution(line), late: true));
            else facts.Add(attribution(line));
        }
        if (slots.Rating is { } rating)
        {
            if (facts.Count > 0) facts.Add(Dot());
            facts.Add(rating(line));
        }
        if (id.Meta is { Length: > 0 } meta)
        {
            if (facts.Count > 0) facts.Add(Dot());
            facts.Add(MetaText(meta));
        }
        if (facts.Count > 0)
            rows.Add(Block("hero-meta", new BoxEl
            {
                Direction = 0, Wrap = true, Gap = WinUiShowRules.FactsGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Justify = justify,
                Children = facts.ToArray(),
            }, late: true));
        if (slots.Ledger is { } ledger) rows.Add(Block("hero-ledger", ledger(line), late: true));
        Element actions = Actions(id, spec.Config, p.Actions, slots, p.Play, tier, form, words: !compact, center: form.Stacked && !compact);
        // At Compact the action row runs under the cover and the words, the primary filling it (W-Show-Compact, W-Episode-Compact); elsewhere it closes the words.
        if (!compact) rows.Add(Block("hero-actions", actions));

        Element cover = Cover(id, slots, p.Actions, form);
        Element header = Compose(form, inset, cover, rows.ToArray(), new BoxEl { Height = form.ListGap }, center: form.Stacked,
            hang: hasEyebrow ? form.EyebrowLine + form.InfoGap : 0f);
        if (compact)
            header = new BoxEl
            {
                Direction = 1, MinWidth = 0f,
                Children =
                [
                    header,
                    new BoxEl
                    {
                        Key = "hero-actions", Direction = 1, MinWidth = 0f, Padding = new Edges4(inset, 0f, inset, form.ListGap),
                        Margin = new Edges4(0f, WinUiShowRules.CompactActionsTop - form.ListGap, 0f, 0f), Children = [FillRow(actions)],
                    },
                ],
            };
        // A header whose show or episode has not been read yet is its own DERIVED shimmer: the same rows, the title and the meta as bars in their line
        // boxes, the verbs as quiet shapes, never an empty title over a lone "·" with a live Play (audit B7, A2's finding). One region at every state,
        // so the read landing swaps the shimmer for the rows in place.
        var cell = ctx.Hooks.UseRef<PendingCell?>(null);
        var pending = cell.Value ??= new PendingCell();
        pending.Now = id.HeaderPending;
        return new BoxEl
        {
            // Its height eases through real layout across a tier (the cover 168 / 232, the short form, the stacked form; audit B9): the episodes under it
            // ride it instead of stepping.
            Key = "detail:show-header", Direction = 1, Shrink = 0f, Animate = HeroHeightMotion,
            Children =
            [
                new SkelRegionEl(
                    Pending: pending.Read, Failed: s_never, Content: () => header, ShimmerSource: () => header, OnFailed: null,
                    Reveal: SkelReveal.FadeOnly, Style: SkeletonStyle.Default, Group: null, SmoothResize: false),
            ],
        };
    }

    /// <summary>The show header's pending flag for its region: one stable reader per mounted header, written each render.</summary>
    sealed class PendingCell
    {
        public bool Now;
        public readonly Func<bool> Read;
        public PendingCell() => Read = () => Now;
    }

    static readonly Func<bool> s_never = static () => false;

    /// <summary>The separator of the facts line and the episode's eyebrow ("·", secondary).</summary>
    static TextEl Dot(float size = float.NaN, float lineHeight = float.NaN)
    {
        var t = Ui.Body("·") with { Color = Tok.TextSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap };
        return float.IsNaN(size) ? t : t with { Size = size, LineHeight = lineHeight };
    }

    /// <summary>The Compact action row: the primary grows to fill the row, the satellites keep their 40 squares (W-Show-Compact: "Resume · 31 min left", ✓, ...).</summary>
    static Element FillRow(Element actions)
    {
        if (actions is not BoxEl row || row.Children is not { Length: > 0 } kids) return actions;
        var fixedKids = new Element[kids.Length];
        fixedKids[0] = new BoxEl { Key = "hero-primary-fill", Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Children = [kids[0]] };
        for (int i = 1; i < kids.Length; i++) fixedKids[i] = kids[i];
        return row with { Wrap = false, Justify = FlexJustify.Start, Margin = default, Children = fixedKids };
    }
}
