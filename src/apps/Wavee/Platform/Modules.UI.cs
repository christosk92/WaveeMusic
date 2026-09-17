// ── Platform/Modules.UI.cs ─────────────────────────────────────────────────────────────────────────────────────────
// the module page and the watch page
//
// Role: UI
// Owner: T
// Wave: 6
// Budget: 1400 lines
// Spec: ch 09 §9.5 (the module page), ch 24 (the watch page's stage), gap register G-021 · G-151 · G-212
//
// THE ONE PAGE A PLAYBACK MODULE DESCRIBES (0.2.9 `Features/Modules/ModulePage.cs` + `WatchPageView.cs`). A module is
// out of process, so it never renders anything: it answers `module/page` with a small declarative `ModulePageDoc` and
// this file draws it with the app's own vocabulary. One page for every module and every entity; nothing switches on a
// module id. Every DECISION is `Modules.cs`'s (the route grammar, the three templates, the watch projection, the
// stage playable, the shared item budget); this file lays out and forwards.
//
// Three states, owned by the engine: `Skel.Region` derives the shimmer from THIS page rendered against the seed (the
// cached document on a revisit, else a figure-space placeholder), mounts the real page on Ready and the failed body
// on Failed. Unknown section kinds and action kinds are skipped, never fatal.
//
// THE STAGE LIVES OUTSIDE THE SCROLLER (ch 24 rule 1). It hosts the app's one composited video — a DestOut hole — so
// no ancestor may carry an opacity, a transition, an offscreen RT or a scroller's edge fade. The skeleton region's
// reveal, the sections' FadeUp and the ScrollView are all such ancestors; the stage is a sibling above them, and the
// stage slot is an EMPTY box for a non-watch document so the scroller is never re-parented by a template change.
//
// THE CLAIM. Only the page holds the document, and only the document names the playable behind an entity (a module's
// entity ids and playable ids are different namespaces), so the ATTACHED page is the one writer of
// `Shell.Ui.ActiveStagePlayable` — from a signal effect gated on `UseIsActive`, reading every input inside the closure.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Sdk;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public static partial class Modules
{
    // ══ 0. THE INSTALL ═════════════════════════════════════════════════════════════════════════════════════════════

    static int s_uiInstalled;

    /// <summary>The Wave-6 composition of the module surfaces (called by App.cs after <see cref="Boot"/>): the
    /// <c>module:</c> page, the two audio seams (G-110), the link router (G-021) and the module video tier (G-148).
    /// Idempotent; every seam is an assignment, so a second call changes nothing.</summary>
    public static void InstallUi()
    {
        if (Interlocked.Exchange(ref s_uiInstalled, 1) != 0) return;
        Shell.SetPage(Shell.RouteKind.Module, static (in Shell.Route route) => PageFor(in route));
        Playback.Audio.ModuleOpen = OpenAudio;
        Playback.Audio.LocalPath = Playlist.LocalPathOf;
        Shell.LinkModules = LinkModulesNow;
        Shell.MatchLink = MatchLinkAsync;
        InstallVideoTier();
    }

    /// <summary>THE <c>Shell.LinkModules</c> seam: the match-capable installed modules, in catalog order, labelled by
    /// their own manifests (an authored loc key wins, then the authored label, then the display name + ellipsis).</summary>
    public static IReadOnlyList<Shell.LinkModule> LinkModulesNow()
    {
        if (s_host is not { } host) return [];
        var rows = new List<Shell.LinkModule>(host.Installed.Count);
        string fallbackPlaceholder = Loc.Get(Strings.Play.Placeholder);
        foreach (InstalledModule module in host.Installed)
        {
            ModuleManifest m = module.Manifest;
            if (!ModuleCapabilities.Declares(m, ModuleCapabilities.Match)) continue;
            string? authored = m.Menu?.LabelLocKey is { Length: > 0 } key && global::FluentGpu.Localization.Localization.Has(key) ? Loc.Get(key) : m.Menu?.Label;
            rows.Add(new Shell.LinkModule(module.Id, Actions.PlayLinkRules.MenuLabel(authored, m.DisplayName, module.Id),
                Actions.PlayLinkRules.PlaceholderFor(m.Menu?.Placeholder, fallbackPlaceholder)));
        }
        return rows;
    }

    /// <summary>The page a <c>module:</c> route renders. Keyed by the page uri, so a different entity is a different
    /// keep-alive slot and a different mount — the frozen ctor values are then exactly right.</summary>
    public static Element PageFor(in Shell.Route route)
    {
        string routeKey = Shell.NameOf(in route);
        string? arg = Shell.ArgOf(in route);
        return Embed.Comp(() => new ModulePage(routeKey, arg)) with { Key = "module-page:" + routeKey };
    }

    // ══ 1. THE PLAY VERBS ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Play a module playable by uri from a page: the cached answer plays at once; otherwise it resolves on the
    /// pool and plays on the UI thread. A failure is the shared play-failure toast with the module's own words.</summary>
    public static void PlayUri(string playableUri)
    {
        if (s_host is not { } host || playableUri.Length == 0) return;
        if (Playables.Get(playableUri) is { } cached) { Play(playableUri, cached); return; }
        _ = Task.Run(async () =>
        {
            try
            {
                ResolvedPlayable resolved = await host.ResolveAsync(playableUri, force: false, CancellationToken.None).ConfigureAwait(false);
                Playback.ToUi(() => Play(playableUri, resolved));
            }
            catch (Exception ex)
            {
                if (Actions.PlayLinkRules.IsCancelled(ex)) return;
                Log.Info("module", "play failed: " + ex.Message);
                Playback.ToUi(() => Notify.Say(Actions.PlayLinkRules.ErrorText(ex, Loc.Get(Strings.Play.Failed)), InfoBarSeverity.Error,
                    Loc.Get(Strings.Play.TryAgain), () => PlayUri(playableUri), Actions.PlayLinkRules.FailureToastKey));
            }
        });
    }

    /// <summary>The stage and the play capsule: the playable in the bar toggles, anything else plays.</summary>
    public static void PlayOrToggle(string playableUri)
    {
        EntityId now = Playback.CurrentId.Peek();
        if (!now.IsEmpty && now.Provider == EntityProvider.Module && string.Equals(now.Text, playableUri, StringComparison.Ordinal))
            Playback.TogglePlay();
        else PlayUri(playableUri);
    }

    /// <summary>Open a web url in the user's browser — only an http(s) url with a host ever reaches the shell.</summary>
    static void OpenUrl(string? url)
    {
        if (!Actions.PlayLinkRules.IsWebUrl(url)) return;
        if (Actions.Services.OpenExternal is { } open) { open(url!); return; }
        InputHooks.Current.Default.OpenUri?.Invoke(url!);
    }

    static void Navigate(string routeKey, string? label) => Shell.GoTo(Shell.Parse(routeKey, label ?? ""));

    /// <summary>What a document action DOES, or null when this build cannot honour it (the control is then ABSENT,
    /// never dead): play through the resolve path, open an http(s) url, or send the id back over module/action.</summary>
    static Action? InvokeFor(string moduleId, string kind, string? id, string? playableId, string? url) => kind switch
    {
        PageAction.KindPlay => playableId is { Length: > 0 } pid ? () => PlayOrToggle(ModuleUri.Encode(moduleId, pid)) : null,
        PageAction.KindOpenUrl => Actions.PlayLinkRules.IsWebUrl(url) ? () => OpenUrl(url) : null,
        PageAction.KindModuleAction => id is { Length: > 0 } actionId ? () => InvokeAction(moduleId, actionId) : null,
        _ => null,
    };

    // ══ 2. THE PAGE ════════════════════════════════════════════════════════════════════════════════════════════════

    const float HeroArt = 232f, CardWidth = 168f, ChannelAvatar = 40f;
    const int ShelfMax = 16, DescriptionLines = 3, TextLines = 6;

    // Figure spaces (U+2007) MEASURE like digits and paint nothing, so the derived shimmer gets a title bar and two
    // shorter lines at believable widths with no string that could flash as real copy.
    static readonly ModulePageDoc Seed = new(ModulePageDoc.CurrentVersion, ModulePageDoc.TemplateEntity,
        new PageHero(new string(' ', 12), null, new string(' ', 8), null, new string(' ', 5), false), [], [], null);

    static readonly EnterExit SectionFadeUp = new(Opacity: 0f, Active: true);
    static readonly LayoutTransition SectionShove = new(TransitionChannels.Position, TransitionDynamics.Tween(Expressive.Fast, Easing.SmoothOut));

    static Task<ModulePageDoc> LoadPage(string pageUri, CancellationToken ct)
        => s_host is { } host
            ? host.PageAsync(pageUri, ct)
            : Task.FromException<ModulePageDoc>(new ModuleException(ModuleErrorCode.Unsupported, "No module host is running."));

    sealed class ModulePage(string routeKey, string? routeArg) : Component
    {
        public override Element Render()
        {
            // Hooks first, unconditionally — the route parse only decides what is DRAWN.
            string pageUri = Pages.UriOf(routeKey) ?? "";
            bool parsed = Pages.TryParseRoute(routeKey, out string moduleId, out _);
            var attached = UseIsActive();
            var page = UseResource(ct => LoadPage(pageUri, ct), Pages.Get(pageUri) ?? Seed);
            Loadable<ModulePageDoc> loadable = page.Loadable;

            UseSignalEffect(() =>
            {
                if (!attached.Value) return;   // subscribe: re-asserts the claim when this page is un-parked
                string staged = parsed && loadable.State.Value == (byte)LoadState.Ready
                    ? Pages.StagePlayableUri(moduleId, loadable.Value.Value) : "";
                if (!string.Equals(Shell.Ui.ActiveStagePlayable.Peek(), staged, StringComparison.Ordinal))
                    Shell.Ui.ActiveStagePlayable.Value = staged;
            });

            if (!parsed) return Controls.Vacancy(Controls.VacancyVoice.Error, title: Loc.Get(Strings.ModulePage.Error));

            // BOTH reads subscribe: the stage derives from the document AND from what is playing.
            ModulePageDoc live = loadable.Value.Value;
            bool ready = loadable.State.Value == (byte)LoadState.Ready;
            string nowUri = PlayingModuleUri();
            string stagePlayable = ready ? Pages.StagePlayableUri(moduleId, live) : "";
            WatchPageModel? model = ready ? WatchPageModel.From(live, Pages.IsPlayingEntity(stagePlayable, nowUri)) : null;
            Resource<ModulePageDoc> resource = page;

            var body = Skel.Region(loadable,
                content: doc => LoadedBody(doc, moduleId, routeArg),
                reveal: SkelReveal.Soft,
                onFailed: () => FailedBody(loadable.Error, resource.Refresh, pageUri),
                group: "module-page:" + pageUri);

            return new BoxEl
            {
                Direction = 1, Grow = 1f, MinHeight = 0f,
                Padding = new Edges4(0f, BrowseLayout.MastheadReserve, 0f, 0f),
                Children =
                [
                    model is null
                        ? new BoxEl { Key = "module-stage-slot" }
                        : new BoxEl
                        {
                            // The props of the stage freeze inside it, so its identity and poster are folded into the key.
                            Key = "module-stage-slot:" + stagePlayable + "|" + (model.PosterUrl ?? ""),
                            Direction = 1, Padding = new Edges4(32f, 0f, 32f, 0f),
                            Children = [Stage(stagePlayable, model.PosterUrl)],
                        },
                    ScrollView(new BoxEl
                    {
                        Direction = 1, MinWidth = 0f,
                        Padding = new Edges4(32f, 40f, 32f, Design.Dock.Reserve + 40f),
                        Children = [body],
                    }) with { Grow = 1f, MinHeight = 0f, ScrollKey = "module-page:" + pageUri },
                ],
            };
        }
    }

    /// <summary>The playing PLAYABLE uri when it is a module's, else "" (subscribes).</summary>
    static string PlayingModuleUri()
    {
        EntityId id = Playback.CurrentId.Value;
        return !id.IsEmpty && id.Provider == EntityProvider.Module ? id.Text : "";
    }

    /// <summary>The watch stage (G-151): <c>Video.WatchStage</c>, the video host's mount point — the poster ground, the
    /// idle play disc, and the one video surface once this page's playable is what plays and the arbitration hands the
    /// surface to the page. Outside the scroller, the region and every section (see the file header).</summary>
    static Element Stage(string stagePlayable, string? posterUrl)
        => Video.WatchStage(stagePlayable, Trimmed(posterUrl), stagePlayable.Length == 0 ? null : () => PlayOrToggle(stagePlayable));

    /// <summary>The failed state: the app's one vacancy grammar with Retry, plus this surface's escape hatch — when the
    /// module already told us a web url for the entity, the user can still go and look at it.</summary>
    static Element FailedBody(Exception? error, Action retry, string pageUri)
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
                Children = [Button.Standard(Loc.Get(Strings.ModulePage.OpenInBrowser), () => OpenUrl(url))],
            });
        return new BoxEl { Key = "module-failed", Direction = 1, Grow = 1f, Children = kids.ToArray() };
    }

    /// <summary>The loaded page: the watch reading when the template asks for it, the entity/custom reading otherwise.</summary>
    static Element LoadedBody(ModulePageDoc doc, string moduleId, string? routeArg)
    {
        string stagePlayable = Pages.StagePlayableUri(moduleId, doc);
        return WatchPageModel.From(doc, Pages.IsPlayingEntity(stagePlayable, PlayingModuleUri())) is { } watch
            ? WatchCaption(watch, moduleId, doc)
            : EntityBody(doc, moduleId, routeArg);
    }

    // ══ 3. THE ENTITY READING ══════════════════════════════════════════════════════════════════════════════════════

    static Element EntityBody(ModulePageDoc doc, string moduleId, string? routeArg)
    {
        var kids = new List<Element>(8);
        PageHero? hero = Pages.HeroFor(doc, routeArg, Loc.Get(Strings.ModulePage.Title));
        if (hero is not null) kids.Add(Hero(hero, moduleId));
        if (ActionRow(doc.Actions, moduleId) is { } actions) kids.Add(actions);

        // The budgets are the module's contract; re-applied here because a page arrives over a pipe and this is the last
        // place that can refuse to draw 4,000 rows. Items are spent in document order.
        PageSection[] sections = doc.Sections ?? [];
        int budget = ModulePageBudget.MaxItems, drawn = 0;
        for (int i = 0; i < sections.Length && drawn < ModulePageBudget.MaxSections; i++)
        {
            if (sections[i] is not { } section || SectionBlock(section, i, moduleId, ref budget) is not { } block) continue;
            kids.Add(block);
            drawn++;
        }
        return new BoxEl { Key = "module-entity", Direction = 1, Gap = Spacing.XL, MinWidth = 0f, Children = kids.ToArray() };
    }

    static Element Hero(PageHero hero, string moduleId)
    {
        var text = new List<Element>(5);
        if (Trimmed(hero.Eyebrow) is { } eyebrow) text.Add(Design.Type.Eyebrow(eyebrow) with { Key = "hero:eyebrow" });

        // "Live" is a property of the thing named, so the badge sits beside the name rather than in the meta line.
        var titleRow = new List<Element>(2) { Design.Type.DetailHero(hero.Title) with { MinWidth = 0f, Shrink = 1f, Wrap = TextWrap.Wrap, MaxLines = 3 } };
        if (hero.IsLive) titleRow.Add(LiveBadge());
        text.Add(new BoxEl { Key = "hero:title", Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f, Children = titleRow.ToArray() });

        if (Trimmed(hero.Subtitle) is { } subtitle)
        {
            string? route = Pages.RouteForEntity(moduleId, Trimmed(hero.SubtitleEntityId));
            text.Add(Embed.Comp(() => new MetaLink(subtitle, route)) with { Key = "hero:subtitle:" + subtitle + "|" + (route ?? "") });
        }
        if (Trimmed(hero.MetaLine) is { } meta)
            text.Add(Design.Type.TrackMeta(meta) with { Key = "hero:meta", MaxLines = 2, Wrap = TextWrap.Wrap, MinWidth = 0f });

        return new BoxEl
        {
            Key = "module-hero", Direction = 0, Gap = Spacing.XL, AlignItems = FlexAlign.Center, MinWidth = 0f, Wrap = true,
            Children =
            [
                // 256 is the shared decode rung: a page opened from a card reuses that card's texture.
                Controls.Artwork(Trimmed(hero.ImageUrl), HeroArt, HeroArt, Radii.Card, decodePx: 256),
                new BoxEl
                {
                    Direction = 1, Gap = Spacing.S, Grow = 1f, Basis = 0f, MinWidth = 240f, Justify = FlexJustify.Center,
                    Children = text.ToArray(),
                },
            ],
        };
    }

    /// <summary>The LIVE word-mark — the player bar's grammar (hairline outline in the accent decor ink, caps at caption
    /// scale), shared by both readings so "live" reads identically wherever the page says it.</summary>
    static Element LiveBadge() => new BoxEl
    {
        Key = "badge:live", Shrink = 0f, Height = 18f, Padding = new Edges4(Spacing.XS, 0f, Spacing.XS, 0f),
        Corners = CornerRadius4.All(2f), BorderWidth = 1f, BorderColor = Design.Accent.Decor,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Children = [new TextEl(Loc.Get(Strings.Play.Live)) { Size = 10f, LineHeight = 14f, Weight = 600, Color = Design.Accent.Decor, Wrap = TextWrap.NoWrap }],
    };

    static Element? ActionRow(PageAction[]? actions, string moduleId)
    {
        if (actions is not { Length: > 0 }) return null;
        var kids = new List<Element>(actions.Length);
        foreach (PageAction? a in actions)
        {
            if (a is null || Trimmed(a.Label) is not { } label) continue;
            if (InvokeFor(moduleId, a.Kind, a.Id, a.PlayableId, a.Url) is not { } invoke) continue;
            kids.Add((a.Primary ? Button.Accent(label, invoke) : Button.Standard(label, invoke)) with { Key = "action:" + a.Id + ":" + a.Kind });
        }
        if (kids.Count == 0) return null;
        return new BoxEl
        {
            Key = "module-actions", Enter = SectionFadeUp, Layout = SectionShove,
            Direction = 0, Gap = Spacing.S, Wrap = true, AlignItems = FlexAlign.Center, Children = kids.ToArray(),
        };
    }

    static Element? SectionBlock(PageSection section, int index, string moduleId, ref int budget)
    {
        string key = "sec:" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + section.Kind;
        switch (section.Kind)
        {
            case PageSection.KindText:
                return Trimmed(section.Text) is { } text
                    ? Section(key, section.Title, Embed.Comp(() => new ExpandableText(text, TextLines)) with { Key = key + ":text:" + text.GetHashCode() })
                    : null;
            case PageSection.KindFacts: return FactsBlock(key, section, ref budget);
            case PageSection.KindPlayables: return PlayablesBlock(key, section, moduleId, ref budget);
            case PageSection.KindCards: return CardsBlock(key, section, moduleId, ref budget);
            case PageSection.KindLinks: return LinksBlock(key, section, ref budget);
            default: return null;   // unknown ⇒ skipped; the rest of the page still renders
        }
    }

    /// <summary>A section shell: the rail header over a body, carrying the block motion (fade up as it lands, FLIP as its
    /// siblings shove it). The page root carries none — the shell's page host already slides it.</summary>
    static Element Section(string key, string? title, Element body) => new BoxEl
    {
        Key = key, Enter = SectionFadeUp, Layout = SectionShove,
        Direction = 1, Gap = Spacing.M, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
        Children = Trimmed(title) is { } t ? [Design.Type.RailHeader(t), body] : [body],
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
                Width = 180f, Shrink = 0f,
                Children = [Controls.StatTile(key + ":" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), value, Trimmed(row[0]) ?? "")],
            });
            budget--;
        }
        if (tiles.Count == 0) return null;
        return Section(key, section.Title, new BoxEl
        {
            Key = key + ":tiles", Direction = 0, Gap = Spacing.S, Wrap = true,
            Stagger = Design.Reduced ? 0f : Design.Motion.MastheadStaggerMs, Children = tiles.ToArray(),
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
                ? Design.Type.TrackMeta(sub) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f } : null;
            Element? trailing = Trimmed(item.Meta) is { } meta ? Design.Type.TrackMeta(meta) with { Shrink = 0f } : null;
            rows.Add(Controls.MediaRow(new Controls.CardData(uri, title, subtitle, Trimmed(item.ImageUrl), play, play, ShowMenu: false),
                artEdge: 40f, trailing: item.IsLive ? LiveBadge() : trailing) with { Key = key + ":row:" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }
        budget -= take;
        if (rows.Count == 0) return null;
        return Section(key, section.Title, new BoxEl { Key = key + ":rows", Direction = 1, Gap = 2f, MinWidth = 0f, Children = rows.ToArray() });
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
                ? Design.Type.TrackMeta(sub) with { MaxLines = 2, Wrap = TextWrap.Wrap, MinWidth = 0f } : null;
            cards.Add(Controls.ShelfCard(new Controls.CardData(playable ?? route ?? "", title, subtitle, Trimmed(item.ImageUrl), open, play,
                ShowMenu: false), CardWidth) with { Key = key + ":card:" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }
        budget -= take;
        if (cards.Count == 0) return null;
        return Section(key, section.Title, new BoxEl { Key = key + ":cards", Direction = 0, Gap = Spacing.M, Wrap = true, MinWidth = 0f, Children = cards.ToArray() });
    }

    static readonly Action NoOp = static () => { };

    static Element? LinksBlock(string key, PageSection section, ref int budget)
    {
        PageItem[] items = section.Items ?? [];
        int take = Pages.Take(items.Length, budget);
        var rows = new List<Element>(take);
        for (int i = 0; i < take; i++)
        {
            if (items[i] is not { } item || Trimmed(item.Title) is not { } title || !Actions.PlayLinkRules.IsWebUrl(item.Url)) continue;
            string url = item.Url!;
            var text = new List<Element>(2) { Design.Type.TrackTitle(title) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f } };
            if (Trimmed(item.Subtitle) is { } sub) text.Add(Design.Type.TrackMeta(sub) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f });
            rows.Add(new BoxEl
            {
                Key = key + ":link:" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Padding = new Edges4(Spacing.M, Spacing.S, Spacing.M, Spacing.S),
                Corners = Radii.ControlAll, Cursor = CursorId.Hand, OnClick = () => OpenUrl(url),
                Role = AutomationRole.Hyperlink, Focusable = true,
                Children =
                [
                    new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Children = text.ToArray() },
                    Icon(Icons.OpenInNewWindow, 14f, Tok.TextTertiary),
                ],
            }.Interactive(Interaction.ListRow));
        }
        budget -= take;
        if (rows.Count == 0) return null;
        return Section(key, section.Title, new BoxEl { Key = key + ":links", Direction = 1, Gap = 2f, MinWidth = 0f, Children = rows.ToArray() });
    }

    // ══ 4. THE WATCH READING (ch 24's module page) ═════════════════════════════════════════════════════════════════

    /// <summary>Everything UNDER the picture: the title one rung below the page hero (the picture is the identity), the
    /// LIVE badge + meta line, the channel row, the document's actions as capsules, the description card with the fact
    /// line folded into its top, and the 16:9 shelf.</summary>
    static Element WatchCaption(WatchPageModel model, string moduleId, ModulePageDoc doc)
    {
        var kids = new List<Element>(6)
        {
            Design.Type.NowPlayingTitle(model.Title) with { Key = "watch:title", MaxLines = 2, Wrap = TextWrap.Wrap, MinWidth = 0f },
        };
        if (model.IsLive || model.MetaLine is { Length: > 0 }) kids.Add(MetaRow(model));
        if (model.ChannelName is { Length: > 0 }) kids.Add(ChannelRow(model, moduleId));
        if (ChipRow(model, moduleId, doc) is { } chips) kids.Add(chips);
        if (DescriptionCard(model) is { } card) kids.Add(card);
        if (WatchShelf(model, moduleId) is { } shelf) kids.Add(shelf);
        return new BoxEl { Key = "watch-caption", Direction = 1, Gap = Spacing.M, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Children = kids.ToArray() };
    }

    static Element MetaRow(WatchPageModel model)
    {
        var kids = new List<Element>(2);
        if (model.IsLive) kids.Add(LiveBadge());
        if (model.MetaLine is { Length: > 0 } meta) kids.Add(Design.Type.TrackMeta(meta) with { MinWidth = 0f, Shrink = 1f, MaxLines = 2, Wrap = TextWrap.Wrap });
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
                PersonPicture.Create("", ChannelAvatar, displayName: name, imageSourcePath: model.ChannelAvatarUrl),
                Design.Type.TrackTitle(name) with { MinWidth = 0f, Shrink = 1f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            ],
        };
        return linked ? row.Interactive(Interaction.ListRow) : row;
    }

    /// <summary>The document's actions as capsules. The play capsule is replaced by an inert "Playing" state capsule
    /// while this page's playable is the item in the bar; nothing is invented (no Save, no Share).</summary>
    static Element? ChipRow(WatchPageModel model, string moduleId, ModulePageDoc doc)
    {
        var kids = new List<Element>(model.Chips.Length + 1);
        bool playing = model.Stage == WatchStageKind.Live;
        foreach (WatchChip chip in model.Chips)
        {
            bool play = string.Equals(chip.Kind, PageAction.KindPlay, StringComparison.Ordinal);
            if (play && playing) { kids.Add(PlayingChip()); continue; }
            if (InvokeFor(moduleId, chip.Kind, chip.Id, chip.PlayableId, chip.Url) is not { } invoke) continue;
            string? glyph = play ? Icons.Play : string.Equals(chip.Kind, PageAction.KindOpenUrl, StringComparison.Ordinal) ? Icons.OpenInNewWindow : null;
            kids.Add(Controls.Pill(chip.Label, invoke, chip.Primary ? ButtonAppearance.Accent : ButtonAppearance.Standard, glyph: glyph)
                with { Key = "chip:" + chip.Id + ":" + chip.Kind });
        }
        if (kids.Count == 0) return null;
        return new BoxEl { Key = "watch:chips", Direction = 0, Gap = Spacing.S, Wrap = true, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = kids.ToArray() };
    }

    static BoxEl PlayingChip() => new()
    {
        Key = "chip:playing", Height = Controls.PillHeight, Shrink = 0f, Direction = 0, Gap = Spacing.S,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Padding = new Edges4(18f, 0f, 18f, 0f),
        Corners = Radii.FullAll, Fill = Tok.FillSubtleSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
        Role = AutomationRole.Text,
        Children =
        [
            Icon(Icons.Play, 12f, Design.Accent.Decor),
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
        if (prose) kids.Add(Embed.Comp(() => new ExpandableText(model.Description!, DescriptionLines)) with { Key = "watch:desc:" + model.Description!.GetHashCode() });
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
                    header: model.ShelfTitle is { Length: > 0 } t ? Controls.SectionHeader(t) : null,
                    minCardW: 220f, maxCardW: 300f, measured: true,
                    keyOf: static (item, i) => item.EntityId ?? item.PlayableId ?? i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    maxItems: ShelfMax),
            ],
        };
    }

    static Element VideoCard(WatchItem item, string moduleId, float width)
    {
        string? playable = item.PlayableId is { Length: > 0 } pid ? ModuleUri.Encode(moduleId, pid) : null;
        string? route = Pages.RouteForEntity(moduleId, item.EntityId);
        Action open = route is not null ? () => Navigate(route!, item.Title) : playable is not null ? () => PlayOrToggle(playable!) : NoOp;
        float w = Math.Max(120f, width), h = MathF.Round(w * 9f / 16f);
        string meta = item.Subtitle is { Length: > 0 } s
            ? item.Meta is { Length: > 0 } m ? s + WatchPageModel.FactSeparator + m : s
            : item.Meta ?? "";
        var kids = new List<Element>(3)
        {
            Controls.Artwork(item.ImageUrl, w, h, Radii.Card),
            Design.Type.CardTitle(item.Title) with { Width = w, MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
        };
        if (meta.Length > 0) kids.Add(Design.Type.TrackMeta(meta) with { Width = w, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f });
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.XS, Width = w, Shrink = 0f, Padding = new Edges4(0f, 0f, 0f, Spacing.S),
            Cursor = CursorId.Hand, OnClick = open, Role = AutomationRole.Button, Focusable = true, Corners = Radii.CardAll,
            Children = kids.ToArray(),
        }.Interactive(Interaction.Subtle);
    }

    // ══ 5. SMALL COMPONENTS ════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Plain module prose, clamped to <c>lines</c> with a quiet More/Less toggle. PLAIN text on purpose: a
    /// module's description is not markup, and nothing a module writes is parsed as a link.</summary>
    sealed class ExpandableText(string text, int lines) : Component
    {
        readonly Signal<bool> _expanded = new(false);

        public override Element Render()
        {
            bool expanded = _expanded.Value;
            bool long_ = text.Length > lines * 90 || text.AsSpan().Count('\n') >= lines;
            var kids = new List<Element>(2)
            {
                new TextEl(text)
                {
                    Size = 14f, LineHeight = 20f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f,
                    MaxLines = expanded ? 0 : lines, Trim = expanded ? TextTrim.None : TextTrim.CharacterEllipsis,
                },
            };
            if (long_)
                kids.Add(HyperlinkButton.Create(Loc.Get(expanded ? Strings.Common.Less : Strings.Common.More),
                    () => _expanded.Value = !_expanded.Peek(), size: ControlSize.Small) with { AlignSelf = FlexAlign.Start });
            return new BoxEl { Direction = 1, Gap = Spacing.XS, MinWidth = 0f, Children = kids.ToArray() };
        }
    }

    /// <summary>The hero subtitle: the player bar's meta-link grammar (hover recolours THIS word), navigating to the page
    /// the document named. Plain text when there is nowhere to go.</summary>
    sealed class MetaLink(string text, string? route) : Component
    {
        readonly Signal<bool> _hover = new(false);

        public override Element Render()
        {
            bool enabled = route is not null;
            bool hover = enabled && _hover.Value;
            return new BoxEl
            {
                MinWidth = 0f, Shrink = 1f, AlignSelf = FlexAlign.Start,
                Cursor = enabled ? CursorId.Hand : null,
                OnClick = enabled ? () => Navigate(route!, text) : null,
                OnHoverMove = enabled ? _ => { if (!_hover.Peek()) _hover.Value = true; } : null,
                OnPointerExit = enabled ? () => { if (_hover.Peek()) _hover.Value = false; } : null,
                Role = enabled ? AutomationRole.Hyperlink : AutomationRole.Text, Focusable = enabled,
                Children =
                [
                    new TextEl(text)
                    {
                        Size = 14f, LineHeight = 20f, Color = hover ? Tok.TextPrimary : Tok.TextSecondary, Underline = hover,
                        Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    },
                ],
            };
        }
    }
}
