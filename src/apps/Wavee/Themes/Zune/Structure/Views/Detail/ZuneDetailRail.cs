// ── Themes/Zune/Structure/Views/Detail/ZuneDetailRail.cs — Zune's identity column, its collapsed strip and the show header (P5 Z6a1) ─────────
//
// Z-Album's left column (240 at Standard, 160 Small, 320 Huge, 400 Huge 4K): the cover at the column's width, the artist in the 18/24 voice,
// the meta line ("album · may 17, 2013 · 13 songs · 74 min", the label case lowering the words; two lines at Small, broken only
// between its runs), the RING row (play and saved with their words at Compact only: from Small up play, shuffle and saved live in the band's
// row 2, audit A4, so the column keeps share and "..."), then the release's courtesy lines (About this release, `Album.ReleasePanel`), the countdown
// of a release not out yet and the description. No title: the title is the panorama over the columns and the band's row 2 (a short window
// folds the panorama and the column leads with the title in the heading voice instead).
//
// One skeleton boundary (the core's header-pending gate): the loading column reserves the same rows at the same heights (the cover, the artist
// line, the meta line, the ring row), so the reveal is a dissolve. A podcast or playlist page's own slots (the owner block, badges, rating, the
// ledger, topics, a primary or satellites) keep their places in the column; their pages' lanes refine them.
//
// THE COLLAPSED STRIP. Zune draws no grip (the column is the tier's width), so it never collapses the column itself; a collapse stored by another
// theme reaches it, and the strip (`Detail.CompactRail`) is the cover and a 40 expand target, so the stored state is one click from undone.
//
// THE SHOW HEADER (`Detail.ShowHeader`): a show's or an episode's vertical arm, built from the same parts (no board draws it under Zune, Q18).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using K = Wavee.Themes.Zune.ZuneDetailKit;
using Z = Wavee.Themes.Zune.ZuneDetailRules;

namespace Wavee.Themes.Zune;

/// <summary>Zune's identity column (<see cref="ViewKeys.DetailRail"/>), its strip (<see cref="ViewKeys.DetailCompactRail"/>) and the show header
/// (<see cref="ViewKeys.DetailShowHeader"/>).</summary>
internal static class ZuneDetailRail
{
    // ── the identity column ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The registered template of <see cref="ViewKeys.DetailRail"/>: the column behind one skeleton boundary, in its own scroller (the
    /// last resort once a long description has given), grown to the row's height.</summary>
    public static Element RailTemplate(DetailRailProps p, in ViewContext ctx)
    {
        var lay = ZuneDetailTiers.Of(ctx.Tier());
        float w = p.RailWidth > 0f ? p.RailWidth : lay.RailW;
        var spec = p.Spec;
        Element region = new SkelRegionEl(
            Pending: p.Pending, Failed: K.Never,
            Content: () => Column(spec, p, lay, w),
            ShimmerSource: () => Skeleton(spec, lay, w),
            OnFailed: null, Reveal: SkelReveal.FadeOnly, Style: SkeletonStyle.Default, Group: null, SmoothResize: false);
        return new BoxEl
        {
            Direction = 1, Width = w, Shrink = 0f, Grow = 1f, MinHeight = 0f, ClipToBounds = true,
            Children = [Ui.ScrollView(region) with { Grow = 1f, Shrink = 1f, MinHeight = 0f, Width = w }],
        };
    }

    /// <summary>The loaded column (see the file header), every row keyed: a row that arrives late fades up, the rows under it glide.</summary>
    static Element Column(Detail.FrameSpec spec, DetailRailProps p, ZuneDetailLayout lay, float w)
    {
        var id = spec.Identity;
        var cfg = spec.Config;
        var slots = spec.Slots;
        var acts = p.Actions;
        var kids = new List<Element>(14);

        if (lay.RailTitle)
            kids.Add(K.Row("rail:title", slots.Title?.Invoke(R.Type.Heading("").Size, float.NaN) ?? Title(id.Title, w)));
        kids.Add(K.Row("rail:cover", K.Cover("rail:cover-art", id, acts, slots, w)));
        // A playlist's description leads its column under the cover (Z-Playlist-Owned-Followed-Huge: description, owner, meta, rings); every other kind's
        // closes it.
        bool descriptionLeads = id.Kind == DetailKind.Playlist;
        if (descriptionLeads && p.DescriptionLines > 0 && K.Description(id, slots, w, p.Accent) is { } lead) kids.Add(K.LateRow("rail:desc", lead));
        if (id.OwnerName is { Length: > 0 } && id.Kind != DetailKind.Album && slots.Attribution is null)
            kids.Add(K.LateRow("rail:owner", K.ArtistText(id.OwnerName, w, lay)));
        kids.Add(K.LateRow("rail:artists", new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, MinHeight = lay.ArtistLine, Width = w, MinWidth = 0f,
            Children = [slots.Attribution?.Invoke(w) ?? K.Artists(id, w, lay)],
        }));
        if (slots.Badges is { } badges) kids.Add(K.LateRow("rail:badges", badges()));
        if (slots.Rating is { } rating) kids.Add(K.LateRow("rail:rating", rating(w)));
        kids.Add(K.LateRow("rail:meta", p.Shown is { } shown
            ? Embed.Comp(new MetaProps(id, w, lay), () => new MetaLine(shown))
            : K.Meta(id, w, lay, lay.MetaLines)));
        if (slots.Ledger is { } ledger) kids.Add(K.LateRow("rail:ledger", ledger(w)));
        if (id.DaylistExpiresAtMs > 0 && slots.Pulse is { } pulse) kids.Add(K.LateRow("rail:daylist", pulse()));
        if (id.ChartNewEntries > 0 && slots.Chart is { } chart) kids.Add(K.LateRow("rail:chart", chart()));
        kids.Add(K.Row("rail:actions", K.Rings(Z.ActionsKey, id, cfg, acts, slots, p.Accent, p.Play, lay, spring: false)));
        if (slots.Topics is { } topics) kids.Add(K.LateRow("rail:topics", topics(w)));
        if (slots.About is { } about) kids.Add(K.LateRow("rail:about", about(w)));
        if (id.UpcomingAtUnixSeconds > 0 && slots.PreRelease is { } pre) kids.Add(K.LateRow("rail:prerelease", pre()));
        if (cfg.Badges == BadgeStyle.TypeYear && slots.ReleasePanel is { } release)
            kids.Add(new BoxEl { Key = "rail:release", Direction = 1, MinWidth = 0f, ClipToBounds = true, Layout = K.GrowShove, Enter = K.FadeUp, Margin = new Edges4(0f, Z.NotesTop - lay.RailGap, 0f, 0f), Children = [release(false)] });
        if (!descriptionLeads && p.DescriptionLines > 0 && K.Description(id, slots, w, p.Accent) is { } description) kids.Add(K.LateRow("rail:desc", description));

        return new BoxEl
        {
            Key = Z.RailKey + ":column", Direction = 1, Gap = lay.RailGap, Width = w, Shrink = 0f, MinWidth = 0f,
            Padding = new Edges4(0f, 0f, 0f, Z.SectionBottom),
            Children = kids.ToArray(),
        };
    }

    /// <summary>The short window's title row: the heading voice as written (a name), two lines at most.</summary>
    static TextEl Title(string title, float w) => R.Type.Heading(title) with
    {
        Color = Tok.TextPrimary, Width = w, MinWidth = 0f, MaxLines = Z.RailTitleLines, Wrap = TextWrap.WrapWholeWords, Trim = TextTrim.CharacterEllipsis,
    };

    /// <summary>The loading column: the same rows at the same heights, unfilled (the deriver paints the bars); a known cover is painted for real.</summary>
    static Element Skeleton(Detail.FrameSpec spec, ZuneDetailLayout lay, float w)
    {
        var id = spec.Identity;
        var kids = new List<Element>(6);
        if (lay.RailTitle) kids.Add(K.Bar(w * Z.SkelMetaFraction, R.Type.Heading("").LineHeight));
        kids.Add(K.CoverSkeleton(id.CoverUrl, w));
        kids.Add(new BoxEl { Height = lay.ArtistLine, Direction = 0, AlignItems = FlexAlign.Center, Children = [K.Bar(w * Z.SkelArtistFraction, lay.ArtistSize)] });
        kids.Add(new BoxEl { Height = lay.MetaLine, Direction = 0, AlignItems = FlexAlign.Center, Children = [K.Bar(w * Z.SkelMetaFraction, lay.MetaSize)] });
        kids.Add(K.RingsSkeleton("skel:actions", K.RingCount(id, spec.Config, spec.Actions, spec.Slots, lay), lay));
        return new BoxEl { Direction = 1, Gap = lay.RailGap, Width = w, Shrink = 0f, Children = kids.ToArray() };
    }

    sealed record MetaProps(Detail.Identity Id, float Width, ZuneDetailLayout Layout);

    /// <summary>The meta line, or, while a find or a filter narrows the list, "3 of 48 songs · 11 min" and the quiet "(filtered)" mark in the same
    /// slot (its own component: a keystroke re-renders one line).</summary>
    sealed class MetaLine(IReadSignal<Detail.ListCount> shown) : Component
    {
        public override Element Render()
        {
            var p = UseProps<MetaProps>();
            var count = shown.Value;
            if (Detail.Text.FilteredMeta(in count) is not { } filtered) return K.Meta(p.Id, p.Width, p.Layout, p.Layout.MetaLines);
            var lay = p.Layout;
            return new BoxEl
            {
                Direction = 0, Width = p.Width, Height = lay.MetaLine * Math.Max(1, lay.MetaLines), Gap = Spacing.XS, AlignItems = FlexAlign.Start, MinWidth = 0f,
                Children =
                [
                    R.Type.DenseMeta(filtered.Count) with
                    {
                        Size = lay.MetaSize, LineHeight = lay.MetaLine, Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label),
                        MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f,
                    },
                    R.Type.DenseMeta(filtered.Mark) with
                    {
                        Size = lay.MetaSize, LineHeight = lay.MetaLine, Color = Tok.TextTertiary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Shrink = 0f,
                    },
                ],
            };
        }
    }

    // ── the collapsed strip ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The registered template of <see cref="ViewKeys.DetailCompactRail"/>: the cover (it expands the column) over a spring and the 40
    /// expand target, the title as the strip's tooltip.</summary>
    public static Element CompactRailTemplate(DetailCompactRailProps p, in ViewContext ctx)
    {
        var id = p.Spec.Identity;
        float cover = MathF.Max(0f, p.StripWidth - 2f * Z.StripPad);
        Element strip = new BoxEl
        {
            Key = "zune.detail.strip", Direction = 1, Width = p.StripWidth, Shrink = 0f, Grow = 1f, Gap = Z.StripPad, AlignItems = FlexAlign.Center,
            Padding = new Edges4(Z.StripPad, 0f, Z.StripPad, Z.StripPad),
            Children =
            [
                new BoxEl
                {
                    Key = "strip:cover", Width = cover, Height = cover, Shrink = 0f, Corners = R.Shape.All(ShapeRole.Cover), ClipToBounds = true,
                    Role = AutomationRole.Button, Cursor = CursorId.Hand, OnClick = p.Expand,
                    Children = [p.Spec.Slots.CompactCover?.Invoke(cover) ?? Controls.Artwork(id.CoverUrl, cover, cover, R.Shape.For(ShapeRole.Cover), decodePx: Z.CoverDecodePx)],
                },
                new BoxEl { Key = "strip:spring", Grow = 1f, MinHeight = 0f },
                new BoxEl
                {
                    Key = "strip:expand", Width = Z.StripTarget, Height = Z.StripTarget, Shrink = 0f, Corners = CornerRadius4.All(Z.StripTarget / 2f),
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, BorderWidth = Z.RingStroke, BorderColor = Tok.TextPrimary,
                    HoverFill = R.Layer.ChromeHover, PressedFill = R.Layer.ChromePressed,
                    Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = p.Expand,
                    Children = [Ui.Icon(Icons.ChevronRight, Z.StripChevron, Tok.TextPrimary)],
                },
            ],
        };
        return ToolTip.Wrap(strip, id.Title, grow: 1f);
    }

    // ── the show header ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The registered template of <see cref="ViewKeys.DetailShowHeader"/>: the panorama (the vertical record's 64/72, wrapped over two lines
    /// with its band hugging the name; Z-Show-Compact, Z-Episode-Compact), the 120 cover beside the meta (the publisher or the show, the rating, the meta line), the ledger, then the ring row
    /// full width; the prerelease card and the release facts under it. ONE HOME PER VERB (audit A4): the rings' own gates read the WINDOW's band, so a
    /// show's follow and save leave the row where the band's row 2 carries "follow". A SHORT window (under 700 tall, 330 at the least) folds the panorama
    /// (eased), the cover row, the ledger and the facts: the fixed header is then the show's name in the heading voice (wrapping over two lines) over the ring row.</summary>
    public static Element ShowHeaderTemplate(DetailShowHeaderProps p, in ViewContext ctx)
    {
        var facts = ctx.Tier();
        bool fold = facts.Policy is not null && facts.Height == HeightTier.Short;
        var lay = ZuneDetailTiers.Hero;
        float gutter = ZuneTiers.FrameOf(facts).Gutter;
        var spec = p.Spec;
        var id = spec.Identity;
        var slots = spec.Slots;
        var panorama = fold ? lay with { PanoramaH = 0f, RailTitle = true } : lay;

        var kids = new List<Element>(6);
        if (fold)
            kids.Add(K.LateRow("hdr:title", slots.Title?.Invoke(R.Type.Heading("").Size, float.NaN) ?? (R.Type.Heading(id.Title) with
            {
                Color = Tok.TextPrimary, MinWidth = 0f, MaxLines = Z.RailTitleLines, Wrap = TextWrap.WrapWholeWords, Trim = TextTrim.CharacterEllipsis,
            })));
        else
        {
            var info = new List<Element>(5);
            if (slots.Badges is { } badges) info.Add(K.LateRow("hdr:badges", badges()));
            if (slots.Attribution is { } attribution) info.Add(K.LateRow("hdr:lead", attribution(float.NaN)));
            else if (id.OwnerName is { Length: > 0 } owner) info.Add(K.LateRow("hdr:owner", K.ArtistText(owner, float.NaN, lay)));
            if (slots.Rating is { } rating) info.Add(K.LateRow("hdr:rating", rating(float.NaN)));
            info.Add(K.LateRow("hdr:meta", K.Meta(id, float.NaN, lay, lines: 2)));
            kids.Add(new BoxEl
            {
                Key = "hdr:cover", Direction = 0, Gap = Z.ShowGap, AlignItems = FlexAlign.End, MinWidth = 0f,
                Children =
                [
                    K.Cover("hdr:cover-art", id, p.Actions, slots, Z.ShowCover),
                    new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Z.HeroIdentityGap, Children = info.ToArray() },
                ],
            });
            if (slots.Ledger is { } ledger) kids.Add(K.LateRow("hdr:ledger", ledger(float.NaN)));
        }
        kids.Add(K.Row("hdr:actions", K.Rings(Z.ActionsKey, id, spec.Config, p.Actions, slots, p.Accent, p.Play, lay, spring: true)));
        if (!fold)
        {
            if (id.UpcomingAtUnixSeconds > 0 && slots.PreRelease is { } pre) kids.Add(K.LateRow("hdr:prerelease", pre()));
            if (spec.Config.Badges == BadgeStyle.TypeYear && slots.ReleasePanel is { } release)
                kids.Add(new BoxEl { Key = "hdr:release", Direction = 1, MinWidth = 0f, ClipToBounds = true, Layout = K.GrowShove, Enter = K.FadeUp, Children = [release(false)] });
        }
        return new BoxEl
        {
            Key = Z.ShowHeaderKey, Direction = 1, Shrink = 0f, MinWidth = 0f,
            Children =
            [
                K.PanoramaBand(id, slots, panorama, gutter),
                new BoxEl
                {
                    Key = "hdr:body", Direction = 1, Gap = fold ? Z.ShowRowsGapShort : Z.ShowRowsGap, Shrink = 0f, MinWidth = 0f,
                    Padding = new Edges4(gutter, fold ? Z.ShowPadTopShort : Z.ShowPadTop, gutter, Z.ShowPadBottom),
                    Children = kids.ToArray(),
                },
            ],
        };
    }
}
