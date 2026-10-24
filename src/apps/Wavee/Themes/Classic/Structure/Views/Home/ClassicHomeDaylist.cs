// ── Themes/Classic/Structure/Views/Home/ClassicHomeDaylist.cs — Classic's `Home.Daylist` (themes plan 6.1, T10a1) ────────────
//
// From `Home\Daylist.UI.cs` (home rebuild, fifth → seventh pass): the daylist "play-now" card over stock controls.
//
// One `Ui.Card` (border, 8-DIP corners, at least 320 tall, NO padding, ClipToBounds — the art and the veil clip to its
// rounded corners; its border paints after its children, so it stays on top of the art) around a `Responsive.Of`
// ZStack built at the card's width — the artist hero's layering (`ClassicArtistHero.Build`: media · veil · copy):
//   art:  `Controls.CoverFill` of `DaylistArt.Of` across the WHOLE card (corner 0: the card's clip rounds it), focused a
//         little above centre (the crop stays centred horizontally);
//   veil: `Palette.ArtistHeroVeil`, horizontal (0 → .96, .30 → .92, .62 → .35, 1 → 0), cover-keyed, in its STRETCH arm
//         (NaN extent: it takes the card's content-driven height), hit-test free;
//   copy: the left column over the veil's opaque side — `DaylistForm.CopyWidth` of the card's content width (half of
//         it within [340, 560]; the whole card below `DaylistForm.SplitMin`) inside `DaylistCopyPadding` 32/32/12/24,
//         the card's insets, which ride on the copy so the art reaches the card's edges.
//   No art url ⇒ neither layer: the plain card, the copy alone. The whole card is the click target.
//   copy column (SpaceBetween)
//     top:    eyebrow (body, secondary: the greeting lives ONLY here) · the title (≤ 2 lines; the 40/52
//             `R.Type.HeroTitle` while the copy is ≥ `DaylistForm.HeroTitleMinText`, else the 28/36 display
//             rung at 600, so a narrow copy keeps the name on one line and the card at its 320 floor) ·
//             tags (ONE 14/20 `SpanTextEl` of link spans, a tag opens Search) · meta (caption, tertiary) ·
//             ONE action row: stock Accent Play + Standard Shuffle + `Controls.SaveButton` + a standalone "…" icon button
//     bottom: the clock (`Home.DaylistClock`, mounted by the clock host the card's host built and keyed)
//
// The "…" button re-enters the engine's context funnel (`ClickRequestsContext`) and finds the card's ATTACHED playlist
// menu (`HomeCardNav.MenuOf`, the grammar every other card wears) — attaching it needs the overlay service, which the host
// hands in. What must SUBSCRIBE (the greeted name, whether the ▶ names Pause) was read by the host; the saved heart is the stock
// `SaveButton`, which subscribes on its own. The cold-load skeleton is the SAME builders fed placeholders (`Skeleton`) — the placeholder
// art slot across the card, no veil — so it shimmers as this card's real shape.
// THE FORM IS THE TIER'S (`Rules/Tiers.Home.cs`, `ClassicDaylistLayout`): Compact stacks the art over the copy (`C-Home-Compact`: 140 art, the copy rising 28
// over it, 40-high actions), Small is a 216 banner with 364 of copy (`C-Home-Small`), Standard is the tuned card, Huge is a 400 hero with 760 of copy at the
// 68/92 display size and the clock a plate in its corner (`C-Home-Huge`), and a Short window caps the hero at a 126 strip carrying the eyebrow and the title only
// (`C-Shell-Short`). The first frame is built at the region's estimated width (`ClassicRegion`), never a fixed 1100.
// Pure rules (core): `DaylistForm` / `DaypartTimeline` / `DaylistCountdownLine` / `DaylistArt` (Items.Rules.cs) and
// `DaypartRules` / `DaylistCountdown` / `DaylistNext`.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.HomeUi;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's daylist card.</summary>
internal static class ClassicHomeDaylist
{
    /// <summary>The card's content, built once per render (or from placeholders for the skeleton) and laid out by
    /// <see cref="CardShape"/> at whatever width the card measures. <c>Art</c> / <c>Veil</c> are the two layers under the
    /// copy — both null for a daylist without art (the plain card); the skeleton has the placeholder
    /// art slot and no veil.</summary>
    readonly record struct Parts(string Eyebrow, string Title, Element Tags, string Meta, Element Actions, Element Clock,
                                 Element? Art, Element? Veil);

    /// <summary>The registered template of <see cref="ViewKeys.HomeDaylist"/>.</summary>
    public static Element Template(HomeDaylistProps props, in ViewContext ctx)
    {
        // The tier's form and the first-frame width are read before any early return: the hooks run on every render, in the same order.
        var facts = ctx.Tier();
        var dl = ClassicHomeTiers.Of(facts).Daylist;
        float firstW = ClassicRegion.EstimateHomeBand(facts, ctx.Hooks.UseContextSignal(Viewport.Size).Peek().Width, hero: true);

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
            PlayButton(dl, Loc.Get(pauses ? Strings.Home.Pause : Strings.Home.Play), Play, pauses ? Icons.Pause : Icons.Play),
            Button.Create(Loc.Get(Strings.Detail.Shuffle), Shuffle, ButtonAppearance.Standard, dl.Buttons, glyph: Icons.Shuffle)
                with { Shrink = 0f },
            Controls.Named(Embed.Comp(() => new Controls.SaveButton { Uri = uri, Name = title, Box = dl.IconBox }) with { Key = "save:" + uri },
                           Loc.Get(Strings.Home.Save)),
            // A STANDALONE icon button, not `Controls.MoreButton`: that one is the list-row overflow whose 0.45-opacity
            // rest + HoverOpacity reveal lights up when its ANCESTOR is hovered — on this card it lit (and pressed) the
            // "…" whenever the card was hovered or pressed.
            Controls.Named(Controls.IconAction(Icons.More, null, requestsContext: true, size: dl.IconBox) with { BlocksDragArm = true },
                           Loc.Get(Strings.Home.More)));

        // The header art across the whole card under the artist hero's horizontal veil; no art ⇒ neither layer (the
        // plain card, like the artist hero's "no url ⇒ flat"). The veil leaf is cover-keyed (a late grading swaps its
        // gradient without rebuilding this card) and keyed on the CARD, the surface, never the url: a new daylist's art
        // re-grades the mounted leaf, which cross-fades its tone instead of remounting. The card's accent is the
        // ladder's payload rung until the grading lands. The Compact stack's veil is vertical (the copy rises over the art's foot).
        string? artUrl = DaylistArt.Of(card.HeaderImageUrl, card.ImageUrl);
        Element? art = artUrl is null ? null : ArtLayer(artUrl);
        Element? veil = artUrl is null ? null
            : Palette.ArtistHeroVeil(artUrl, vertical: dl.Form == ClassicDaylistForm.Stacked, float.NaN, float.NaN, key: "daylist-veil:" + uri,
                                     payloadAccent: card.Accent,
                                     // horizontal: held under the whole copy column; Compact's vertical: held from where the copy rises over the art
                                     hold: dl.Form == ClassicDaylistForm.Stacked ? ClassicHomeRules.StackedVeilHold(dl.ArtH)
                                         : DaylistForm.VeilHold(firstW, dl.CopyPad.Horizontal));

        // The card IS the daylist: a click anywhere outside its buttons opens the playlist (the buttons are nested
        // click targets, so Play/Shuffle/♡/… keep their own action). The tile ramp is the card's hover/press feedback.
        var root = CardShape(new Parts(props.Eyebrow, title, Tags(card), Strings.Home.DaylistMeta(card.TrackCount), actions,
                                       props.Clock ?? new BoxEl(), art, veil), dl, firstW)
            .Interactive(Interaction.Tile) with
            {
                OnClick = () => HomeCardNav.Open(in c), Cursor = CursorId.Hand,
                Role = AutomationRole.Button, Focusable = true,
            };
        // The playlist menu rides the card root: a right-click anywhere on the card and the "…" button (which climbs
        // to this handler through the context funnel) open the SAME menu, the button's anchored at itself.
        return menu is not null && host is not null ? root.WithContextMenu(host, menu) : root;
    }

    /// <summary>The cold-load shape: the SAME builders as <see cref="Template"/>, fed placeholder words, stand-in buttons
    /// and a static ring/timeline. Never early-outs — the seed zone's card is a blank (no uri). The embed's
    /// <see cref="ComponentEl.SkeletonProxy"/>, at the tier's form.</summary>
    internal static Element Skeleton(ClassicHomeLayout layout)
    {
        var dl = layout.Daylist;
        string words = Loc.Get(Strings.Home.YourDaylist);
        var actions = ActionRow(dl,
            PlayButton(dl, Loc.Get(Strings.Home.Play), static () => { }, Icons.Play),
            Button.Create(Loc.Get(Strings.Detail.Shuffle), static () => { }, ButtonAppearance.Standard, dl.Buttons, glyph: Icons.Shuffle)
                with { Shrink = 0f },
            Controls.IconAction(Icons.More, null, size: dl.IconBox),
            Controls.IconAction(Icons.More, null, size: dl.IconBox));
        var current = DaypartRules.OfHour(DateTime.Now.Hour);
        var line = ClassicHomeDaylistClock.Line((TextSpans)new TextSpan[] { new(Strings.Home.NextDaylistIn(DaypartRules.Countdown(0))) });
        // The caption's placeholder is the real rule read at "now": a caption-width line with the next daypart's word.
        string caption = DaylistNext.Caption(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), current,
                                             TimeZoneInfo.Local, System.Globalization.CultureInfo.CurrentCulture);
        float nominal = float.IsNaN(dl.CopyW) ? 2f * DaylistForm.CopyMax : dl.CopyW;
        var clock = ClassicHomeDaylistClock.Shape(ProgressRing.Determinate(0f, ClassicHomeRules.RingSize), line, caption, null, (int)current,
                                                  dl.Clock, TextWidthEstimate(dl, nominal));
        // The art slot as its watched placeholder across the card (the real shape), and no veil: a component the
        // deriver cannot see into would shimmer as one stray bar.
        return CardShape(new Parts(words, Loc.Get(Strings.Home.Layout.Daylist), TagLine(new TextSpan[] { new(words) }, null),
                                   Strings.Home.DaylistMeta(50), actions, clock, ArtLayer(null), null), dl, nominal);
    }

    /// <summary>The primary action at the tier's size; the Compact stack's grows to take the row's slack (<c>C-Home-Compact</c>).</summary>
    static Element PlayButton(ClassicDaylistLayout dl, string label, Action onClick, string glyph)
        => Button.Create(label, onClick, ButtonAppearance.Accent, dl.Buttons, glyph: glyph)
            with { MinWidth = Controls.PrimaryMinWidth, Shrink = 0f, Grow = dl.Form == ClassicDaylistForm.Stacked ? 1f : 0f };

    /// <summary>The copy's text width at a card <paramref name="cardW"/> wide: the clock block's first-frame width.</summary>
    internal static float TextWidthEstimate(ClassicDaylistLayout dl, float cardW)
    {
        float pad = dl.CopyPad.Horizontal;
        return dl.Form switch
        {
            ClassicDaylistForm.Card => DaylistForm.CopyWidth(MathF.Max(0f, cardW - pad)),
            ClassicDaylistForm.Hero => ClassicHomeRules.ClockPlateW - ClassicHomeRules.ClockPlatePadding.Horizontal,
            ClassicDaylistForm.Stacked => MathF.Max(0f, cardW - pad),
            _ => MathF.Max(0f, MathF.Min(dl.CopyW, cardW) - pad),
        };
    }

    /// <summary>The card: a Fluent card frame (border, 8-DIP corners) with NO padding — the art reaches its edges and
    /// the insets ride on the copy (the tier's <see cref="ClassicDaylistLayout.CopyPad"/>) — that clips its layers to its rounded corners
    /// (the border paints after the children, so it stays on top of the art), around the layers built at the card's
    /// width (<see cref="Row"/>). The card is the tier's height: a 320 floor for the tuned card, a fixed 216 / 400 / 126, or the stack's own, each
    /// carried by the layers (the copy's floor, the layered forms' height) and never declared on the card: a crossing between forms (the content tier a
    /// docked panel or a window resize changes) eases the card's size through real layout (<see cref="ClassicTierMotion.SizeReflow"/>), the layers
    /// filling it, so its fill shrinks or grows and the zones under it ride it. A declared height would be mirrored by the view's anchor and hold the
    /// zone at the new height while the card eased inside it.</summary>
    static BoxEl CardShape(Parts parts, ClassicDaylistLayout dl, float firstWidth)
        => Ui.Card(Responsive.Of("home:daylist", w => Row(parts, w, dl), fallback: firstWidth, grow: 1f))
            with
            {
                Padding = default, ClipToBounds = true,
                MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
                Animate = Design.Reduced ? null : ClassicTierMotion.SizeReflow,
            };

    /// <summary>The art layer: <see cref="Controls.CoverFill"/> across the whole card, corner 0 (the card's rounded clip
    /// rounds it), focused a little above centre. A null <paramref name="url"/> is the watched placeholder slot (the
    /// skeleton's).</summary>
    static Element ArtLayer(string? url)
        => Controls.CoverFill(url, 0f, ClassicHomeRules.DaylistArtDecodePx, focusY: ClassicHomeRules.DaylistArtFocusY);

    /// <summary>The card's layers at card width <paramref name="w"/> (the card has no padding, so this is its whole width), in the tier's form.</summary>
    static Element Row(Parts p, float w, ClassicDaylistLayout dl) => dl.Form switch
    {
        ClassicDaylistForm.Stacked => Stacked(p, dl),
        ClassicDaylistForm.Card => CardRow(p, w, dl),
        _ => Layered(p, w, dl),
    };

    /// <summary>The tuned card (Standard): the art, its veil, then the copy over the veil's opaque side —
    /// <see cref="DaylistForm.CopyWidth"/> of the content width inside the copy padding, plus that
    /// padding. Neither layer measures anything, so the copy alone decides the card's height above its 320 floor, and
    /// the stack stretches both layers over the whole card and the copy over its full height (SpaceBetween pins the
    /// clock to the foot). The three slots never move — an absent layer is an empty, hit-test-free box — and the copy
    /// is keyed, so art arriving late mounts the layers without remounting the copy and its clock.</summary>
    static Element CardRow(Parts p, float w, ClassicDaylistLayout dl)
    {
        var pad = dl.CopyPad;
        float text = DaylistForm.CopyWidth(MathF.Max(0f, w - pad.Horizontal));
        var copy = new BoxEl
        {
            Key = "daylist-copy",
            Direction = 1, Width = text + pad.Horizontal, MinWidth = 0f, MinHeight = ClassicHomeRules.DaylistCardMinHeight,
            // SpaceBetween pins the clock to the card's foot; the Gap is its floor when a two-line title fills the card. The card's 320 floor is this
            // column's (the card's own height eases between forms).
            Justify = FlexJustify.SpaceBetween, Padding = pad, Gap = Spacing.XL,
            Children = [Top(p, text, dl, withActions: true), ClockSlot(p.Clock, plate: false)],
        };
        return new BoxEl
        {
            ZStack = true, Grow = 1f, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
            Children = [p.Art ?? NoLayer(), p.Veil ?? NoLayer(), copy],
        };
    }

    /// <summary>The fixed-height forms over a horizontal veil: Small's 216 banner (364 of copy, the clock a line), Short's 126 strip (the eyebrow and the title
    /// only) and Huge's 400 hero (760 of copy, the actions pinned to the foot, the clock a plate in the corner).</summary>
    static Element Layered(Parts p, float w, ClassicDaylistLayout dl)
    {
        var pad = dl.CopyPad;
        float copyW = MathF.Min(dl.CopyW, MathF.Max(0f, w));
        float text = MathF.Max(0f, copyW - pad.Horizontal);
        bool hero = dl.Form == ClassicDaylistForm.Hero;
        bool strip = dl.Form == ClassicDaylistForm.Strip;
        // The hero pins its actions to the foot inside the same top block every form draws them in, so the action row keeps its parent (and its
        // buttons their nodes) across the 1920 crossing.
        Element[] copyKids = strip ? [Top(p, text, dl, withActions: false)]
            : hero ? [Top(p, text, dl, withActions: true, pinActions: true)]
            : [Top(p, text, dl, withActions: true), ClockSlot(p.Clock, plate: false)];
        var copy = new BoxEl
        {
            Key = "daylist-copy",
            Direction = 1, Width = copyW, MinWidth = 0f,
            Justify = strip ? FlexJustify.Start : FlexJustify.SpaceBetween, Padding = pad, Gap = Spacing.S,
            Children = copyKids,
        };
        Element plate = hero
            ? new BoxEl
            {
                AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.End,
                Margin = new Edges4(0f, 0f, ClassicHomeRules.ClockPlateInset, ClassicHomeRules.ClockPlateInset),
                Children = [ClockSlot(p.Clock, plate: true)],
            }
            : NoLayer();
        return new BoxEl
        {
            // The form's fixed height (216 / 400 / 126) is the layers', not the card's (CardShape): they fill the card while its height eases.
            ZStack = true, Grow = 1f, MinWidth = 0f, AlignItems = FlexAlign.Stretch, MinHeight = dl.CardH,
            Children = [p.Art ?? NoLayer(), p.Veil ?? NoLayer(), copy, plate],
        };
    }

    /// <summary>Compact's stack (<c>C-Home-Compact</c>): the art band on top under a vertical veil, the copy rising 28 over its foot with the actions 40 high
    /// and the clock under them. A daylist without art is the copy alone.</summary>
    static Element Stacked(Parts p, ClassicDaylistLayout dl)
    {
        bool hasArt = p.Art is not null;
        var copy = new BoxEl
        {
            Key = "daylist-copy",
            Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Padding = dl.CopyPad, Gap = Spacing.S,
            Margin = hasArt ? new Edges4(0f, -ClassicHomeRules.StackedOverlap, 0f, 0f) : default,
            Children = [Top(p, float.NaN, dl, withActions: true), ClockSlot(p.Clock, plate: false)],
        };
        return new BoxEl
        {
            Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
            Children =
            [
                hasArt
                    ? new BoxEl
                    {
                        Key = "daylist-art", ZStack = true, Height = dl.ArtH, MinWidth = 0f, AlignItems = FlexAlign.Stretch, ClipToBounds = true,
                        Children = [p.Art!, p.Veil ?? NoLayer()],
                    }
                    : NoLayer(),
                copy,
            ],
        };
    }

    /// <summary>The clock in its place: in the copy (every form but the hero) or on the hero's corner plate. The two places are two trees (a crossing
    /// between the hero and another form re-parents the clock), so each is keyed and swaps under the tier cross-fade instead of the clock rebuilding
    /// with a cut.</summary>
    static Element ClockSlot(Element clock, bool plate) => new BoxEl
    {
        Key = plate ? "daylist-clock:plate" : "daylist-clock:copy", Direction = 1, MinWidth = 0f,
        Enter = ClassicTierMotion.SwapFade, Exit = ClassicTierMotion.SwapFade, Transition = ClassicTierMotion.SwapFadeMotion,
        Children = [clock],
    };

    /// <summary>An absent layer's slot: empty and hit-test free.</summary>
    static BoxEl NoLayer() => new() { HitTestVisible = false };

    /// <summary>The copy's top block at text width <paramref name="text"/> (NaN: the column's own): eyebrow · title · tags · meta · the ONE
    /// action row (the strip keeps the eyebrow and the title; the hero parts its actions to the foot).</summary>
    static BoxEl Top(Parts p, float text, ClassicDaylistLayout dl, bool withActions, bool pinActions = false)
    {
        var kids = new List<Element>(5)
        {
            // Compact's stack rises the eyebrow over the art's foot: primary there (secondary read 4 : 1 on the veil), secondary on the plate forms.
            (dl.Form == ClassicDaylistForm.Stacked ? Ui.Body(p.Eyebrow).Primary() : Ui.Body(p.Eyebrow).Secondary())
                with { MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
            TitleRung(p.Title, text, dl) with
            {
                MaxLines = dl.TitleLines, Wrap = dl.TitleLines > 1 ? TextWrap.Wrap : TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                Margin = new Edges4(0f, Spacing.XS, 0f, 0f),
            },
        };
        if (dl.Details)
        {
            kids.Add(p.Tags);
            // Secondary, not tertiary: the copy sits on the cover veil, whose far side lets the art through (tertiary read 3.2 : 1 there).
            kids.Add(Ui.Caption(p.Meta).Secondary() with
            {
                MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                Margin = new Edges4(0f, Spacing.XS, 0f, 0f),
            });
            // The hero's actions sit at the copy's foot: a spacer takes the room between, never less than the copy's own gap.
            if (withActions && pinActions) kids.Add(new BoxEl { Key = "daylist-actions-gap", Grow = 1f, MinHeight = Spacing.S, HitTestVisible = false });
            if (withActions) kids.Add(p.Actions);
        }
        return new BoxEl { Direction = 1, MinWidth = 0f, Grow = pinActions ? 1f : 0f, Children = kids.ToArray() };
    }

    /// <summary>The title's rung: the tuned card's 40/52 <c>HeroTitle</c> while the copy is at least
    /// <see cref="DaylistForm.HeroTitleMinText"/> wide, else the 28/36 display rung at 600 (<see cref="DaylistForm.UseHeroTitle"/>);
    /// the compact forms hold the 28/36 and the Huge hero the 68/92 display size.</summary>
    static TextEl TitleRung(string title, float text, ClassicDaylistLayout dl) => dl.Title switch
    {
        ClassicDaylistTitle.Display68 => Ui.Display(title) with { Weight = 600 },
        ClassicDaylistTitle.Display28 => R.Type.DetailHero(title) with { Weight = 600 },
        _ => DaylistForm.UseHeroTitle(text) ? R.Type.HeroTitle(title) : R.Type.DetailHero(title) with { Weight = 600 },
    };

    /// <summary>The ONE action row: 20 below the meta (12 in the compact forms), 8 apart, never wrapping (the copy's floor fits it).</summary>
    static BoxEl ActionRow(ClassicDaylistLayout dl, params Element[] actions) => new()
    {
        // Keyed: the hero's foot spacer precedes it in the same block, and a positional match would hand its node to the spacer.
        Key = "daylist-actions",
        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Wrap = false, MinWidth = 0f,
        Margin = new Edges4(0f, dl.Form == ClassicDaylistForm.Card ? Spacing.XL : Spacing.M, 0f, 0f),
        Children = actions,
    };

    /// <summary>The tags as ONE 14/20 line: every tag a secondary link span (the engine's Hand cursor over its own laid
    /// rects), " · " separators in secondary too (over the veil, tertiary does not reach 4.5 : 1), one line with an ellipsis. Clicks resolve by span INDEX
    /// (`OnSpanClick`): tags sit at the even indices, separators at the odd ones.</summary>
    static Element Tags(HomeCard card)
    {
        int seedCount = Math.Min(card.SeedCount, ClassicHomeRules.DaylistMaxTags);
        var tags = new List<string>(seedCount);
        for (int i = 0; i < seedCount; i++)
        {
            string tag = card.SeedAt(i);
            if (tag.Length > 0) tags.Add(tag);
        }
        if (tags.Count == 0) return new BoxEl();

        var spans = new TextSpan[tags.Count * 2 - 1];
        for (int i = 0; i < tags.Count; i++)
        {
            if (i > 0) spans[2 * i - 1] = new TextSpan(" · ", Color: Tok.TextSecondary);
            spans[2 * i] = new TextSpan(tags[i], Color: Tok.TextSecondary, IsLink: true);
        }
        return TagLine(spans, i => { if ((i & 1) == 0 && (i >> 1) < tags.Count) HomeVerbs.OpenTag(tags[i >> 1]); });
    }

    /// <summary>The tag line's paragraph: the body rung's metrics (never a hand size), secondary base, 6 below the title.</summary>
    static SpanTextEl TagLine(TextSpan[] spans, Action<int>? onSpanClick)
    {
        var body = Ui.Body("");
        return new SpanTextEl(spans)
        {
            Size = body.Size, LineHeight = body.LineHeight, Color = Tok.TextSecondary,
            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            Margin = new Edges4(0f, Spacing.XS + Spacing.XXS, 0f, 0f),
            OnSpanClick = onSpanClick,
        };
    }
}
