using System;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>
/// The ZERO-PIN state, shared by every mode (R3.0: one renderer ⇒ one drop zone). A MOVE out of <c>WaveeSidebar.cs</c>
/// (where it lived while Classic hand-built its own pane) into shared scope, plus the R3.1.5 restyle.
///
/// <para>WHAT CHANGED AND WHY. At rest it used to be a 40-DIP row carrying ONE dim caption and no border — indistinguishable
/// from an empty-section hint, so nothing said "this is a target". It is now a real 56-DIP CARD: a solid 1px
/// <c>StrokeCardDefault</c> hairline, the pin mark, and two lines that name the gesture AND its alternative ("or use the
/// pin action on any library item"), so a user who will never drag still learns how to pin. The dashed accent border,
/// the <c>AccentSubtle</c> fill and the 72-DIP growth are now reserved for a live COMPATIBLE drag — the state where a
/// bigger, louder target is actually useful. That is the whole trade the user's screenshot review asked for: the empty
/// pinned section stops dominating the pane while the generous target survives exactly when it matters.</para>
///
/// <para>A TRACK drag shares the generic resource discriminator but fails the payload capability test, so no pin
/// affordance appears. Pin eligibility remains centralized in <c>WaveeResourceDragPayload.CanPin</c>.</para>
///
/// <para>Its own Component so <c>UseDragState</c> — which re-renders its consumer on every drag CONTENT edge (begin/end,
/// the target under the pointer, effect, caption) — is scoped to this card instead of the whole sidebar.</para>
/// </summary>
sealed class SidebarPinDropZone : Component
{
    /// <summary>The resting card height. Read by a virtualizing host that must size the slot before building the row
    /// (the pane's plan extent); the 56↔72 growth is a measured reflow on top of it.</summary>
    public const float RestHeight = SidebarRowGeometry.PinDropZoneRestHeight;

    /// <summary>The height while a compatible drag is live.</summary>
    public const float ActiveHeight = 72f;

    static readonly LayoutTransition Resize = new(
        TransitionChannels.Size, MotionTok.ContentResize.ToDynamics(),
        Size: SizeMode.Reflow, Anchor: SizeAnchor.Trailing);

    readonly Action<object?, int> _accept;

    public SidebarPinDropZone(Action<object?, int> accept) => _accept = accept;

    public override Element Render()
    {
        var drag = UseDragState();
        var over = UseSignal(false);
        // Typed target: the accept test IS the pin-eligibility test, so a payload that reaches a handler is pinnable by
        // construction (the engine only enters a target whose CanAccept passed).
        var spec = UseMemo(() => Drop.Target<WaveeResourceDragPayload>(
            WaveeDragKinds.Resource,
            accepts: static p => p.CanPin,
            // The zone's own copy says "drop to pin"; the chip says WHAT gets pinned, which is the half the user is
            // dragging and can no longer see once the chip covers it.
            caption: static p => Strings.Drag.Pin(p.Name),
            onEnter: (_, _) => over.Value = true,
            onOver: (_, _) => over.Value = true,
            onLeave: _ => over.Value = false,
            onDrop: (p, _) => { over.Value = false; _accept(p, 0); },
            visualPolicy: DropTargetVisualPolicy.Spotlight), DepKey.Empty);

        bool compatible = drag.Active
            && string.Equals(drag.Kind, WaveeDragKinds.Resource, StringComparison.Ordinal)
            && WaveeResourceDrag.Unwrap(drag.Payload) is { CanPin: true };
        bool hovering = over.Value;
        bool active = compatible || hovering;

        return new BoxEl
        {
            Key = "pins-empty",
            Height = active ? ActiveHeight : RestHeight,
            // The pane already owns the 8-DIP horizontal inset (SidebarPaneMetrics.PanePad), so the card only needs its
            // own trailing gap — a second 4-DIP inset here would push it out of the row band.
            Margin = new Edges4(0f, 0f, 0f, Spacing.XS),
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
            // AUDITED against SidebarPaneMetrics.ContentLane and deliberately NOT on it: this is the CARD family (a
            // bordered, dashed plate), whose own edge is what the eye aligns on — the plate starts at PanePad like every
            // row's fill, and its content is padded inside it, exactly as SidebarPaneSlot.Card/Prompt do.
            Padding = new Edges4(Spacing.M, 0f, Spacing.M, 0f),
            Corners = Radii.ControlAll,
            DropTarget = spec,
            Fill = active ? Tok.AccentSubtle : ColorF.Transparent,
            // REST: a solid hairline card (it must read as a container, not as a caption).
            // ACTIVE: the dashed accent target — the dash pattern is what says "drop here", so it is drag-only.
            BorderColor = active ? Tok.AccentDefault : Tok.StrokeCardDefault,
            BorderWidth = 1f,
            BorderDashOn = active ? Spacing.XS : 0f,
            BorderDashOff = active ? Spacing.XXS : 0f,
            Transition = MotionTok.ControlFaster,
            Layout = Resize,
            Children =
            [
                Icon(Icons.Pin, 16f, active ? Tok.AccentTextPrimary : Tok.TextTertiary),
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, Gap = Spacing.XXS,
                    Children =
                    [
                        new TextEl(Loc.Get(Strings.Sidebar.DropToPin))
                        {
                            Size = 12f, Weight = (ushort)(active ? 600 : 400),
                            Color = active ? Tok.AccentTextPrimary : Tok.TextSecondary,
                            MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                        },
                        // ONE line, ellipsised: the hint is a nudge, not a paragraph. (A two-line wrap is what made the
                        // resting state feel like a billboard.)
                        new TextEl(Loc.Get(Strings.Sidebar.Pin.EmptyHint))
                        {
                            Size = 11f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                        },
                    ],
                },
            ],
        };
    }
}

/// <summary>
/// THE "+" CREATE AFFORDANCE. One component for every surface that offers it: the pane's PlaylistTree section header,
/// a folder row's trailing slot, the compact rail's footer and Library V3's header — so "what does + do" cannot drift
/// per design (the whole reason this component exists rather than four hand-built boxes).
///
/// <para><b>It grew back its flyout.</b> §3.1.6 deleted the old [Playlist · Folder] menu because Spotify folder
/// create/rename/delete did not exist and the Folder arm was a no-op — dead UI promising a command we did not have.
/// The wire landed with P3 (<c>FolderActions</c>), so the reason is gone: a caller that passes <paramref
/// name="menu"/> gets a real flyout anchored at the button, built at OPEN time (labels resolve then, never at render
/// time — the culture-epoch rule) exactly like <c>SidebarPaneSortButton</c>. A caller that passes none keeps the plain
/// direct invoke it had.</para>
///
/// <para><b>It is also a DROP DESTINATION.</b> Dropping playlists on it creates a folder holding them; dropping a
/// track set on the header "+" creates a playlist from them. The cue is the <c>SidebarPinDropZone</c> idiom — a BOUND
/// accent plate, because this runs while a drag is live and a re-render per pointer move is exactly what a cue cannot
/// afford. The spec and its <paramref name="dropActive"/> probe are the CALLER's: only the pane knows what a drop
/// there means, and one drop decision lives in one place (rule 10).</para>
///
/// <para><b>The hover reveal</b> (<paramref name="revealOpacity"/>, the folder-row instance): the engine's reveal
/// cascade lights <c>HoverOpacity</c> on ROW hover with no app hover tracking — but hover flags are NOT updated while
/// a drag is live, so the BOUND base opacity is what shows the "+" mid-gesture. Two owners, one channel, and neither
/// needs the other to work.</para>
///
/// <para><c>BlocksDragArm</c> stops the drag-arm walk here, so pressing "+" on a draggable row neither arms that row's
/// drag nor toggles the folder under it (the click dispatch already stops at the nearest clickable).</para>
/// </summary>
sealed class SidebarCreateButton : Component
{
    readonly Action _onPlaylist;
    readonly Func<ContextMenuModel?>? _menu;
    readonly DropTargetSpec? _drop;
    readonly Func<bool>? _dropActive;
    readonly Func<float>? _revealOpacity;
    readonly float _box, _glyph;

    public SidebarCreateButton(Action onPlaylist,
                               Func<ContextMenuModel?>? menu = null,
                               DropTargetSpec? drop = null,
                               Func<bool>? dropActive = null,
                               Func<float>? revealOpacity = null,
                               float box = 24f, float glyph = 14f)
    {
        _onPlaylist = onPlaylist; _menu = menu; _drop = drop; _dropActive = dropActive;
        _revealOpacity = revealOpacity; _box = box; _glyph = glyph;
    }

    public override Element Render()
    {
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var svc = UseContext(Overlay.Service);

        void Activate()
        {
            // No menu, no overlay service, or a factory that answered nothing: the button keeps its ONE verb rather
            // than opening an empty flyout — an affordance that opens nothing reads as broken.
            if (_menu is null || svc is null) { _onPlaylist(); return; }
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            if (_menu() is not { } model || model.Rows.Count == 0) { _onPlaylist(); return; }
            handle.Value = svc.Open(
                () => anchor.Value,
                () => MenuFlyout.Create(model.Rows, () => handle.Value?.Close()),
                FlyoutPlacement.BottomEdgeAlignedRight,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
                { ConstrainToRootBounds = false });
            handle.Value.ClosedAction = () => handle.Value = null;
        }

        var box = new BoxEl
        {
            Width = _box, Height = _box, Shrink = 0f,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(4f),
            Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true,
            // dnd recipe 1: the arm walk stops here, so pressing "+" is never a handle for dragging the row under it.
            BlocksDragArm = true,
            OnRealized = h => anchor.Value = h,
            OnClick = Activate,
            DropTarget = _drop,
            Children = [Icon(Icons.Add, _glyph, Tok.TextSecondary)],
        }.Interactive(Interaction.Subtle);

        // AFTER Interactive: it rewrites Fill/BorderColor wholesale from the recipe, so the drag cue has to land on top
        // of it rather than under it.
        if (_dropActive is { } cue)
            box = box with
            {
                Fill = Prop.Of(() => cue() ? Tok.AccentDefault with { A = 0.18f } : ColorF.Transparent),
                BorderColor = Prop.Of(() => cue() ? Tok.AccentDefault : ColorF.Transparent),
                BorderWidth = 1f,
            };
        if (_revealOpacity is { } reveal) box = box with { Opacity = Prop.Of(reveal), HoverOpacity = 1f };

        return ToolTip.Wrap(box, Loc.Get(_menu is null
            ? Strings.Sidebar.CreatePlaylistTooltip
            : Strings.Sidebar.CreateTooltip));
    }
}
