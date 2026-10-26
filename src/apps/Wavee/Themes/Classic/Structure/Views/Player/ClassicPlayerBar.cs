// ── Themes/Classic/Structure/Views/Player/ClassicPlayerBar.cs — Classic's `Player.Bar` (themes plan 6.1, T6a) ──────────────
//
// From `Shell.PlayerBar.UI.cs`: the element tree `PlayerBarContent.Render` built inline (the dock, the top activity edge, the
// single centred row with its left identity block, centre transport + seek and right cluster), and the parts it placed: the
// transport and command buttons, the reserved slots, the "⋯", the volume, devices and remote-device faces. Elements, keys,
// transitions and bound channels are exactly what the shell built before the move, so Classic renders and animates as it always
// did. The podcast transport (the ±10 s steps, the speed button and its panel) is `ClassicPlayerBarPodcast.cs`.
//
//   THE COST MODEL (ch 20 §9). The BAR is rich (marquees, links, menus, drag, drop) but re-renders only on LOW-frequency facts;
//   everything HOT is either a compositor bind (the seek fill + thumb, the volume rail: the seek rail's `Player.SeekRail` and the
//   slider's own binds) or an isolated bound text channel (the two time labels); and the dock root is a LAYOUT FIREWALL
//   (`IsolateLayout`), so a title change never re-solves the window.
//
//   THE CORE HALF RUNS FIRST. `props.Host.Use(ctx.Hooks)` runs the bar's bookkeeping hooks (the level machine, the face hold's
//   layout effects, the deadline timers, the volume mirror) on this template's hook context and answers the decided
//   `PlayerBarView`; everything below draws that view. This template's own hooks (the hover signal, the captured nodes, the like
//   pop and the face cross-fade) follow, as they followed the bookkeeping in the single component.
//
//   PER WINDOW TIER (TC2, `ClassicPlayerTiers`): the level the core hands in is already the tier's (the sub-ladder clamped to the window
//   tier's range); the template draws the tier's FORM. Compact is the 56-DIP MINI bar (the progress hairline, the identity as one door to
//   the now-playing sheet, the heart and the play disc at 40 x 40); Small and Standard the single centred row as before; Huge the three
//   zones (a 360 identity zone with the 56 cover, the transport with shuffle and repeat CENTRED in a column of at most 1040, a 360 right
//   zone with a 128 volume rail). Every hook runs in every form, in the same order; the form only changes what is assembled.
//
//   What the template places but does not draw itself: the seek rail and the time labels (`props.SeekBar` / `props.TimeText` are the
//   core hosts shared with the stage and the on-media strip; they mount `Player.SeekRail` / `Player.TimeLabel`, drawn by
//   `ClassicTransport.cs`). The credits line under the title is this template's own (`ClassicPlayerBar.Credits.cs`, over the facts
//   `props.Credits` states); the device picker's menu is `Player.DevicePicker`.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's player bar.</summary>
internal static partial class ClassicPlayerBar
{
    /// <summary>The registered template of <see cref="ViewKeys.PlayerBar"/>.</summary>
    public static Element Template(PlayerBarProps props, in ViewContext ctx)
    {
        var rc = ctx.Hooks;
        var verbs = props.Verbs;

        // ── the core half: its hooks, then the decided view ──
        var v = props.Host.Use(rc);
        var m = v.Metrics;
        // The window tier's form (TC2): read every render (a hook), so a crossing re-renders the bar once, on the crossing frame.
        var barTier = ClassicPlayerTiers.BarOf(ctx.Tier());
        bool huge = barTier.Form == ClassicBarForm.Huge;
        float artSize = huge ? ClassicPlayerTiers.HugeArt : m.ArtSize;

        // ── hooks (positional, stable order) ──
        var titleLinkHover = rc.UseSignal(false);      // the title's link ink
        var likeNode = rc.UseRef<NodeHandle>(default);
        var likePrevious = rc.UseRef((0, false));
        var metaNode = rc.UseRef<NodeHandle>(default);            // the text block the face swap cross-fades
        var facePrevious = rc.UseRef<EntityRef>(default);         // the identity that block last stated
        var videoAnchor = rc.UseRef<NodeHandle>(default);
        var videoMenu = rc.UseRef<OverlayHandle?>(null);
        var overlay = rc.UseContext(Overlay.Service);
        var input = rc.UseContext(InputHooks.Current);
        var begin = rc.UseContext(SharedTransition.Begin);   // the Expand door opens the fullscreen stage with the art's flight captured first (null headless)
        var barNode = rc.UseRef<NodeHandle>(default);
        float artScale = rc.UseContext(Viewport.Scale);
        // The title and the artists are marquees of one group (the engine control: the start at rest, two passes after a track change,
        // a pass on hover, one line moving at a time, the title first; nothing moves while the window is covered).
        var marquees = rc.UseRef<Marquee.Group?>(null);
        marquees.Value ??= new Marquee.Group();

        const string titleKey = "np-title";
        const string artistsKey = "np-artists";

        // The heart pops on the SAME playable's save edge only — played on the captured node, because a keyed
        // remount of a focusable button would reset its hover/focus mid-toggle.
        int playingSlot = v.Face.IsNone ? 0 : v.Face.Slot;
        rc.UseLayoutEffect(() =>
        {
            var (previousSlot, previouslyLiked) = likePrevious.Value;
            likePrevious.Value = (playingSlot, v.Liked);
            if (props.LikePops(previousSlot, previouslyLiked, playingSlot, v.Liked) && v.LikeLit
                && !likeNode.Value.IsNull && rc.Anim is { } anim)
                anim.IconSwapIn(likeNode.Value);   // the kit recipe honours reduced motion itself
        }, DepKey.From(playingSlot, v.Liked ? 1 : 0));

        float box = m.ButtonBox, glyph = m.ButtonGlyph;

        // THE FORM (TC2). ONE tree in every form: the Compact mini bar is the same row as the full bar with the transport cut to the play disc and
        // the seek row and the right cluster gone, so what survives a 528 crossing (the cover, the title block, the heart and the play disc)
        // keeps its node and glides into its new place, and the bar's own height eases between the mini's 56 and the 72 through real layout. The
        // two forms were two trees: the cover, the title, the play disc and the text block remounted on every crossing.
        bool mini = barTier.Form == ClassicBarForm.Mini;
        // What the form adds on a crossing fades in; the bar's first frame (a mount) does not replay it.
        var shown = rc.UseRef(false);
        LayoutTransition formMotion = shown.Value ? ClassicPlayerBarLayout.FormMotion : ClassicPlayerBarLayout.MoveMotion;
        LayoutTransition seekFormMotion = shown.Value ? ClassicPlayerBarLayout.SeekFormMotion : ClassicPlayerBarLayout.MoveMotion;
        shown.Value = true;

        // ── LEFT — the now-playing identity ──────────────────────────────────────────────────────────────────────
        Prop<ColorF> titleInk = Prop.Of(() => titleLinkHover.Value && props.TitleHasRoute()
            ? Tok.AccentTextPrimary
            : TitleInkOf(props.TitleInk()));
        // Both lines are KEYED ("np-title"/"np-artists"): the remote-device line is a keyed insert that precedes
        // them, and an unkeyed title would reuse the artists' mounted marquee host (#139).
        // The plain (non-scrolling) title: the run is also the tooltip's measuring style (below). The mini bar's sits on its 18 line.
        var titleRun = new TextEl(Prop.Of<string>(props.TitleText))
        {
            Size = ClassicPlayerBarLayout.TitleSize, Weight = ClassicPlayerBarLayout.TitleWeight, Color = titleInk,
            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
        if (mini) titleRun = titleRun with { LineHeight = ClassicPlayerTiers.MiniTitleLine };
        // The mini bar's title never scrolls and is no link: the whole identity is ONE door there.
        long face = ((long)v.Face.Kind << 32) | (uint)v.Face.Slot;   // the stated playable: a new one restarts the marquees' passes
        BoxEl titleEl = v.Marquee && !mini
            ? Marquee.Of(Prop.Of<string>(props.TitleText), new Marquee.Style
            {
                FontSize = ClassicPlayerBarLayout.TitleSize, Weight = ClassicPlayerBarLayout.TitleWeight, Foreground = titleInk,
                Speed = ClassicPlayerBarLayout.MarqueeSpeed, FadeBand = ClassicPlayerBarLayout.MarqueeFade,
            }, marquees.Value, order: 0, identity: face)
            : new BoxEl
            {
                ClipToBounds = true, MinWidth = 0f,
                Children = [titleRun],
            };
        titleEl = titleEl with { Key = titleKey };
        if (v.TitleNav && !mini)
            titleEl = titleEl with
            {
                Cursor = CursorId.Hand, OnClick = props.NavigateTitle,
                Role = AutomationRole.Hyperlink, Focusable = true,
                OnHoverMove = _ => { if (!titleLinkHover.Peek()) titleLinkHover.Value = true; },
                OnPointerExit = () => { if (titleLinkHover.Peek()) titleLinkHover.Value = false; },
            };
        // A scrolling title shows itself in full (no tip: an empty text never measures cut); a plain one, the mini bar's always (it never
        // scrolls), is cut at the slot, so its full text rides a tooltip that exists only while it is cut. The same host in both forms and with
        // the marquee on or off, so the title keeps its node across the 528 crossing (the mini bar's title had no tip with the marquee on; B11).
        Element titleSlot = Controls.TrimTip(titleEl, v.Marquee && !mini ? "" : props.TitleText(), titleRun) with { Key = titleKey };

        var metaKids = new List<Element>(3);
        if (mini)
        {
            // The second line: the artists or, while the output plays on another device, "Playing on" in their place (the bar's remote state at
            // every tier). The two lines share one slot of one height and cross-fade in it, never a cut.
            metaKids.Add(titleSlot);
            if (props.DeviceIsRemote())
                metaKids.Add(new BoxEl
                {
                    Key = "remote-device-line", ClipToBounds = true, MinWidth = 0f,
                    Enter = ClassicTierMotion.SwapFade, Exit = ClassicTierMotion.SwapFade, Transition = ClassicTierMotion.SwapFadeMotion,
                    Children = [Embed.Comp(() => new MiniRemote(props))],
                });
            else if (v.ShowArtistLine)
                metaKids.Add(new BoxEl
                {
                    Key = artistsKey, ClipToBounds = true, MinWidth = 0f,
                    Enter = ClassicTierMotion.SwapFade, Exit = ClassicTierMotion.SwapFade, Transition = ClassicTierMotion.SwapFadeMotion,
                    Children = [Embed.Comp(() => new MiniCredits(props))],
                });
        }
        else
        {
            if (v.RemoteLine)
                metaKids.Add(new BoxEl
                {
                    Key = "remote-device-line", Animate = ClassicPlayerBarLayout.ItemMotion,
                    Children = [Embed.Comp(() => new RemoteDeviceLine(props))],
                });
            metaKids.Add(titleSlot);
            if (v.ShowArtistLine)
                metaKids.Add(v.Marquee
                    ? Marquee.Content(Embed.Comp(() => new Credits(props, compact: false)), Prop.Of(() => props.Credits().Line(ClassicTransportLayout.CreditSeparator)),
                        new Marquee.Style
                        {
                            FontSize = ClassicTransportLayout.CreditSize, Speed = ClassicPlayerBarLayout.MarqueeSpeed, FadeBand = ClassicPlayerBarLayout.MarqueeFade,
                        }, marquees.Value, order: 1, identity: face) with { Key = artistsKey }
                    : new BoxEl
                    {
                        Key = artistsKey, ClipToBounds = true, MinWidth = 0f,
                        Children = [Embed.Comp(() => new Credits(props, compact: true))],
                    });
        }

        var metaCol = new BoxEl
        {
            Key = "meta", Animate = ClassicPlayerBarLayout.MoveMotion,
            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Shrink = 1f,
            Gap = mini ? ClassicPlayerTiers.MiniLinesGap : ClassicPlayerBarLayout.MetaGap, Justify = FlexJustify.Center, ClipToBounds = true,
            OnRealized = h => metaNode.Value = h,
            Children = metaKids.ToArray(),
        };
        // The face's ONE authored change: the text block cross-fades when the identity it states actually swaps.
        // Never on the way INTO a hold or out of it — the held face IS the outgoing one, so nothing changed.
        var faceRef = v.Face;
        rc.UseLayoutEffect(() =>
        {
            var was = facePrevious.Value;
            facePrevious.Value = faceRef;
            if (was.IsNone || faceRef.IsNone || was.Equals(faceRef)) return;
            if (metaNode.Value.IsNull || rc.Anim is not { } anim) return;
            anim.Animate(metaNode.Value, AnimChannel.Opacity, 0f, 1f,
                ClassicPlayerBarLayout.FaceSwap.EffectiveDurationMs(AnimChannel.Opacity), Easing.FluentDecelerate);
        }, DepKey.From((int)faceRef.Kind, faceRef.Slot));

        float artBox = mini ? ClassicPlayerTiers.MiniArt : artSize;
        var art = new BoxEl
        {
            Key = "art", Width = artBox, Height = artBox, Shrink = 0f,
            Cursor = v.ArtNav && !mini ? CursorId.Hand : null,
            OnClick = v.ArtNav && !mini ? props.NavigateArt : null,
            Role = v.ArtNav && !mini ? AutomationRole.Hyperlink : AutomationRole.None,
            Focusable = v.ArtNav && !mini,
            // In the mini bar the door takes the press, the cover inside it none.
            HitTestVisible = !mini,
            // The morph tag ONLY: the click above keeps today's navigation and there is NO double-click entry (a
            // double-click's first click would still navigate, InputDispatcher.DoubleClickMs = 500). The Expand button
            // is the bar's door; the flight captures this TAGGED art wherever the click lands. A morph-tagged slot
            // mounts no shimmer and a frozen placeholder — accepted for a 48-DIP thumbnail.
            Children = [Controls.Artwork(v.ArtUrl, artBox, artBox, mini ? ClassicPlayerTiers.MiniArtCorner : ClassicPlayerBarLayout.ArtCorner,
                morphKey: props.ArtMorphKey, scale: artScale, keepWhileHidden: true)],
        };
        // The heart's SLOT is the level's; an idle bar shows no face in it (and the face cannot be hit or focused). The mini bar's is 40 x 40.
        float likeBox = mini ? ClassicPlayerTiers.MiniButton : box, likeGlyph = mini ? ClassicPlayerTiers.MiniGlyph : glyph;
        var saveIcon = v.FaceIsEpisode ? ActionIcons.Resolve(ActionIcons.Save, v.Liked) : ActionIcons.Resolve(ActionIcons.Heart, v.Liked);
        var saveButton = BarButton(saveIcon.Glyph ?? Icons.Add, () => verbs.ToggleLike(), v.LikeEnabled, v.Liked,
            likeBox, likeGlyph, onRealized: h => likeNode.Value = h, platedWhenLatched: false) with { BlocksDragArm = true };
        // Named for BOTH faces (the heart is an icon with no label of its own): an episode saves, a track likes.
        Element saveFace = ToolTip.Wrap(saveButton, v.FaceIsEpisode
            ? Loc.Get(v.Liked ? Strings.Podcast.Reader.RemoveSaved : Strings.Podcast.Reader.Save)
            : Loc.Get(v.Liked ? Strings.Menu.Saved : Strings.Menu.SaveToLiked));
        var likeSlot = Slot("like", likeBox, likeBox, v.LikeLit, saveFace);

        // The cover and the text block; the heart rides beside them in the identity zone. In the mini bar this flow IS the door (C-PlayerBar
        // mini): the 48-tall identity that opens the now-playing sheet, its subtle plate under the cover and the two lines, never under the heart.
        var leftFlow = new BoxEl
        {
            Key = "left-flow", Grow = 1f, Shrink = 1f, MinWidth = 0f,
            Direction = 0, AlignItems = FlexAlign.Center, ClipToBounds = !mini,
            Gap = mini ? ClassicPlayerTiers.MiniDoorGap : huge ? ClassicPlayerTiers.HugeLeftGap : m.LeftGap,
            Children = mini || v.ShowTitleBlock ? [art, metaCol] : [art],
        };
        if (mini)
            leftFlow = (leftFlow with
            {
                Height = ClassicPlayerTiers.MiniDoorH, Padding = new Edges4(0f, 0f, ClassicPlayerTiers.MiniDoorPadRight, 0f), Corners = Radii.ControlAll,
                Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand,
                OnClick = props.OpenNowPlaying,
                Draggable = v.Draggable ? Drag.Source(props.DragPayload) : null,
            }).Interactive(Interaction.Subtle).WithContextMenu(overlay, () => verbs.NowPlayingMenu());
        // No hover "…" here: the now-playing cluster's menu is the right-click / Menu key / long-press below (owner,
        // 2026-10-02) — an overlay "…" landed between the title and the heart, over the end of the title.
        var left = new BoxEl
        {
            Key = "left",
            // The level's identity width; the mini bar's door takes the row's room.
            Width = mini ? float.NaN : huge ? ClassicPlayerTiers.HugeZoneW : m.LeftW, Grow = mini ? 1f : 0f, Shrink = mini ? 1f : 0f, MinWidth = 0f,
            Animate = ClassicPlayerBarLayout.MoveMotion,
            Direction = 0, AlignItems = FlexAlign.Center, Gap = mini ? ClassicPlayerTiers.MiniGap : huge ? ClassicPlayerTiers.HugeLeftGap : m.LeftGap,
            // The factory PEEKS at promotion time: the mounted cluster outlives every track change. An idle bar is
            // not a drag handle at all. (The mini bar's door carries its own.)
            Draggable = v.Draggable && !mini ? Drag.Source(props.DragPayload) : null,
            Children = mini || (v.ShowTitleBlock && v.ShowLikeSlot) ? [leftFlow, likeSlot] : [leftFlow],
        };
        // Right-click / Menu key / long-press: the track menu over the now-playing target, with its header. ONE seam
        // feeds this cluster AND the immersive stage (`Stage.NowPlayingMenu`, installed by the track menu's owner);
        // unset, the factory answers null and nothing opens. (The mini bar's door carries it.)
        if (!mini) left = left.WithContextMenu(overlay, () => verbs.NowPlayingMenu());

        // ── CENTRE — transport + seek (empty while a full-bleed surface owns the transport) ─────────────────────
        // The mini bar keeps the play disc alone, 40 x 40.
        var transportKids = new List<Element>(5);
        if (!mini && v.OwnsTransport && v.ShowPrevNext)
            transportKids.Add(BarButton(Icons.Previous, () => verbs.Previous(), v.PrevEnabled, false, box, glyph)
                with { Key = "prev" });
        if (!mini && v.OwnsTransport && v.Episode)
            transportKids.Add(SeekStepButton(props, -ClassicPlayerBarLayout.SeekStepMs, box, v.SeekStepEnabled) with { Key = "seek-back-10" });
        if (v.OwnsTransport)
            transportKids.Add(PrimaryButton(props,
                    v.Errored ? ClassicPlayerBarLayout.PlaySolid : v.PauseGlyph ? ClassicPlayerBarLayout.PauseSolid : ClassicPlayerBarLayout.PlaySolid,
                    v.Primary, mini ? ClassicPlayerTiers.MiniButton : m.PrimaryBox, mini ? ClassicPlayerTiers.MiniPlayGlyph : m.PrimaryGlyph)
                with { Key = "primary", Animate = ClassicPlayerBarLayout.MoveMotion });
        if (!mini && v.OwnsTransport && v.ShowPrevNext)
            transportKids.Add(BarButton(Icons.Next, () => verbs.Next(), v.NextEnabled, false, box, glyph)
                with { Key = "next" });

        if (!mini && v.OwnsTransport && v.Episode)
        {
            transportKids.Add(SeekStepButton(props, ClassicPlayerBarLayout.SeekStepMs, box, v.SeekStepEnabled) with { Key = "seek-forward-10" });
            transportKids.Add(Embed.Comp(() => new EpisodeSpeed(props, m.EpisodeSpeedButtonW, m.MinButtonBox)) with { Key = "episode-speed" });
        }

        // Stable keys keep the seek rail's identity (scrub state, cached width) across the 760-DIP label breakpoint.
        var seekKids = new List<Element>(3);
        if (!mini && v.OwnsTransport && v.ShowTimesElapsed)
            seekKids.Add(new BoxEl { Key = "elapsed", Animate = ClassicPlayerBarLayout.ItemMotion, Children = [props.TimeText(false)] });
        if (!mini && v.OwnsTransport)
            seekKids.Add(new BoxEl
            {
                Key = "seek", Direction = 0, Grow = 1f, Shrink = 1f, MinWidth = 0f, ClipToBounds = true, Animate = ClassicPlayerBarLayout.MoveMotion,
                Children = [props.SeekBar()],
            });
        if (!mini && v.OwnsTransport && v.ShowTimesRemaining)
            seekKids.Add(new BoxEl { Key = "remaining", Animate = ClassicPlayerBarLayout.ItemMotion, Children = [props.TimeText(true)] });

        // SHUFFLE AND REPEAT. At Huge they flank the transport (C-PlayerBar huge); below it they lead the right cluster. Both places are in the
        // CENTRE COLUMN, so the 1920 crossing moves them inside one parent: they keep their nodes and glide (two parents remounted them). Below
        // Huge they follow the seek row, set off by the row gap plus the slack the right cluster reserves for the video split (where the right
        // box used to start, so nothing moves from where it was); the right box then starts one cluster gap after them.
        bool shuffle = !mini && (v.Slots & RightSlot.Shuffle) != 0 && (!huge || v.OwnsTransport);
        bool repeat = !mini && (v.Slots & RightSlot.Repeat) != 0 && (!huge || v.OwnsTransport);
        bool flanksRight = !mini && !huge && (shuffle || repeat);
        float rowGap = mini ? ClassicPlayerTiers.MiniGap : huge ? ClassicPlayerTiers.HugeRowGap : m.RowGap;
        float slack = 0f;
        if (flanksRight)
        {
            float content = 0f;
            int count = 0;
            for (int bit = 1; bit <= (int)RightSlot.More; bit <<= 1)
                if ((v.Slots & (RightSlot)bit) != 0) { content += v.SlotWidth((RightSlot)bit); count++; }
            slack = MathF.Max(0f, v.RightWidth - content - m.RightGap * MathF.Max(0, count - 1));
        }
        Element? shuffleSlot = shuffle ? RightSlotOf(RightSlot.Shuffle) : null;
        Element? repeatSlot = repeat ? RightSlotOf(RightSlot.Repeat) : null;

        var transport = new BoxEl
        {
            Key = "transport", Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Animate = ClassicPlayerBarLayout.MoveMotion, Children = transportKids.ToArray(),
        };
        var centre = new BoxEl
        {
            Key = "centre", Grow = mini ? 0f : 1f, Shrink = mini ? 0f : 1f, MinWidth = 0f, Animate = ClassicPlayerBarLayout.MoveMotion,
            Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Margin = new Edges4(rowGap, 0f, 0f, 0f),
            // A drop on the transport is PLAY NEXT — a front-insert, never an immediate playback change. (Not on the mini bar's play disc.)
            DropTarget = mini ? null : Drop.Target<DragPayload>(Drag.Resource,
                accepts: static p => p.CanCopyTracks,
                onDrop: (p, session) => verbs.DropPlayNext(p),
                caption: static _ => Loc.Get(Strings.Drag.PlayNext),
                refusalCaption: static p => Loc.Get(p.Kind == DragKind.Artist ? Strings.Drag.CantAddArtist : Strings.Drag.NothingToAdd),
                visualPolicy: DropTargetVisualPolicy.Spotlight),
            Children =
            [
                // The transport and the seek row in one column: the whole centre below Huge, a column of at most 1040 centred at Huge.
                new BoxEl
                {
                    Key = "centre-column", Grow = mini ? 0f : 1f, Shrink = mini ? 0f : 1f, MinWidth = 0f,
                    MaxWidth = huge ? ClassicPlayerTiers.HugeCentreMaxW : float.NaN,
                    Direction = 0, AlignItems = FlexAlign.Center,
                    Children = CentreColumn(),
                },
            ],
        };

        Element[] CentreColumn()
        {
            if (mini) return [transport];
            var seekRow = new BoxEl
            {
                Key = "seek-row", Direction = 0, Grow = 1f, Shrink = 1f, MinWidth = 0f, AlignItems = FlexAlign.Center,
                Margin = new Edges4(m.ClusterGap, 0f, 0f, 0f),
                Gap = m.SeekGap, Animate = seekFormMotion, Children = seekKids.ToArray(),
            };
            var kids = new List<Element>(5);
            if (huge)
            {
                if (shuffleSlot is not null) kids.Add(shuffleSlot.Value);
                kids.Add(transport);
                if (repeatSlot is not null) kids.Add(repeatSlot.Value);
                kids.Add(seekRow);
                return kids.ToArray();
            }
            kids.Add(transport);
            kids.Add(seekRow);
            float lead = rowGap + slack;
            if (shuffleSlot.Is(out BoxEl s)) { kids.Add(s with { Margin = new Edges4(lead, 0f, 0f, 0f) }); lead = m.RightGap; }
            if (repeatSlot.Is(out BoxEl r)) kids.Add(r with { Margin = new Edges4(lead, 0f, 0f, 0f) });
            return kids.ToArray();
        }

        // ── RIGHT — shuffle/repeat · volume · lyrics · video · queue · devices · expand · ⋯ ──────────────────────
        // Every slot the LEVEL has is in the row at its fixed width (the core's `RightSlots`, in bit order) EXCEPT the
        // video split, which exists iff the current track has a video — the cluster reclaims its 52 DIP otherwise
        // rather than carrying an unlit hole (user decision 2026-09-16). Playback state only lights a face
        // (the core's `SlotFaceVisible`, `v.Lit`). The cluster's width is therefore one of exactly TWO values per level
        // (with / without the split; the wider is the layout's `RightWMax`), a track starting WITHOUT a video moves nothing in the
        // centre, and a video track landing eases the row once via MoveMotion on `right`.
        void OpenVideoMenu()
        {
            if (videoMenu.Value is { IsOpen: true } open) { open.Close(); return; }
            // The menu rises over the spot the anchor's tooltip bubble occupies (the bubble sits above the chevron, the menu
            // opens above it too), and the pointer travelling to a row would sit inside the bubble's safe zone for the
            // whole 5 s dwell. Opening the menu (a right-click or keyboard open sends no press to the wrapper) closes it.
            ToolTip.CloseOpen();
            var items = props.VideoPlacementMenu();
            videoMenu.Value = overlay.Open(
                () => videoAnchor.Value,
                () => Slots.View(ViewKeys.VideoPlacementMenu, new VideoPlacementMenuProps { Items = items, Close = () => videoMenu.Value?.Close() }),
                FlyoutPlacement.TopEdgeAlignedLeft,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
            videoMenu.Value.ClosedAction = () => videoMenu.Value = null;
        }

        Element RightSlotOf(RightSlot slot)
        {
            float w = huge && slot == RightSlot.VolumeSlider ? ClassicPlayerTiers.HugeVolumeW : v.SlotWidth(slot);
            bool lit = (v.Lit & slot) != 0;
            switch (slot)
            {
                case RightSlot.Shuffle:
                    return Slot("shuffle", w, box, lit,
                        BarButton(Icons.Shuffle, () => verbs.ToggleShuffle(), v.CanTransport, v.Shuffle, box, glyph), ClassicPlayerBarLayout.FlankMotion);
                case RightSlot.Repeat:
                    return Slot("repeat", w, box, lit,
                        BarButton(v.RepeatGlyph, () => verbs.CycleRepeat(), v.CanTransport, v.RepeatOn, box, glyph), ClassicPlayerBarLayout.FlankMotion);
                case RightSlot.Volume:
                {
                    bool popup = v.VolumePopup;
                    return Slot("volume", w, box, lit,
                        Embed.Comp(() => new VolumeButton(props, popup, box, glyph))
                            with { Key = (popup ? "volume-popup-" : "volume-inline-") + box + "-" + glyph });
                }
                case RightSlot.VolumeSlider:
                    // The STOCK slider style — a 22-DIP ring, not 0.2.9's old 12-DIP grab; thickness clears the ring.
                    return Slot("volume-slider", w, box, lit,
                        Slider.Create(props.Volume, props.SetVolume, new Slider.SliderOptions { ThumbToolTipValueConverter = props.VolumeText },
                            length: huge ? ClassicPlayerTiers.HugeVolumeW : m.VolumeSliderW, thickness: Slider.DefaultStyle.ThumbRingDiameter));
                case RightSlot.Lyrics:
                    return Slot("lyrics", w, box, lit,
                        ToolTip.Wrap(BarButton(WaveeIcons.Lyrics, props.OpenLyrics, lit, v.LyricsOpen, box, glyph, font: WaveeIcons.Font),
                            Loc.Get(v.Episode ? "podcast.reader.transcript" : Strings.Player.Lyrics)));
                case RightSlot.Video:
                    // Only built while hasVideo holds (the slot is in `slots` iff the track has a video); the face
                    // additionally needs an Active playable, so it stays dark through Loading/Reconnecting/Error.
                    // Latched, the SPLIT wears the one checked plate across both halves (tuned P2: the same plate Lyrics, Queue and
                    // Devices take, never a 32 plate beside an unplated chevron); both glyphs take the latched ink.
                    var (videoPlate, videoPlateHover) = CheckedPlate(v.VideoLive && lit);
                    return Slot("video", w, box, lit, new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center,
                        Corners = Radii.ControlAll, Fill = videoPlate, HoverFill = videoPlateHover, PressedFill = videoPlate,
                        BrushTransitionMs = Design.Motion.Faster,
                        OnRealized = h => videoAnchor.Value = h,
                        OnContextRequested = _ => OpenVideoMenu(),
                        Children =
                        [
                            ToolTip.Wrap(
                                BarButton(Icons.Movie, () => verbs.ToggleVideo(), lit, v.VideoLive, box, glyph, platedWhenLatched: false),
                                Loc.Get(v.VideoLive ? Strings.Player.SwitchToAudio : Strings.Player.SwitchToVideo)),
                            // Narrow but FULL-HEIGHT, so the two glyphs share one centre line.
                            ToolTip.Wrap(
                                BarButton(Icons.ChevronDownSmall, OpenVideoMenu, lit, v.VideoLive, m.SplitChevronW, ClassicPlayerBarLayout.SplitChevronGlyph,
                                    boxHeight: box, platedWhenLatched: false),
                                Loc.Get(Strings.Player.VideoOptions)),
                        ],
                    });
                case RightSlot.Queue:
                    return Slot("queue", w, box, lit,
                        BarButton(Icons.Queue, props.OpenQueue, true, v.QueueOpen, box, glyph));
                case RightSlot.Devices:
                    return Slot("devices", w, box, lit, Embed.Comp(() => new DevicesButton(props, box, glyph)));
                case RightSlot.Expand:
                    // The bar's door to the fullscreen stage. The now-playing RAIL toggle moved to the "⋯" overflow
                    // (OverflowCommand.NowPlaying), so the rail keeps a door; this button never latches.
                    return Slot("expand", w, box, lit,
                        ToolTip.Wrap(
                            BarButton(Icons.FullScreen, () => verbs.OpenStage(begin, "bar-expand"), true, false, box, glyph),
                            Loc.Get(Strings.Player.Fullscreen)));
                default:
                    // Reserved only where the idle menu is already non-empty, so the command set is never empty here.
                    return Slot("more", w, box, lit, MoreButton(props, v.Overflow, box, glyph));
            }
        }

        var rightKids = new List<Element>(10);
        for (int bit = 1; !mini && bit <= (int)RightSlot.More; bit <<= 1)
        {
            var slot = (RightSlot)bit;
            if (slot is RightSlot.Shuffle or RightSlot.Repeat) continue;   // the centre column carries them (above)
            if ((v.Slots & slot) != 0) rightKids.Add(RightSlotOf(slot));
        }

        // At Huge the right zone mirrors the identity zone (360, the board's), so the transport column sits on the bar's centre line.
        var right = huge
            ? new BoxEl
            {
                Key = "right", MinWidth = ClassicPlayerTiers.HugeZoneW, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End,
                Margin = new Edges4(rowGap, 0f, 0f, 0f),
                Gap = m.RightGap, Animate = formMotion, Children = rightKids.ToArray(),
            }
            : new BoxEl
            {
                // With shuffle and repeat in the centre column the slack went with them: the box is its slots' width, one cluster gap after them.
                Key = "right", Width = flanksRight ? float.NaN : v.RightWidth, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End,
                Margin = new Edges4(flanksRight ? m.RightGap : rowGap, 0f, 0f, 0f),
                Gap = m.RightGap, Animate = formMotion, Children = rightKids.ToArray(),
            };

        // ── assemble: the top edge (the activity edge, or the mini bar's progress hairline) + the single centred row ─────────────────
        Element topEdge = mini
            ? props.ProgressLine is { } line
                ? new BoxEl { Key = "mini-progress", Height = ClassicPlayerTiers.MiniProgressH, Shrink = 0f, Direction = 0, Children = [line()] }
                : new BoxEl { Key = "mini-progress", Height = ClassicPlayerTiers.MiniProgressH, Shrink = 0f, Fill = Tok.StrokeDividerDefault }
            : v.TopEdgeSweeps
                ? ProgressBar.Indeterminate(m.TopEdgeWidth)
                : new BoxEl
                {
                    // The ONE seam on this edge.
                    Height = 1f,
                    Fill = Prop.Of(static () => FluentGpu.Dsl.Theme.Dark ? Tok.StrokeDividerDefault : ClassicPlayerBarLayout.TopEdgeSeamLight),
                };

        // THE SLOT: the bar's height. The 528 crossing changes it (the mini's 56, the 72) and it eases through real layout, so the page above
        // yields frame by frame (the frame's bar reservations ride the same recipe); the bar inside fills it, its row centred, so its parts glide
        // with the edge. The slot is the layout firewall (a title change re-solves THIS subtree only). It sits under an auto-sized root: the
        // view's anchor mirrors its root's DECLARED height, so a slot that was the root left the anchor (and the page above) at the new height
        // at once while the slot eased inside it.
        return new BoxEl
        {
            Direction = 1, Shrink = 0f,
            Children =
            [
                new BoxEl
                {
                    Direction = 1, Height = barTier.Height, Shrink = 0f, ClipToBounds = true, IsolateLayout = true,
                    Animate = ctx.Reduced ? null : ClassicTierMotion.HeightReflow,
                    Children =
                    [
                        new BoxEl
                        {
                            // NO fill, NO shadow, NO Elevation.DockTop: the dock is a paint-site omission over live Mica.
                            Direction = 1, Grow = 1f, MinHeight = 0f,
                            Focusable = true, FocusVisualMargin = Edges4.All(Spacing.XXS),
                            OnRealized = node => barNode.Value = node,
                            OnClick = mini ? null : () => input.FocusNode?.Invoke(barNode.Value, true),
                            OnKeyDown = e => props.OnKey(e, input.GetFocus?.Invoke() == barNode.Value),
                            Children =
                            [
                                topEdge,
                                new BoxEl
                                {
                                    Key = "player-row", Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center,
                                    // The clusters' gaps are their own leading margins (the right box's depends on what precedes it).
                                    Padding = mini ? ClassicPlayerTiers.MiniPadding
                                        : huge ? new Edges4(ClassicPlayerTiers.HugeRowPad, 0f, ClassicPlayerTiers.HugeRowPad, 0f) : new Edges4(m.RowPad, 0f, m.RowPad, 0f),
                                    Animate = ClassicPlayerBarLayout.MoveMotion,
                                    // ALWAYS three clusters above Compact: the row's shape is the level's, never the state's. The mini bar: the door and
                                    // its heart, then the play disc while this bar owns the transport.
                                    Children = !mini ? [left, centre, right] : v.OwnsTransport ? [left, centre] : [left],
                                },
                            ],
                        },
                    ],
                },
            ],
        };
    }

    // ══ THE MINI BAR'S LINES (Compact, TC2: C-PlayerBar tier="mini", C-Shell-Compact) ═════════════════════════════════════

    /// <summary>The mini bar's "Playing on" line in the artist line's place: the device glyph and the words in the remote line's accent (88 %), plain
    /// (the whole identity is ONE door; the picker is the sheet's and the full bar's).</summary>
    private sealed class MiniRemote(PlayerBarProps props) : Component
    {
        public override Element Render()
        {
            string? name = props.RemoteDeviceName();
            if (name is null) return new BoxEl { Height = 0f, HitTestVisible = false };
            ColorF ink = Tok.AccentDefault with { A = ClassicPlayerBarLayout.RemoteLineRestAlpha };
            return new BoxEl
            {
                Height = ClassicPlayerTiers.MiniSubLine, Direction = 0, AlignItems = FlexAlign.Center, Gap = ClassicPlayerTiers.MiniRemoteGap,
                MinWidth = 0f, ClipToBounds = true,
                Children =
                [
                    new TextEl(Icons.Devices) { Size = ClassicPlayerTiers.MiniRemoteGlyph, FontFamily = Tok.Definition.Fonts.Icon, Color = ink, Shrink = 0f },
                    new TextEl(Strings.Player.PlayingOn(name))
                    {
                        Size = ClassicPlayerTiers.MiniSubSize, LineHeight = ClassicPlayerTiers.MiniSubLine, Color = ink,
                        MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
                    },
                ],
            };
        }
    }

    /// <summary>The mini bar's artist line: the names as plain text (the whole identity is ONE door, so nothing in it is a link of its own).</summary>
    private sealed class MiniCredits(PlayerBarProps props) : Component
    {
        public override Element Render()
        {
            _ = UseComputed(props.CreditsStamp).Value;
            var credits = props.Credits();
            string text = credits.Show is { } show ? show.Text : JoinNames(credits.Artists);
            return new TextEl(text)
            {
                Size = ClassicPlayerTiers.MiniSubSize, LineHeight = ClassicPlayerTiers.MiniSubLine, Color = Tok.TextSecondary,
                MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            };
        }

        static string JoinNames(PlayerCreditLink[] artists)
        {
            var names = new List<string>(artists.Length);
            foreach (var a in artists) if (a.Text.Length > 0) names.Add(a.Text);
            return string.Join(", ", names);
        }
    }

    private static ColorF TitleInkOf(NowPlayingInk ink) => ink switch
    {
        NowPlayingInk.Secondary => Tok.TextSecondary,
        NowPlayingInk.Critical => ClassicPlayerBarLayout.CriticalInk,
        _ => Tok.TextPrimary,
    };

    // ══ THE BUTTONS ═══════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A transport TOGGLE / command glyph. The on-state is WinUI's AppBarToggleButton checked visual — an
    /// accent glyph over <c>FillSubtleSecondary</c>, cross-faded over 83 ms — and the resting off-state is completely
    /// unpainted (a row of resting plates would re-plate the dock). Flat 32/16 at every level.
    /// <para><paramref name="platedWhenLatched"/> false is the HEART: its saved state is already said twice — the glyph
    /// swaps to the filled heart and takes the accent — so a checked plate behind it is a third, boxy signal next to the
    /// title. It keeps the accent ink and stays unpainted, latched or not. The video split's halves pass false too: the split
    /// wears <see cref="CheckedPlate"/> across both of them.</para></summary>
    private static BoxEl BarButton(string glyphText, Action onClick, bool enabled, bool latched, float box, float glyphSize,
        Action<NodeHandle>? onRealized = null, string? font = null, float boxHeight = float.NaN,
        bool platedWhenLatched = true)
    {
        ColorF ink = !enabled ? Tok.TextDisabled : latched ? Tok.AccentDefault : Tok.TextSecondary;
        ColorF inkHover = !enabled ? Tok.TextDisabled : latched ? Tok.AccentDefault : Tok.TextPrimary;
        var (plate, plateHover) = CheckedPlate(latched && enabled && platedWhenLatched);
        return new BoxEl
        {
            Width = box, Height = float.IsNaN(boxHeight) ? box : boxHeight,
            Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.ControlAll,
            Fill = plate, HoverFill = plateHover, PressedFill = plate,
            BrushTransitionMs = Design.Motion.Faster,
            HoverScale = R.Motion.ScaleEmphatic.HoverIf(enabled), PressScale = R.Motion.ScaleEmphatic.PressIf(enabled),
            // Clicking never moves focus, so the shell's Space shortcut keeps working after a click.
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
            OnRealized = onRealized,
            IsEnabled = enabled, OnClick = onClick, Cursor = enabled ? CursorId.Hand : null,
            Children = [new TextEl(glyphText) { Size = glyphSize, FontFamily = font ?? Tok.Definition.Fonts.Icon, Color = ink, HoverColor = inkHover }],
        };
    }

    /// <summary>THE checked plate (tuned P2): the one "on" plate every latched right-cluster button shares (Lyrics, Queue, Devices
    /// and the whole video split), its rest and hover fills; an unlatched button is unpainted.</summary>
    private static (ColorF Fill, ColorF Hover) CheckedPlate(bool on)
        => on ? (Tok.FillSubtleSecondary, Tok.FillSubtleTertiary) : (ColorF.Transparent, ColorF.Transparent);

    /// <summary>A RESERVED slot in the bar's row: a fixed <paramref name="w"/>×<paramref name="h"/> box the level owns,
    /// whose <paramref name="face"/> is either lit (opaque, hit-testable) or dark (transparent, inert) — cross-faded over
    /// the control token, never entered/exited, so a face lighting up moves NOTHING around it. The face's own
    /// <c>IsEnabled</c> should follow <paramref name="lit"/> too, so a dark face is not a focus stop.</summary>
    private static BoxEl Slot(string key, float w, float h, bool lit, Element face, LayoutTransition? motion = null) => new()
    {
        Key = key, Width = w, Height = h, Shrink = 0f,
        Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Opacity = lit ? 1f : 0f, HitTestVisible = lit,
        // Opacity-only layout transition: a face lights or dims over the control-normal beat with NO bounds channel,
        // so the slot's rect (and every neighbour's) never moves. Element.Transition alone would snap here — the
        // reconciler routes it only through Enter/Exit synthesis, not a live static Opacity change.
        Animate = motion ?? ClassicPlayerBarLayout.FaceMotion,
        Children = [face],
    };

    /// <summary>The primary play/pause: a filled Fluent/WinUI transport circle (same verb as Stage.Play). Disabled, the
    /// plate falls back to a subtle rest and the glyph to disabled ink. The glyph is drawn at 0.8× the level's glyph size
    /// — a solid shape carries more weight than the outline the size was tuned for — and the play triangle is nudged
    /// right by a twelfth of its size, its optical centre (the pause bars are symmetric and stay put).</summary>
    private static BoxEl PrimaryButton(PlayerBarProps props, string glyphText, PrimaryVerb verb, float box, float glyphSize)
    {
        bool enabled = verb != PrimaryVerb.None;
        var verbs = props.Verbs;
        float solid = MathF.Round(glyphSize * ClassicPlayerBarLayout.PrimaryGlyphScale);
        float nudge = glyphText == ClassicPlayerBarLayout.PlaySolid ? MathF.Round(solid / ClassicPlayerBarLayout.PlayNudgeDivisor) : 0f;
        Action onClick = verb == PrimaryVerb.Retry ? () => verbs.Retry() : () => verbs.TogglePlay("playerbar.button");
        return new BoxEl
        {
            Width = box, Height = box, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.Circle(box),
            Fill = enabled ? Tok.AccentDefault : Tok.FillSubtleSecondary,
            HoverFill = enabled ? Tok.AccentSecondary : Tok.FillSubtleSecondary,
            PressedFill = enabled ? Tok.AccentTertiary : Tok.FillSubtleSecondary,
            BrushTransitionMs = Design.Motion.Faster,
            HoverScale = R.Motion.ScaleEmphatic.HoverIf(enabled), PressScale = R.Motion.ScaleEmphatic.PressIf(enabled),
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
            IsEnabled = enabled, Cursor = enabled ? CursorId.Hand : null,
            OnClick = onClick,
            Children =
            [
                new TextEl(glyphText)
                {
                    Size = solid, FontFamily = Tok.Definition.Fonts.Icon,
                    Color = enabled ? Tok.TextOnAccentPrimary : Tok.TextDisabled,
                    Margin = new Edges4(nudge, 0f, 0f, 0f),
                },
            ],
        };
    }

    // ── the "⋯" overflow ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The overflow button's FACE, remounted by a Key over its COMMAND SET (the slot around it is the level's, so a
    /// command set changing with playback swaps the face in place). Rows are built at open time from live reads (the core's
    /// <c>OverflowRow</c>), so a latched toggle's check mark is fresh on every open without the key having to encode it.</summary>
    private static Element MoreButton(PlayerBarProps props, OverflowCommand[] commands, float box, float glyphSize)
    {
        int key = commands.Length;
        for (int i = 0; i < commands.Length; i++) key = key * 11 + (int)commands[i] + 1;
        return Embed.Comp(() => new MoreMenu(props, commands, box, glyphSize)) with { Key = "more#" + key };
    }

    private sealed class MoreMenu(PlayerBarProps props, OverflowCommand[] commands, float box, float glyphSize) : Component
    {
        readonly PlayerBarProps _props = props;
        readonly OverflowCommand[] _commands = commands;
        readonly float _box = box, _glyph = glyphSize;

        public override Element Render()
        {
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            var overlay = UseContext(Overlay.Service);
            var begin = UseContext(SharedTransition.Begin);   // the "Full screen" row captures the art's flight before the stage mounts
            var commands = _commands;
            var props = _props;

            void Toggle()
            {
                if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
                if (commands.Length == 0) return;
                var items = new List<MenuFlyoutItem>(commands.Length);
                for (int i = 0; i < commands.Length; i++) items.Add(props.OverflowRow(commands[i], begin));
                // A PLAIN MenuFlyout upward — never CommandBarFlyout, whose second clip fought the reveal.
                handle.Value = overlay.Open(
                    () => anchor.Value,
                    () => MenuFlyout.Create(items, () => handle.Value?.Close(), size: OverlayFit.MenuSize),
                    FlyoutPlacement.TopEdgeAlignedRight,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
                handle.Value.ClosedAction = () => handle.Value = null;
            }

            return new BoxEl
            {
                Width = _box, Height = _box, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                HoverScale = R.Motion.ScaleEmphatic.Hover, PressScale = R.Motion.ScaleEmphatic.Press,
                Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
                OnClick = Toggle, Cursor = CursorId.Hand, OnRealized = h => anchor.Value = h,
                Children = [new TextEl(Icons.More) { Size = _glyph, FontFamily = Tok.Definition.Fonts.Icon, Color = Tok.TextSecondary, HoverColor = Tok.TextPrimary }],
            };
        }
    }

    // ══ VOLUME ══════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The volume glyph. Inline form: a click MUTES. Popup form (the levels without the inline rail): a click opens the
    /// vertical rail above it. Remounted by its Key when the form changes. It re-renders only when the MUTE GLYPH flips — a
    /// volume drag moves the compositor rail, not this.</summary>
    private sealed class VolumeButton(PlayerBarProps props, bool popup, float box, float glyphSize) : Component
    {
        readonly PlayerBarProps _props = props;
        readonly bool _popup = popup;
        readonly float _box = box, _glyph = glyphSize;

        public override Element Render()
        {
            var props = _props;
            bool muted = props.Muted.Value;
            var overlay = UseContext(Overlay.Service);
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            bool popupForm = _popup;

            void Click()
            {
                if (!popupForm) { props.ToggleMute(); return; }
                if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
                handle.Value = overlay.Open(
                    () => anchor.Value,
                    () => new BoxEl
                    {
                        Width = ClassicPlayerBarLayout.VolumePopupWidth, Height = ClassicPlayerBarLayout.VolumePopupHeight,
                        Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        Padding = ClassicPlayerBarLayout.VolumePopupPadding,
                        Children =
                        [
                            Slider.Create(props.Volume, props.SetVolume,
                                new Slider.SliderOptions { Vertical = true, IsThumbToolTipEnabled = false },
                                length: ClassicPlayerBarLayout.VolumePopupRailLength, thickness: ClassicPlayerBarLayout.VolumePopupRailThickness),
                        ],
                    },
                    FlyoutPlacement.TopCenter,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
                        { ConstrainToRootBounds = false });
                handle.Value.ClosedAction = () => handle.Value = null;
            }

            return BarButton(muted ? Icons.Mute : Icons.Volume, Click, true, false, _box, _glyph, h => anchor.Value = h);
        }
    }

    // ══ DEVICES ═════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The Devices button. LATCHED only while playback is on ANOTHER device (the same owner-gated verdict the
    /// "Playing on" line reads, so the two cannot disagree). It also opens ITSELF when the model bumps the picker request
    /// (a toast's "Choose device") — only for a request that post-dates its mount, and the bar mounts exactly one of these, so
    /// the toast opens exactly one picker. The picker's rows are the core's until T6b gives the picker its own key.</summary>
    private sealed class DevicesButton(PlayerBarProps props, float box, float glyphSize) : Component
    {
        readonly PlayerBarProps _props = props;
        readonly float _box = box, _glyph = glyphSize;

        public override Element Render()
        {
            var props = _props;
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            var overlay = UseContext(Overlay.Service);
            bool latched = props.DeviceIsRemote();
            int request = props.DevicePickerRequest.Value;
            var lastRequest = UseRef(request);

            void Toggle() => props.ToggleDevicePicker(overlay, anchor, handle);

            UseEffect(() =>
            {
                if (request == lastRequest.Value) return;
                lastRequest.Value = request;
                if (handle.Value is not { IsOpen: true }) Toggle();
            }, request);

            return BarButton(Icons.Devices, Toggle, true, latched, _box, _glyph, h => anchor.Value = h);
        }
    }

    /// <summary>"🖳 Playing on &lt;device&gt;" — 13 DIP, accent @ 88 % → full accent on hover, above the title. A second
    /// anchor for the SAME picker.</summary>
    private sealed class RemoteDeviceLine(PlayerBarProps props) : Component
    {
        readonly PlayerBarProps _props = props;

        public override Element Render()
        {
            var props = _props;
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            var overlay = UseContext(Overlay.Service);
            string? name = props.RemoteDeviceName();
            if (name is null) return new BoxEl { Height = 0f, HitTestVisible = false };

            ColorF accent = Tok.AccentDefault;
            ColorF rest = accent with { A = ClassicPlayerBarLayout.RemoteLineRestAlpha };
            return new BoxEl
            {
                Height = ClassicPlayerBarLayout.RemoteLineHeight, Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS,
                MinWidth = 0f, AlignSelf = FlexAlign.Stretch, ClipToBounds = true,
                Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
                OnClick = () => props.ToggleDevicePicker(overlay, anchor, handle), OnRealized = h => anchor.Value = h,
                Children =
                [
                    new TextEl(Icons.Devices) { Size = ClassicPlayerBarLayout.RemoteLineSize, FontFamily = Tok.Definition.Fonts.Icon, Color = rest, HoverColor = accent },
                    new BoxEl
                    {
                        Shrink = 1f, MinWidth = 0f, ClipToBounds = true,
                        Children =
                        [
                            new TextEl(Strings.Player.PlayingOn(name))
                            {
                                Size = ClassicPlayerBarLayout.RemoteLineSize, LineHeight = ClassicPlayerBarLayout.RemoteLineTextHeight,
                                Weight = ClassicPlayerBarLayout.RemoteLineWeight, Color = rest, HoverColor = accent,
                                MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
                            },
                        ],
                    },
                ],
            };
        }
    }
}
