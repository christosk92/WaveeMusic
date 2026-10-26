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
// THE SHOW HEADER (`Detail.ShowHeader`): a show's or an episode's vertical arm, built from the same parts (no board draws it under Zune, Q18). Since audit
// B9 it IS the rail drawn stacked (`DetailRailProps.Stacked`): one identity at every width, so a crossing glides the cover, the facts and the rings.

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
    /// last resort once a long description has given), grown to the row's height; or, STACKED (<see cref="DetailRailProps.Stacked"/>: a show's or an
    /// episode's page below the mode edge, audit B9), the same identity as the fixed header across the page. ONE tree in both forms: the cover, the
    /// facts, the ledger and the rings keep their nodes and glide between the arrangements; what only the column draws (the topics, About, the
    /// description) fades on the swap.</summary>
    public static Element RailTemplate(DetailRailProps p, in ViewContext ctx)
    {
        var enter = ZuneMotion.SwapEnter(ZuneMotion.Rendered(ctx.Hooks));
        var facts = ctx.Tier();
        bool stacked = p.Stacked;
        var column = ZuneDetailTiers.ColumnOf(facts);
        var lay = stacked ? ZuneDetailTiers.Hero : column;
        float gutter = ZuneTiers.FrameOf(facts).Gutter;
        float w = stacked ? float.NaN : p.RailWidth > 0f ? p.RailWidth : lay.RailW;
        var spec = p.Spec;
        // A Short window gives the column little height: a playlist's description closes it there, as every other kind's does, so the rings (play, saved)
        // stand above the play bar at rest (V4: Z-Playlist-Owned-Followed-Small) and the description scrolls under them. The stacked header folds to the
        // name over the rings there.
        bool shortWindow = facts.Policy is not null && facts.Height == HeightTier.Short;
        // The column's loading state: the header shimmers it whole until it has answered (the stacked header never shimmered; one boundary in both forms,
        // so the identity never re-parents).
        Element region = new SkelRegionEl(
            Pending: stacked ? K.Never : p.Pending, Failed: K.Never,
            Content: () => Column(spec, p, lay, w, shortWindow, enter, stacked, gutter),
            ShimmerSource: () => Skeleton(spec, column, stacked ? column.RailW : w),
            OnFailed: null, Reveal: SkelReveal.FadeOnly, Style: SkeletonStyle.Default, Group: null, SmoothResize: false);
        return new BoxEl
        {
            Direction = 1, Width = w, Shrink = 0f, Grow = stacked ? 0f : 1f, MinHeight = 0f, ClipToBounds = true,
            Children = [Ui.ScrollView(region) with { ContentSized = true, Grow = stacked ? 0f : 1f, Shrink = 1f, MinHeight = 0f, Width = w }],
        };
    }

    /// <summary>The loaded identity (see the file header), every row keyed: a row that arrives late fades up, the rows under it glide. The column: the cover
    /// over the facts, the status rows, the rings, then the courtesy rows; on a Short window (<paramref name="shortWindow"/>) a playlist's description
    /// closes the column instead of leading it and the status rows follow the rings (the same keyed rows, moved), so the rings stand above the play bar at
    /// rest. STACKED (the fixed header): the cover BESIDE the facts (bottom-aligned), the ledger, the rings across the page, the countdown and the release
    /// facts, on the frame's gutter; a Short window folds it to the name over the rings.</summary>
    static Element Column(Detail.FrameSpec spec, DetailRailProps p, ZuneDetailLayout lay, float w, bool shortWindow, EnterExit? enter, bool stacked, float gutter)
    {
        var id = spec.Identity;
        var cfg = spec.Config;
        var slots = spec.Slots;
        var acts = p.Actions;
        var kids = new List<Element>(14);
        bool fold = stacked && shortWindow;

        // A short window's title leads (the panorama folds): the editor arrives from the panorama, a cross-fade of the swap's family.
        if (stacked ? fold : lay.RailTitle)
            kids.Add(K.Row("rail:title", slots.Title?.Invoke(R.Type.Heading("").Size, float.NaN) ?? Title(id.Title, w)) with
            {
                Enter = enter, Exit = ZuneMotion.SwapFade,
            });

        // ── the cover row: the cover OVER the facts (the column), BESIDE them (the stacked header) ──
        bool descriptionLeads = !stacked && id.Kind == DetailKind.Playlist && !shortWindow;
        if (!fold)
        {
            var info = new List<Element>(6);
            // The stacked header leads its facts with the badges; the column states them after the artists.
            if (stacked && slots.Badges is { } badgesLead) info.Add(K.LateRow("rail:badges", badgesLead()));
            if (id.OwnerName is { Length: > 0 } && id.Kind != DetailKind.Album && slots.Attribution is null)
                info.Add(K.LateRow("rail:owner", K.ArtistText(id.OwnerName, w, lay)));
            // The artists' line: the column's always (the page's attribution, else the billed artists); the stacked header's only where the page has an
            // attribution of its own (a show's publisher).
            if (!stacked || slots.Attribution is not null)
                info.Add(K.LateRow("rail:artists", new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, MinHeight = stacked ? 0f : lay.ArtistLine, Width = w, MinWidth = 0f,
                    Children = [slots.Attribution?.Invoke(w) ?? K.Artists(id, w, lay)],
                }) with { Enter = enter ?? K.FadeUp, Exit = ZuneMotion.SwapFade });
            if (!stacked && slots.Badges is { } badges) info.Add(K.LateRow("rail:badges", badges()));
            if (slots.Rating is { } rating) info.Add(K.LateRow("rail:rating", rating(w)));
            int metaLines = stacked ? Z.ShowMetaLines : lay.MetaLines;
            info.Add(K.LateRow("rail:meta", p.Shown is { } shown
                ? Embed.Comp(new MetaProps(id, w, lay, metaLines), () => new MetaLine(shown))
                : K.Meta(id, w, lay, metaLines)));

            var coverKids = new List<Element>(3)
            {
                // The page's anchor: its bounds tween from the top-left, so its corner stays pinned across a re-arrangement.
                K.Cover("rail:cover-art", id, acts, slots, stacked ? Z.ShowCover : w) with
                {
                    Animate = Z.HeroGeometry, TransformOriginX = 0f, TransformOriginY = 0f,
                },
            };
            // A playlist's description leads its column under the cover (Z-Playlist-Owned-Followed-Huge: description, owner, meta, rings).
            if (descriptionLeads && p.DescriptionLines > 0 && K.Description(id, slots, w, p.Accent) is { } lead)
                coverKids.Add(K.LateRow("rail:desc", lead) with { Exit = ZuneMotion.SwapFade });
            coverKids.Add(new BoxEl
            {
                Key = "rail:info", Direction = 1, MinWidth = 0f, Layout = Z.Arrange,
                Grow = stacked ? 1f : 0f, Shrink = 1f, Basis = stacked ? 0f : float.NaN, Width = w,
                Gap = stacked ? Z.HeroIdentityGap : lay.RailGap,
                Children = info.ToArray(),
            });
            kids.Add(new BoxEl
            {
                Key = "rail:cover-row", Direction = (byte)(stacked ? 0 : 1), MinWidth = 0f, Shrink = 0f, Layout = Z.Arrange,
                Gap = stacked ? Z.ShowGap : lay.RailGap, AlignItems = stacked ? FlexAlign.End : FlexAlign.Stretch,
                Children = coverKids.ToArray(),
            });
        }

        // The status rows (a show's played ledger, the daylist's pulse, the chart's news) stand between the facts and the rings; on a Short window the
        // column's follow the rings, so the verbs and their state stay above the play bar at rest (V4). The stacked header draws the ledger alone.
        var status = new List<Element>(3);
        if (!fold && slots.Ledger is { } ledger) status.Add(K.LateRow("rail:ledger", ledger(w)));
        if (!stacked && id.DaylistExpiresAtMs > 0 && slots.Pulse is { } pulse) status.Add(K.LateRow("rail:daylist", pulse()));
        if (!stacked && id.ChartNewEntries > 0 && slots.Chart is { } chart) status.Add(K.LateRow("rail:chart", chart()));
        bool statusAfter = !stacked && shortWindow;
        if (!statusAfter) kids.AddRange(status);
        // The ring row eases its height (a Short window's play and saved can wrap it onto a second line), so the rows under it glide. Across the page
        // (stacked) it springs its trailing rings to the far edge.
        kids.Add(K.Row("rail:actions", K.Rings(Z.ActionsKey, id, cfg, acts, slots, p.Accent, p.Play, lay, spring: stacked)) with { Layout = K.GrowShove });
        if (statusAfter) kids.AddRange(status);
        if (!stacked && slots.Topics is { } topics) kids.Add(K.LateRow("rail:topics", topics(w)) with { Exit = ZuneMotion.SwapFade });
        if (!stacked && slots.About is { } about) kids.Add(K.LateRow("rail:about", about(w)) with { Exit = ZuneMotion.SwapFade });
        if (!fold && id.UpcomingAtUnixSeconds > 0 && slots.PreRelease is { } pre) kids.Add(K.LateRow("rail:prerelease", pre()));
        if (!fold && cfg.Badges == BadgeStyle.TypeYear && slots.ReleasePanel is { } release)
            kids.Add(new BoxEl
            {
                Key = "rail:release", Direction = 1, MinWidth = 0f, ClipToBounds = true, Layout = K.GrowShove, Enter = K.FadeUp,
                Margin = new Edges4(0f, stacked ? 0f : Z.NotesTop - lay.RailGap, 0f, 0f), Children = [release(false)],
            });
        if (!stacked && !descriptionLeads && p.DescriptionLines > 0 && K.Description(id, slots, w, p.Accent) is { } description)
            kids.Add(K.LateRow("rail:desc", description) with { Exit = ZuneMotion.SwapFade });

        return new BoxEl
        {
            Key = Z.RailKey + ":column", Direction = 1, Width = w, Shrink = 0f, MinWidth = 0f,
            Gap = stacked ? (fold ? Z.ShowRowsGapShort : Z.ShowRowsGap) : lay.RailGap,
            Padding = stacked
                ? new Edges4(gutter, fold ? Z.ShowPadTopShort : Z.ShowPadTop, gutter, Z.ShowPadBottom)
                : new Edges4(0f, 0f, 0f, Z.SectionBottom),
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

    sealed record MetaProps(Detail.Identity Id, float Width, ZuneDetailLayout Layout, int Lines);

    /// <summary>The meta line, or, while a find or a filter narrows the list, "3 of 48 songs · 11 min" and the quiet "(filtered)" mark in the same
    /// slot (its own component: a keystroke re-renders one line).</summary>
    sealed class MetaLine(IReadSignal<Detail.ListCount> shown) : Component
    {
        public override Element Render()
        {
            var p = UseProps<MetaProps>();
            var count = shown.Value;
            if (Detail.Text.FilteredMeta(in count) is not { } filtered) return K.Meta(p.Id, p.Width, p.Layout, p.Lines);
            var lay = p.Layout;
            return new BoxEl
            {
                Direction = 0, Width = p.Width, Height = lay.MetaLine * Math.Max(1, p.Lines), Gap = Spacing.XS, AlignItems = FlexAlign.Start, MinWidth = 0f,
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

    /// <summary>The registered template of <see cref="ViewKeys.DetailShowHeader"/>: the identity drawn STACKED (<see cref="RailTemplate"/>; Zune's own frame
    /// draws the rail stacked at every show and episode page below the mode edge, audit B9, and the panorama in its band). The cover beside the facts
    /// (the publisher or the show, the rating, the meta line), the ledger, then the ring row full width; the prerelease card and the release facts under
    /// it. ONE HOME PER VERB (audit A4): the rings' own gates read the WINDOW's band, so a show's follow and save leave the row where the band's row 2
    /// carries "follow". A SHORT window folds it to the show's name in the heading voice over the ring row.</summary>
    public static Element ShowHeaderTemplate(DetailShowHeaderProps p, in ViewContext ctx)
        => RailTemplate(new DetailRailProps
        {
            Spec = p.Spec, Actions = p.Actions, RailWidth = 0f, TitleSize = 0f, DescriptionLines = 0, Accent = p.Accent, Play = p.Play,
            Pending = K.Never, Stacked = true,
        }, ctx);
}
