// ── Themes/WinUi/Structure/Views/Discovery/Home/WinUiHomeDaylist.cs — WinUI's `Home.Daylist` (themes plan 6.2; P6 W5) ───────────────────
//
// Boards W-Home (Standard), W-Home-Huge, W-Home-Small, W-Home-Compact. The daylist is a CARD (the card fill, r8, 24 padding at Standard and Huge, 16 below),
// not a hero: the cover is a square r4 tile at the left, the copy beside it, the clock behind a hairline at the right.
//
//   Triple (Standard 272 cover, Huge 288)    [ cover | copy (Grow) | hairline + clock column (300 / 320) ]   24 apart
//   Pair (Small, 120 cover)                  [ cover | copy ]  the clock is one line at the eyebrow's end
//   Stack (Compact, 96 cover)                [ cover | eyebrow, title, meta ] over the tags, the 40-high actions, and the clock footer under a hairline
//
//   copy column  (6 apart; 2 at Small and Compact)
//     eyebrow    caption 12/16 secondary: "Your daylist" (the page's greeting is the headline now)
//     title      Title Large 40/52 (Triple, up to three lines) · Subtitle 20/28 (Pair, one line; Stack, two)
//     tags       ONE 14/20 line of accent link spans, " · " between (a tag opens Search); at Small the meta follows on the same line
//     meta       14/20 secondary: "Playlist · Spotify · 50 songs" (12/16 at Compact)
//     actions    Play (accent) · Shuffle · Save · "..."   pinned to the card's foot (a spacer Grows above them)
//
// The card is the click target for the playlist, its buttons nested click targets. "..." re-enters the engine's context funnel and finds the card's ATTACHED
// playlist menu (`HomeCardNav.MenuOf`). What must SUBSCRIBE (whether the primary names Pause) was read by the host; the saved heart is the stock `SaveButton`.
// The cold-load skeleton is the SAME builders fed placeholders (`Skeleton`). Pure rules (core): `DaypartTimeline` / `DaylistCountdownLine` / `DaylistNext`.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.HomeUi;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's daylist card.</summary>
internal static class WinUiHomeDaylist
{
    /// <summary>The card's content, built once per render (or from placeholders for the skeleton) and laid out by <see cref="CardShape"/>.</summary>
    readonly record struct Parts(string Title, Element Tags, string Meta, Element Actions, Element Clock, Element Cover);

    /// <summary>The registered template of <see cref="ViewKeys.HomeDaylist"/>.</summary>
    public static Element Template(HomeDaylistProps props, in ViewContext ctx)
    {
        // The tier's form is read before any early return: the hooks run on every render, in the same order.
        var facts = ctx.Tier();
        var layout = WinUiHomeTiers.Of(facts);
        var dl = layout.Daylist;

        var zone = props.Zone;
        if (zone.Items.Count == 0) return new BoxEl();
        var card = zone.Items[0];
        string uri = card.Uri;
        if (uri.Length == 0) return new BoxEl();

        var host = props.Overlay;
        var menu = HomeCardNav.MenuOf(in card);
        var c = card;
        string title = card.Title;

        void Play() => HomeCardNav.Play(in c);
        // Shuffle STARTS the daylist shuffled; it never toggles (HomeCardNav.Play pauses a daylist that already plays).
        void Shuffle() => HomeVerbs.ShuffleContext(c.Uri);
        // The ▶ names what a press does: Pause while this daylist is the context playing (read by the host: a subscribing, coarse-first read).
        bool pauses = props.Pauses;

        var actions = ActionRow(dl,
            PlayButton(dl, Loc.Get(pauses ? Strings.Home.Pause : Strings.Home.Play), Play, pauses ? Icons.Pause : Icons.Play, pauses),
            Button.Create(Loc.Get(Strings.Detail.Shuffle), Shuffle, ButtonAppearance.Standard, dl.Buttons) with { Shrink = 0f },
            new BoxEl { Grow = dl.Form == WinUiDaylistForm.Stack ? 1f : 0f, MinWidth = 0f },
            Controls.Named(Embed.Comp(() => new Controls.SaveButton { Uri = uri, Name = title, Box = dl.IconBox }) with { Key = "save:" + uri },
                           Loc.Get(Strings.Home.Save)),
            // A STANDALONE icon button, not `Controls.MoreButton`: that one is the list-row overflow whose rest opacity lights up with its ANCESTOR's hover.
            Controls.Named(Controls.IconAction(Icons.More, null, requestsContext: true, size: dl.IconBox) with { BlocksDragArm = true },
                           Loc.Get(Strings.Home.More)));

        string? artUrl = DaylistArt.Of(card.HeaderImageUrl, card.ImageUrl) ?? card.ImageUrl;
        Element cover = CoverTile(artUrl, dl);

        // The card IS the daylist: a click anywhere outside its buttons opens the playlist. The tile ramp is the card's hover/press feedback.
        var root = CardShape(new Parts(title, Tags(card, dl), Strings.Home.DaylistMeta(card.TrackCount), actions, props.Clock ?? new BoxEl(), cover), dl)
            .Interactive(Interaction.Tile) with
            {
                OnClick = () => HomeCardNav.Open(in c), Cursor = CursorId.Hand,
                Role = AutomationRole.Button, Focusable = true,
            };
        // The playlist menu rides the card root: a right-click anywhere on the card and the "..." button (which climbs to this handler through the context
        // funnel) open the SAME menu, the button's anchored at itself.
        return menu is not null && host is not null ? root.WithContextMenu(host, menu) : root;
    }

    /// <summary>The cold-load shape: the SAME builders as <see cref="Template"/>, fed placeholder words, stand-in buttons and a static ring/rows. Never early-outs —
    /// the seed zone's card is a blank (no uri). The embed's <see cref="ComponentEl.SkeletonProxy"/>, at the tier's form.</summary>
    internal static Element Skeleton(WinUiHomeLayout layout)
    {
        var dl = layout.Daylist;
        string words = Loc.Get(Strings.Home.YourDaylist);
        var actions = ActionRow(dl,
            PlayButton(dl, Loc.Get(Strings.Home.Play), static () => { }, Icons.Play, pauses: false),
            Button.Create(Loc.Get(Strings.Detail.Shuffle), static () => { }, ButtonAppearance.Standard, dl.Buttons) with { Shrink = 0f },
            new BoxEl { Grow = dl.Form == WinUiDaylistForm.Stack ? 1f : 0f, MinWidth = 0f },
            Controls.IconAction(Icons.More, null, size: dl.IconBox),
            Controls.IconAction(Icons.More, null, size: dl.IconBox));
        var current = DaypartRules.OfHour(DateTime.Now.Hour);
        var line = WinUiHomeDaylistClock.Line((TextSpans)new TextSpan[] { new(Strings.Home.NextDaylistIn(DaypartRules.Countdown(0))) }, dl);
        // The caption's placeholder is the real rule read at "now": a caption-width line with the next daypart's word.
        string caption = DaylistNext.Caption(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), current,
                                             TimeZoneInfo.Local, System.Globalization.CultureInfo.CurrentCulture);
        var clock = WinUiHomeDaylistClock.Shape(ProgressRing.Determinate(0f, dl.Ring), line, caption, null, (int)current, dl, ClockWidthEstimate(dl, 1000f));
        // The cover slot as its watched placeholder (the real shape): a component the deriver cannot see into would shimmer as one stray bar.
        return CardShape(new Parts(Loc.Get(Strings.Home.Layout.Daylist), TagLine(new TextSpan[] { new(words) }, null, dl),
                                   Strings.Home.DaylistMeta(50), actions, clock, CoverTile(null, dl)), dl);
    }

    /// <summary>The primary action at the tier's size; the Compact button is the board's 20-padded 40.</summary>
    static Element PlayButton(WinUiDaylistLayout dl, string label, Action onClick, string glyph, bool pauses)
        => Button.Create(label, onClick, ButtonAppearance.Accent, dl.Buttons, glyph: glyph)
            with { MinWidth = Controls.PrimaryMinWidth, Shrink = 0f };

    /// <summary>The clock column's text width at a card <paramref name="cardW"/> wide: the clock block's first-frame width.</summary>
    internal static float ClockWidthEstimate(WinUiDaylistLayout dl, float cardW)
        => dl.Form == WinUiDaylistForm.Triple ? MathF.Max(0f, dl.ClockW - dl.Gap - 1f) : MathF.Max(0f, cardW - 2f * dl.Pad);

    /// <summary>The card: a Fluent card frame (fill, border, r8) with the tier's padding, around the layers of the form. The card's height is its content's:
    /// the cover's edge plus both paddings at Standard and Huge (320 / 336).</summary>
    static BoxEl CardShape(Parts parts, WinUiDaylistLayout dl)
        => Ui.Card(Row(parts, dl)) with
        {
            Padding = new Edges4(dl.Pad, dl.Pad, dl.Pad, dl.Pad), ClipToBounds = true, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
        };

    /// <summary>The cover: a square r4 tile of the tier's edge (the board's `border-radius: 4px`).</summary>
    static Element CoverTile(string? url, WinUiDaylistLayout dl)
        => new BoxEl
        {
            Width = dl.Cover, Height = dl.Cover, Shrink = 0f,
            Children = [Controls.Artwork(url, dl.Cover, dl.Cover, Radii.Control, decodePx: WinUiHomeRules.DaylistArtDecodePx)],
        };

    static Element Row(Parts p, WinUiDaylistLayout dl) => dl.Form switch
    {
        WinUiDaylistForm.Stack => Stacked(p, dl),
        WinUiDaylistForm.Pair => Pair(p, dl),
        _ => Triple(p, dl),
    };

    /// <summary>Standard and Huge: [cover | copy | hairline + clock]. The row stretches its children to the cover's height, so the copy's spacer parts the
    /// actions to the foot.</summary>
    static Element Triple(Parts p, WinUiDaylistLayout dl) => new BoxEl
    {
        Key = "daylist-row", Direction = 0, Gap = dl.Gap, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
        Children =
        [
            p.Cover,
            Copy(p, dl, eyebrowTrail: null) with { Key = "daylist-copy", Grow = 1f, Shrink = 1f, Basis = 0f },
            new BoxEl
            {
                Key = "daylist-clock-col", Direction = 0, Width = dl.ClockW, Shrink = 0f, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
                // A crossing re-places the clock (Small: one line at the eyebrow's end): the arriving form FADES IN. No exit fade: a leaving node's ghost
                // keeps its layout slot until it has faded, which would hold the card at the old height and then drop the page 158 DIP at once.
                Enter = WinUiDiscoveryMotion.SwapFade, Transition = WinUiDiscoveryMotion.SwapFadeMotion,
                Children =
                [
                    new BoxEl { Width = 1f, Shrink = 0f, Fill = Tok.StrokeDividerDefault },
                    new BoxEl
                    {
                        Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
                        Padding = new Edges4(dl.Gap, 0f, 0f, 0f), Children = [p.Clock],
                    },
                ],
            },
        ],
    };

    /// <summary>Small: [cover | copy], the clock a line at the eyebrow's end.</summary>
    static Element Pair(Parts p, WinUiDaylistLayout dl) => new BoxEl
    {
        Key = "daylist-row", Direction = 0, Gap = dl.Gap, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
        Children = [p.Cover, Copy(p, dl, eyebrowTrail: p.Clock) with { Key = "daylist-copy", Grow = 1f, Shrink = 1f, Basis = 0f }],
    };

    /// <summary>Compact: the cover beside the eyebrow, title and meta, then the tags, the actions and the clock footer, each at the card's width.</summary>
    static Element Stacked(Parts p, WinUiDaylistLayout dl) => new BoxEl
    {
        Direction = 1, Gap = Spacing.M, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
        Children =
        [
            new BoxEl
            {
                Direction = 0, Gap = dl.Gap, MinWidth = 0f, AlignItems = FlexAlign.Start,
                Children =
                [
                    p.Cover,
                    new BoxEl
                    {
                        Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Gap = WinUiHomeRules.CopyGapSmall,
                        Children = [Eyebrow(), Title(p.Title, dl), MetaLine(p.Meta, small: true)],
                    },
                ],
            },
            p.Tags,
            p.Actions,
            p.Clock,
        ],
    };

    /// <summary>The copy column of Standard, Huge and Small: eyebrow (the clock line trailing it at Small) · title · tags · meta (the tags carry it at Small)
    /// · a spacer · the actions.</summary>
    static BoxEl Copy(Parts p, WinUiDaylistLayout dl, Element? eyebrowTrail)
    {
        bool pair = dl.Form == WinUiDaylistForm.Pair;
        // KEYED children: a Triple <-> Pair crossing drops the meta line, and position-matched children would remount the actions (and the save heart).
        var kids = new List<Element>(6)
        {
            pair
                ? new BoxEl
                {
                    Key = "daylist-eyebrow-row", Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f,
                    Children = eyebrowTrail is null
                        ? [Eyebrow() with { Grow = 1f, Shrink = 1f }]
                        : [Eyebrow() with { Grow = 1f, Shrink = 1f }, ClockLine(eyebrowTrail.Value)],
                }
                : Eyebrow() with { Key = "daylist-eyebrow" },
            Title(p.Title, dl) with { Key = "daylist-title" },
            p.Tags with { Key = "daylist-tags" },
        };
        if (!pair) kids.Add(MetaLine(p.Meta, small: false) with { Key = "daylist-meta" });
        kids.Add(new BoxEl { Key = "daylist-spacer", Grow = 1f, MinWidth = 0f });
        kids.Add(p.Actions with { Key = "daylist-actions" });
        return new BoxEl
        {
            Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
            Gap = pair ? WinUiHomeRules.CopyGapSmall : WinUiHomeRules.CopyGapLarge, Children = kids.ToArray(),
        };
    }

    /// <summary>Small's clock line at the eyebrow's end, in a host that fades in with a crossing (the Triple column's twin; no exit ghost, see there).</summary>
    static Element ClockLine(Element clock) => new BoxEl
    {
        Key = "daylist-clock-line", Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, Shrink = 0f,
        Enter = WinUiDiscoveryMotion.SwapFade, Transition = WinUiDiscoveryMotion.SwapFadeMotion,
        Children = [clock],
    };

    /// <summary>The eyebrow: caption 12/16 in the secondary ink, "Your daylist".</summary>
    static TextEl Eyebrow()
        => Ui.Caption(Loc.Get(Strings.Theme.Winui.Discovery.YourDaylist)).Secondary()
            with { MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };

    /// <summary>The title at the tier's rung: Title Large 40/52 (Triple), Subtitle 20/28 (Pair one line, Stack two).</summary>
    static TextEl Title(string title, WinUiDaylistLayout dl)
        => (dl.TitleLarge ? R.Type.HeroTitle(title) : R.Type.ModuleHeader(title)) with
        {
            MaxLines = dl.TitleLines, Wrap = dl.TitleLines > 1 ? TextWrap.Wrap : TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };

    /// <summary>The meta line: body 14/20 secondary (caption at Compact), one line.</summary>
    static TextEl MetaLine(string meta, bool small)
        => (small ? Ui.Caption(meta).Secondary() : Ui.Body(meta).Secondary()) with
        {
            MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };

    /// <summary>The ONE action row, 8 apart, never wrapping (the copy's floor fits it).</summary>
    static BoxEl ActionRow(WinUiDaylistLayout dl, params Element[] actions) => new()
    {
        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Wrap = false, MinWidth = 0f, Children = actions,
    };

    /// <summary>The tags as ONE 14/20 line: every tag an accent link span (the engine's Hand cursor over its own laid rects), " · " separators in the primary
    /// ink, one line with an ellipsis; at Small the meta follows in the secondary ink. Clicks resolve by span INDEX (`OnSpanClick`): tags sit at the even
    /// indices, separators at the odd ones.</summary>
    static Element Tags(HomeCard card, WinUiDaylistLayout dl)
    {
        int seedCount = Math.Min(card.SeedCount, WinUiHomeRules.DaylistMaxTags);
        var tags = new List<string>(seedCount);
        for (int i = 0; i < seedCount; i++)
        {
            string tag = card.SeedAt(i);
            if (tag.Length > 0) tags.Add(tag);
        }
        bool trailMeta = dl.Form == WinUiDaylistForm.Pair;
        string meta = trailMeta ? Strings.Home.DaylistMeta(card.TrackCount) : "";
        if (tags.Count == 0 && meta.Length == 0) return new BoxEl();

        var spans = new List<TextSpan>(tags.Count * 2 + 1);
        for (int i = 0; i < tags.Count; i++)
        {
            if (i > 0) spans.Add(new TextSpan(" · ", Color: Tok.TextPrimary));
            spans.Add(new TextSpan(tags[i], Color: Tok.AccentTextPrimary, IsLink: true));
        }
        if (meta.Length > 0) spans.Add(new TextSpan((tags.Count > 0 ? " · " : "") + meta, Color: Tok.TextSecondary));
        int tagCount = tags.Count;
        return TagLine(spans.ToArray(), i => { if ((i & 1) == 0 && (i >> 1) < tagCount) HomeVerbs.OpenTag(tags[i >> 1]); }, dl);
    }

    /// <summary>The tag line's paragraph: the body rung's metrics (never a hand size), primary base.</summary>
    static SpanTextEl TagLine(TextSpan[] spans, Action<int>? onSpanClick, WinUiDaylistLayout dl)
    {
        var body = Ui.Body("");
        return new SpanTextEl(spans)
        {
            Size = body.Size, LineHeight = body.LineHeight, Color = Tok.TextPrimary,
            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            OnSpanClick = onSpanClick,
        };
    }
}
