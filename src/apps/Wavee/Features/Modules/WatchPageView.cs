using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using Wavee.Backend.Modules;
using Wavee.Core;
using Wavee.Features.Video;
using Wavee.Sdk;
using static FluentGpu.Dsl.Ui;
// Both assemblies declare `MediaForm` and both meanings are live in this file (what the module SAYS a playable is vs
// what the app plays it AS), so neither gets to be the unqualified one — the same rule ModulePage.cs states.
using SdkForm = Wavee.Sdk.MediaForm;

namespace Wavee;

/// <summary>
/// The WATCH page's elements — the YouTube-shaped reading of a module document that
/// <see cref="ModulePageDoc.TemplateWatch"/> asks for. Every DECISION behind it (which section becomes the fact line,
/// which becomes the shelf, where the channel identity comes from) already happened in the pure
/// <see cref="WatchPageModel"/>; this file only draws it, with the app's own vocabulary and nothing invented.
///
/// <para><b>The picture is the page.</b> A watch entity's identity is its frame, not its name, so the stage is a
/// full-width 16:9 box pinned above everything and the title drops a rung below <c>WaveeType.PageHero</c>
/// (<c>NowPlayingTitle</c>, 20/28/600) — a 28-DIP display line over a 560-DIP picture reads as two competing
/// identities. Everything else is a CAPTION under that picture: title, meta, channel row, capsules, description card,
/// shelf.</para>
///
/// <para><b>Three fatal hazards live in <see cref="Stage"/>, and they are why it is shaped the way it is.</b>
/// <list type="number">
/// <item>A composited video is a DestOut hole punched into the REAL back buffer. An ancestor
///   <c>TransitionChannels.Opacity</c> multiplies straight into <c>DrawVideoCmd.Opacity</c> (a see-through video with
///   the page bleeding through); an ancestor opacity-GROUP, blur or edge-fade pushes an offscreen RT and the punch
///   never reaches the back buffer at all — <b>the hole vanishes entirely, silently</b>
///   (<c>DockedVideoSurface.cs</c>'s motion paragraph). So the stage carries NO Enter/Exit/Layout transition, NO
///   Opacity, NO stagger, and it is deliberately mounted OUTSIDE <c>ModulePage.Section()</c> (which applies
///   <c>DetailRail.FadeUp</c>), OUTSIDE <c>Skel.Region</c> (whose reveal fades opacity) and OUTSIDE the page
///   <c>ScrollView</c> (whose auto edge-fade is the same offscreen RT).</item>
/// <item><c>AspectRatio</c> is IGNORED on a ZStack node — <c>FlexLayout.MeasureZStack</c> returns before the aspect
///   block ever runs. That is the trap that measured the docked art tile at 326x160 instead of its designed shape. So
///   the aspect box here is a plain COLUMN and the ZStack is its <c>Grow = 1f</c> child.</item>
/// <item><c>Grow = 1</c> inside a NaN-height parent measures 0, which is why the aspect box must derive a real height
///   before the ZStack asks for it.</item>
/// </list></para>
///
/// <para><b>Idle → live must not reflow.</b> The stage envelope is UNCONDITIONAL: the same box, the same height, the
/// same letterbox ground whether a poster or a video is inside it. Only what paints changes, and the one visible
/// cross-fade is the element's own <c>PosterMotion</c> dissolving its <c>PosterContent</c> on the first decoded frame
/// — which is exactly why the idle layer is <see cref="DockedVideoSurface.PosterGround"/> itself and not a
/// hand-rolled lookalike that would cut instead of dissolve.</para>
/// </summary>
static class WatchPageView
{
    /// <summary>The stage's height ceiling. The page twin of <c>ShellResponsiveLayout.DockedVideoMaxH</c> — the same
    /// 560 DIP, declared separately because the two surfaces are sized by different things (the rail's width vs the
    /// content column's) and coupling them would make a rail tuning silently retune the page.
    ///
    /// <para>Clamped AFTER the 16:9 derive, on purpose: on a very wide column the box becomes WIDER than 16:9 and the
    /// ELEMENT pillarboxes inside it with its own <c>ShowLetterboxBars</c>, so the transport's Aspect-ratio menu still
    /// reaches those bars. A wrapper that letterboxed instead would put the bars where the element cannot see them,
    /// which is the defect the docked art tile already paid for once.</para></summary>
    public const float StageMaxH = 560f;

    /// <summary>The idle stage's round play button. 64 DIP — big enough to be the picture's focal point at a
    /// 560-DIP stage and still a control rather than a decoration.</summary>
    const float PlayCtaSize = 64f;

    const float ChannelAvatar = 40f;
    const int ShelfMax = 16;
    const int DescriptionLines = 3;

    // ── the stage ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The full-width 16:9 picture at the top of the page: the letterbox ground, the poster, one accent play
    /// affordance while idle, and the app's ONE video surface once this page's entity is what is playing.</summary>
    /// <param name="model">The projected page.</param>
    /// <param name="stagePlayable">The PLAYABLE uri this page's stage would host (<c>ModuleUri.Encode(moduleId,
    /// playAction.PlayableId)</c>), or <c>""</c> when it would host nothing. Deliberately NOT the page's own entity
    /// uri: a module's entity ids and its playable ids are different namespaces (YouTube's page is
    /// <c>video:tRsQsTMvPNg</c>, its playable is <c>tRsQsTMvPNg</c>), and the arbitration compares against
    /// <c>CurrentTrack.Uri</c>, which is always a playable uri — passing the page uri here made both terms
    /// permanently unequal and the stage never lit.</param>
    /// <param name="bridge">The playback bridge (placement + what is playing).</param>
    /// <param name="ui">Shell chrome state; <c>ActiveStagePlayable</c> is the attached-page half of the arbitration,
    /// written by <c>ModulePage</c> in this same id space.</param>
    /// <param name="onPlay">The page's play verb, or null when the document offers none (the CTA is then absent
    /// rather than dead).</param>
    public static Element Stage(WatchPageModel model, string stagePlayable, PlaybackBridge? bridge, ShellUi? ui,
                                Action? onPlay)
    {
        // The mount gate is a FUNCTION, never a captured bool: it is evaluated by Flow.Show's own node-owned effect
        // (see the Flow.Show comment below) long after this Render returned.
        bool Live() => bridge is not null && ui is not null
            && DockedVideoHosting.ShouldMount(DockedVideoFace.PageStage, bridge.VideoPlacementNow(), stagePlayable,
                                              ui.ActiveStagePlayable.Value, bridge.CurrentTrack.Value?.Uri);

        var layers = new List<Element>(3)
        {
            // ALWAYS the bottom layer, live or not: it is the ground the video's DestOut punch erases, and it is the
            // element's OWN poster composition, so the idle→live handover is one dissolve rather than a swap of two
            // similar-looking trees.
            DockedVideoSurface.PosterGround(ImageOf(model.PosterUrl)),
        };

        if (onPlay is not null) layers.Add(Flow.Show(() => !Live(), PlayCta(onPlay)));

        // Flow.Show, NOT a C# `if`, and this is load-bearing. Flow.KeepAlive exit-FREEZES the outgoing page in the
        // same reconcile pass as the route change, and RunComponent skips a frozen/parked component entirely — so a
        // branch taken inside Render() can never be re-evaluated to hand the surface back, and the video would stay
        // parented to a dead page (tripping OneSurfacePerPlayerGuard, one surface per player). Flow.Show's predicate
        // runs from an effect bound to the NODE (AddBinding), which is not frozen, so the mount still tracks reality
        // while the page itself is parked.
        layers.Add(Flow.Show(Live, Embed.Comp(() => new DockedVideoSurface
        {
            Face = DockedVideoFace.PageStage,
            OwnerStagePlayable = stagePlayable,
        }) with { Key = "module-stage-video" }));

        return new BoxEl
        {
            Key = "module-stage",
            Shrink = 0f, Direction = 1,
            // Width and Height both NaN + a finite available width ⇒ FlexLayout derives h = contentW / (16/9).
            AspectRatio = 16f / 9f,
            MaxHeight = StageMaxH,
            ClipToBounds = true, Fill = Tok.MediaLetterbox,
            // NO Enter / Exit / Layout / Opacity / Stagger — see the class doc's hazard 1. This is not an omission to
            // fill in later; adding any of them erases or washes out the hole.
            Children =
            [
                new BoxEl
                {
                    // A plain column above, a ZStack HERE — hazard 2: AspectRatio never reaches a ZStack's measure.
                    Grow = 1f, MinHeight = 0f, ZStack = true, ClipToBounds = true,
                    Children = layers.ToArray(),
                },
            ],
        };
    }

    /// <summary>The idle stage's single affordance: ONE round accent play button, centred on the poster.
    ///
    /// <para>A circle, not the labelled media capsule. On a full-bleed picture the capsule reads as "a card with a
    /// button on it"; the disc reads as "this is a video", which is the whole claim the stage is making. It is still
    /// the app's own control — <c>WaveeCta.Icon</c>'s square arm wearing <see cref="Radii.Full"/>, so it inherits the
    /// button appearance ramp, the focus ring, the Space/Enter mechanics and the capsule's own hover/press rung — only
    /// the geometry differs.</para>
    ///
    /// <para>Dropping the visible label must NOT drop it from the accessibility tree, so the disc carries the same
    /// "Play" string as a tooltip: that is how every other glyph-only affordance in this file's neighbourhood is named
    /// (<c>DockedVideoSurface.Glyph</c>), and it names the control for a screen reader and for a pointer user at
    /// once.</para></summary>
    static Element PlayCta(Action onPlay) => new BoxEl
    {
        Key = "module-stage-cta",
        Grow = 1f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Children =
        [
            ToolTip.Wrap(WaveeCta.Icon(Icons.Play, onPlay, ButtonAppearance.Accent, size: PlayCtaSize),
                Loc.Get(Strings.Detail.Play)),
        ],
    };

    // ── the caption ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Everything UNDER the picture: title, the LIVE pill + meta line, the channel row, the document's
    /// actions as capsules, the description card (fact line first) and the 16:9 shelf.</summary>
    /// <param name="model">The projected page.</param>
    /// <param name="moduleId">The owning module — every route and every synthetic track id is namespaced by it.</param>
    /// <param name="go">The shell's navigate verb.</param>
    /// <param name="invokeFor">What a capsule DOES, supplied by the page: the watch page's play chip is an explicit
    /// video intent, which is a decision about this surface and not about a chip.</param>
    /// <param name="isPlaying">True while this page's entity is the item in the bar — the play capsule is then
    /// replaced by an inert "Playing" capsule rather than offering to start what is already running.</param>
    /// <param name="acts">Action services (the track menu's world).</param>
    /// <param name="bridge">The playback bridge.</param>
    /// <param name="overlay">The overlay service the context menu opens into.</param>
    public static Element Caption(WatchPageModel model, string moduleId, Action<string, string?> go,
                                  Func<WatchChip, Action?> invokeFor, bool isPlaying,
                                  ActionServices? acts, PlaybackBridge? bridge, IOverlayService? overlay)
    {
        var kids = new List<Element>(6)
        {
            // One rung BELOW WaveeType.PageHero on purpose — see the class doc: the picture is now the identity.
            WaveeType.NowPlayingTitle(model.Title) with
            {
                Key = "watch:title", MaxLines = 2, Wrap = TextWrap.Wrap, MinWidth = 0f,
            },
        };

        if (model.IsLive || model.MetaLine is { Length: > 0 }) kids.Add(MetaRow(model));
        if (model.ChannelName is { Length: > 0 }) kids.Add(ChannelRow(model, moduleId, go));

        if (ChipRow(model, moduleId, invokeFor, isPlaying, acts, bridge, overlay) is { } chips) kids.Add(chips);

        if (DescriptionCard(model, go) is { } card) kids.Add(card);
        if (Shelf(model, moduleId, go, acts, bridge) is { } shelf) kids.Add(shelf);

        return new BoxEl
        {
            Key = "watch-caption", Direction = 1, Gap = Spacing.M, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
            Children = kids.ToArray(),
        };
    }

    /// <summary>The LIVE word-mark beside the facts line. Both halves are the page's existing grammar — the badge is
    /// <c>ModulePage.LiveBadge</c> verbatim, so "this is live" reads identically on both layouts.</summary>
    static Element MetaRow(WatchPageModel model)
    {
        var kids = new List<Element>(2);
        if (model.IsLive) kids.Add(ModulePage.LiveBadge());
        if (model.MetaLine is { Length: > 0 } meta)
            kids.Add(WaveeType.TrackMeta(meta) with { MinWidth = 0f, Shrink = 1f, MaxLines = 2, Wrap = TextWrap.Wrap });
        return new BoxEl
        {
            Key = "watch:meta", Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Children = kids.ToArray(),
        };
    }

    /// <summary>The owner row: a circular avatar and a name. It NAVIGATES when the document named an entity to go to,
    /// and is plain inert text when it did not — a styled-but-dead link is a lie, and one pointing at a route nothing
    /// renders is worse (the same rule <c>ModulePage.ModuleMetaLink</c> already states for the hero subtitle).</summary>
    static Element ChannelRow(WatchPageModel model, string moduleId, Action<string, string?> go)
    {
        string name = model.ChannelName!;
        string? route = ModulePages.RouteForEntity(moduleId, model.ChannelEntityId);
        bool linked = route is { Length: > 0 };
        return new BoxEl
        {
            Key = "watch:channel", Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.M, Spacing.XS),
            Corners = Radii.FullAll, HoverFill = linked ? Tok.FillSubtleSecondary : ColorF.Transparent,
            Cursor = linked ? CursorId.Hand : (CursorId?)null,
            OnClick = linked ? () => go(route!, name) : null,
            Role = linked ? AutomationRole.Hyperlink : AutomationRole.Text,
            Focusable = linked, AllowFocusOnInteraction = false,
            Children =
            [
                PersonPicture.Create("", ChannelAvatar, displayName: name, imageSourcePath: model.ChannelAvatarUrl),
                WaveeType.TrackTitle(name) with
                {
                    MinWidth = 0f, Shrink = 1f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
            ],
        };
    }

    /// <summary>The document's actions as capsules, plus the overflow. Nothing here is invented: there is no Save and
    /// no Share, because a module that wants them says so in its own <see cref="ModulePageDoc.Actions"/>.</summary>
    static Element? ChipRow(WatchPageModel model, string moduleId, Func<WatchChip, Action?> invokeFor, bool isPlaying,
                           ActionServices? acts, PlaybackBridge? bridge, IOverlayService? overlay)
    {
        var kids = new List<Element>(model.Chips.Length + 1);
        string? playableId = null;
        string? artUrl = model.PosterUrl;

        for (int i = 0; i < model.Chips.Length; i++)
        {
            WatchChip chip = model.Chips[i];
            bool play = string.Equals(chip.Kind, PageAction.KindPlay, StringComparison.Ordinal);
            if (play) playableId ??= chip.PlayableId;

            // Already playing: the primary capsule states the FACT instead of offering to start what is running. Not
            // a disabled button — a greyed "Play" invites a click that would do nothing visible.
            if (play && isPlaying)
            {
                kids.Add(PlayingChip() with { Key = "chip:playing" });
                continue;
            }

            Action? invoke = invokeFor(chip);
            if (invoke is null) continue;   // an action we cannot honour is ABSENT, never a dead capsule
            string? glyph = play ? Icons.Play
                : string.Equals(chip.Kind, PageAction.KindOpenUrl, StringComparison.Ordinal) ? Icons.OpenInNewWindow
                : null;
            kids.Add(WaveeCta.Pill(chip.Label, invoke,
                appearance: chip.Primary ? ButtonAppearance.Accent : ButtonAppearance.Standard,
                glyph: glyph) with { Key = "chip:" + chip.Id + ":" + chip.Kind });
        }

        // The overflow is the app's ONE track menu over this page's own playable — the same menu (and the same
        // right-click grammar) a playables row already attaches, so a watch page is not a second vocabulary.
        if (playableId is { Length: > 0 } pid && acts is not null && overlay is not null)
        {
            Track track = LocalPlayables.ForModule(moduleId, pid, model.Title, SdkForm.Video,
                model.ChannelName is { Length: > 0 } c ? new[] { c } : null, artUrl);
            kids.Add(WaveeCta.Icon(Icons.More, null, requestsContext: true)
                .WithContextMenu(overlay, () => Menus.ModuleTrack(new ActionContext(
                    ActionTarget.ForTracks(new[] { track }), acts)))
                with { Key = "chip:more" });
        }

        if (kids.Count == 0) return null;
        return new BoxEl
        {
            Key = "watch:chips", Direction = 0, Gap = Spacing.S, Wrap = true, AlignItems = FlexAlign.Center,
            MinWidth = 0f, Children = kids.ToArray(),
        };
    }

    /// <summary>The inert "Playing" capsule. Built on the pill's own geometry rather than a disabled button so it
    /// reads as a STATE badge in the capsule row, not as a control the user failed to press.</summary>
    static BoxEl PlayingChip() => new()
    {
        Height = WaveeCta.PillHeight, Shrink = 0f, Direction = 0, Gap = Spacing.S,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = new Edges4(18f, 0f, 18f, 0f),
        Corners = Radii.FullAll, Fill = Tok.FillSubtleSecondary,
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
        Role = AutomationRole.Text,
        Children =
        [
            Icon(Icons.Play, 12f, WaveeAccent.Decor),
            Ui.Caption(Loc.Get(Strings.ModulePage.Playing)) with { Weight = 600, Wrap = TextWrap.NoWrap },
        ],
    };

    /// <summary>The description card: the dissolved fact line in bold over the module's prose, behind the app's card
    /// fill. This is where the grey fact tiles WENT — the values kept, the labels dropped, the bento retired.</summary>
    static Element? DescriptionCard(WatchPageModel model, Action<string, string?> go)
    {
        bool hasFacts = model.FactLine is { Length: > 0 };
        bool hasText = model.Description is { Length: > 0 };
        if (!hasFacts && !hasText) return null;

        var kids = new List<Element>(2);
        if (hasFacts) kids.Add(Body(model.FactLine!) with { Key = "watch:facts", Weight = 600, MinWidth = 0f });
        if (hasText)
            kids.Add(RichText.ExpandableFlex(model.Description, 14f, Tok.TextSecondary, Tok.AccentTextPrimary,
                DescriptionLines, "watch-desc", onNavUri: r => go(r, null)));

        return new BoxEl
        {
            Key = "watch:description", Direction = 1, Gap = Spacing.S, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
            Padding = new Edges4(Spacing.L, Spacing.M, Spacing.L, Spacing.M),
            Corners = CornerRadius4.All(Radii.Card),
            Fill = Tok.FillCardSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children = kids.ToArray(),
        };
    }

    /// <summary>The 16:9 shelf under the card — <c>MediaCard.VideoCard</c> in a measured <c>PagedShelf</c>, which is
    /// already a wide thumbnail plus a title plus one free meta line, so a related-video row needs no new card type.
    /// A cell NAVIGATES by entity id and PLAYS by playable id, mirroring the entity layout's cards and playables
    /// blocks exactly; nothing invents a destination the module did not state.</summary>
    static Element? Shelf(WatchPageModel model, string moduleId, Action<string, string?> go,
                          ActionServices? acts, PlaybackBridge? bridge)
    {
        WatchItem[] items = model.Shelf;
        if (items.Length == 0) return null;
        int count = Math.Min(items.Length, ShelfMax);

        return new BoxEl
        {
            Key = "watch:shelf", Direction = 1, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
            Children =
            [
                PagedShelf.Create(
                    count,
                    cardAt: (i, w) =>
                    {
                        WatchItem item = items[i];
                        string uri = item.PlayableId is { Length: > 0 } p ? ModuleUri.Encode(moduleId, p) : "";
                        string? route = ModulePages.RouteForEntity(moduleId, item.EntityId);
                        void Play()
                        {
                            if (item.PlayableId is not { Length: > 0 } pid) return;
                            Track track = LocalPlayables.ForModule(moduleId, pid, item.Title, SdkForm.Video,
                                item.Subtitle is { Length: > 0 } s ? new[] { s } : null, item.ImageUrl);
                            VideoActions.PlayAs(acts?.Svc?.Player, bridge ?? acts?.Playback, track,
                                PlayLinkActions.FormFor(SdkForm.Video));
                        }
                        void Open()
                        {
                            if (route is { Length: > 0 }) go(route, item.Title);
                            else Play();
                        }
                        return MediaCard.VideoCard(ImageOf(item.ImageUrl), item.Title, MetaOf(item), uri,
                            Open, Play, w);
                    },
                    measured: true,
                    header: model.ShelfTitle is { Length: > 0 } t ? Surfaces.SectionHeader(t) : null),
            ],
        };
    }

    /// <summary><c>MediaCard.VideoCard</c>'s third line is a FREE meta string (it is named <c>duration</c> only
    /// because its first caller had one), so the cell's subtitle and its trailing fact share it on the page's own
    /// separator rather than losing one of the two.</summary>
    static string MetaOf(WatchItem item)
    {
        bool sub = item.Subtitle is { Length: > 0 };
        bool meta = item.Meta is { Length: > 0 };
        if (sub && meta) return item.Subtitle + WatchPageModel.FactSeparator + item.Meta;
        if (sub) return item.Subtitle!;
        return meta ? item.Meta! : "";
    }

    static Image? ImageOf(string? url) => url is { Length: > 0 } ? new Image(url) : null;
}
