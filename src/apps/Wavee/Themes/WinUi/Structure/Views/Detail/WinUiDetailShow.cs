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
        float measure = MathF.Max(0f, (float.IsNaN(p.PageWidth) || p.PageWidth <= 0f ? 0f : p.PageWidth) - 2f * inset - (form.Stacked ? 0f : form.Cover + form.Gap));
        float line = measure > 0f ? measure : float.NaN;

        var rows = new List<Element>(8);
        bool hasEyebrow = form.Eyebrow && (id.Eyebrow.Length > 0 || slots.Badges is not null);
        if (hasEyebrow)
        {
            var eyebrow = new List<Element>(2);
            if (id.Eyebrow.Length > 0) eyebrow.Add(WinUiDetailKit.EyebrowRun(id.Eyebrow, form));
            if (slots.Badges is { } badges) eyebrow.Add(badges());
            rows.Add(Block("hero-eyebrow", new BoxEl
            {
                Direction = 0, Gap = WinUiDetailRules.ActionGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Justify = form.Stacked ? FlexJustify.Center : FlexJustify.Start, Children = eyebrow.ToArray(),
            }, late: true));
        }
        rows.Add(Block("hero-title", slots.Title is { } edit ? edit(form.TitleSize, form.TitleLine) : R.Type.DetailHero(id.Title) with
        {
            Size = form.TitleSize, LineHeight = form.TitleLine, Weight = 600, Color = Tok.TextPrimary,
            Wrap = TextWrap.WrapWholeWords, MaxLines = WinUiDetailRules.TitleMaxLines, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            AlignSelf = form.Stacked ? FlexAlign.Center : FlexAlign.Auto,
        }));
        if (slots.Attribution is { } attribution) rows.Add(Block("hero-attribution", attribution(line), late: true));
        if (slots.Rating is { } rating) rows.Add(Block("hero-rating", rating(line), late: true));
        if (id.Meta is { Length: > 0 } meta)
            rows.Add(Block("hero-meta", new TextEl(meta)
            {
                Size = WinUiDetailRules.MetaSize, LineHeight = WinUiDetailRules.MetaLine, Color = Tok.TextTertiary,
                AlignSelf = form.Stacked ? FlexAlign.Center : FlexAlign.Auto, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            }, late: true));
        if (slots.Ledger is { } ledger) rows.Add(Block("hero-ledger", ledger(line), late: true));
        rows.Add(Block("hero-actions", Actions(id, spec.Config, p.Actions, slots, p.Play, tier, form, words: !form.Stacked, center: form.Stacked)));

        Element cover = Cover(id, slots, p.Actions, form);
        Element header = Compose(form, inset, cover, rows.ToArray(), new BoxEl { Height = form.ListGap }, center: form.Stacked,
            hang: hasEyebrow ? form.EyebrowLine + form.InfoGap : 0f);
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
}
