// ── Shell/Shell.PlayerBar.UI.cs ────────────────────────────────────────────────────────────────────────────────────
// the player bar's CORE pieces: the now-playing reads the face is drawn from, the credits line, the shared seek rail with its
// chapter decoration and the time labels, the device picker's menu and rows, and the overflow's rows. The bar's PICTURE (the
// row, the clusters, the buttons, the slots, the "⋯" and volume faces, the remote-device line, the speed controls) is the
// structure's since T6a: `Themes\Classic\Structure\Views\Player\ClassicPlayerBar.cs`. The hooks and decisions that feed it are
// `Shell.PlayerBarHost` (`Shell.PlayerBar.Host.cs`, [CoreHost]).
//
// Role: UI (the pieces shared with the stage and the on-media strip until T6b / T7 give them keys)
// Owner: I (stage B, I2)
// Wave: 4
// Budget: 2000 lines
// Spec: ch 20 §0-§6, §9 (1,900-2,100); the pure rules it renders from are `Shell.cs` §7 and `Shell.PlayerBar.cs`
//
// THE COST MODEL, ported as one design (ch 20 §9 "Traps"): the BAR is rich (marquees, links, menus, drag, drop) but
// re-renders only on LOW-frequency facts; everything HOT is either a compositor bind (the seek fill + thumb, the volume
// rail) or an isolated bound text channel (the two time labels); and the dock root is a LAYOUT FIREWALL
// (`IsolateLayout`), so a title change never re-solves the window.
//
// THE CLOCK. The playhead is extrapolated from the model's TIMESTAMPED sample — `State.Position(now)`, i.e.
// `PosMs + (now − PosQpc)` on `Playback.FrameNowMs`, the same frame clock the sample was stamped with — never from an
// `Environment.TickCount64` anchor of its own (0.2.9's `_tickWallMs`, deleted). The pixel-due interval that repaints it
// is armed only while the playhead ADVANCES (`SeekRail.Advances`); a paused, loading or refilling bar wakes no frames.
//
// PROPS FREEZE AT MOUNT — the places this surface meets it: the tier arrives as a SIGNAL; the marquee's text and ink arrive as
// `Prop.Of` thunks that read everything they depend on; `BarSeekRail`'s enablement is derived in Render; the volume button is
// remounted by a Key that encodes its form and the "⋯" by a Key over its command set (both in the structure's view; the "⋯"
// rows are built at OPEN time from live reads through `OverflowItem`, so checked states can never be stale).

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Controls.Media;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;

using Wavee.Theming.Contracts;

using RepeatMode = Wavee.Spotify.Decode.RepeatMode;
using ViewSlots = FluentGpu.Theming.Slots;

namespace Wavee;

public static partial class Shell
{
    // ══ 0. THE SHARED SURFACES ═══════════════════════════════════════════════════════════════════════════════════════

    // THE MOUNT POINT is the view key `Player.Bar` (`ShellFrameProps.PlayerBar`): the frame places it, the structure's template
    // draws it and `PlayerBarHost` (below) is the core half the template runs first. Until T6a this was `Shell.PlayerBarDock()`.

    // THE SHARED VERBS (play/pause, shuffle, repeat, retry, like, play-next drop…) and the transport fact set live in
    // `Playback/PlayerVerbs.cs` (`PlayerVerbs.Default`, `PlayerVerbs.TransportFacts`): one play button's worth of behaviour
    // in the app, called by the bar, the stage, the on-media transport, the palette and the queue pills.

    /// <summary>The transport's seek rail as a reusable surface — the immersive stage's identity block mounts the same
    /// one (ch 21). <paramref name="feed"/> is the on-media transport's <c>PlayerChromeFeed</c> (null for the player-bar
    /// instance, which has no idle machine to report to): a scrub is chrome activity, so the rail that drives it must be
    /// able to say so. A <c>PlayerChromeFeed</c> is inert without a <c>MediaPlayerElement</c> owner, so the fullscreen
    /// stage (which has none) reports a scrub through <paramref name="onScrubbing"/> instead (<c>true</c> on press,
    /// <c>false</c> on commit or cancel) — its own idle machine's <c>SetScrubbing</c>. Both are STRUCTURAL: fixed at mount.
    /// <paramref name="reveal"/> (also structural) is the on-media transport's chrome-visible signal: while it reads
    /// <c>false</c> the strip is only faded out, not unmounted, so the rail parks its pixel stepper and its report
    /// effect instead of moving a transform nobody can see (each tick is a recorded and presented main-window frame);
    /// the reveal re-syncs the fraction before the fade-in. Null = always live (the bar, the stage).</summary>
    public static Element SeekBar(PlayerChromeFeed? feed = null, Action<bool>? onScrubbing = null, IReadSignal<bool>? reveal = null)
        => Embed.Comp(() => new BarSeekRail(feed, onScrubbing, reveal));

    /// <summary>A transport time label. <paramref name="remaining"/> picks the right slot (−remaining ⇄ duration, or
    /// the live mark while live); <paramref name="ink"/> is the stage's theme-invariant on-media override (null = the
    /// bar's own caption ink). Both are STRUCTURAL and fixed for the label's life. <paramref name="reveal"/> is the
    /// <see cref="SeekBar"/> signal of the same name: a concealed label holds its last text and stops following the
    /// position, and the reveal brings it current.</summary>
    public static Element TimeText(bool remaining, ColorF? ink = null, IReadSignal<bool>? reveal = null)
        => Embed.Comp(() => new BarTimeText(remaining, ink, reveal));

    /// <summary>The ONE float the volume rails bind (inline + popup). A slider needs a FloatSignal and the model
    /// publishes a Signal&lt;float&gt;; <see cref="PlayerBarHost"/> mirrors model → rail with an equality gate, and the
    /// rail's own writes go to the model through <see cref="Playback.SetVolume"/>.</summary>
    static readonly FloatSignal s_barVolume = new(1f);

    // ── the now-playing reads (every one peeks or reads the model LIVE — never a render-time capture) ────────────────

    /// <summary>(kind, slot, row version): the ONE subscription the bar takes on the catalog. The memo's equality
    /// cut-off means a 300-row playlist landing elsewhere in the scope re-renders nothing here.</summary>
    static (EntityKind Kind, int Slot, uint Version) BarRowStamp()
    {
        var r = Playback.Current.Value;
        if (r.IsNone) return (EntityKind.Unknown, 0, 0u);
        var table = Entities.TableFor(r.Kind);
        if (table is null) return (r.Kind, r.Slot, 0u);
        _ = Entities.ScopeEpoch.Value;   // FIRST (G-179): a scope switch re-points the table read below
        _ = table.Changed.Value;
        return (r.Kind, r.Slot, (uint)r.Slot < (uint)table.Count ? table.Version[r.Slot] : 0u);
    }

    /// <summary>WHICH identity the now-playing face shows, and the arm that chose it. Every part of the face reads
    /// THIS — the render for the art, the heart and the routes, the bound title channel, the artists line — so the
    /// six things that used to change at once on a skip now change together or not at all. The hold itself is the core
    /// model <see cref="NowPlayingFace"/>; this is its reactive read.
    ///
    /// <para>Reactive: it reads <c>Playback.Current</c>, the row's own table (through <see cref="NowPlayingFace.TitleOf"/>)
    /// and <see cref="NowPlayingFace.Wake"/>, so a caller inside a bound channel re-runs when the face flips arm.</para></summary>
    static (FaceArm Arm, EntityRef Ref) BarFaceNow()
    {
        var current = Playback.Current.Value;
        var face = NowPlayingFace.Shared;
        _ = face.Wake.Value;                        // the hold deadline (nothing else wakes a reader for it)
        return face.Resolve(BarStateNow(), current, NowPlayingFace.TitleKnown, Playback.FrameNowMs());
    }

    static PlayerState BarStateNow()
        => PlayerBarRules.StateOf(!Playback.CurrentId.Value.IsEmpty, Playback.Error.Value, Playback.PhaseSignal.Value,
            Playback.Recovery.Value);

    /// <summary>The title line's text, read LIVE inside a bound channel (the marquee host freezes its ctor args).</summary>
    static string BarTitleText()
    {
        var (arm, r) = BarFaceNow();
        string title = NowPlayingFace.TitleOf(r);
        // A HELD face states the outgoing title verbatim — never "Loading…", which is the placeholder's word and
        // was the loudest of the six things a 150 ms skip used to change.
        if (arm == FaceArm.Hold) return title;
        return PlayerBarRules.TextOf(BarStateNow(), title.Length > 0) switch
        {
            NowPlayingText.Title => title,
            NowPlayingText.NothingPlaying => Loc.Get(Strings.Player.NothingPlaying),
            NowPlayingText.Reconnecting => Loc.Get(Strings.Player.Reconnecting),
            // G-212: the reason on the model, not one fixed sentence for every fault.
            NowPlayingText.CannotPlay => Loc.Get(PlayerBarRules.FaultTitleKey(Playback.Error.Value)),
            _ => Loc.Get(Strings.Player.Loading),
        };
    }

    static string? BarArtUrl(EntityRef r)
    {
        if (r.IsNone) return null;
        return r.Kind switch
        {
            EntityKind.Track when new Track(r.Slot) is { IsValid: true } t && t.Knows(TrackFields.Image) => Controls.ArtUrl(t.ImageId),
            EntityKind.Episode when new Episode(r.Slot) is { IsValid: true } e && e.Knows(EpisodeFields.Image) => Controls.ArtUrl(e.ImageId),
            _ => null,
        };
    }

    /// <summary>Title → the album (a track) or the show (an episode). Resolved at INVOKE time, against the row the
    /// face is SHOWING: a click on a held title goes where the held title says, not where the row still loading
    /// behind it would.</summary>
    static Route BarTitleRoute()
    {
        var r = BarFaceNow().Ref;
        if (r.IsNone) return Route.None;
        if (r.Kind == EntityKind.Episode)
        {
            var e = new Episode(r.Slot);
            return e.IsValid && e.Show.IsValid ? For(e.Show.Uri, e.Show.Title) : Route.None;
        }
        var t = new Track(r.Slot);
        return t.IsValid ? LinkFor(t, LinkSlot.Title) : Route.None;
    }

    /// <summary>Art → the playback CONTEXT (a playlist, Liked Songs, an album) — not derivable from the track, so the
    /// module slot answers first and the context uri is the fallback.</summary>
    static Route BarArtRoute()
    {
        var (arm, r) = BarFaceNow();
        var t = r.Kind == EntityKind.Track ? new Track(r.Slot) : default;
        if (t.IsValid && LinkFor(t, LinkSlot.Art) is { IsNone: false } module) return module;
        // Held: the context the held row was RESOLVED under, not the one being loaded into.
        var context = arm == FaceArm.Hold ? NowPlayingFace.Shared.HeldContext : Playback.ContextUri.Peek();
        return context.IsEmpty ? Route.None : For(new EntityUri(context));
    }

    static object? BarDragPayload()
    {
        var t = NowPlayingFace.TrackPeek();
        if (!t.IsValid) return null;
        string uri = t.Uri.Text;
        return new DragPayload(DragKind.Track, uri, uri, t.Title, new EntityRef(EntityKind.Track, t.Slot),
            Tracks: [t], ArtUrl: Controls.ArtUrl(t.ImageId));
    }

    // ══ 3. THE ARTISTS LINE ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Per-name links (a track's credits) or ONE link (an episode's show). Compact (marquee off): the first
    /// name, ellipsised, plus the "+N" chip when there is more than one.</summary>
    sealed class BarArtistsLine(bool compact) : Component
    {
        readonly bool _compact = compact;

        public override Element Render()
        {
            _ = UseComputed<long>(ArtistLineStamp).Value;
            // The FACE's row: a held face keeps its credits, so the line never empties and the text block never
            // re-centres from two lines to one and back on a skip.
            var r = BarFaceNow().Ref;
            if (r.IsNone) return new BoxEl { Direction = 0 };

            if (r.Kind == EntityKind.Episode)
            {
                var e = new Episode(r.Slot);
                if (!e.IsValid || !e.Show.IsValid) return new BoxEl { Direction = 0 };
                return new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f,
                    Children = [MetaLink(e.Show.Title, For(e.Show.Uri, e.Show.Title), _compact, "show:" + e.Show.Slot)],
                };
            }

            var t = new Track(r.Slot);
            // A PARTIAL credit list must not render: the line waits for the whole group, and a playable with no
            // credits renders an EMPTY row that still spends the column's 2-DIP gap (W20).
            if (!t.IsValid || !t.Knows(TrackFields.Artists)) return new BoxEl { Direction = 0 };
            var slots = t.ArtistSlots.ToArray();
            if (slots.Length == 0) return new BoxEl { Direction = 0 };

            if (_compact && slots.Length > 1)
            {
                var first = new Artist(slots[0]);
                return new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, Gap = Spacing.XS,
                    Children =
                    [
                        MetaLink(first.Name, first.Uri.IsValid ? For(first.Uri, first.Name) : Route.None, true, "artist:" + slots[0]),
                        Embed.Comp(() => new BarArtistOverflowChip(slots)) with { Key = "npmore:" + slots[0] + ":" + slots.Length },
                    ],
                };
            }

            var kids = new List<Element>(slots.Length * 2);
            for (int i = 0; i < slots.Length; i++)
            {
                var a = new Artist(slots[i]);
                string name = a.Name;
                if (name.Length == 0) continue;
                if (kids.Count > 0) kids.Add(new TextEl(", ") { Size = 12f, Color = Tok.TextSecondary });
                // A name with no route is INERT, not styled-and-dead.
                kids.Add(MetaLink(name, a.Uri.IsValid ? For(a.Uri, name) : Route.None, false, "artist:" + slots[i]));
            }
            return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, Children = kids.ToArray() };
        }

        static Element MetaLink(string text, Route route, bool trim, string key)
            => Embed.Comp(() => new BarMetaLink(text, route, trim)) with { Key = key + ":" + text };

        /// <summary>The track row's version folded with its credited artists' versions — re-render only when THESE
        /// rows change.</summary>
        static long ArtistLineStamp()
        {
            var r = BarFaceNow().Ref;
            if (r.IsNone) return 0L;
            if (r.Kind == EntityKind.Episode)
            {
                _ = Entities.ScopeEpoch.Value;   // FIRST (G-179): a scope switch re-points the table read below
                _ = Entities.Current.Episodes.Changed.Value;
                _ = Entities.Current.Shows.Changed.Value;
                var e = new Episode(r.Slot);
                return e.IsValid ? ((long)r.Slot << 32) ^ e.Version ^ (e.Show.IsValid ? (long)e.Show.Version << 16 : 0L) : 0L;
            }
            _ = Entities.ScopeEpoch.Value;   // FIRST (G-179): a scope switch re-points the table read below
            _ = Entities.Current.Tracks.Changed.Value;
            _ = Entities.Current.Artists.Changed.Value;
            var t = new Track(r.Slot);
            if (!t.IsValid) return 0L;
            long h = ((long)r.Slot << 32) ^ t.Version;
            var slots = t.ArtistSlots;
            for (int i = 0; i < slots.Length; i++) h = h * 31 + new Artist(slots[i]).Version + slots[i];
            return h;
        }
    }

    /// <summary>A clickable now-playing meta link. It drives its OWN foreground: <c>TextEl.HoverColor</c> follows the
    /// ancestor hover path, which would light every link while the pointer is anywhere on the meta column.</summary>
    sealed class BarMetaLink(string text, Route route, bool trim) : Component
    {
        readonly string _text = text;
        readonly Route _route = route;
        readonly bool _trim = trim;

        public override Element Render()
        {
            var hover = UseSignal(false);
            bool enabled = !_route.IsNone;
            var route = _route;
            return new BoxEl
            {
                Cursor = enabled ? CursorId.Hand : null,
                OnClick = enabled ? () => PlayerVerbs.Default.Navigate(route) : null,
                OnHoverMove = enabled ? _ => { if (!hover.Peek()) hover.Value = true; } : null,
                OnPointerExit = enabled ? () => { if (hover.Peek()) hover.Value = false; } : null,
                ClipToBounds = true, Shrink = 1f, MinWidth = _trim ? 0f : float.NaN,
                Role = enabled ? AutomationRole.Hyperlink : AutomationRole.Text,
                Children =
                [
                    new TextEl(_text)
                    {
                        Size = 12f, Color = hover.Value ? Tok.TextPrimary : Tok.TextSecondary,
                        MaxLines = _trim ? 1 : 0, Trim = _trim ? TextTrim.CharacterEllipsis : TextTrim.None,
                        MinWidth = _trim ? 0f : float.NaN,
                    },
                ],
            };
        }
    }

    /// <summary>The "+N" credits chip. Opens EVERY credited artist UPWARD from the dock (0.2.9 asked for
    /// BottomEdgeAlignedLeft from a bottom dock and relied on the overlay re-fit; ch 20 W20 asks 0.3 to anchor it up).
    /// Keyed by its artist set at the call site, so the frozen slot array is the right one.</summary>
    sealed class BarArtistOverflowChip(int[] slots) : Component
    {
        readonly int[] _slots = slots;

        public override Element Render()
        {
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            var overlay = UseContext(Overlay.Service);
            var slots = _slots;

            void Toggle()
            {
                if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
                var items = new MenuFlyoutItem[slots.Length];
                for (int i = 0; i < slots.Length; i++)
                {
                    var a = new Artist(slots[i]);
                    var route = a.Uri.IsValid ? For(a.Uri, a.Name) : Route.None;
                    items[i] = new MenuFlyoutItem(a.Name, default, !route.IsNone, () => PlayerVerbs.Default.Navigate(route));
                }
                handle.Value = overlay.Open(
                    () => anchor.Value,
                    () => MenuFlyout.Create(items, () => handle.Value?.Close()),
                    FlyoutPlacement.TopEdgeAlignedLeft,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
                handle.Value.ClosedAction = () => handle.Value = null;
            }

            return new BoxEl
            {
                Shrink = 0f, Padding = new Edges4(Spacing.XXS, 0f, Spacing.XXS, 0f),
                OnRealized = h => anchor.Value = h,
                Role = AutomationRole.Button, Cursor = CursorId.Hand, OnClick = Toggle, BlocksDragArm = true,
                Children = [FluentGpu.Dsl.Ui.Caption("+" + (slots.Length - 1)) with { Weight = 600, Color = Tok.TextTertiary }],
            };
        }
    }

    // ══ 4. THE "⋯" ROWS ═══════════════════════════════════════════════════════════════════════════════════════════════

    // The overflow's FACE (the button, the menu's placement) is the structure's; WHICH rows it carries is core
    // (`PlayerBarRules.Overflow`) and what a row says and does is built here, from live peeks at open time.

    /// <summary>One overflow row, from LIVE peeks at open time.</summary>
    static MenuFlyoutItem OverflowItem(OverflowCommand command, Action<string>? begin)
    {
        var facts = PlayerVerbs.TransportFacts(track: false);
        bool railOpen = Ui.RailOpen.Peek();
        var mode = Ui.Mode.Peek();
        switch (command)
        {
            case OverflowCommand.Previous:
                return new MenuFlyoutItem(Loc.Get(Strings.Player.Previous), Icons.Previous, facts.PrevEnabled, static () => PlayerVerbs.Default.Previous());
            case OverflowCommand.Next:
                return new MenuFlyoutItem(Loc.Get(Strings.Player.Next), Icons.Next, facts.NextEnabled, static () => PlayerVerbs.Default.Next());
            case OverflowCommand.Shuffle:
                return MenuFlyoutItem.Toggle(Loc.Get(Strings.Player.Shuffle), Playback.Shuffle.Peek(), static () => PlayerVerbs.Default.ToggleShuffle(),
                    Icons.Shuffle, facts.CanTransport);
            case OverflowCommand.Repeat:
            {
                var repeat = Playback.Repeat.Peek();
                return MenuFlyoutItem.Toggle(Loc.Get(Strings.Player.Repeat), repeat != RepeatMode.Off, static () => PlayerVerbs.Default.CycleRepeat(),
                    PlayerBarRules.RepeatGlyph(repeat), facts.CanTransport);
            }
            case OverflowCommand.Lyrics:
                return MenuFlyoutItem.Toggle(Loc.Get(Playback.CurrentId.Peek().Kind == EntityKind.Episode ? "podcast.reader.transcript" : Strings.Player.Lyrics), railOpen && mode == RailMode.Lyrics,
                    static () => PlayerVerbs.Default.OpenPanel(RailMode.Lyrics), new IconRef { Glyph = WaveeIcons.Lyrics, Font = WaveeIcons.Font });
            case OverflowCommand.Queue:
                return new MenuFlyoutItem(Loc.Get(Strings.Player.Queue), Icons.Queue, true, static () => PlayerVerbs.Default.OpenPanel(RailMode.Queue));
            case OverflowCommand.NowPlaying:
                return new MenuFlyoutItem(Loc.Get(Strings.Player.NowPlaying), Icons.ChevronUp, true, static () => PlayerVerbs.Default.OpenPanel(RailMode.NowPlaying));
            case OverflowCommand.FullScreen:
                // The stage's door at the tiers without the Expand slot (PlayerBarRules.Overflow).
                return new MenuFlyoutItem(Loc.Get(Strings.Player.Fullscreen), Icons.FullScreen, true, () => PlayerVerbs.Default.OpenStage(begin, "bar-overflow"));
            case OverflowCommand.Video:
            {
                bool live = Video.PlacementCore.IsActive(Video.State.Surface.Peek());
                // A cascading sub-menu over the SAME placement ladder the inline chevron opens.
                return MenuFlyoutItem.SubMenu(Loc.Get(live ? Strings.Player.SwitchToAudio : Strings.Player.SwitchToVideo),
                    Video.PlacementMenu(includeFullscreen: true), Icons.Movie);
            }
            default:
            {
                bool muted = Playback.Muted.Peek();
                return new MenuFlyoutItem(Loc.Get(muted ? Strings.Player.Unmute : Strings.Player.Mute),
                    muted ? Icons.Volume : Icons.Mute, true, static () => Playback.ToggleMute());
            }
        }
    }

    // ══ 5. DEVICES ═══════════════════════════════════════════════════════════════════════════════════════════════════

    // The Devices button and the "Playing on" line are the structure's (they call `ToggleDevicePicker`); the picker's MENU is the
    // structure's too since T6b (`Player.DevicePicker`, `ClassicDevicePicker`), over the entries `RailWiring.ReadDevicePicker` decides.
    // The overlay (where it opens, how it dismisses, the picker-opened side effect) stays here with the bar's toggle.

    /// <summary>One picker, two anchors (the Devices button, the "Playing on" line): upward, right-aligned to the
    /// anchor, light-dismiss, focus trap. The menu caps its own scroll height at <see cref="DeviceRoster.PickerMaxHeight"/>
    /// (window height − 96 DIP, G-210) rather than trusting the engine's own fixed 468-DIP <c>MenuFlyout</c> cap, which
    /// closes 0.2.9's uncapped-roster trap (ch 20 §9, parity 81) even on a short/undocked window. The body re-renders WHILE OPEN
    /// (a transfer landing swaps the checks in place, parity 79).</summary>
    static void BarToggleDevicePicker(IOverlayService overlay, Ref<NodeHandle> anchor, Ref<OverlayHandle?> handle)
    {
        if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
        PlayerVerbs.Default.OpenDevicePicker();                                // the cluster hears it, as the official client's picker does
        var props = RailWiring.DevicePicker(() => handle.Value?.Close());      // built once per open: the content builder may run again
        handle.Value = overlay.Open(
            () => anchor.Value,
            () => ViewSlots.View(ViewKeys.DevicePicker, props),
            FlyoutPlacement.TopEdgeAlignedRight,
            // The roster already caps itself to the window (PickerMaxHeight = height - 96), so it never needs to
            // escape the root bounds -- and NOT escaping is what keeps it on the engine's in-window acrylic path
            // instead of leasing a popup window, whose flat plate the owner rejected.
            new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = true });
        handle.Value.ClosedAction = () => handle.Value = null;
    }

    /// <summary>THE device picker's menu items for the stage's "Playing on" flyout, over the SAME <see cref="DevicePicker.Items"/> the
    /// bar's picker composes its entries from (<c>RailWiring.ReadDevicePicker</c>), so the two surfaces cannot disagree about a row or
    /// what it does. The stage mounts <c>Player.DevicePicker</c> instead when T7a moves its flyout, and this goes. Local
    /// (this-computer) rows are plain commands, not radios: choosing one persists the output for the next session and
    /// pulls playback here, but the engine does not yet re-route a live session to it, so no row claims to be "the
    /// endpoint in use".</summary>
    internal static List<MenuFlyoutItem> DevicePickerMenuItems(
        Playback.Owner owner, int activeSlot, ReadOnlySpan<Playback.Devices.Row> connect,
        ReadOnlySpan<Playback.Audio.LocalAudioDevice> local, string? selectedLocalId, bool localSupported,
        Playback.Audio.Opened observed, Spotify.Audio.Quality asked)
    {
        var picker = DevicePicker.Items(owner, activeSlot, connect, local, selectedLocalId, localSupported, observed, asked);
        var items = new List<MenuFlyoutItem>(picker.Count);
        foreach (var item in picker)
        {
            var row = item.Row;
            var intent = item.Intent;
            items.Add(row.Kind switch
            {
                DevicePickerRowKind.Separator => MenuFlyoutItem.Separator,
                // A disabled command row stands in as the section header (MenuFlyout has no header kind; neither does WinUI).
                // Quality rides the same disabled-row shape: it is informational, never a radio.
                DevicePickerRowKind.Header or DevicePickerRowKind.Empty or DevicePickerRowKind.Quality
                    => new MenuFlyoutItem(row.Label, default, false),
                DevicePickerRowKind.LocalDefault => new MenuFlyoutItem(row.Label, Icons.ThisPc, row.Enabled,
                        row.Enabled ? () => DeviceVerbs.Run(intent) : null)
                    with { AcceleratorText = row.Accelerator },
                DevicePickerRowKind.LocalDevice => new MenuFlyoutItem(row.Label, DeviceRoster.LocalGlyph(row.LocalKind), row.Enabled,
                        row.Enabled ? () => DeviceVerbs.Run(intent) : null)
                    with { AcceleratorText = row.Accelerator },
                DevicePickerRowKind.ConnectDevice => MenuFlyoutItem.RadioItem(row.Label, row.IsChecked,
                    () => DeviceVerbs.Run(intent), DeviceRoster.ConnectGlyph(row.ConnectKind)),
                _ => new MenuFlyoutItem(row.Label, default, false),
            });
        }
        return items;
    }

    // ══ 7. THE SEEK BAR ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The bespoke media rail (not a Slider): the SCRUB GATE, the smooth extrapolated playhead and
    /// click-anywhere scrub committing ONE seek per gesture. The scrub itself (the gesture, the pixel dwell, the
    /// seek-generation hold, the keyboard ladder) is the core <see cref="ScrubController"/>; this view maps a pointer to a
    /// fraction and draws its signals. The fill and thumb are compositor binds on <see cref="ScrubController.Display"/>; the
    /// component re-renders only on enablement / mode / advancing edges.</summary>
    sealed class BarSeekRail : Component
    {
        const float HitHeight = 32f;         // WinUI SliderHorizontalHeight
        const float RingDiameter = 22f;      // Slider.DefaultStyle.ThumbRingDiameter

        readonly PlayerChromeFeed? _chromeFeed;
        readonly Action<bool>? _onScrubbing;   // the fullscreen stage's scrub seam (its idle machine's SetScrubbing); null on the bar
        readonly IReadSignal<bool>? _reveal;   // the on-media strip's chrome-visible signal (null = always live); see SeekBar
        readonly ScrubController _scrub;       // the gesture, the dwell, the seek-generation hold: core
        readonly FloatSignal _chapterHover = new(0f);
        readonly Signal<bool> _chapterHovering = new(false);

        // The scrub READOUT (U-7, V-PA40): the time under the pointer, in a bubble over the thumb for the length of a pointer gesture. The thumb
        // ring is not hit-testable, so it cannot carry a tooltip of its own, and `ToolTip.Wrap` can neither follow a live value (it closes the
        // bubble on every text change) nor survive a press (it dismisses on one) — so the rail opens the Slider's own kind of bubble, anchored
        // to the thumb's NODE (a position source only), on the press and closes it on the release, the cancel and the unmount.
        NodeHandle _thumbNode;
        OverlayHandle? _readout;
        IOverlayService? _overlay;
        readonly Action<NodeHandle> _onThumbRealized;
        readonly Func<NodeHandle> _readoutAnchor;
        readonly Func<Element> _readoutContent;
        readonly Prop<string> _readoutText;

        // Wired ONCE: a bind thunk and the handlers are fields, so a re-render allocates none of them.
        readonly Prop<Affine2D> _fillBind;
        readonly Prop<Affine2D> _thumbBind;
        readonly Action _recompute, _onReport, _onCommit, _onCancel;
        readonly Func<SeekRailMode> _modeNow;
        readonly Func<float> _dwellNow;
        readonly Action<Point2> _onDown, _onDrag, _onHover;
        readonly Action _onExit;
        readonly Action<RectF> _onBounds;

        public BarSeekRail(PlayerChromeFeed? chromeFeed = null, Action<bool>? onScrubbing = null, IReadSignal<bool>? reveal = null)
        {
            _chromeFeed = chromeFeed;
            _onScrubbing = onScrubbing;
            _reveal = reveal;
            _scrub = ScrubController.ForPlayback(OnGesture);
            _onThumbRealized = node => _thumbNode = node;
            _readoutAnchor = () => _thumbNode;
            _readoutText = Prop.Of<string>(ReadoutLabel);
            _readoutContent = ReadoutBubble;
            _fillBind = Prop.Of(() => Affine2D.Scale(MathF.Max(Math.Clamp(_scrub.Display.Value, 0f, 1f), 1e-4f), 1f));
            _thumbBind = Prop.Of(() => Affine2D.Translation(SeekRail.ThumbX(_scrub.RailPx.Value, _scrub.Display.Value, RingDiameter), 0f));
            _recompute = _scrub.Recompute;
            _modeNow = _scrub.Mode;
            _dwellNow = _scrub.DwellMs;
            _onReport = OnReport;
            _onCommit = OnCommit;
            _onCancel = OnCancel;
            _onDown = OnDown;
            _onDrag = OnDragMove;
            _onHover = local => { _chapterHovering.Value = true; _chapterHover.SetIfChanged(_scrub.FractionAt(local.X)); };
            _onExit = () => _chapterHovering.Value = false;
            _onBounds = b => _scrub.SetRailWidth(b.W);
        }

        public override Element Render()
        {
            var gate = _scrub.Gate();
            bool enabled = gate.Enabled;
            bool advances = gate.Advances;
            // The three-way SHAPE through a memo: a live window republishing several times a second re-renders nothing.
            var mode = UseComputed(_modeNow).Value;
            float dwell = UseComputed(_dwellNow).Value;

            // A report (position, duration, window, edge, phase) re-derives the RESTING fraction — an effect, not Render.
            UseSignalEffect(_onReport);
            // The pixel-due stepper: armed only while the playhead advances on its own (paused ⇒ disarmed ⇒ no frames), and
            // only while someone can see it (SeekRail.Ticks: the on-media strip revealed, the window not covered).
            // UseInterval also pauses under a parked / minimised window.
            bool concealed = _reveal is { } reveal && !reveal.Value;   // subscribe — the reveal re-renders and re-arms
            bool windowHidden = UseContext(InputHooks.Current).WindowOccluded?.Value ?? false;
            UseInterval(_recompute, dwell, enabled: SeekRail.Ticks(advances, mode, concealed, windowHidden));
            _overlay = UseContext(Overlay.Service);
            // V-PA24/X5: the rail can be unmounted mid-gesture (the stage closing, a tier remount, a theme swap): a live scrub must not leave
            // the pump holding the main voice behind a grain voice nobody will ever release. `ScrubCancel` is idempotent.
            UseEffect(() => () => { if (_scrub.AbortIfScrubbing()) CloseReadout(); }, DepKey.Empty);

            // NOTHING TO REWIND: the rail stops pretending to be one. After every hook, so the hook order never varies.
            if (mode == SeekRailMode.Line) return Embed.Comp(static () => new BarLiveLine());

            var chapterId = Playback.CurrentId.Value;
            uint chapterEpoch = Entities.ScopeEpoch.Value;
            bool chapterRail = PlayerBarFacts.Seek.ChapterRail(chapterId.Kind, mode);
            var chapterIdentity = new ChapterResources.Identity(chapterEpoch, Entities.Current.Key.Account, chapterId);
            Element chapters = chapterRail
                ? Embed.Comp(() => new BarChapterTimeline(chapterIdentity, _scrub.RailPx, _chapterHover, _scrub.Fraction, _chapterHovering, _scrub.Scrubbing))
                    with { Key = "player-chapters:" + chapterEpoch + ":" + chapterId.Text }
                : new BoxEl { HitTestVisible = false };
            var s = Slider.DefaultStyle;
            var fill = new BoxEl
            {
                Grow = 1f, Height = s.TrackHeight, AlignSelf = FlexAlign.Center,
                Fill = enabled ? s.ValueFill : s.ValueFillDisabled,
                HoverFill = enabled ? s.ValueFillPointerOver : s.ValueFillDisabled,
                PressedFill = enabled ? s.ValueFillPressed : s.ValueFillDisabled,
                // Square: a scaled rounded rect shows cap slivers at the value edge.
                Corners = CornerRadius4.All(0f), HitTestVisible = false,
                TransformOriginX = 0f, Transform = _fillBind,
            };
            // The DVR rail's right end IS the live edge, so it is marked.
            Element[] railKids = mode == SeekRailMode.Dvr
                ? new Element[]
                {
                    fill,
                    new BoxEl
                    {
                        Grow = 1f, Height = s.TrackHeight, Direction = 0, Justify = FlexJustify.End, HitTestVisible = false,
                        Children = [new BoxEl { Width = 2f, Height = s.TrackHeight, Shrink = 0f, Fill = Tok.AccentDefault, HitTestVisible = false }],
                    },
                }
                : new Element[] { fill };

            float rest = enabled ? s.InnerRestScale : s.InnerDisabledScale;
            var stack = new BoxEl
            {
                // Shrink + MinWidth 0 on every layer: the engine's flex items do not shrink by default, and a ZStack can
                // measure its available width as its own size, so without them the rail keeps that width and paints under the
                // right cluster to the window edge.
                ZStack = true, Grow = 1f, Shrink = 1f, MinWidth = 0f, Height = HitHeight, AlignItems = FlexAlign.Center, HitTestVisible = false,
                Children =
                [
                    new BoxEl
                    {
                        Height = s.TrackHeight, Grow = 1f, Shrink = 1f, MinWidth = 0f, AlignSelf = FlexAlign.Center,
                        Fill = enabled ? s.RailFill : s.RailFillDisabled,
                        Corners = CornerRadius4.All(s.TrackCornerRadius), ClipToBounds = true, ZStack = true,
                        HitTestVisible = false, Children = railKids,
                    },
                    chapters,
                    new BoxEl
                    {
                        Width = RingDiameter, Height = RingDiameter,
                        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        Corners = CornerRadius4.All(s.ThumbCornerRadius),
                        Fill = s.ThumbRing, HoverFill = s.ThumbRing, PressedFill = s.ThumbRing,
                        BorderBrush = s.ThumbBorder, BorderWidth = s.ThumbBorderWidth,
                        Opacity = 0f, HoverOpacity = enabled ? 1f : 0f, PressedOpacity = enabled ? 1f : 0f,
                        HitTestVisible = false, Transform = _thumbBind, OnRealized = _onThumbRealized,
                        Children =
                        [
                            new BoxEl
                            {
                                Width = s.InnerThumbDiameter, Height = s.InnerThumbDiameter,
                                Corners = CornerRadius4.All(s.InnerThumbDiameter * 0.5f),
                                Fill = enabled ? s.ThumbFill : s.ThumbFillDisabled,
                                HoverFill = enabled ? s.ThumbFillPointerOver : s.ThumbFillDisabled,
                                PressedFill = enabled ? s.ThumbFillPressed : s.ThumbFillDisabled,
                                ScaleX = rest, ScaleY = rest,
                                HoverScale = enabled ? s.InnerHoverScale / s.InnerRestScale : 1f,
                                PressScale = enabled ? s.InnerPressScale / s.InnerRestScale : 1f,
                                HoverDurationMs = Design.Motion.Standard, PressDurationMs = Design.Motion.Standard,
                                HitTestVisible = false,
                            },
                        ],
                    },
                    // The keyboard ladder's commit clock (zero-size; the rail never re-renders for it): mounted wherever a rail is, so the bar,
                    // the fullscreen stage and the on-media transport all commit their Left/Right steps.
                    Embed.Comp(static () => new BarKeyLadderTick()) with { Key = "key-ladder-tick" },
                ],
            };

            // Click-anywhere + drag scrub; OnClick is the drag-END commit edge. Chapter decoration never owns input.
            return new BoxEl
            {
                Grow = 1f, Shrink = 1f, MinWidth = 0f, Height = HitHeight, Direction = 0, AlignItems = FlexAlign.Center,
                Role = AutomationRole.Slider, IsEnabled = enabled,
                Cursor = enabled ? CursorId.Hand : null,
                OnBoundsChanged = _onBounds,
                OnPointerDown = enabled ? _onDown : null,
                OnDrag = enabled ? _onDrag : null,
                OnClick = enabled ? _onCommit : null,
                OnDragCanceled = enabled ? _onCancel : null,
                OnHoverMove = chapterRail && enabled ? _onHover : null,
                OnPointerExit = chapterRail ? _onExit : null,
                Children = [stack],
            };
        }

        void OnReport()
        {
            // Concealed (the on-media strip faded out): nothing paints, so no report writes the transform. The read is the
            // effect's subscription to the reveal, so showing the strip re-runs this and re-syncs the fraction first.
            if (_reveal is { } reveal && !reveal.Value) return;
            _ = Playback.PositionMs.Value;
            _ = Playback.DurationMs.Value;
            _ = Playback.Live.Value;
            _ = Playback.IsBehindLive.Value;
            _ = Playback.PhaseSignal.Value;
            _ = Playback.LastSeekLandedGen.Value;    // a landing re-runs this even when no position moved with it
            _ = ScrubController.KeyPreviewMs.Value;  // the keyboard ladder's target moves the thumb too
            // The drop-point hold ends on a LANDING past the generation the commit saw (V-PA12), and re-derives the resting
            // fraction (`ScrubController.OnModelReport`): the same hold covers the bar, the flagship stage and the video
            // overlay (all host this rail). No landing (a foreign owner, a parked deck) -> SeekRail.CommitHoldMs ends it.
            _scrub.OnModelReport();
        }

        /// <summary>The gesture's edges, fanned out to the chrome-activity seams: <c>true</c> on press, <c>false</c> on commit or
        /// cancel (a scrub is chrome activity, so the rail that drives it must be able to say so).</summary>
        void OnGesture(bool active)
        {
            _chromeFeed?.SetScrubbing(active);
            _onScrubbing?.Invoke(active);
        }

        void OnDown(Point2 local)
        {
            if (!_scrub.Begin(_scrub.FractionAt(local.X))) return;   // jump to the press point; the reducer decides grains or a visual-only scrub
            OpenReadout();
        }

        void OnDragMove(Point2 local) => _scrub.Update(_scrub.FractionAt(local.X));

        /// <summary>The drag-END edge. The controller always releases the gate (a commit that cannot happen cancels), so the
        /// readout closes either way.</summary>
        void OnCommit()
        {
            _scrub.Commit();
            CloseReadout();
        }

        void OnCancel()
        {
            _scrub.Cancel();
            CloseReadout();
        }

        // ── the scrub readout (U-7) ──

        /// <summary>Open the readout bubble where the controller wants one (<see cref="ScrubController.ReadoutWanted"/>: a plain
        /// track, not a DVR window or an episode's chapter rail).</summary>
        void OpenReadout()
        {
            if (_overlay is null || _readout is { IsOpen: true } || !_scrub.ReadoutWanted) return;
            _readout = _overlay.Open(_readoutAnchor, _readoutContent, FlyoutPlacement.Top,
                new PopupOptions(FocusTrap: false, DismissBehavior: DismissBehavior.None, Chrome: PopupChrome.Raw));
        }

        void CloseReadout()
        {
            var open = _readout;
            _readout = null;
            if (open is { IsOpen: true }) open.Close();
        }

        /// <summary>The bubble's text: the rail's own scrub target, live from the pointer fraction — signal-bound, so the bubble itself never
        /// re-renders per move (the Slider's thumb tip does the same).</summary>
        string ReadoutLabel()
        {
            float fraction = _scrub.Fraction.Value;
            long ms = _scrub.TargetMsAt(fraction);
            return s_clockText.Get(ms / 1000L, static sec => Playback.TimeFormat.Clock(sec * 1000L));
        }

        Element ReadoutBubble() => new BoxEl
        {
            Fill = ColorF.Transparent, Acrylic = Tok.AcrylicFlyout,
            BorderColor = Tok.StrokeFlyoutDefault, BorderWidth = 1f,
            Corners = Radii.ControlAll, Shadow = Elevation.Flyout,
            Padding = new Edges4(8f, 3f, 8f, 5f),
            Children = [new TextEl(_readoutText) { Size = 15f, Color = Tok.TextPrimary }],
        };
    }

    /// <summary>The rail for a broadcast with NOTHING TO REWIND: a 2-DIP accent line that breathes 0.55 ↔ 1 over 3 s —
    /// only while playing, the window is active and motion is not reduced; otherwise the loop is REPLACED IN PLACE by a
    /// finite flat track so the frame loop quiesces. Not a slider: no hit test, no role.</summary>
    sealed class BarLiveLine : Component
    {
        static readonly Keyframe[] Breathe = [new(0f, 0.55f), new(0.5f, 1f), new(1f, 0.55f)];
        static readonly Keyframe[] Flat = [new(0f, 1f), new(1f, 1f)];

        public override Element Render()
        {
            bool playing = Playback.IsPlaying.Value;
            bool windowActive = UseIsActive().Value;
            bool breathe = playing && windowActive && !Design.Reduced;   // reduced motion is a VALUE, never a hook branch
            UseKeyframes(AnimChannel.Opacity, breathe ? Breathe : Flat, breathe ? 3000f : 1f, breathe, DepKey.From(breathe));

            // Shrink on both, like BarSeekRail (G-254): a grown line keeps its last arranged width across a narrowing
            // resize unless it may shrink, and pushes the right cluster off the bar.
            return new BoxEl
            {
                Grow = 1f, Shrink = 1f, Height = 32f, Direction = 0, AlignItems = FlexAlign.Center,
                HitTestVisible = false, Role = AutomationRole.None,
                Children =
                [
                    new BoxEl
                    {
                        Grow = 1f, Shrink = 1f, MinWidth = 0f, Height = 2f, Corners = CornerRadius4.All(1f),
                        Fill = Tok.AccentDefault, HitTestVisible = false,
                    },
                ],
            };
        }
    }

    // ══ 8. THE TIME LABELS ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Formatted once per whole second and cached: the labels tick at 1 Hz forever.</summary>
    static readonly FormatCache<long> s_clockText = new();
    static readonly FormatCache<long> s_minusClockText = new();
    static readonly FormatCache<long> s_goLiveText = new();

    /// <summary>One time label. The digits are a BOUND text channel — reading the position in Render would rebuild
    /// the whole label at tick rate (0.2.9's steady-churn census finding). The structural reads (live or not) go
    /// through a memo; the show-remaining preference is read INSIDE the bind.</summary>
    sealed class BarTimeText : Component
    {
        readonly bool _remaining;
        readonly ColorF? _ink;
        readonly IReadSignal<bool>? _reveal;   // the on-media strip's chrome-visible signal (null = always live)
        string _shown = "";                    // the last text the bind produced: what a concealed label keeps holding
        readonly Prop<string> _label;

        public BarTimeText(bool remaining, ColorF? ink, IReadSignal<bool>? reveal = null)
        {
            _remaining = remaining;
            _ink = ink;
            _reveal = reveal;
            _label = Prop.Of<string>(Label);
        }

        string Label()
        {
            // Concealed: hold the last text. Returning before the position read drops this bind's subscription to it, so a
            // hidden label wakes nothing at 1 Hz; the reveal (read first, so it stays subscribed) re-runs the bind.
            if (_reveal is { } reveal && !reveal.Value) return _shown;
            long position = Playback.PositionMs.Value;
            long duration = Playback.DurationMs.Value;
            bool isLive = Playback.Live.Value.IsLive;
            long tunedIn = Playback.TunedInAtMs.Value;
            // U-7: while the pointer drags (the reducer's scrub model) or the keyboard ladder is live, the label follows the TARGET — the
            // elapsed slot counts up to it and the remaining slot counts down from it. A live broadcast keeps its own clock (tune-in).
            long scrub = Playback.ScrubPositionMs.Value;
            long key = ScrubController.KeyPreviewMs.Value;
            if (!isLive) position = scrub >= 0L ? scrub : key >= 0L ? key : position;
            var (ms, minus) = TimeLabel.Of(_remaining, Prefs.PlayerBar.ShowRemaining(), position, duration, isLive, tunedIn,
                isLive ? Playback.UnixNowMs() : 0L);
            long seconds = ms / 1000L;
            return _shown = minus
                ? s_minusClockText.Get(seconds, static sec => "-" + Playback.TimeFormat.Clock(sec * 1000L))
                : s_clockText.Get(seconds, static sec => Playback.TimeFormat.Clock(sec * 1000L));
        }

        public override Element Render()
        {
            bool isLive = UseComputed(static () => Playback.Live.Value.IsLive).Value;
            if (TimeLabel.RightSlotIsLive(_remaining, isLive))
            {
                var ink = _ink;
                return Embed.Comp(() => new BarLiveSlot(ink));
            }

            bool right = _remaining;
            var caption = FluentGpu.Dsl.Ui.Caption("");
            return new BoxEl
            {
                // The slot is a reservation at rest (0:09 → 0:10 moves nothing) and a FLOOR while live (h:mm:ss).
                Width = isLive ? float.NaN : TimeLabel.SlotW, MinWidth = TimeLabel.SlotW, Shrink = 0f,
                Direction = 0, AlignItems = FlexAlign.Center,
                Justify = right ? FlexJustify.Start : FlexJustify.End,   // the digits hug the rail
                HoverFill = right ? (_ink is null ? Tok.FillSubtleSecondary : R.OnMedia.GlassHover) : ColorF.Transparent,
                PressedFill = right ? (_ink is null ? Tok.FillSubtleTertiary : R.OnMedia.GlassPressed) : ColorF.Transparent,
                Corners = Radii.ControlAll,
                OnClick = right ? static () => PlayerVerbs.Default.ToggleRemaining() : null,
                Cursor = right ? CursorId.Hand : null,
                Children =
                [
                    _ink is { } inkColor
                        ? caption with { Text = _label, Color = inkColor, Wrap = TextWrap.NoWrap }
                        : caption with { Text = _label, Wrap = TextWrap.NoWrap },
                ],
            };
        }
    }

    /// <summary>The right slot while live: ONE 104-DIP reservation, right-aligned, holding the LIVE word-mark or —
    /// behind a rewindable window — the GO LIVE action. The swap moves nothing (parity 65); which of the two is the
    /// model's DECIDED edge state, never a threshold here.</summary>
    sealed class BarLiveSlot(ColorF? ink) : Component
    {
        readonly ColorF? _ink = ink;
        readonly Prop<string> _goLive = Prop.Of(static () => s_goLiveText.Get(Playback.Live.Value.BehindMs / 1000L,
            static sec => Loc.Get(Strings.Play.GoLive) + " " + Strings.Play.Behind(Playback.TimeFormat.Clock(sec * 1000L))));

        public override Element Render()
        {
            bool offer = UseComputed(static () => TimeLabel.OffersGoLive(Playback.Live.Value, Playback.IsBehindLive.Value)).Value;
            ColorF fg = _ink ?? R.Ink.AccentDecor;
            Element face = offer
                ? new BoxEl
                {
                    // A TEXT button, not a plate — a filled accent here would outrank the transport itself.
                    MinWidth = 44f, Shrink = 0f, Height = 20f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Padding = new Edges4(Spacing.XS, 0f, Spacing.XS, 0f), Corners = Radii.ControlAll,
                    HoverFill = _ink is null ? Tok.FillSubtleSecondary : R.OnMedia.GlassHover,
                    PressedFill = _ink is null ? Tok.FillSubtleTertiary : R.OnMedia.GlassPressed,
                    HoverScale = R.Motion.ScaleSubtle.Hover, PressScale = R.Motion.ScaleSubtle.Press,
                    Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                    OnClick = static () => PlayerVerbs.Default.GoLive(),
                    Children = [new TextEl(_goLive) { Size = 9f, LineHeight = 12f, Weight = 600, Color = fg, Wrap = TextWrap.NoWrap }],
                }
                : new BoxEl
                {
                    Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, Gap = Spacing.XXS,
                    Children =
                    [
                        // STATIC red: a blinking dot would keep the frame loop awake for its whole life.
                        new BoxEl { Width = 6f, Height = 6f, Shrink = 0f, Corners = CornerRadius4.All(3f), Fill = Tok.SystemFillCritical },
                        new BoxEl
                        {
                            Height = 14f, Padding = new Edges4(Spacing.XXS, 0f, Spacing.XXS, 0f), Shrink = 0f,
                            Corners = CornerRadius4.All(2f), BorderWidth = 1f, BorderColor = fg,
                            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                            Children = [new TextEl(Loc.Get(Strings.Play.Live)) { Size = 9f, LineHeight = 12f, Weight = 600, Color = fg, Wrap = TextWrap.NoWrap }],
                        },
                    ],
                };

            return new BoxEl
            {
                Width = TimeLabel.LiveSlotW, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End,
                Children = [face],
            };
        }
    }
}
