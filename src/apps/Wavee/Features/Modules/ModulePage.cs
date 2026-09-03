using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Backend.Modules;
using Wavee.Core;
using Wavee.Features.Browse;
using Wavee.Sdk;
using static FluentGpu.Dsl.Ui;
// `MediaForm` is declared in BOTH Wavee.Core (the app's play-as intent: Default/Audio/Video) and Wavee.Sdk (what a
// module says a playable IS). They mean different things and both belong in this file, so neither gets to be the
// unqualified one.
using SdkForm = Wavee.Sdk.MediaForm;

namespace Wavee;

/// <summary>
/// The ONE page a playback module describes (Part 9). A module is out of process, so it never renders anything: it
/// answers <c>module/page</c> with a small declarative <see cref="ModulePageDoc"/> and THIS class draws it with the
/// app's own detail vocabulary — the same hero type ramp, fact tiles, track rows and card shelves an album page uses.
/// One page class for every module and every entity kind; nothing here switches on a module id.
///
/// <para><b>Three states, owned by the engine.</b> <c>Skel.Region</c> derives the loading shimmer from THIS page
/// rendered against the resource's own seed document (a hero with placeholder runs → a cover shimmer plus two bars),
/// mounts the real page on Ready, and mounts <see cref="ErrorState"/> on Failed. There is no hand-authored second
/// tree and no by-hand state branch.</para>
///
/// <para><b>Unknown is skipped, never fatal.</b> A section kind this build does not know is dropped and the rest of
/// the page still renders, which is what lets a module ship a new kind before the app understands it. The document's
/// budgets are enforced here too — the module's SDK checks them, but "the SDK checked it" is only true of a module
/// that used the SDK.</para>
///
/// <para><b>No page-level entrance.</b> ContentHost's keep-alive boundary already slides the whole page in; a second
/// entrance on the page root would double-animate the swap. The SECTIONS carry the motion instead
/// (<c>DetailRail.FadeUp</c> + <c>DetailRail.Shove</c>), exactly like the detail surface's trailing blocks.</para>
///
/// <para><b>Two readings, one shell (<see cref="ModulePageDoc.TemplateWatch"/>).</b> A watch document is drawn as a
/// YouTube-shaped page: a full-width 16:9 <see cref="WatchPageView.Stage"/> pinned above the scroller and
/// <see cref="WatchPageView.Caption"/> inside it, instead of the square hero + fact tiles below. Everything ELSE on
/// this class — the route parse, the fetch, the skeleton, the failed state, the retry, the whole entity layout — is
/// shared verbatim, because the template asks for a different READING of the same document, never a second schema.
/// The stage slot is an empty box for a non-watch document, so the container is structurally identical in both cases
/// and nothing re-parents when a module starts (or stops) emitting the template.</para>
///
/// <para><b>Why the stage lives OUTSIDE the scroller.</b> It hosts a real composited video, which is a DestOut hole
/// punched into the back buffer: an ancestor opacity, blur or edge-fade erases it silently. The page
/// <c>ScrollView</c>, <c>Skel.Region</c>'s reveal and <c>Section</c>'s <c>DetailRail.FadeUp</c> are all exactly such
/// ancestors — see <see cref="WatchPageView"/>'s class doc for the full three-hazard case.</para>
/// </summary>
sealed class ModulePage : Component
{
    // Props freeze at mount, and that is correct here: ContentHost keys the page by its whole route, so a different
    // entity is a different keep-alive slot and therefore a different mount — never this instance re-pointed.
    readonly Route _route;

    public ModulePage(Route route) => _route = route;

    // ── the loading seed ────────────────────────────────────────────────────────────────────────────────────────────
    // Figure spaces (U+2007): they MEASURE like digits and paint nothing, so the derived shimmer gets a hero title bar
    // and two shorter lines at believable widths without any string that could flash as real copy.
    const string SeedTitle = "            ";
    const string SeedLine = "        ";
    const string SeedMeta = "     ";

    static readonly ModulePageDoc Seed = new(
        ModulePageDoc.CurrentVersion, ModulePageDoc.TemplateEntity,
        new PageHero(SeedTitle, null, SeedLine, null, SeedMeta, false),
        [], [], null);

    const float HeroArt = 232f;
    const float CardWidth = 168f;

    public override Element Render()
    {
        var go = UseContext(HistoryStore.NavCtx);
        var acts = UseContext(ActionServices.Slot);
        var bridge = UseContext(PlaybackBridge.Slot);
        var lib = UseContext(LibraryBridge.Slot);
        var overlay = UseContext(Overlay.Service);
        var ui = UseContext(ShellUi.Slot);   // the page-stage half of the video-host arbitration

        // The route carries BOTH halves (module id + module-private entity id) — `module:wavee:module:<id>:<b64>` — so
        // there is nothing to look up before the fetch and nothing to keep in step.
        if (!ModulePages.TryParseRoute(_route.Name, out string moduleId, out string entityId))
            return EmptyState.Build(Loc.Get(Strings.ModulePage.Error));

        string pageUri = ModuleUri.Encode(moduleId, entityId);
        var host = ModuleHost.Current;

        var reload = UseSignal(0);
        int gen = reload.Value;   // subscribe → Retry re-fires the load

        // The SEED is the sync page cache when it holds this entity. A revisit then paints its stage on the first
        // frame instead of measuring an empty box and reflowing the whole column when the fetch lands — and a cold
        // page still gets the figure-space placeholder, so nothing ever flashes real copy it does not have.
        var page = UseResource(
            ct => host is null
                ? System.Threading.Tasks.Task.FromException<ModulePageDoc>(
                    new ModuleException(ModuleErrorCode.Unsupported, "no module host"))
                : host.PageAsync(pageUri, ct),
            ModulePages.Get(pageUri) ?? Seed, (pageUri, gen)).Loadable;

        // BOTH reads subscribe, and both must: the stage is derived from the document AND from what is playing, so a
        // page that peeked would light its picture one navigation late (SubtitleRoute peeks for the opposite reason —
        // it is a one-shot link frozen into the mounted span).
        ModulePageDoc live = page.Value.Value;
        string? nowUri = bridge?.CurrentTrack.Value?.Uri;
        string stagePlayable = StagePlayableUri(moduleId, live);
        var model = WatchPageModel.From(live, IsPlayingEntity(stagePlayable, nowUri));

        // THE writer of ShellUi.ActiveStagePlayable, and it must be this class: only the page holds the DOCUMENT, and
        // only the document names the playable behind an entity (a module's entity ids and its playable ids are
        // different namespaces — see WatchPageModel.StagePlayableIdOf). ContentHost knows which route is attached but
        // could never map it to a playable, which is exactly how the two terms of the arbitration ended up in
        // different id spaces and the stage never lit.
        //
        // From an EFFECT, never during Render: a render-time write into a signal the shell reads is what the
        // backwards-write guard exists to catch.
        //
        // Gated on UseIsActive so only the ATTACHED page writes. A UseSignalEffect is a runtime effect, NOT the
        // component's render-effect — parking suspends the latter and leaves the former firing — so without this gate
        // a keep-alive-parked watch page would happily re-claim the surface from behind the page the user is looking
        // at. The gate also PEEKS the signal rather than reading it, so a parked page is never even woken by someone
        // else's write. Inactive is a no-op, not a clear: window-minimize folds into the same signal, and clearing
        // there would tear the surface down and rebuild it on every restore.
        // Every value the effect decides on is read INSIDE the closure, never captured from the enclosing render.
        // UseSignalEffect registers its callback exactly once, at mount (RenderContext.UseSignalEffect: `if (idx < 0)`),
        // so a captured local is frozen at the FIRST render's value for the life of the component — and the first
        // render of this page happens while the document is still the loading Seed, which names no play action at all.
        // Capturing it therefore pinned the claim to "" forever: the page rendered its stage, the caption said
        // "Playing", and the surface stayed in the rail because the claim never moved. Reading the loadable's signal
        // here is also what SUBSCRIBES the effect to the document, so the claim is re-asserted the moment it lands.
        var attached = UseIsActive();
        UseSignalEffect(() =>
        {
            if (!attached.Value) return;   // subscribe → re-asserts the claim the moment this page is un-parked
            if (ui is null) return;
            string staged = StagePlayableUri(moduleId, page.Value.Value);   // subscribe → and when the document lands
            if (string.Equals(ui.ActiveStagePlayable.Peek(), staged, StringComparison.Ordinal)) return;
            ui.ActiveStagePlayable.Value = staged;
        });

        var body = Skel.Region(
            page,
            content: doc => WatchPageModel.From(doc, IsPlayingEntity(StagePlayableUri(moduleId, doc), nowUri)) is { } watch
                ? WatchPageView.Caption(watch, moduleId, go,
                    chip => ChipInvoke(chip, moduleId, watch, acts, bridge),
                    watch.Stage == WatchStageKind.Live, acts, bridge, overlay)
                : BuildBody(doc, moduleId, entityId, go, acts, bridge, lib, overlay),
            reveal: SkelReveal.Soft,
            onFailed: () => FailedBody(page.Error, () => reload.Value++),
            group: "module-page:" + pageUri);

        var content = new BoxEl
        {
            Direction = 1,
            // No masthead term here any more: the OUTER column reserves the overlay band so the stage clears it, and
            // paying it twice would push the body a whole band down the page.
            Padding = new Edges4(32f, 40f, 32f, PlayerDock.Reserve + 40f),
            Children = [body],
        };

        // ONE container shape for both readings — an EMPTY box in the stage slot for a non-watch document — so the
        // scroller is never re-parented and never remounted by a template change.
        return new BoxEl
        {
            Direction = 1, Grow = 1f, MinHeight = 0f,
            Padding = new Edges4(0f, BrowseLayout.MastheadReserve, 0f, 0f),
            Children =
            [
                model is null
                    ? new BoxEl()
                    : WatchPageView.Stage(model, stagePlayable, bridge, ui, StagePlay(model, moduleId, acts, bridge)),
                ScrollView(content) with { Grow = 1f, MinHeight = 0f, ScrollKey = "module-page:" + pageUri },
            ],
        };
    }

    // ── the watch page's play verb ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>The PLAYABLE uri this page's stage would host, or <c>""</c> when it would host nothing (a non-watch
    /// document, or one that names no play action).
    ///
    /// <para>The document does not state its own playable anywhere except on its play ACTION, and a module's entity id
    /// is NOT its playable id: this page's route carries entity <c>video:tRsQsTMvPNg</c> while the thing that plays is
    /// playable <c>tRsQsTMvPNg</c>. Everything that compares this page against the player bar — the stage's mount
    /// gate, the rail's yield, the docked capability bit, and <see cref="IsPlayingEntity"/> — therefore speaks the ONE
    /// id space <c>CurrentTrack.Uri</c> speaks, and this is where a page enters it.</para></summary>
    static string StagePlayableUri(string moduleId, ModulePageDoc? doc)
        => WatchPageModel.StagePlayableIdOf(doc) is { } playableId
            ? ModuleUri.Encode(moduleId, playableId)
            : "";

    /// <summary>Is the item in the bar the very thing this page's stage would host? One ordinal compare, in the one id
    /// space — see <see cref="StagePlayableUri"/>.</summary>
    static bool IsPlayingEntity(string stagePlayable, string? nowUri)
        => stagePlayable.Length > 0 && nowUri is { Length: > 0 }
        && string.Equals(stagePlayable, nowUri, StringComparison.Ordinal);

    /// <summary>What a watch CAPSULE does. Deliberately the page's job rather than the view's: the mapping from a
    /// document action to an app verb is policy, and the view must stay a renderer.</summary>
    static Action? ChipInvoke(WatchChip chip, string moduleId, WatchPageModel model,
                              ActionServices? acts, PlaybackBridge? bridge)
        => chip.Kind switch
        {
            PageAction.KindPlay => chip.PlayableId is { Length: > 0 } playableId
                ? () => PlayWatch(moduleId, playableId, model, acts, bridge)
                : null,
            // The guard lives in ShellOpen; an action whose url it refuses simply has no capsule.
            PageAction.KindOpenUrl => ShellOpen.IsWebUrl(chip.Url) ? () => ShellOpen.OpenUrl(chip.Url) : null,
            PageAction.KindModuleAction => chip.Id is { Length: > 0 } actionId
                ? () => ModuleActions.Invoke(moduleId, actionId)
                : null,
            _ => null,   // an unknown kind is skipped, exactly like an unknown section
        };

    /// <summary>The stage's own play affordance: the document's FIRST play action, or null when it offers none (the
    /// CTA is then absent rather than dead).</summary>
    static Action? StagePlay(WatchPageModel model, string moduleId, ActionServices? acts, PlaybackBridge? bridge)
    {
        for (int i = 0; i < model.Chips.Length; i++)
        {
            WatchChip chip = model.Chips[i];
            if (!string.Equals(chip.Kind, PageAction.KindPlay, StringComparison.Ordinal)) continue;
            if (chip.PlayableId is not { Length: > 0 } playableId) continue;
            return () => PlayWatch(moduleId, playableId, model, acts, bridge);
        }
        return null;
    }

    /// <summary>Play a watch page's entity — and do it AS VIDEO, unconditionally.
    ///
    /// <para><see cref="PlayModule"/> reads the form from the resolve cache and falls back to
    /// <see cref="SdkForm.Audio"/>, which is right for the entity layout's action row and WRONG here: on a COLD watch
    /// page (opened by deep link, by search, by a shelf cell — nothing resolved yet) that fallback starts audio and
    /// the stage never lights, which reads as a broken page rather than as a cache miss. On a watch page the play
    /// button IS an explicit watch intent, so it states one. That intent is a ONE-PLAY scope
    /// (<c>PlaybackBridge.PrimeVideoIntentFor</c> via <see cref="VideoActions.PlayAs"/>) that dies at the next track
    /// boundary, so it cannot leak onto the rest of the queue; a playable that turns out to have no video degrades
    /// through the existing availability path exactly as it does everywhere else.</para></summary>
    static void PlayWatch(string moduleId, string playableId, WatchPageModel model,
                          ActionServices? acts, PlaybackBridge? bridge)
    {
        Track track = LocalPlayables.ForModule(moduleId, playableId, model.Title, SdkForm.Video,
            model.ChannelName is { Length: > 0 } channel ? new[] { channel } : null, model.PosterUrl);
        VideoActions.PlayAs(acts?.Svc?.Player, bridge ?? acts?.Playback, track, PlayLinkActions.FormFor(SdkForm.Video));
    }

    // ── the failed state ────────────────────────────────────────────────────────────────────────────────────────────
    // The app's ONE error grammar (ErrorState → EmptyState: display headline, one caption, one QUIET action), plus the
    // escape hatch that is specific to this surface: when the module already told us a web url for this entity, the
    // user can still go and look at it even though we could not draw it.
    Element FailedBody(Exception? error, Action retry)
    {
        string? url = OpenUrlOf(ModulePages.Get(ModulePages.UriOf(_route.Name)));
        var kids = new List<Element>(2) { ErrorState.Build(error, retry, Loc.Get(Strings.ModulePage.Error)) };
        if (url is { Length: > 0 })
            kids.Add(new BoxEl
            {
                Direction = 0, Justify = FlexJustify.Center, Padding = new Edges4(0f, Spacing.S, 0f, 0f),
                Children = [Button.Standard(Loc.Get(Strings.ModulePage.OpenInBrowser), () => ShellOpen.OpenUrl(url))],
            });
        return new BoxEl { Direction = 1, Grow = 1f, Children = kids.ToArray() };
    }

    /// <summary>The first <c>openUrl</c> action on a document, or null. Used by the error state and by the module
    /// track menu's "Open on &lt;Module&gt;" row.</summary>
    internal static string? OpenUrlOf(ModulePageDoc? doc)
    {
        PageAction[] actions = doc?.Actions ?? [];
        for (int i = 0; i < actions.Length; i++)
            if (string.Equals(actions[i].Kind, PageAction.KindOpenUrl, StringComparison.Ordinal)
                && ShellOpen.IsWebUrl(actions[i].Url))
                return actions[i].Url;
        return null;
    }

    // ── the loaded page ─────────────────────────────────────────────────────────────────────────────────────────────
    Element BuildBody(ModulePageDoc doc, string moduleId, string entityId, Action<string, string?> go,
                      ActionServices? acts, PlaybackBridge? bridge, LibraryBridge? lib, IOverlayService? overlay)
    {
        bool custom = string.Equals(doc.Template, ModulePageDoc.TemplateCustom, StringComparison.Ordinal);
        var kids = new List<Element>(8);

        // `entity` always wears a hero (falling back to whatever name the navigating surface carried); `custom` is
        // sections-only and shows one ONLY when the module supplied it.
        PageHero? hero = doc.Hero ?? (custom ? null : new PageHero(_route.Arg ?? Loc.Get(Strings.ModulePage.Title),
            null, null, null, null, false));
        if (hero is not null) kids.Add(Hero(hero, moduleId, entityId, go, bridge));
        if (doc.Actions is { Length: > 0 }) kids.Add(ActionRow(doc.Actions, moduleId, hero, acts, bridge));

        // The budgets are the module's contract; they are re-applied HERE because a page arrives over a pipe and this
        // is the last place that can refuse to draw 4,000 rows.
        PageSection[] sections = doc.Sections ?? [];
        int budget = ModulePageBudget.MaxItems;
        int drawn = 0;
        for (int i = 0; i < sections.Length && drawn < ModulePageBudget.MaxSections; i++)
        {
            if (sections[i] is not { } section) continue;
            if (SectionBlock(section, i, moduleId, ref budget, go, acts, bridge, lib, overlay) is not { } block) continue;
            kids.Add(block);
            drawn++;
        }

        return new BoxEl { Direction = 1, Gap = Spacing.XL, MinWidth = 0f, Children = kids.ToArray() };
    }

    // ── hero ────────────────────────────────────────────────────────────────────────────────────────────────────────
    Element Hero(PageHero hero, string moduleId, string entityId, Action<string, string?> go, PlaybackBridge? bridge)
    {
        var text = new List<Element>(5);
        if (hero.Eyebrow is { Length: > 0 } eyebrow)
            text.Add(DetailRail.EyebrowRun(eyebrow) with { Key = "hero:eyebrow" });

        // Title + the LIVE badge on ONE row: "live" is a property of the thing named, not a separate fact, so it sits
        // beside the name rather than in the meta line below it.
        var titleRow = new List<Element>(2) { WaveeType.PageHero(hero.Title) with { MinWidth = 0f, Shrink = 1f } };
        if (hero.IsLive) titleRow.Add(LiveBadge());
        text.Add(new BoxEl
        {
            Key = "hero:title", Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f,
            Children = titleRow.ToArray(),
        });

        if (hero.Subtitle is { Length: > 0 } subtitle)
        {
            string? subtitleRoute = SubtitleRoute(moduleId, entityId, bridge);
            text.Add(Embed.Comp(() => new ModuleMetaLink(subtitle, subtitleRoute, go))
                with { Key = "hero:subtitle:" + subtitle + "|" + (subtitleRoute ?? "") });
        }

        if (hero.MetaLine is { Length: > 0 } meta)
            text.Add(WaveeType.TrackMeta(meta) with { Key = "hero:meta", MaxLines = 2, Wrap = TextWrap.Wrap });

        var column = new BoxEl
        {
            Direction = 1, Gap = Spacing.S, Grow = 1f, Basis = 0f, MinWidth = 0f,
            Justify = FlexJustify.Center,
            Children = text.ToArray(),
        };

        // 256 is the app's shared decode rung: the same square a Home card and a detail cover resolve to, so a page
        // opened from a card reuses that texture rather than decoding a second copy.
        Element art = Surfaces.Artwork(ImageOf(hero.ImageUrl), hero.Title.Length * 7, HeroArt, HeroArt, Radii.Card,
            decodePx: 256);

        return new BoxEl
        {
            Key = "module-hero", Direction = 0, Gap = Spacing.XL, AlignItems = FlexAlign.Center,
            MinWidth = 0f, Wrap = true,
            Children = [art, column],
        };
    }

    /// <summary>Where the hero's subtitle goes — and, honestly, when it goes NOWHERE.
    ///
    /// <para>A <see cref="PageHero"/> carries no subtitle ENTITY: the document describes this entity, not what it
    /// belongs to. The publisher id lives on the RESOLVE answer (<c>ResolvedPlayable.SubtitleEntityId</c>), which we
    /// hold only for a playable we have actually resolved. So the link exists exactly when the playable whose own page
    /// this IS (its <c>PageEntityId</c> matches this route) is the one in the bar — the case the art tile and the title
    /// click produce. Every other time the subtitle is plain text, because inventing a destination is worse than not
    /// offering one.</para></summary>
    static string? SubtitleRoute(string moduleId, string entityId, PlaybackBridge? bridge)
    {
        // PEEK, not Value: this is a one-shot derivation at page build. Subscribing would re-render the whole page
        // on every track change for a link that is frozen into the mounted span anyway.
        Track? now = bridge?.CurrentTrack.Peek();
        if (now is null) return null;
        string? ownRoute = ModulePages.RouteFor(now, LinkSlot.Title);
        return string.Equals(ownRoute, ModulePages.RouteForEntity(moduleId, entityId), StringComparison.Ordinal)
            ? ModulePages.RouteFor(now, LinkSlot.Artist)
            : null;
    }

    /// <summary>The LIVE word-mark. Same grammar as the player bar's pill — a hairline outline in the accent decor ink,
    /// caps at caption scale — so "this is live" reads identically wherever the app says it.
    /// <para><c>internal</c> because the WATCH layout puts the SAME badge on its meta line: two spellings of "live"
    /// on two readings of one document is exactly the drift the single-owner rule exists to stop.</para></summary>
    internal static Element LiveBadge() => new BoxEl
    {
        Key = "hero:live", Shrink = 0f, Height = 18f,
        Padding = new Edges4(Spacing.XS, 0f, Spacing.XS, 0f),
        Corners = CornerRadius4.All(2f), BorderWidth = 1f, BorderColor = WaveeAccent.Decor,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Children =
        [
            new TextEl(Loc.Get(Strings.Play.Live))
            {
                Size = 10f, LineHeight = 14f, Weight = 600, Color = WaveeAccent.Decor, Wrap = TextWrap.NoWrap,
            },
        ],
    };

    // ── actions ─────────────────────────────────────────────────────────────────────────────────────────────────────
    Element ActionRow(PageAction[] actions, string moduleId, PageHero? hero, ActionServices? acts, PlaybackBridge? bridge)
    {
        var kids = new List<Element>(actions.Length);
        for (int i = 0; i < actions.Length; i++)
        {
            PageAction a = actions[i];
            if (a is not { Label.Length: > 0 }) continue;
            Action? invoke = InvokeFor(a, moduleId, hero, acts, bridge);
            if (invoke is null) continue;   // an action we cannot honour is ABSENT, never a dead button
            kids.Add((a.Primary ? Button.Accent(a.Label, invoke) : Button.Standard(a.Label, invoke))
                with { Key = "action:" + a.Id + ":" + a.Kind });
        }

        if (kids.Count == 0) return new BoxEl();
        return new BoxEl
        {
            Key = "module-actions", Enter = DetailRail.FadeUp, Layout = DetailRail.Shove,
            Direction = 0, Gap = Spacing.S, Wrap = true, AlignItems = FlexAlign.Center,
            Children = kids.ToArray(),
        };
    }

    Action? InvokeFor(PageAction a, string moduleId, PageHero? hero, ActionServices? acts, PlaybackBridge? bridge)
    {
        switch (a.Kind)
        {
            case PageAction.KindPlay:
                if (a.PlayableId is not { Length: > 0 } playableId) return null;
                string title = hero?.Title ?? a.Label;
                string? artUrl = hero?.ImageUrl;
                // ONE verb, ONE ordering: PlayAs lights the video surface for THIS uri before the play command when the
                // module says the playable is video, and leaves the standing intent alone when it says audio.
                return () => PlayModule(moduleId, playableId, title, artUrl, acts, bridge);

            case PageAction.KindOpenUrl:
                // The guard lives in ShellOpen; an action whose url it refuses simply has no button.
                return ShellOpen.IsWebUrl(a.Url) ? () => ShellOpen.OpenUrl(a.Url) : null;

            case PageAction.KindModuleAction:
                if (a.Id is not { Length: > 0 } actionId) return null;
                return () => ModuleActions.Invoke(moduleId, actionId);

            default:
                return null;   // an unknown kind is skipped, exactly like an unknown section
        }
    }

    static void PlayModule(string moduleId, string playableId, string? title, string? artworkUrl,
                           ActionServices? acts, PlaybackBridge? bridge)
    {
        // The FORM comes from the resolve cache when the module has already told us; otherwise Default lets the normal
        // resolve path decide. Never guessed from the page.
        SdkForm sdkForm = ModulePlayables.Get(ModuleUri.Encode(moduleId, playableId))?.Form ?? SdkForm.Audio;
        Track track = LocalPlayables.ForModule(moduleId, playableId, title, sdkForm, null, artworkUrl);
        VideoActions.PlayAs(acts?.Svc?.Player, bridge ?? acts?.Playback, track, PlayLinkActions.FormFor(sdkForm));
    }

    // ── sections ────────────────────────────────────────────────────────────────────────────────────────────────────
    Element? SectionBlock(PageSection section, int index, string moduleId, ref int budget,
                          Action<string, string?> go, ActionServices? acts, PlaybackBridge? bridge,
                          LibraryBridge? lib, IOverlayService? overlay)
    {
        string key = "sec:" + index + ":" + section.Kind;
        switch (section.Kind)
        {
            case PageSection.KindText:
                if (section.Text is not { Length: > 0 } text) return null;
                return Section(key, section.Title,
                    RichText.ExpandableFlex(text, 14f, Tok.TextSecondary, Tok.AccentTextPrimary, 6, key,
                        onNavUri: r => go(r, null)));

            case PageSection.KindFacts:
                return FactsBlock(key, section, ref budget);

            case PageSection.KindPlayables:
                return PlayablesBlock(key, section, moduleId, ref budget, go, acts, bridge, lib, overlay);

            case PageSection.KindCards:
                return CardsBlock(key, section, moduleId, ref budget, go, acts, bridge);

            case PageSection.KindLinks:
                return LinksBlock(key, section, ref budget);

            default:
                return null;   // unknown kind ⇒ skipped, and the rest of the page still renders
        }
    }

    /// <summary>A section shell: the shared rail header over a body, carrying the block motion (fade up as it lands,
    /// FLIP as its siblings shove it). The page root deliberately carries none — ContentHost slides it.</summary>
    static Element Section(string key, string? title, Element body)
    {
        Element[] kids = title is { Length: > 0 } t
            ? new Element[] { WaveeType.RailHeader(t), body }
            : new Element[] { body };
        return new BoxEl
        {
            Key = key, Enter = DetailRail.FadeUp, Layout = DetailRail.Shove,
            Direction = 1, Gap = Spacing.M, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
            Children = kids,
        };
    }

    // facts — the album "About this release" bento: a wrap-grow row of compact value/label tiles.
    Element? FactsBlock(string key, PageSection section, ref int budget)
    {
        string[][] rows = section.Rows ?? [];
        if (rows.Length == 0) return null;
        var tiles = new List<Element>(rows.Length);
        for (int i = 0; i < rows.Length && budget > 0; i++)
        {
            string[] row = rows[i];
            if (row is not { Length: >= 2 }) continue;
            string label = row[0] ?? "";
            string value = row[1] ?? "";
            if (value.Length == 0) continue;
            tiles.Add(FactTile(value, label, key + ":" + i));
            budget--;
        }

        if (tiles.Count == 0) return null;
        float stagger = Motion.ReducedMotion ? 0f : WaveeMotion.MastheadStaggerMs;
        return Section(key, section.Title, new BoxEl
        {
            Key = key + ":tiles", Direction = 0, Gap = Spacing.S, Wrap = true, Stagger = stagger,
            Children = tiles.ToArray(),
        });
    }

    static Element FactTile(string value, string label, string key) => Wavee.Components.StatTile.Create(key, value, label);

    // playables — the app's ONE track row (TrackRow.ArtCard) over synthetic module tracks, so a module's episode list
    // behaves like every other list in the app: click plays or toggles, right-click opens the track menu.
    static readonly ColumnSet PlayableCols =
        new(Album: false, By: false, Date: false, Video: true, Plays: false, Heart: false, Thumb: false);

    Element? PlayablesBlock(string key, PageSection section, string moduleId, ref int budget,
                            Action<string, string?> go, ActionServices? acts, PlaybackBridge? bridge,
                            LibraryBridge? lib, IOverlayService? overlay)
    {
        PageItem[] items = section.Items ?? [];
        if (items.Length == 0) return null;
        int take = Math.Min(items.Length, budget);
        var rows = new List<Element>(take);
        for (int i = 0; i < take; i++)
        {
            PageItem item = items[i];
            if (item is not { Title.Length: > 0 } || item.PlayableId is not { Length: > 0 } playableId) continue;
            SdkForm form = item.Form ?? SdkForm.Audio;
            Track track = LocalPlayables.ForModule(moduleId, playableId, item.Title, form,
                item.Subtitle is { Length: > 0 } s ? new[] { s } : null, item.ImageUrl);
            var st = TrackRow.StateOf(bridge, lib, track);
            Element row = TrackRow.ArtCard(track, st, PlayableCols, go,
                onPlay: () => TrackRow.Invoke(bridge, track,
                    () => VideoActions.PlayAs(acts?.Svc?.Player, bridge ?? acts?.Playback, track,
                        PlayLinkActions.FormFor(form))),
                art: WaveeSize.ArtThumb,
                showArtists: item.Subtitle is { Length: > 0 },
                explicitBadge: false,
                showDuration: false,
                kind: TrackRow.ArtCardKind.Rail,
                showArtwork: true);

            var box = new BoxEl
            {
                Key = key + ":row:" + i, Direction = 1, Corners = Radii.ControlAll,
                HoverFill = Tok.FillSubtleSecondary,
                Children = [row],
            };
            rows.Add(acts is not null && overlay is not null
                ? box.WithContextMenu(overlay, () => Menus.ModuleTrack(new ActionContext(
                    ActionTarget.ForTracks(new[] { track }), acts)))
                : box);
        }

        budget -= rows.Count;
        if (rows.Count == 0) return null;
        return Section(key, section.Title, Embed.Comp(() => new RampedRows(rows.ToArray()))
            with { Key = key + ":rows" });
    }

    // cards — the shared shelf card, wrapped rather than virtualized: the document is budget-capped, so the row count
    // is bounded by construction and a virtualizer would only add a viewport nothing needs.
    Element? CardsBlock(string key, PageSection section, string moduleId, ref int budget,
                        Action<string, string?> go, ActionServices? acts, PlaybackBridge? bridge)
    {
        PageItem[] items = section.Items ?? [];
        if (items.Length == 0) return null;
        int take = Math.Min(items.Length, budget);
        var cards = new List<Element>(take);
        for (int i = 0; i < take; i++)
        {
            PageItem item = items[i];
            if (item is not { Title.Length: > 0 }) continue;
            // A card navigates to another page of the SAME module when the module named one, else it opens the url,
            // else it plays. Nothing invents a destination the module did not state.
            string? route = ModulePages.RouteForEntity(moduleId, item.EntityId);
            string cardUri = item.PlayableId is { Length: > 0 } pid ? ModuleUri.Encode(moduleId, pid) : "";
            void Open()
            {
                if (route is { Length: > 0 }) go(route, item.Title);
                else if (ShellOpen.IsWebUrl(item.Url)) ShellOpen.OpenUrl(item.Url);
                else PlayCard(item);
            }

            void PlayCard(PageItem it)
            {
                if (it.PlayableId is not { Length: > 0 } p) return;
                PlayModule(moduleId, p, it.Title, it.ImageUrl, acts, bridge);
            }

            cards.Add(MediaCard.Shelf(ImageOf(item.ImageUrl), item.Title, item.Subtitle ?? "", cardUri,
                Open, () => PlayCard(item), CardWidth) with { Key = key + ":card:" + i });
        }

        budget -= cards.Count;
        if (cards.Count == 0) return null;
        return Section(key, section.Title, new BoxEl
        {
            Key = key + ":cards", Direction = 0, Gap = Spacing.M, Wrap = true, MinWidth = 0f,
            Children = cards.ToArray(),
        });
    }

    // links — plain rows that leave the app. Http(s) only (ShellOpen's guard); anything else is not drawn at all.
    Element? LinksBlock(string key, PageSection section, ref int budget)
    {
        PageItem[] items = section.Items ?? [];
        if (items.Length == 0) return null;
        int take = Math.Min(items.Length, budget);
        var rows = new List<Element>(take);
        for (int i = 0; i < take; i++)
        {
            PageItem item = items[i];
            if (item is not { Title.Length: > 0 } || !ShellOpen.IsWebUrl(item.Url)) continue;
            string url = item.Url!;
            rows.Add(new BoxEl
            {
                Key = key + ":link:" + i,
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Padding = new Edges4(Spacing.M, Spacing.S, Spacing.M, Spacing.S),
                Corners = Radii.ControlAll, HoverFill = Tok.FillSubtleSecondary,
                Cursor = CursorId.Hand, OnClick = () => ShellOpen.OpenUrl(url),
                Role = AutomationRole.Hyperlink, Focusable = true,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f,
                        Children = item.Subtitle is { Length: > 0 } sub
                            ? [WaveeType.TrackTitle(item.Title) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                               WaveeType.TrackMeta(sub) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }]
                            : [WaveeType.TrackTitle(item.Title) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }],
                    },
                    Icon(Icons.OpenInNewWindow, 14f, Tok.TextTertiary),
                ],
            }.Interactive(Interaction.Subtle));
            budget--;
        }

        if (rows.Count == 0) return null;
        return Section(key, section.Title, new BoxEl
        {
            Key = key + ":links", Direction = 1, Gap = 2f, MinWidth = 0f, Children = rows.ToArray(),
        });
    }

    static Image? ImageOf(string? url) => url is { Length: > 0 } ? new Image(url) : null;

    // ── the progressive reveal ──────────────────────────────────────────────────────────────────────────────────────
    /// <summary>A row list that reveals in <see cref="DetailRevealRamp.Chunk"/>-sized slices over a few frames rather
    /// than mounting a hundred rows in one. The ramp math is the pure, unit-tested <see cref="DetailRevealRamp"/>; the
    /// clock is mounted only while it runs, so the frame loop quiesces the moment the list is whole.</summary>
    sealed class RampedRows : Component
    {
        readonly Element[] _rows;
        public RampedRows(Element[] rows) => _rows = rows;

        public override Element Render()
        {
            // A short list is never worth a ramp: it costs a frame of clock to save nothing.
            if (_rows.Length <= DetailRevealRamp.Chunk)
                return new BoxEl { Direction = 1, Gap = 2f, MinWidth = 0f, Children = _rows };

            var reveal = UseSignal(DetailRevealRamp.Chunk);
            int shown = Math.Min(_rows.Length, reveal.Value == DetailRevealRamp.Done ? _rows.Length : reveal.Value);
            var slice = new Element[shown];
            Array.Copy(_rows, slice, shown);

            Element clock = new BoxEl
            {
                HitTestVisible = false, Width = 0f, Height = 0f,
                Children = [Flow.Show(() => reveal.Value != DetailRevealRamp.Done,
                    Embed.Comp(() => new TickerClock { OnFrame = _ => reveal.Value = DetailRevealRamp.Next(reveal.Peek(), _rows.Length) }))],
            };
            return new BoxEl
            {
                Direction = 1, Gap = 2f, MinWidth = 0f,
                Children = [new BoxEl { Direction = 1, Gap = 2f, MinWidth = 0f, Children = slice }, clock],
            };
        }
    }

    // ── the subtitle link ───────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>The hero's subtitle: the player bar's now-playing meta-link grammar (hover recolours THIS word, not the
    /// row it sits in), navigating to the module page the playable named. Plain text when there is nowhere to go —
    /// styled-but-inert is a lie, and a link to a route nothing renders is worse.</summary>
    sealed class ModuleMetaLink : Component
    {
        readonly string _text;
        readonly string? _route;
        readonly Action<string, string?> _go;

        public ModuleMetaLink(string text, string? route, Action<string, string?> go)
        {
            _text = text;
            _route = route;
            _go = go;
        }

        public override Element Render()
        {
            var hover = UseSignal(false);
            bool enabled = _route is { Length: > 0 };
            return new BoxEl
            {
                MinWidth = 0f, Shrink = 1f, ClipToBounds = true,
                Cursor = enabled ? CursorId.Hand : (CursorId?)null,
                OnClick = enabled ? () => _go(_route!, _text) : null,
                OnHoverMove = enabled ? _ => { if (!hover.Peek()) hover.Value = true; } : null,
                OnPointerExit = enabled ? () => { if (hover.Peek()) hover.Value = false; } : null,
                Role = enabled ? AutomationRole.Hyperlink : AutomationRole.Text,
                Focusable = enabled, AllowFocusOnInteraction = false,
                Children =
                [
                    new TextEl(_text)
                    {
                        Size = 14f, LineHeight = 20f,
                        Color = enabled && hover.Value ? Tok.TextPrimary : Tok.TextSecondary,
                        Underline = enabled && hover.Value,
                        Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    },
                ],
            };
        }
    }
}

/// <summary>The fire-and-forget <c>module/action</c> call behind a page's <c>moduleAction</c> button. Its own type so
/// the page stays a renderer: a button press must never await an RPC on the UI thread, and a module that refuses is a
/// log line, not a crash.</summary>
static class ModuleActions
{
    public static void Invoke(string moduleId, string actionId)
    {
        var host = ModuleHost.Current;
        if (host is null) return;
        _ = InvokeCoreAsync(host, moduleId, actionId);
    }

    static async System.Threading.Tasks.Task InvokeCoreAsync(ModuleHost host, string moduleId, string actionId)
    {
        try
        {
            if (host.ProcessFor(moduleId) is not { } process) return;
            await process.RequestAsync(Wavee.Sdk.Protocol.ModuleMethods.Action,
                new Wavee.Sdk.Protocol.ModuleActionParams(actionId),
                Wavee.Sdk.Protocol.SdkJsonContext.Default.ModuleActionParams,
                Wavee.Sdk.Protocol.SdkJsonContext.Default.ModuleActionResult,
                ModuleTimeouts.Diagnostics, System.Threading.CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            WaveeLog.Instance.Log(WaveeLogLevel.Warning, "module." + moduleId,
                "page action '" + actionId + "' failed: " + ex.Message);
        }
    }
}
