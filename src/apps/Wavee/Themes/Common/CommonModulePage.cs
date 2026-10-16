// ── Themes/Common/CommonModulePage.cs — the module page, a Common block (themes plan 6.5, T10b3) ────────────────────────────
//
// From `Platform\Modules.UI.cs` (§2-§5): the picture of `Pages.Module`. One page for every module and every entity; nothing switches on a module id. A
// module is out of process, so it never renders anything: it answers `module/page` with a small declarative `ModulePageDoc` and this file draws it with
// the app's own vocabulary. Written once for every theme (a Common block); Classic registers it like its own views. `Modules.PageHost` keeps the route
// parse, the load, the claim and the verbs.
//
// TIERS (themes plan 3.2, TC10): the page reads `ctx.Tier()` into a `CommonModuleLayout` (Rules\Tiers.cs): the page gutter (16 at Compact, 32 above) and the max width of the
// stage and the scroller's column (a row holding one capped column, left-aligned: the Huge fill rule), and a hit target for the buttons, chips and links (40 at Compact). The
// hero, the cards, the fact tiles and the chip row already wrap, so Compact needs no other arrangement. A component (the prose toggle, the subtitle link) reads the tier itself.
//
// Three states, owned by the engine: `Skel.Region` derives the shimmer from THIS page rendered against the seed (the cached document on a revisit, else
// a figure-space placeholder), mounts the real page on Ready and the failed body on Failed. Unknown section kinds and action kinds are skipped, never fatal.
//
// THE STAGE LIVES OUTSIDE THE SCROLLER (ch 24 rule 1). It hosts the app's one composited video — a DestOut hole — so no ancestor may carry an opacity, a
// transition, an offscreen RT or a scroller's edge fade. The skeleton region's reveal, the sections' FadeUp and the ScrollView are all such ancestors; the
// stage is a sibling above them, and the stage slot is an EMPTY box for a non-watch document so the scroller is never re-parented by a template change.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Sdk;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using static Wavee.Modules;

namespace Wavee.Themes.Common;

/// <summary>The module page (the Common block every structure registers).</summary>
internal static class CommonModulePage
{
    static readonly EnterExit SectionFadeUp = new(Opacity: 0f, Active: true);
    static readonly LayoutTransition SectionShove = new(TransitionChannels.Position, TransitionDynamics.Tween(Expressive.Fast, Easing.SmoothOut));
    static readonly Action NoOp = static () => { };

    /// <summary>The registered template of <see cref="ViewKeys.ModulePage"/>.</summary>
    public static Element Template(ModulePageProps props, in ViewContext ctx)
    {
        if (!props.Parsed) return Controls.Vacancy(Controls.VacancyVoice.Error, title: Loc.Get(Strings.ModulePage.Error));

        // BOTH reads subscribe: the stage derives from the document AND from what is playing.
        Loadable<ModulePageDoc> loadable = props.Loadable;
        string moduleId = props.ModuleId, pageUri = props.PageUri;
        string? routeArg = props.RouteArg;
        ModulePageDoc live = loadable.Value.Value;
        bool ready = loadable.State.Value == (byte)LoadState.Ready;
        string nowUri = PlayingModuleUri();
        string stagePlayable = ready ? Pages.StagePlayableUri(moduleId, live) : "";
        WatchPageModel? model = ready ? WatchPageModel.From(live, Pages.IsPlayingEntity(stagePlayable, nowUri)) : null;
        Action retry = props.Retry;
        var layout = CommonModuleLayout.For(ctx.Tier());
        float target = layout.Target;

        var body = Skel.Region(loadable,
            content: doc => LoadedBody(doc, moduleId, routeArg, target),
            reveal: SkelReveal.Soft,
            onFailed: () => FailedBody(loadable.Error, retry, pageUri, target),
            group: "module-page:" + pageUri);

        return new BoxEl
        {
            Direction = 1, Grow = 1f, MinHeight = 0f,
            Padding = new Edges4(0f, BrowseMastheadMetrics.Reserve, 0f, 0f),
            Children =
            [
                model is null
                    ? new BoxEl { Key = "module-stage-slot" }
                    : new BoxEl
                    {
                        // The props of the stage freeze inside it, so its identity and poster are folded into the key.
                        Key = "module-stage-slot:" + stagePlayable + "|" + (model.PosterUrl ?? ""),
                        Direction = 0, Padding = new Edges4(layout.Gutter, 0f, layout.Gutter, 0f),
                        Children = [Capped(layout, Stage(stagePlayable, model.PosterUrl))],
                    },
                ScrollView(new BoxEl
                {
                    Direction = 0, MinWidth = 0f,
                    Padding = new Edges4(layout.Gutter, CommonModuleRules.ScrollPad, layout.Gutter, R.Space.DockReserve + CommonModuleRules.ScrollPad),
                    Children = [Capped(layout, body)],
                }) with { Grow = 1f, MinHeight = 0f, ScrollKey = props.ScrollKey },
            ],
        };
    }

    /// <summary>One column that grows to the tier's max width and stops, at the page's left edge (the Huge fill rule).</summary>
    static Element Capped(CommonModuleLayout layout, Element content)
        => new BoxEl { Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MaxWidth = layout.MaxWidth, Children = [content] };

    /// <summary>The watch stage (G-151): <c>Video.WatchStage</c>, the video host's mount point — the poster ground, the
    /// idle play disc, and the one video surface once this page's playable is what plays and the arbitration hands the
    /// surface to the page. Outside the scroller, the region and every section (see the file header).</summary>
    static Element Stage(string stagePlayable, string? posterUrl)
        => Video.WatchStage(stagePlayable, Trimmed(posterUrl), stagePlayable.Length == 0 ? null : () => PlayOrToggle(stagePlayable));

    /// <summary>The failed state: the app's one vacancy grammar with Retry, plus this surface's escape hatch — when the
    /// module already told us a web url for the entity, the user can still go and look at it.</summary>
    static Element FailedBody(Exception? error, Action retry, string pageUri, float target)
    {
        string? subtitle = error is ModuleException { Message.Length: > 0 } me ? me.Message : null;
        var kids = new List<Element>(2)
        {
            Controls.Vacancy(Controls.VacancyVoice.Error, title: Loc.Get(Strings.ModulePage.Error), subtitle: subtitle, onAction: retry),
        };
        if (Pages.OpenUrlOf(Pages.Get(pageUri)) is { } url)
            kids.Add(new BoxEl
            {
                Direction = 0, Justify = FlexJustify.Center, Padding = new Edges4(0f, Spacing.S, 0f, 0f),
                Children = [CommonTouch.Button(Loc.Get(Strings.ModulePage.OpenInBrowser), () => OpenUrl(url), target)],
            });
        return new BoxEl { Key = "module-failed", Direction = 1, Grow = 1f, Children = kids.ToArray() };
    }

    /// <summary>The loaded page: the watch reading when the template asks for it, the entity/custom reading otherwise.</summary>
    static Element LoadedBody(ModulePageDoc doc, string moduleId, string? routeArg, float target)
    {
        string stagePlayable = Pages.StagePlayableUri(moduleId, doc);
        return WatchPageModel.From(doc, Pages.IsPlayingEntity(stagePlayable, PlayingModuleUri())) is { } watch
            ? WatchCaption(watch, moduleId, doc, target)
            : EntityBody(doc, moduleId, routeArg, target);
    }

    // ══ 1. THE ENTITY READING ══════════════════════════════════════════════════════════════════════════════════════

    static Element EntityBody(ModulePageDoc doc, string moduleId, string? routeArg, float target)
    {
        var kids = new List<Element>(8);
        PageHero? hero = Pages.HeroFor(doc, routeArg, Loc.Get(Strings.ModulePage.Title));
        if (hero is not null) kids.Add(Hero(hero, moduleId));
        if (ActionRow(doc.Actions, moduleId, target) is { } actions) kids.Add(actions);

        // The budgets are the module's contract; re-applied here because a page arrives over a pipe and this is the last
        // place that can refuse to draw 4,000 rows. Items are spent in document order.
        PageSection[] sections = doc.Sections ?? [];
        int budget = ModulePageBudget.MaxItems, drawn = 0;
        for (int i = 0; i < sections.Length && drawn < ModulePageBudget.MaxSections; i++)
        {
            if (sections[i] is not { } section || SectionBlock(section, i, moduleId, ref budget, target) is not { } block) continue;
            kids.Add(block);
            drawn++;
        }
        return new BoxEl { Key = "module-entity", Direction = 1, Gap = Spacing.XL, MinWidth = 0f, Children = kids.ToArray() };
    }

    static Element Hero(PageHero hero, string moduleId)
    {
        var text = new List<Element>(5);
        if (Trimmed(hero.Eyebrow) is { } eyebrow) text.Add(R.Type.Eyebrow(eyebrow) with { Key = "hero:eyebrow" });

        // "Live" is a property of the thing named, so the badge sits beside the name rather than in the meta line.
        var titleRow = new List<Element>(2) { R.Type.DetailHero(hero.Title) with { MinWidth = 0f, Shrink = 1f, Wrap = TextWrap.Wrap, MaxLines = CommonModuleRules.HeroTitleLines } };
        if (hero.IsLive) titleRow.Add(LiveBadge());
        text.Add(new BoxEl { Key = "hero:title", Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f, Children = titleRow.ToArray() });

        if (Trimmed(hero.Subtitle) is { } subtitle)
        {
            string? route = Pages.RouteForEntity(moduleId, Trimmed(hero.SubtitleEntityId));
            text.Add(Embed.Comp(() => new MetaLink(subtitle, route)) with { Key = "hero:subtitle:" + subtitle + "|" + (route ?? "") });
        }
        if (Trimmed(hero.MetaLine) is { } meta)
            text.Add(R.Type.TrackMeta(meta) with { Key = "hero:meta", MaxLines = CommonModuleRules.HeroMetaLines, Wrap = TextWrap.Wrap, MinWidth = 0f });

        return new BoxEl
        {
            Key = "module-hero", Direction = 0, Gap = Spacing.XL, AlignItems = FlexAlign.Center, MinWidth = 0f, Wrap = true,
            Children =
            [
                // 256 is the shared decode rung: a page opened from a card reuses that card's texture.
                Controls.Artwork(Trimmed(hero.ImageUrl), CommonModuleRules.HeroArt, CommonModuleRules.HeroArt, Radii.Card, decodePx: CommonModuleRules.HeroArtDecodePx),
                new BoxEl
                {
                    Direction = 1, Gap = Spacing.S, Grow = 1f, Basis = 0f, MinWidth = CommonModuleRules.HeroCopyMinWidth, Justify = FlexJustify.Center,
                    Children = text.ToArray(),
                },
            ],
        };
    }

    /// <summary>The LIVE word-mark — the player bar's grammar (hairline outline in the accent decor ink, caps at caption
    /// scale), shared by both readings so "live" reads identically wherever the page says it.</summary>
    static Element LiveBadge() => new BoxEl
    {
        Key = "badge:live", Shrink = 0f, Height = CommonModuleRules.BadgeHeight, Padding = new Edges4(Spacing.XS, 0f, Spacing.XS, 0f),
        Corners = CornerRadius4.All(CommonModuleRules.BadgeRadius), BorderWidth = 1f, BorderColor = R.Ink.AccentDecor,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Children = [new TextEl(Loc.Get(Strings.Play.Live)) { Size = CommonModuleRules.BadgeSize, LineHeight = CommonModuleRules.BadgeLine, Weight = 600, Color = R.Ink.AccentDecor, Wrap = TextWrap.NoWrap }],
    };

    static Element? ActionRow(PageAction[]? actions, string moduleId, float target)
    {
        if (actions is not { Length: > 0 }) return null;
        var kids = new List<Element>(actions.Length);
        foreach (PageAction? a in actions)
        {
            if (a is null || Trimmed(a.Label) is not { } label) continue;
            if (InvokeFor(moduleId, a.Kind, a.Id, a.PlayableId, a.Url) is not { } invoke) continue;
            kids.Add(CommonTouch.Button(label, invoke, target, accent: a.Primary) with { Key = "action:" + a.Id + ":" + a.Kind });
        }
        if (kids.Count == 0) return null;
        return new BoxEl
        {
            Key = "module-actions", Enter = SectionFadeUp, Layout = SectionShove,
            Direction = 0, Gap = Spacing.S, Wrap = true, AlignItems = FlexAlign.Center, Children = kids.ToArray(),
        };
    }

    static Element? SectionBlock(PageSection section, int index, string moduleId, ref int budget, float target)
    {
        string key = "sec:" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + section.Kind;
        switch (section.Kind)
        {
            case PageSection.KindText:
                return Trimmed(section.Text) is { } text
                    ? Section(key, section.Title, Embed.Comp(() => new ExpandableText(text, CommonModuleRules.TextLines)) with { Key = key + ":text:" + text.GetHashCode() })
                    : null;
            case PageSection.KindFacts: return FactsBlock(key, section, ref budget);
            case PageSection.KindPlayables: return PlayablesBlock(key, section, moduleId, ref budget);
            case PageSection.KindCards: return CardsBlock(key, section, moduleId, ref budget);
            case PageSection.KindLinks: return LinksBlock(key, section, ref budget, target);
            default: return null;   // unknown ⇒ skipped; the rest of the page still renders
        }
    }

    /// <summary>A section shell: the rail header over a body, carrying the block motion (fade up as it lands, FLIP as its
    /// siblings shove it). The page root carries none — the shell's page host already slides it.</summary>
    static Element Section(string key, string? title, Element body) => new BoxEl
    {
        Key = key, Enter = SectionFadeUp, Layout = SectionShove,
        Direction = 1, Gap = Spacing.M, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
        Children = Trimmed(title) is { } t ? [R.Type.RailHeader(t), body] : [body],
    };

    static Element? FactsBlock(string key, PageSection section, ref int budget)
    {
        string[][] rows = section.Rows ?? [];
        var tiles = new List<Element>(rows.Length);
        for (int i = 0; i < rows.Length && budget > 0; i++)
        {
            if (rows[i] is not { Length: >= 2 } row || Trimmed(row[1]) is not { } value) continue;
            tiles.Add(new BoxEl
            {
                Key = key + ":tile:" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Width = CommonModuleRules.FactTileWidth, Shrink = 0f,
                Children = [Controls.StatTile(key + ":" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), value, Trimmed(row[0]) ?? "")],
            });
            budget--;
        }
        if (tiles.Count == 0) return null;
        return Section(key, section.Title, new BoxEl
        {
            Key = key + ":tiles", Direction = 0, Gap = Spacing.S, Wrap = true,
            Stagger = Design.Reduced ? 0f : R.Motion.MastheadStaggerMs, Children = tiles.ToArray(),
        });
    }

    static Element? PlayablesBlock(string key, PageSection section, string moduleId, ref int budget)
    {
        PageItem[] items = section.Items ?? [];
        int take = Pages.Take(items.Length, budget);
        var rows = new List<Element>(take);
        for (int i = 0; i < take; i++)
        {
            if (items[i] is not { } item || Trimmed(item.Title) is not { } title || Trimmed(item.PlayableId) is not { } playableId) continue;
            string uri = ModuleUri.Encode(moduleId, playableId);
            Action play = () => PlayOrToggle(uri);
            Element? subtitle = Trimmed(item.Subtitle) is { } sub
                ? R.Type.TrackMeta(sub) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f } : null;
            Element? trailing = Trimmed(item.Meta) is { } meta ? R.Type.TrackMeta(meta) with { Shrink = 0f } : null;
            rows.Add(Controls.Surface(new Controls.CardData(uri, title, subtitle, Trimmed(item.ImageUrl), play, play)
                { Trailing = item.IsLive ? LiveBadge() : trailing }, Shape.Row(CommonModuleRules.PlayableRowHeight)) with { Key = key + ":row:" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }
        budget -= take;
        if (rows.Count == 0) return null;
        return Section(key, section.Title, new BoxEl { Key = key + ":rows", Direction = 1, Gap = CommonModuleRules.ListGap, MinWidth = 0f, Children = rows.ToArray() });
    }

    static Element? CardsBlock(string key, PageSection section, string moduleId, ref int budget)
    {
        PageItem[] items = section.Items ?? [];
        int take = Pages.Take(items.Length, budget);
        var cards = new List<Element>(take);
        for (int i = 0; i < take; i++)
        {
            if (items[i] is not { } item || Trimmed(item.Title) is not { } title) continue;
            // A card navigates to another page of the SAME module when the module named one, else opens its url, else
            // plays. Nothing invents a destination the module did not state.
            string? route = Pages.RouteForEntity(moduleId, Trimmed(item.EntityId));
            string? playable = Trimmed(item.PlayableId) is { } pid ? ModuleUri.Encode(moduleId, pid) : null;
            string? url = item.Url;
            Action? play = playable is null ? null : () => PlayOrToggle(playable!);
            Action open = route is not null ? () => Navigate(route!, title)
                : Actions.PlayLinkRules.IsWebUrl(url) ? () => OpenUrl(url)
                : play ?? NoOp;
            Element? subtitle = Trimmed(item.Subtitle) is { } sub
                ? R.Type.TrackMeta(sub) with { MaxLines = CommonModuleRules.CardSubtitleLines, Wrap = TextWrap.Wrap, MinWidth = 0f } : null;
            cards.Add(Controls.Surface(new Controls.CardData(playable ?? route ?? "", title, subtitle, Trimmed(item.ImageUrl), open, play), Shape.Shelf(captionLines: CommonModuleRules.CardSubtitleLines), CommonModuleRules.CardWidth) with { Key = key + ":card:" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }
        budget -= take;
        if (cards.Count == 0) return null;
        return Section(key, section.Title, new BoxEl { Key = key + ":cards", Direction = 0, Gap = Spacing.M, Wrap = true, MinWidth = 0f, Children = cards.ToArray() });
    }

    static Element? LinksBlock(string key, PageSection section, ref int budget, float target)
    {
        PageItem[] items = section.Items ?? [];
        int take = Pages.Take(items.Length, budget);
        var rows = new List<Element>(take);
        for (int i = 0; i < take; i++)
        {
            if (items[i] is not { } item || Trimmed(item.Title) is not { } title || !Actions.PlayLinkRules.IsWebUrl(item.Url)) continue;
            string url = item.Url!;
            var text = new List<Element>(2) { R.Type.TrackTitle(title) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f } };
            if (Trimmed(item.Subtitle) is { } sub) text.Add(R.Type.TrackMeta(sub) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f });
            rows.Add(new BoxEl
            {
                Key = key + ":link:" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f, MinHeight = target,
                Padding = new Edges4(Spacing.M, Spacing.S, Spacing.M, Spacing.S),
                Corners = Radii.ControlAll, Cursor = CursorId.Hand, OnClick = () => OpenUrl(url),
                Role = AutomationRole.Hyperlink, Focusable = true,
                Children =
                [
                    new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Children = text.ToArray() },
                    Icon(Icons.OpenInNewWindow, CommonModuleRules.LinkGlyph, Tok.TextTertiary),
                ],
            }.Interactive(Interaction.ListRow));
        }
        budget -= take;
        if (rows.Count == 0) return null;
        return Section(key, section.Title, new BoxEl { Key = key + ":links", Direction = 1, Gap = CommonModuleRules.ListGap, MinWidth = 0f, Children = rows.ToArray() });
    }

    // ══ 2. THE WATCH READING (ch 24's module page) ═════════════════════════════════════════════════════════════════

    /// <summary>Everything UNDER the picture: the title one rung below the page hero (the picture is the identity), the
    /// LIVE badge + meta line, the channel row, the document's actions as capsules, the description card with the fact
    /// line folded into its top, and the 16:9 shelf.</summary>
    static Element WatchCaption(WatchPageModel model, string moduleId, ModulePageDoc doc, float target)
    {
        var kids = new List<Element>(6)
        {
            R.Type.NowPlayingTitle(model.Title) with { Key = "watch:title", MaxLines = CommonModuleRules.TitleLines, Wrap = TextWrap.Wrap, MinWidth = 0f },
        };
        if (model.IsLive || model.MetaLine is { Length: > 0 }) kids.Add(MetaRow(model));
        if (model.ChannelName is { Length: > 0 }) kids.Add(ChannelRow(model, moduleId));
        if (ChipRow(model, moduleId, target) is { } chips) kids.Add(chips);
        if (DescriptionCard(model) is { } card) kids.Add(card);
        if (WatchShelf(model, moduleId) is { } shelf) kids.Add(shelf);
        return new BoxEl { Key = "watch-caption", Direction = 1, Gap = Spacing.M, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Children = kids.ToArray() };
    }

    static Element MetaRow(WatchPageModel model)
    {
        var kids = new List<Element>(2);
        if (model.IsLive) kids.Add(LiveBadge());
        if (model.MetaLine is { Length: > 0 } meta) kids.Add(R.Type.TrackMeta(meta) with { MinWidth = 0f, Shrink = 1f, MaxLines = CommonModuleRules.HeroMetaLines, Wrap = TextWrap.Wrap });
        return new BoxEl { Key = "watch:meta", Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = kids.ToArray() };
    }

    /// <summary>The owner row: avatar + name. It navigates when the document named an entity, and is plain text when it
    /// did not — a styled-but-dead link is a lie.</summary>
    static Element ChannelRow(WatchPageModel model, string moduleId)
    {
        string name = model.ChannelName!;
        string? route = Pages.RouteForEntity(moduleId, model.ChannelEntityId);
        bool linked = route is not null;
        var row = new BoxEl
        {
            Key = "watch:channel", Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, MinWidth = 0f, AlignSelf = FlexAlign.Start,
            Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.M, Spacing.XS), Corners = Radii.FullAll,
            Cursor = linked ? CursorId.Hand : null, OnClick = linked ? () => Navigate(route!, name) : null,
            Role = linked ? AutomationRole.Hyperlink : AutomationRole.Text, Focusable = linked,
            Children =
            [
                PersonPicture.Create("", CommonModuleRules.ChannelAvatar, displayName: name, imageSourcePath: model.ChannelAvatarUrl),
                R.Type.TrackTitle(name) with { MinWidth = 0f, Shrink = 1f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            ],
        };
        return linked ? row.Interactive(Interaction.ListRow) : row;
    }

    /// <summary>The document's actions as capsules. The play capsule is replaced by an inert "Playing" state capsule
    /// while this page's playable is the item in the bar; nothing is invented (no Save, no Share).</summary>
    static Element? ChipRow(WatchPageModel model, string moduleId, float target)
    {
        var kids = new List<Element>(model.Chips.Length + 1);
        bool playing = model.Stage == WatchStageKind.Live;
        foreach (WatchChip chip in model.Chips)
        {
            bool play = string.Equals(chip.Kind, PageAction.KindPlay, StringComparison.Ordinal);
            if (play && playing) { kids.Add(PlayingChip()); continue; }
            if (InvokeFor(moduleId, chip.Kind, chip.Id, chip.PlayableId, chip.Url) is not { } invoke) continue;
            string? glyph = play ? Icons.Play : string.Equals(chip.Kind, PageAction.KindOpenUrl, StringComparison.Ordinal) ? Icons.OpenInNewWindow : null;
            kids.Add((chip.Primary
                ? CommonTouch.Lift(Controls.PrimaryButton(chip.Label, invoke, Tok.AccentDefault, glyph), target)
                : CommonTouch.Button(chip.Label, invoke, target, glyph: glyph))
                with { Key = "chip:" + chip.Id + ":" + chip.Kind });
        }
        if (kids.Count == 0) return null;
        return new BoxEl { Key = "watch:chips", Direction = 0, Gap = Spacing.S, Wrap = true, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = kids.ToArray() };
    }

    static BoxEl PlayingChip() => new()
    {
        Key = "chip:playing", Height = Controls.ButtonHeight, Shrink = 0f, Direction = 0, Gap = Spacing.S,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Padding = new Edges4(CommonModuleRules.PlayingChipPad, 0f, CommonModuleRules.PlayingChipPad, 0f),
        Corners = Radii.ControlAll, Fill = Tok.FillSubtleSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
        Role = AutomationRole.Text,
        Children =
        [
            Icon(Icons.Play, CommonModuleRules.PlayingChipGlyph, R.Ink.AccentDecor),
            Ui.Caption(Loc.Get(Strings.ModulePage.Playing)) with { Weight = 600, Wrap = TextWrap.NoWrap },
        ],
    };

    /// <summary>The description card: the dissolved fact line in bold over the prose. Where the fact tiles WENT.</summary>
    static Element? DescriptionCard(WatchPageModel model)
    {
        bool facts = model.FactLine is { Length: > 0 }, prose = model.Description is { Length: > 0 };
        if (!facts && !prose) return null;
        var kids = new List<Element>(2);
        if (facts) kids.Add(Ui.Body(model.FactLine!) with { Key = "watch:facts", Weight = 600, MinWidth = 0f });
        if (prose) kids.Add(Embed.Comp(() => new ExpandableText(model.Description!, CommonModuleRules.DescriptionLines)) with { Key = "watch:desc:" + model.Description!.GetHashCode() });
        return new BoxEl
        {
            Key = "watch:description", Direction = 1, Gap = Spacing.S, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
            Padding = new Edges4(Spacing.L, Spacing.M, Spacing.L, Spacing.M), Corners = CornerRadius4.All(Radii.Card),
            Fill = Tok.FillCardSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, Children = kids.ToArray(),
        };
    }

    /// <summary>The 16:9 shelf: a measured paged shelf of video cards. A cell NAVIGATES by entity id and PLAYS by
    /// playable id, mirroring the entity reading's cards.</summary>
    static Element? WatchShelf(WatchPageModel model, string moduleId)
    {
        if (model.Shelf.Length == 0) return null;
        return new BoxEl
        {
            Key = "watch:shelf", Direction = 1, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
            Children =
            [
                PagedShelf.Create(model.Shelf, (item, _, w) => VideoCard(item, moduleId, w),
                    onInvoke: (item, _) => OpenVideo(item, moduleId),   // the slot owns the click: the card's own open
                    header: model.ShelfTitle is { Length: > 0 } t ? Controls.SectionHeader(t) : null,
                    minCardW: CommonModuleRules.ShelfCardMin, maxCardW: CommonModuleRules.ShelfCardMax, measured: true,
                    keyOf: static (item, i) => item.EntityId ?? item.PlayableId ?? i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    maxItems: CommonModuleRules.ShelfMax),
            ],
        };
    }

    /// <summary>A 16:9 shelf cell: the shared surface's <see cref="Shape.Video"/> — the FAB at rest when the item names a
    /// playable (no playable, no dead FAB), the now-playing overlay keyed on that playable's uri, the hover plate and the
    /// hand cursor — over the thumb at 16:9, a 2-line title and the subtitle · meta as the caption. A module's item is
    /// neither a track nor an entity the app owns, so it carries no menu and no drag.</summary>
    static Element VideoCard(WatchItem item, string moduleId, float width)
    {
        string? playable = item.PlayableId is { Length: > 0 } pid ? ModuleUri.Encode(moduleId, pid) : null;
        string? route = Pages.RouteForEntity(moduleId, item.EntityId);
        Action open = () => OpenVideo(item, moduleId);
        Action? play = playable is null ? null : () => PlayOrToggle(playable!);
        string meta = item.Subtitle is { Length: > 0 } s
            ? item.Meta is { Length: > 0 } m ? s + WatchPageModel.FactSeparator + m : s
            : item.Meta ?? "";
        var data = new Controls.CardData(playable ?? route ?? "", item.Title, null, item.ImageUrl, open, play)
        {
            CoverAspect = CommonModuleRules.VideoAspect,
            Caption = meta.Length > 0 ? meta : null,
        };
        return Controls.Surface(data, WatchVideoShape, Math.Max(CommonModuleRules.VideoCardMinWidth, width));
    }

    /// <summary>A shelf cell's click, shared with the shelf slot's <c>onInvoke</c>: the item's entity page, else its playable
    /// (a cell that names neither does nothing).</summary>
    static void OpenVideo(WatchItem item, string moduleId)
    {
        if (Pages.RouteForEntity(moduleId, item.EntityId) is { } route) Navigate(route, item.Title);
        else if (item.PlayableId is { Length: > 0 } pid) PlayOrToggle(ModuleUri.Encode(moduleId, pid));
    }

    /// <summary>The watch shelf's card: the video shape with the two-line title this shelf has always allowed.</summary>
    static SurfaceShape WatchVideoShape => Shape.Video with { TitleLines = CommonModuleRules.TitleLines };

    // ══ 3. SMALL COMPONENTS ════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Plain module prose, clamped to <c>lines</c> with a quiet More/Less toggle. PLAIN text on purpose: a
    /// module's description is not markup, and nothing a module writes is parsed as a link.</summary>
    sealed class ExpandableText(string text, int lines) : CommonTierComponent
    {
        readonly Signal<bool> _expanded = new(false);

        public override Element Render()
        {
            float target = CommonModuleLayout.For(ReadTier()).Target;
            bool expanded = _expanded.Value;
            bool long_ = text.Length > lines * CommonModuleRules.CharsPerLine || text.AsSpan().Count('\n') >= lines;
            var kids = new List<Element>(2)
            {
                new TextEl(text)
                {
                    Size = CommonModuleRules.TextSize, LineHeight = CommonModuleRules.TextLine, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f,
                    MaxLines = expanded ? 0 : lines, Trim = expanded ? TextTrim.None : TextTrim.CharacterEllipsis,
                },
            };
            if (long_)
                kids.Add(CommonTouch.Link(Loc.Get(expanded ? Strings.Common.Less : Strings.Common.More),
                    () => _expanded.Value = !_expanded.Peek(), target, ControlSize.Small) with { AlignSelf = FlexAlign.Start });
            return new BoxEl { Direction = 1, Gap = Spacing.XS, MinWidth = 0f, Children = kids.ToArray() };
        }
    }

    /// <summary>The hero subtitle: the player bar's meta-link grammar (hover recolours THIS word), navigating to the page
    /// the document named. Plain text when there is nowhere to go.</summary>
    sealed class MetaLink(string text, string? route) : CommonTierComponent
    {
        readonly Signal<bool> _hover = new(false);

        public override Element Render()
        {
            bool enabled = route is not null;
            bool hover = enabled && _hover.Value;
            float tierTarget = CommonModuleLayout.For(ReadTier()).Target;
            float target = enabled ? tierTarget : 0f;
            return new BoxEl
            {
                MinWidth = 0f, MinHeight = target, Shrink = 1f, AlignSelf = FlexAlign.Start, AlignItems = FlexAlign.Center,
                Cursor = enabled ? CursorId.Hand : null,
                OnClick = enabled ? () => Navigate(route!, text) : null,
                OnHoverMove = enabled ? _ => { if (!_hover.Peek()) _hover.Value = true; } : null,
                OnPointerExit = enabled ? () => { if (_hover.Peek()) _hover.Value = false; } : null,
                Role = enabled ? AutomationRole.Hyperlink : AutomationRole.Text, Focusable = enabled,
                Children =
                [
                    new TextEl(text)
                    {
                        Size = CommonModuleRules.TextSize, LineHeight = CommonModuleRules.TextLine, Color = hover ? Tok.TextPrimary : Tok.TextSecondary, Underline = hover,
                        Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    },
                ],
            };
        }
    }
}
