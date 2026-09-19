using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>The Liked Songs hero cover PLUS its "Cover style" affordance: hovering (or focusing) the cover reveals a
/// scrim pill in the bottom-right corner, and the pill opens a flyout of LIVE miniatures — the nine treatments,
/// composed from the user's own newest likes — as one radio group.
///
/// <para>Mounted by the DETAIL surfaces only (the rail's cover, the vertical header's, the vertical hero's). The
/// compact strip, the Home cards, the shelf and the sidebar get the bare <see cref="LikedSongsArtwork.Dynamic"/>: at
/// 48–80 DIP a pill would cover the artwork it is offering to change, and a page that is ABOUT the collection is the
/// honest place to keep its settings.</para>
///
/// <para><b>Ink on imagery is <c>WaveeOnMedia</c>, never a theme token</b> — the cover under the pill can be any
/// luminance (a Rainbow treatment is, by construction, every luminance at once), so the pill is always a dark scrim
/// with white ink. <c>PlaylistInlineEdit.EditableCover</c>'s <c>CoverOverlay</c> is the pattern, including the
/// always-mounted overlay whose reveal is a BOUND opacity transition: the pill is composited in and out, never mounted
/// and unmounted, so hovering the cover costs no reconcile.</para></summary>
sealed class LikedCoverPicker : Component
{
    /// <summary>The cover with its picker. Keyed by geometry for <see cref="LikedSongsArtwork.Dynamic"/>'s reason —
    /// the size is frozen at mount (component-props-contract.md), so a rail-width drag has to remount rather than
    /// leave the picker anchored to an edge that moved.</summary>
    internal static Element Cover(float size, float radius = Radii.Card, string? morphKey = null)
        => Embed.Comp(() => new LikedCoverPicker(size, radius, morphKey)) with
        {
            Key = "liked-cover-pick:" + (int)size + ":" + (int)radius + ":" + morphKey,
        };

    readonly float _size;
    readonly float _radius;
    readonly string? _morphKey;

    // Reveal state as SIGNALS rather than as component state: the pill's opacity is a bound Prop, so a hover or a focus
    // move repaints one composited node instead of re-rendering the cover under it (which would re-request every tile
    // decode in the treatment).
    readonly Signal<bool> _hovered = new(false);
    readonly Signal<bool> _focused = new(false);
    readonly Signal<bool> _open = new(false);

    // Instance state, not hooks: the anchor sink is allocated ONCE per mount rather than once per render.
    NodeHandle _anchor;
    OverlayHandle? _handle;
    readonly Action<NodeHandle> _anchorSink;

    LikedCoverPicker(float size, float radius, string? morphKey)
    {
        _size = size; _radius = radius; _morphKey = morphKey;
        _anchorSink = h => _anchor = h;
    }

    public override Element Render()
    {
        var overlay = UseContext(Overlay.Service);

        void Toggle()
        {
            if (overlay is null) return;
            if (_handle is { IsOpen: true } already) { already.Close(); return; }
            _handle = overlay.Open(
                () => _anchor,
                static () => Embed.Comp(() => new LikedCoverStyleFlyout()),
                FlyoutPlacement.BottomEdgeAlignedLeft,
                // Flyout chrome (acrylic + stroke + elevation + the reveal transition) is the HOST's, not ours — the
                // one thing a picker over artwork must not do is hand-roll a second flyout material. FocusTrap so the
                // radio group's roving arrows stay inside it; ConstrainToRootBounds so a cover near the window's foot
                // flips the flyout up instead of drawing it off-screen.
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss,
                                 Chrome: PopupChrome.Flyout)
                { ConstrainToRootBounds = true });
            _open.Value = true;
            _handle.ClosedAction = () => { _handle = null; _open.Value = false; };
        }

        void Key(KeyEventArgs e)
        {
            // Down / F4 open a flyout from its anchor — the app's one keyboard contract for an anchored surface
            // (ArtistFacePile, every SelectorBar flyout). Space/Enter arrive as OnClick.
            if (e.KeyCode is not (Keys.Down or Keys.F4)) return;
            Toggle();
            e.Handled = true;
        }

        return new BoxEl
        {
            ZStack = true, Width = _size, Height = _size, Shrink = 0f,
            // Hover is read on the WHOLE cover, not on the pill: the pill has to be visible BEFORE the pointer can
            // reach it, which is the entire point of a reveal affordance.
            OnHoverMove = _ => { if (!_hovered.Peek()) _hovered.Value = true; },
            OnPointerExit = () => { if (_hovered.Peek()) _hovered.Value = false; },
            Children =
            [
                LikedSongsArtwork.Dynamic(_size, _radius, _morphKey),
                Pill(Toggle, Key),
            ],
        };
    }

    Element Pill(Action toggle, Action<KeyEventArgs> key) => new BoxEl
    {
        AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.End,
        Margin = new Edges4(0f, 0f, 10f, 10f),
        Direction = 0, AlignItems = FlexAlign.Center, Gap = 6f, Shrink = 0f,
        Height = 30f, Padding = new Edges4(10f, 0f, 12f, 0f),
        Corners = Radii.FullAll,
        Fill = WaveeOnMedia.ScrimRest, HoverFill = WaveeOnMedia.ScrimHover, PressedFill = WaveeOnMedia.ScrimPressed,
        BorderWidth = 1f, BorderColor = WaveeOnMedia.Stroke,
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
        OnClick = toggle, OnKeyDown = key, OnRealized = _anchorSink,
        // FOCUS is part of the reveal, not an afterthought: the pill is a real tab stop, and a keyboard user landing on
        // an invisible control is the failure mode a hover-only reveal ships with by default.
        OnFocusChanged = f => { if (_focused.Peek() != f) _focused.Value = f; },
        // Bound — a compositor-only cross-fade on ONE node. It stays lit while the flyout is up so the affordance does
        // not vanish the moment the pointer travels into the surface it opened.
        Opacity = Prop.Of(() => _hovered.Value || _focused.Value || _open.Value ? 1f : 0f),
        Transition = MotionTok.ControlNormal,
        Children =
        [
            Icon(Icons.Brush, 14f, WaveeOnMedia.Ink),
            new TextEl(Loc.Get(Strings.Detail.LikedCover.Pill))
            {
                Size = 12f, LineHeight = 16f, Weight = 600, Color = WaveeOnMedia.Ink,
                MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            },
        ],
    };
}

/// <summary>The flyout body: a title, a 3-wide radio grid of live miniatures, and the footnote that says where the
/// artwork comes from.
///
/// <para><b>The miniatures are the REAL treatments</b>, built by the same <see cref="LikedCoverTreatments.Build"/> the
/// full-size cover uses, from the same tile list, in <c>mini</c> form (static — no ambient loops — with capped cell
/// counts). Swatch stand-ins were rejected on the merits: seven of the nine styles differ only in how the SAME art is
/// ARRANGED, which a swatch cannot show. The cost is bounded by construction — every url in the grid is already
/// resident from the cover behind the flyout, at the same 64/128/256 decode buckets, so nine thumbnails are texture
/// cache hits rather than ninety decodes.</para>
///
/// <para>A component (not a static builder) for two reasons: it reads <c>LibraryStore</c> and the persisted style
/// through context, and it must re-render on <c>AppearancePrefs.Bump()</c> so the checked card tracks the value that
/// actually persisted rather than a local echo of the click (E15 — the apply path is read-back-after-write).</para></summary>
sealed class LikedCoverStyleFlyout : Component
{
    const float MiniEdge = 76f;      // WaveePicker.CoverMini's 92 card minus its 8-per-side resting inset
    const float MiniRadius = 5f;
    const int Columns = 3;
    /// <summary>WinUI's <c>FlyoutContentPadding</c> (16,15,16,17) — the presenter deliberately supplies none.</summary>
    static readonly Edges4 CardPad = new(16f, 15f, 16f, 17f);
    static readonly string[] NoTiles = [];

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var store = UseContext(LibraryStore.Slot);

        // E4: the picker is the SECOND thing that charges the liked-list warm (the first is a non-Stock cover). A user
        // who opened this flyout has asked to see their likes arranged nine ways; a user who never opens it, and never
        // leaves Stock, still pays nothing. Idempotent and asynchronous — a guarded one-shot, not a write during render.
        store?.EnsureLiked();
        var tracks = store?.Liked.Value.Value ?? (IReadOnlyList<Track>)Array.Empty<Track>();
        var tiles = LikedCoverRules.Tiles(tracks);
        var cells = tiles as string[] ?? ToArray(tiles);
        // The content key the treatment leaves compare on (an array compares by reference — see LikedCoverArt).
        string tileKey = string.Join('', cells);

        var order = LikedCoverRules.PickerOrder;
        var requested = AppearancePrefs.LikedCover(svc?.Settings);
        int selected = IndexOf(order, requested);

        void Apply(int i)
        {
            if (svc?.Settings is not { } settings || (uint)i >= (uint)order.Length) return;
            // Idempotent and cheap, which it MUST be: Strip fires onChange on every keyboard rove, not only on a
            // commit (selection follows focus — the WinUI RadioButtons contract). The full-size cover behind the
            // flyout repaints on the Bump, so roving IS the preview and there is no separate apply step.
            settings.Set(WaveeSettings.LikedCoverStyle, LikedCoverRules.ToSetting(order[i]));
            AppearancePrefs.Bump();
        }

        Element Mini(int i, bool on)
        {
            var style = order[i];
            // Below its floor a style cannot honestly compose, so its miniature shows exactly what picking it would
            // paint TODAY — the stock cover — dimmed, with the same name under it.
            //
            // DELIVERED DEVIATION from the plan's E1 ("selectable = false"): the card stays selectable. RadioButtons
            // has no per-ITEM enabled state (only a group-wide IsEnabled), and forging one by swallowing the apply
            // would leave the group's selection ring parked on a card whose value never persisted — a visibly broken
            // radio. Selecting an unfed style is also a legitimate forward-looking choice: the setting is stored, the
            // cover paints Stock, and the treatment lights up by itself the moment the library reaches its floor (E3),
            // which is precisely the behaviour E1 describes for a growing library.
            bool fed = cells.Length >= LikedCoverRules.MinTiles(style);
            var effective = LikedCoverRules.Effective(style, cells.Length);
            Element thumb = new BoxEl
            {
                Width = MiniEdge, Height = MiniEdge, Shrink = 0f, HitTestVisible = false,
                Opacity = fed ? 1f : 0.45f,
                Children =
                [
                    // morphKey null, deliberately: nine miniatures claiming the hero cover's shared-element id would
                    // make a Home→detail fly pick one of them as the flight's participant.
                    LikedCoverTreatments.Build(effective, cells, tileKey, tracks.Count,
                                               MiniEdge, MiniRadius, mini: true, morphKey: null),
                ],
            };
            return WaveePicker.Titled(
                WaveePicker.Card(on, WaveePicker.CoverMini, thumb),
                Loc.Get(LikedCoverRules.NameKey(style)), on);
        }

        return new BoxEl
        {
            Direction = 1, Gap = Spacing.M, Shrink = 0f,
            // The flyout PRESENTER supplies acrylic, stroke, elevation and 2 DIP of menu padding — the content card's
            // own inset is the caller's (WinUI FlyoutContentPadding 16,15,16,17; GenericFlyout carries the same
            // number). Width is BORDER-box (the ArtistFacePile flyout's 280-with-8-padding-holding-264 arithmetic), so
            // it is exactly three cards, their two gutters and the inset — the grid can neither wrap early nor leave a
            // fourth column's worth of slack.
            Padding = CardPad,
            Width = Columns * WaveePicker.CoverMini.Width + (Columns - 1) * Spacing.M
                    + CardPad.Left + CardPad.Right,
            Children =
            [
                // Colour belongs to the call site (WaveeType.Eyebrow's own contract): this is the flyout's heading, so
                // it is primary ink — not the tertiary a card's kind-label carries.
                WaveeType.Eyebrow(Loc.Get(Strings.Detail.LikedCover.Title)) with { Color = Tok.TextPrimary },
                WaveePicker.Strip(order.Length, selected, Mini, Apply, maxColumns: Columns),
                new TextEl(Loc.Get(Strings.Detail.LikedCover.Note))
                {
                    Size = 12f, LineHeight = 16f, Color = Tok.TextTertiary,
                    Wrap = TextWrap.WrapWholeWords, MaxLines = 3,
                },
            ],
        };
    }

    /// <summary>The persisted style's slot in the picker, or Stock's when it has none.
    ///
    /// <para><c>PickerOrder</c> is total over the enum now that Lens has landed, so the shipped default checks its own
    /// card on a fresh install — including on a library too small to feed it, where the card is dimmed but still the
    /// checked one, because the SETTING says Lens even while <c>Effective</c> paints Stock. The Stock fallback below
    /// is what covers E11's hand-edited or downgraded value: an int this build does not define reads as Stock, and the
    /// picker shows Stock checked.</para></summary>
    static int IndexOf(LikedCoverStyle[] order, LikedCoverStyle style)
    {
        int stock = 0;
        for (int i = 0; i < order.Length; i++)
        {
            if (order[i] == style) return i;
            if (order[i] == LikedCoverStyle.Stock) stock = i;
        }
        return stock;
    }

    static string[] ToArray(IReadOnlyList<string> tiles)
    {
        if (tiles.Count == 0) return NoTiles;
        var arr = new string[tiles.Count];
        for (int i = 0; i < arr.Length; i++) arr[i] = tiles[i];
        return arr;
    }
}
