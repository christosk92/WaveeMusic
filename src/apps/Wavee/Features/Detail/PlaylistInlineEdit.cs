using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FluentGpu;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Scroll;
using FluentGpu.Signals;
using FluentGpu.WindowsApi.Dialogs;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>Inline playlist metadata editing on the detail rail — read-only hero text by default; hover eases in a
/// pill chrome (fill + hairline border, engine-eased — no re-render) plus a fading pencil, click swaps to an
/// <see cref="EditableText"/> with save/cancel buttons (focus-preserving) and an animated saving→saved chip.
/// Cover: hover/drag cross-fades an overlay; click picks, drop replaces. Commits resolve uri from the live
/// <see cref="Loadable{T}"/> at event time.</summary>
static class PlaylistInlineEdit
{
    internal static Element Cover(Loadable<DetailModel> full, float size, float radius = Radii.Card, bool shadow = true,
                                  string? morphKey = null, int decodePx = 256, bool preferLargest = false, float saturation = 1f)
        => Embed.Comp(() => new EditableCover(full, size, radius, shadow, morphKey, decodePx, preferLargest, saturation))
            with { Key = $"pl-edit-cover:{(int)size}:{(int)radius}:{shadow}:{morphKey}:{decodePx}:{preferLargest}" };

    /// <summary>The editable arm of the hero title. <paramref name="lineHeight"/> is the type-ramp line height paired
    /// with <paramref name="titleSize"/> (Title 28/36, TitleLarge 40/52 …) — the caller owns the rung, so both arms of
    /// the title (this one and the plain <c>TextEl</c>) sit on the identical metric. Default weight is the ramp's 600.
    /// <para><paramref name="displayFace"/> selects the DISPLAY optical face, for the surface whose plain arm uses it
    /// (the unified detail hero). It replaced an <c>onMedia</c> axis that went dead with the immersive hero: both arms
    /// of one title have to agree on the FACE for the same reason they have to agree on the rung.</para></summary>
    internal static Element Title(Loadable<DetailModel> full, float width, float titleSize, ushort weight = 600,
                                  bool displayFace = false, float lineHeight = float.NaN, int maxLines = 0)
        => Embed.Comp(() => new EditableTitle(full, width, titleSize, weight, displayFace, lineHeight, maxLines))
            with { Key = $"pl-edit-title:{(int)width}:{(int)titleSize}:{weight}:{displayFace}:{(int)lineHeight}:{maxLines}" };

    internal static Element Description(Loadable<DetailModel> full, float width, int maxLines, DetailHandlers h)
        => Embed.Comp(() => new EditableDescription(full, width, maxLines, h))
            with { Key = $"pl-edit-desc:{(int)width}:{maxLines}" };

    internal static Element OwnerRow(Loadable<DetailModel> full, float width)
        => Embed.Comp(() => new PlaylistOwnerRow(full, width)) with { Key = $"pl-owner-row:{(int)width}" };

    internal static Element OwnerMenu(Loadable<DetailModel> full, DetailHandlers h)
        => Embed.Comp(() => new OwnerOverflowMenu(full, h)) with { Key = "pl-owner-menu" };

    internal static Element ShareButton(Loadable<DetailModel> full, float size = 40f)
        => Embed.Comp(() => new PlaylistShareButton(full, size)) with { Key = $"pl-share:{(int)size}" };

    /// <summary>The shared adaptive invite affordance (owner row + collaborator pile): a labeled pill when the row is
    /// wide, a round icon button + tooltip when narrow. Clicking opens the Invite &amp; access flyout anchored to it.
    /// Self-gated (returns an empty box when the caller can't administer permissions), so both call sites just embed it.</summary>
    internal static Element InviteButton(Loadable<DetailModel> full, float availableWidth)
        => Embed.Comp(() => new PlaylistInviteControl(full, availableWidth)) with { Key = $"pl-invite:{(int)availableWidth}" };

    /// <summary>Live Spotify playlist owner mutations (permission, delete, invite) — false when fake backend or logged out.</summary>
    internal static bool SpotifyEditsLive(Services? svc)
        => svc?.RealPlaylistMutations is not null && svc.Session.Status == AuthStatus.Authenticated;

    // ── THE edit gate ────────────────────────────────────────────────────────────────────────────────────────────
    // Every playlist edit affordance in the app asks exactly one of these two questions, and neither is
    // `Capabilities.CanEditItems` alone any more: a playlist that was deleted or whose access was revoked under the
    // reader still carries the capabilities it had when it loaded, so the page would keep offering edits the server can
    // only refuse. Routing the drag source, the drop target, the keyboard block move, the title/description/cover
    // editors, the owner overflow and the invite control through ONE pair is what makes "a notice mounts ⇒ the edits go
    // away" a single fact rather than nine call sites that have to agree.

    /// <summary>May the user change this playlist's CONTENTS (add / remove / reorder rows) right now?</summary>
    internal static bool Editable(DetailModel m) => m.Notice == DetailNotice.None && m.Capabilities.CanEditItems;

    /// <summary>May the user change this playlist's METADATA (name / description / cover / permissions) right now?</summary>
    internal static bool EditableMetadata(DetailModel m) => m.Notice == DetailNotice.None && m.Capabilities.CanEditMetadata;

    /// <summary>The shared half of both gates, for the two OWNER affordances whose capability is neither items nor
    /// metadata (the overflow's Delete, the invite control's permission administration): the page is not under a notice.</summary>
    internal static bool Live(DetailModel m) => m.Notice == DetailNotice.None;

    static void PatchDetail(Loadable<DetailModel> full, Func<DetailModel, DetailModel> patch)
    {
        if ((LoadState)full.State.Peek() != LoadState.Ready) return;
        full.SetReady(patch(full.Value.Peek()));
    }

    static async Task RefreshPlaylistDetailAsync(Services? svc, Loadable<DetailModel> full, string uri, CancellationToken ct = default)
    {
        if (svc is null || !SpotifyEditsLive(svc)) return;
        var fresh = await DetailPage.ReloadPlaylistDetailAsync(svc, uri, ct).ConfigureAwait(false);
        if (fresh is not null) full.SetReady(fresh);
    }

    // ── save plumbing ────────────────────────────────────────────────────────────────────────────────────────────

    const int StatusIdle = 0, StatusSaving = 1, StatusSaved = 2;

    /// <summary>The persistent read↔edit swap shell: the SAME node across both branches, so its height change (text
    /// block ↔ field box) TWEENS through layout (<see cref="MotionRecipes.CardResize"/>) instead of snapping — the
    /// branch roots' Enter fades ride inside it.</summary>
    static Element AnimatedSwap(float width, Element body) => new BoxEl
    {
        Width = width, Animate = MotionRecipes.CardResize,
        Children = [body],
    };

    static async Task<bool> SaveDetailsAsync(LibraryBridge lib, string uri, string? name, string? description, bool? collaborative, string? previousName = null)
    {
        try
        {
            await lib.UpdatePlaylistDetailsAsync(uri, name, description, collaborative, previousName).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            PlaylistEditErrors.Toast(ex, PlaylistEditVerb.Rename);
            return false;
        }
    }

    static async Task<bool> ApplyCoverJpegAsync(LibraryBridge lib, string uri, byte[] jpeg)
    {
        try
        {
            await lib.SetPlaylistCoverJpegAsync(uri, jpeg).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            PlaylistEditErrors.Toast(ex);
            return false;
        }
    }

    static async Task<bool> TryCoverPathAsync(LibraryBridge lib, string uri, string path)
    {
        if (path is not { Length: > 0 } || !File.Exists(path)) return false;
        string ext = Path.GetExtension(path);
        if (!ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            Toast.Show(Loc.Get(Strings.Detail.Edit.PickCover), new ToastOptions { Severity = InfoBarSeverity.Warning });
            return false;
        }
        return await ApplyCoverJpegAsync(lib, uri, await File.ReadAllBytesAsync(path).ConfigureAwait(false)).ConfigureAwait(false);
    }

    // ── shared visuals ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The transient saving→saved chip. Mount-gated by the caller (status != idle); keyed by state so the
    /// saving→saved swap replays the enter fade, and unmount fades out via Exit.</summary>
    /// <summary>The header's PENDING chip: "{n} changes pending" while this playlist has unacked edits in the durable
    /// outbox. Sibling of the saving→saved chip and deliberately a separate one: "Saved" is about the edit you just
    /// made landing LOCALLY, this is about the server not having it yet, and collapsing them would let a queued-offline
    /// burst read as saved-and-done. A Component so it appears / counts / disappears off its own per-uri signal without
    /// re-rendering the hero title next to it.</summary>
    internal static Element PendingChip(string uri)
        => Embed.Comp(() => new PlaylistPendingChip(uri)) with { Key = "pl-pending:" + uri };

    sealed class PlaylistPendingChip : Component
    {
        readonly string _uri;
        public PlaylistPendingChip(string uri) => _uri = uri;

        public override Element Render()
        {
            var lib = UseContext(LibraryBridge.Slot);
            if (lib is null) return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
            int pending = lib.PendingEdits(_uri).Value;    // subscribe → this uri only
            if (pending <= 0) return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };

            return ToolTip.Wrap(new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = 6f, Shrink = 0f,
                Padding = new Edges4(8f, 3f, 10f, 3f), Corners = CornerRadius4.All(12f),
                Fill = Tok.FillSubtleSecondary,
                Enter = new EnterExit(Opacity: 0f, Active: true),
                Children =
                [
                    Ui.Icon(Icons.Refresh, 12f, Tok.TextTertiary),
                    new TextEl(Strings.Detail.Edit.PendingCount(pending)) { Size = 11f, Weight = 600, Color = Tok.TextSecondary },
                ],
            }, Loc.Get(Strings.Detail.Edit.PendingSync));
        }
    }

    static Element StatusChip(int status) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = 6f, Shrink = 0f,
        Padding = new Edges4(8f, 3f, 10f, 3f), Corners = CornerRadius4.All(12f),
        Fill = Tok.FillSubtleSecondary,
        Key = "pl-edit-status",
        Animate = MotionRecipes.TextSwap,
        Children =
        [
            ZStack(new BoxEl
            {
                Key = "pl-status-icon:" + status,
                Width = 16f, Height = 16f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Animate = MotionRecipes.IconSwap,
                Children = [status == StatusSaving
                    ? ProgressRing.Indeterminate(16f, true, Tok.TextSecondary)
                    : Ui.Icon(Icons.Accept, 12f, Tok.AccentTextPrimary)],
            }),
            ZStack(new BoxEl
            {
                Key = "pl-status-text:" + status,
                Animate = MotionRecipes.TextSwap,
                Children = [new TextEl(status == StatusSaving
                    ? Loc.Get(Strings.Detail.Edit.Saving)
                    : Loc.Get(Strings.Detail.Edit.Saved))
                { Size = 11f, Weight = 600, Color = Tok.TextSecondary }],
            }),
        ],
    };

    /// <summary>A labeled save/cancel pill (no enter-fade — must be visible the instant edit mode mounts).</summary>
    static Element EditFieldBtn(string glyph, string label, bool accent, Action onClick) => new BoxEl
    {
        Direction = 0, Gap = 6f, AlignItems = FlexAlign.Center, Height = 32f, Shrink = 0f,
        Padding = new Edges4(10f, 0f, 12f, 0f), Corners = CornerRadius4.All(16f),
        Fill = accent ? Tok.AccentTextPrimary with { A = 0.16f } : ColorF.Transparent,
        HoverFill = accent ? Tok.AccentTextPrimary with { A = 0.26f } : Tok.FillSubtleSecondary,
        PressedFill = accent ? Tok.AccentTextPrimary with { A = 0.12f } : Tok.FillSubtleTertiary,
        Cursor = CursorId.Hand, Focusable = true, AllowFocusOnInteraction = false,
        Role = AutomationRole.Button, OnClick = onClick,
        Children =
        [
            Ui.Icon(glyph, 13f, accent ? Tok.AccentTextPrimary : Tok.TextSecondary),
            new TextEl(label) { Size = 12f, Weight = 600, Color = accent ? Tok.AccentTextPrimary : Tok.TextSecondary },
        ],
    };

    /// <summary>Save/cancel row below the field — always visible, never clipped by the field chrome.</summary>
    static Element SaveCancelRow(Action save, Action cancel) => new BoxEl
    {
        Direction = 0, Gap = Spacing.S, Justify = FlexJustify.End, Shrink = 0f,
        Children =
        [
            EditFieldBtn(Icons.Accept, Loc.Get(Strings.Detail.Edit.Save), accent: true, save),
            EditFieldBtn(Icons.Cancel, Loc.Get(Strings.Detail.Edit.Cancel), accent: false, cancel),
        ],
    };

    /// <summary>Field + save/cancel row; <paramref name="shell"/> captures the outer box for bring-into-view (field +
    /// buttons, so the rail scrolls enough to show both).</summary>
    static Element EditChrome(Element field, float width, Action save, Action cancel, Ref<NodeHandle> shell,
        RenderContext ctx, Action<Action> post) => new BoxEl
    {
        Direction = 1, Width = width, Gap = Spacing.S,
        OnRealized = h =>
        {
            shell.Value = h;
            // Double-post: first pump realizes the field, second pump has final layout bounds for scroll math.
            post(() => post(() => BringIntoView(ctx, h, margin: 40f)));
        },
        Children = [field, SaveCancelRow(save, cancel)],
    };

    /// <summary>Focus the field when its root realizes (deferred one pump). visual:false = pointer-style focus —
    /// caret at end, no select-all (the inline-edit affordance; select-all is for Tab keyboard focus only).</summary>
    static TemplateParts FocusOnMount(InputHooks hooks, Action<Action> post)
    {
        var parts = new TemplateParts();
        parts[EditableText.PartRoot] = b => b with
        {
            OnRealized = h => post(() => hooks.FocusNode?.Invoke(h, false)),
        };
        return parts;
    }

    /// <summary>Re-scroll the nearest vertical viewport whenever edit chrome grows (enter edit, draft wraps, status
    /// chip mounts) so the field + save row stay in view.</summary>
    static void UseEditScroll(Component c, Action<Action> post, Signal<bool> editing, Ref<NodeHandle> shell,
        string uriKey, string draftSnapshot)
    {
        c.Context.UseLayoutEffect(() =>
        {
            if (!editing.Peek() || shell.Value.IsNull) return;
            post(() => BringIntoView(c.Context, shell.Value, margin: 32f));
        }, DepKey.From(HashCode.Combine(editing.Value, uriKey, draftSnapshot)));
    }

    /// <summary>Snap-scroll a node into its nearest viewport. SNAP, not a smooth chase: edit-enter must land before the
    /// user types. <see cref="ScrollIntoView"/> owns the mechanics.</summary>
    static void BringIntoView(RenderContext ctx, NodeHandle node, float margin = 16f)
        => ScrollIntoView.Bring(ctx, node, margin);

    // ── cover ────────────────────────────────────────────────────────────────────────────────────────────────────

    sealed class EditableCover : Component
    {
        readonly Loadable<DetailModel> _full;
        readonly float _size;
        readonly float _radius;
        readonly bool _shadow;
        readonly string? _morphKey;
        readonly int _decodePx;
        readonly bool _preferLargest;
        // The read-only HeroArtwork sibling this rail/hero slot swaps against (DetailRail.Build / DetailVerticalHero.
        // Build) paints at 1.18 oversaturation; without threading the same value here the editable↔read-only remount
        // (H3) is not just a structural swap, it visibly pops the cover's vibrancy for a frame.
        readonly float _saturation;
        readonly Signal<bool> _hovered = new(false);
        readonly Signal<bool> _dropOver = new(false);
        readonly Signal<int> _status = new(StatusIdle);
        readonly Ref<DropTargetSpec?> _dropSpec = new(null);
        int _saveEpoch;

        public EditableCover(Loadable<DetailModel> full, float size, float radius, bool shadow,
                             string? morphKey, int decodePx, bool preferLargest, float saturation = 1f)
        {
            _full = full; _size = size; _radius = radius; _shadow = shadow;
            _morphKey = morphKey; _decodePx = decodePx; _preferLargest = preferLargest; _saturation = saturation;
        }

        public override Element Render()
        {
            var lib = UseContext(LibraryBridge.Slot);
            var svc = UseContext(Services.Slot);
            var m = _full.Value.Value;
            bool coverEditable = lib is not null && EditableMetadata(m) && m.ContextUri is { Length: > 0 };
            // DIAGNOSTIC ONLY (see DetailCoverTrace): this component wraps the hero's art when the playlist is editable,
            // and its verdict decides WHICH element tree renders — an editable↔read-only flip between the preview model
            // and the full model would restructure the cover's subtree (a remount ⇒ H3), so the verdict is logged too.
            if (DetailCoverTrace.On)
                WaveeLog.Instance.Debug("detail", "cover", "editable-cover",
                    WaveeLogField.Of("arm", "editable"),
                    WaveeLogField.Of("ctx", m.ContextUri),
                    WaveeLogField.Of("editable", coverEditable),
                    WaveeLogField.Of("hasLib", lib is not null),
                    WaveeLogField.Of("size", _size),
                    WaveeLogField.Of("decodePx", _decodePx),
                    WaveeLogField.Of("preferLargest", _preferLargest),
                    WaveeLogField.Of("cover", DetailCoverTrace.Id(m.Cover, _preferLargest)),
                    WaveeLogField.Of("state", ((LoadState)_full.State.Peek()).ToString()),
                    WaveeLogField.Of("morphKey", _morphKey));
            if (lib is null || !EditableMetadata(m) || m.ContextUri is not { } uri)
                return DetailRail.HeroArtwork(m, _size, _radius, connected: false,
                    saturation: _saturation, morphKey: _morphKey, decodePx: _decodePx, preferLargest: _preferLargest);

            var drag = UseDragState();
            bool compatibleDrag = drag.Active && drag.Kind == DropKinds.Files;
            bool dropCue = compatibleDrag || _dropOver.Value;

            _dropSpec.Value ??= new DropTargetSpec(
                [DropKinds.Files],
                OnEnter: _ => _dropOver.Value = true,
                OnLeave: _ => _dropOver.Value = false,
                OnDrop: s =>
                {
                    _dropOver.Value = false;
                    if (s.Payload is FileDropData { Count: > 0 } files)
                        _ = ApplyPathAsync(lib, svc, uri, files.Paths[0]);
                });

            return new BoxEl
            {
                Width = _size, Height = _size, Corners = CornerRadius4.All(_radius),
                Shadow = _shadow ? Elevation.Card : default, ClipToBounds = true, ZStack = true,
                Cursor = CursorId.Hand,
                DropTarget = _dropSpec.Value,
                OnHoverMove = _ => { if (!_hovered.Peek()) _hovered.Value = true; },
                OnPointerExit = () => { if (_hovered.Peek()) _hovered.Value = false; },
                OnClick = () => _ = PickCoverAsync(lib, svc, uri),
                Children =
                [
                    DetailRail.HeroArtwork(m, _size, _radius, connected: false,
                        saturation: _saturation, morphKey: _morphKey, decodePx: _decodePx, preferLargest: _preferLargest),
                    // Always-mounted overlay — the cross-fade is a bound-opacity transition (compositor-only).
                    CoverOverlay(dropCue),
                ],
            };
        }

        Element CoverOverlay(bool dropActive)
        {
            // Image overlays must NOT use theme fill/text tokens — artwork can be any luminance. Always a dark scrim +
            // white foreground (the same pattern as ArtistPage hero scrims).
            const float scrimA = 0.52f, scrimDropA = 0.68f;
            var onImage = ColorF.FromRgba(255, 255, 255);
            return new BoxEl
            {
                Fill = ColorF.FromRgba(0, 0, 0) with { A = dropActive ? scrimDropA : scrimA },
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = 6f,
                HitTestVisible = false,
                Opacity = Prop.Of(() => _status.Value != StatusIdle || _hovered.Value || _dropOver.Value || dropActive ? 1f : 0f),
                Transition = MotionTok.ControlNormal,
                Children = _status.Value != StatusIdle
                    ?
                    [
                        StatusChip(_status.Peek()),
                    ]
                    :
                    [
                        Ui.Icon(Icons.Camera, 32f, onImage),
                        new TextEl(dropActive
                            ? Loc.Get(Strings.Detail.Edit.DropCover)
                            : Loc.Get(Strings.Detail.Edit.ChangeCover))
                        {
                            Size = 12f, Weight = 600, Color = onImage,
                            Wrap = TextWrap.Wrap, MaxLines = 2, Width = 120f,
                        },
                    ],
            };
        }

        async Task ApplyPathAsync(LibraryBridge lib, Services? svc, string uri, string path)
        {
            await RunSaveAsync(
                async () =>
                {
                    bool saved = await TryCoverPathAsync(lib, uri, path).ConfigureAwait(false);
                    if (saved && svc?.RealStore?.GetPlaylist(uri) is { } header)
                        PatchDetail(_full, m => m with { Cover = header.Cover });
                    return saved;
                },
                _status, () => ++_saveEpoch, () => _saveEpoch).ConfigureAwait(false);
        }

        async Task PickCoverAsync(LibraryBridge lib, Services? svc, string uri)
        {
            string? path = FilePicker.OpenFile(FluentApp.WindowHandle, Loc.Get(Strings.Detail.Edit.PickCover),
                new[] { ("JPEG", "*.jpg;*.jpeg") });
            if (path is null) return;
            await ApplyPathAsync(lib, svc, uri, path).ConfigureAwait(false);
        }
    }

    // ── title ────────────────────────────────────────────────────────────────────────────────────────────────────

    sealed class EditableTitle : Component
    {
        readonly Loadable<DetailModel> _full;
        readonly float _width;
        readonly float _titleSize;
        readonly ushort _weight;
        readonly bool _displayFace;
        readonly float _lineHeight;
        readonly int _maxLines;
        readonly Signal<string> _draft = new("");
        readonly Signal<bool> _editing = new(false);
        readonly Signal<bool> _hovered = new(false);
        readonly Signal<int> _status = new(StatusIdle);
        readonly Ref<NodeHandle> _editShell = new(NodeHandle.Null);
        int _saveEpoch;
        // The title as it stood when the user STARTED editing. Undo's `previousName` must be that, not the live model's
        // title at commit time: a remote rename landing mid-edit would otherwise record an undo that restores the
        // OTHER device's name — an edit the user never made and cannot recognise. (The draft itself is never re-seeded
        // while editing, so the same push cannot overwrite what they are typing either: draft wins, undo remembers.)
        string _titleAtEditStart = "";

        public EditableTitle(Loadable<DetailModel> full, float width, float titleSize, ushort weight, bool displayFace,
                             float lineHeight, int maxLines)
        {
            _full = full; _width = width; _titleSize = titleSize; _weight = weight; _displayFace = displayFace;
            _lineHeight = lineHeight;
            _maxLines = maxLines > 0 ? maxLines : (displayFace ? 2 : 3);
        }

        public override Element Render()
        {
            var lib = UseContext(LibraryBridge.Slot);
            var hooks = UseContext(InputHooks.Current);
            var post = UsePost();
            var m = _full.Value.Value;
            string? uri = m.ContextUri;
            UseLayoutEffect(() => { if (!_editing.Peek()) _draft.Value = m.Title; }, (uri ?? "", m.Title));
            // JUST CREATED HERE (PlaylistCreateIntent, armed by PlaylistCreateFlow at the navigation edge): open in edit
            // mode so the numbered default name is selected and ready to be replaced. A layout effect, not a render-time
            // write — and keyed on the uri, so it fires on the frame the real uri arrives, not on the preview frame
            // before it. Take() consumes, so returning to the same playlist later does not re-open the editor.
            UseLayoutEffect(() =>
            {
                if (uri is not { Length: > 0 } fresh || !PlaylistCreateIntent.Take(fresh)) return;
                _titleAtEditStart = m.Title;
                _editing.Value = true;
            }, uri ?? "");
            UseEditScroll(this, post, _editing, _editShell, uri ?? "", _draft.Value);

            if (lib is null || !EditableMetadata(m) || uri is null)
            {
                // The type ramp's paired line height, handed in by the caller — not a multiple of the size, which
                // produced a different leading for every title size on the page.
                return (_displayFace ? WaveeType.DetailHero(m.Title) : WaveeType.PageHero(m.Title)) with
                {
                    Size = _titleSize, MinSize = 18f, Weight = _weight, Width = _width, LineHeight = _lineHeight,
                    MaxWidth = _width,
                    Wrap = TextWrap.WrapWholeWords, MaxLines = _maxLines, Trim = TextTrim.CharacterEllipsis,
                    Color = Tok.TextPrimary,
                };
            }

            if (_editing.Value)
            {
                float fieldH = MathF.Max(40f, _titleSize + 12f);
                return AnimatedSwap(_width, EditChrome(
                    Embed.Comp(() => new EditableText
                    {
                        Text = _draft,
                        Width = _width,
                        Height = fieldH,
                        FontSize = _titleSize,
                        PlaceCaretAtEndOnFocus = true,
                        Placeholder = Loc.Get(Strings.Detail.Edit.NamePlaceholder),
                        OnCommit = _ => EndEdit(lib, commit: true),
                        OnCancel = () => EndEdit(lib, commit: false),
                        OnFocusChanged = gained => { if (!gained) EndEdit(lib, commit: true); },
                        Parts = FocusOnMount(hooks, post),
                    }) with { Key = uri },
                    _width, () => EndEdit(lib, commit: true), () => EndEdit(lib, commit: false), _editShell, Context, post));
            }

            int status = _status.Value;
            string title = string.IsNullOrWhiteSpace(m.Title) ? Loc.Get(Strings.Detail.Edit.NamePlaceholder) : m.Title;
            // The hover pill: padding + compensating negative margin keep the hero text at its resting x/y; the fill
            // and hairline border ease in engine-side (HoverFade channel — no re-render), cursor is always I-beam.
            Element titleRow = new BoxEl
            {
                Direction = 0,
                Width = _width + 16f,
                Gap = Spacing.S, AlignItems = FlexAlign.Center,
                Margin = new Edges4(-8f, -4f, -8f, -4f), Padding = new Edges4(8f, 4f, 8f, 4f),
                Corners = CornerRadius4.All(Radii.Control),
                HoverFill = Tok.FillSubtleSecondary,
                BorderWidth = 1f, BorderColor = ColorF.Transparent,
                HoverBorderColor = Tok.StrokeControlDefault,
                Cursor = CursorId.IBeam,
                OnHoverMove = _ => { if (!_hovered.Peek()) _hovered.Value = true; },
                OnPointerExit = () => { if (_hovered.Peek()) _hovered.Value = false; },
                OnClick = () => { _titleAtEditStart = m.Title; _editing.Value = true; },
                Enter = new EnterExit(Opacity: 0f, Active: true),
                Children =
                [
                    (_displayFace ? WaveeType.DetailHero(title) : WaveeType.PageHero(title)) with
                    {
                        Size = _titleSize, MinSize = 18f, Weight = _weight, Grow = 1f,
                        LineHeight = _lineHeight,
                        Wrap = TextWrap.WrapWholeWords, MaxLines = _maxLines, Trim = TextTrim.CharacterEllipsis,
                        Color = string.IsNullOrWhiteSpace(m.Title) ? Tok.TextTertiary : Tok.TextPrimary,
                    },
                    // Always-mounted pencil — fades via a bound-opacity transition (no discrete pop).
                    new BoxEl
                    {
                        Width = 20f, Height = 20f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        Opacity = Prop.Of(() => _hovered.Value && _status.Value == StatusIdle ? 1f : 0f),
                        Transition = MotionTok.ControlFast,
                        Children = [Ui.Icon(Icons.Edit, MathF.Max(14f, _titleSize * 0.4f), Tok.TextSecondary)],
                    },
                ],
            };
            // The transient status must never participate in the hero title's horizontal measure. Putting the Saved
            // chip in titleRow stole width for 1.8 s and forced a live wrap exactly as the editor branch was being
            // replaced; the retained old glyph run and the newly-wrapped last character then painted together. A stable
            // column keeps the title width invariant and gives status its own trailing row.
            return AnimatedSwap(_width, new BoxEl
            {
                Direction = 1, Width = _width, Gap = status == StatusIdle ? 0f : 4f,
                Children =
                [
                    titleRow,
                    // The trailing status row carries BOTH chips: the transient saving→saved one and the persistent
                    // "{n} changes pending" one. Neither may participate in the hero title's horizontal measure (see
                    // above), which is exactly why they live here rather than in titleRow.
                    new BoxEl
                    {
                        Direction = 0, Width = _width, Gap = 6f, Justify = FlexJustify.End,
                        Children = status != StatusIdle ? [StatusChip(status), PendingChip(uri)] : [PendingChip(uri)],
                    },
                ],
            });
        }

        void EndEdit(LibraryBridge lib, bool commit)
        {
            if (!_editing.Peek()) return;
            _editing.Value = false;
            var cur = _full.Value.Peek();
            if (!commit) { _draft.Value = cur.Title; return; }
            if (cur.ContextUri is not { } uri) return;
            string next = _draft.Peek().Trim();
            if (next.Length == 0 || next == cur.Title) { _draft.Value = cur.Title; return; }
            _ = RunSaveAsync(async () =>
            {
                bool saved = await SaveDetailsAsync(lib, uri, next, null, null, _titleAtEditStart).ConfigureAwait(false);
                if (saved) PatchDetail(_full, m => m with { Title = next });
                return saved;
            }, _status, () => ++_saveEpoch, () => _saveEpoch);
        }
    }

    // ── description ──────────────────────────────────────────────────────────────────────────────────────────────

    sealed class EditableDescription : Component
    {
        readonly Loadable<DetailModel> _full;
        readonly float _width;
        readonly int _maxLines;
        readonly DetailHandlers _h;
        readonly Signal<string> _draft = new("");
        readonly Signal<bool> _editing = new(false);
        readonly Signal<bool> _hovered = new(false);
        readonly Signal<int> _status = new(StatusIdle);
        readonly Ref<NodeHandle> _editShell = new(NodeHandle.Null);
        readonly Ref<NodeHandle> _readShell = new(NodeHandle.Null);
        int _saveEpoch;

        public EditableDescription(Loadable<DetailModel> full, float width, int maxLines, DetailHandlers h)
        { _full = full; _width = width; _maxLines = maxLines; _h = h; }

        public override Element Render()
        {
            var lib = UseContext(LibraryBridge.Slot);
            var hooks = UseContext(InputHooks.Current);
            var post = UsePost();
            var m = _full.Value.Value;
            string? uri = m.ContextUri;
            UseLayoutEffect(() => { if (!_editing.Peek()) _draft.Value = m.Description ?? ""; }, (uri ?? "", m.Description ?? ""));
            UseEditScroll(this, post, _editing, _editShell, uri ?? "", _draft.Value);

            if (_maxLines <= 0 || lib is null || !EditableMetadata(m) || uri is null)
                return new BoxEl();

            // Fixed edit height (~3 lines): never derive from the read-state line cap (can be huge on a tall rail) and
            // never grow with wrapped text — overflow scrolls inside the clipped field; the rail scrolls to the shell.
            const float fieldH = 72f;
            bool hasDesc = m.Description is { Length: > 0 };

            if (_editing.Value)
            {
                return AnimatedSwap(_width, EditChrome(
                    Embed.Comp(() => new EditableText
                    {
                        Text = _draft,
                        Width = _width,
                        Height = fieldH,
                        AcceptsReturn = true,
                        Placeholder = Loc.Get(Strings.Detail.Edit.DescriptionPlaceholder),
                        OnCommit = _ => EndEdit(lib, commit: true),
                        OnCancel = () => EndEdit(lib, commit: false),
                        OnFocusChanged = gained => { if (!gained) EndEdit(lib, commit: true); },
                        Parts = FocusOnMount(hooks, post),
                    }) with { Key = uri },
                    _width, () => EndEdit(lib, commit: true), () => EndEdit(lib, commit: false), _editShell, Context, post));
            }

            int status = _status.Value;
            Element descriptionRow = new BoxEl
            {
                Direction = 0, Width = _width + 16f, Gap = Spacing.S, AlignItems = FlexAlign.Start,
                Margin = new Edges4(-8f, -4f, -8f, -4f), Padding = new Edges4(8f, 4f, 8f, 4f),
                Corners = CornerRadius4.All(Radii.Control),
                HoverFill = Tok.FillSubtleSecondary,
                BorderWidth = 1f, BorderColor = ColorF.Transparent,
                HoverBorderColor = Tok.StrokeControlDefault,
                Cursor = CursorId.IBeam,
                OnRealized = h => _readShell.Value = h,
                OnHoverMove = _ => { if (!_hovered.Peek()) _hovered.Value = true; },
                OnPointerExit = () => { if (_hovered.Peek()) _hovered.Value = false; },
                OnClick = () =>
                {
                    _editing.Value = true;
                    post(() => BringIntoView(Context, _readShell.Value, margin: 48f));
                },
                Enter = new EnterExit(Opacity: 0f, Active: true),
                Children =
                [
                    // FLEX text (Grow/Basis=0): wraps to whatever width the row leaves after the pencil/chip take
                    // their intrinsic space — no hand-computed width reservations (the title row's exact model).
                    hasDesc
                        ? RichText.ExpandableFlex(m.Description!, 12f, Tok.TextSecondary, Tok.AccentTextPrimary,
                            _maxLines, uri,
                            u => { if (RichText.RouteForUri(u) is { } k) _h.Go(k, null); })
                        : new TextEl(Loc.Get(Strings.Detail.Edit.DescriptionPlaceholder))
                        {
                            Size = 12f, Color = Tok.TextTertiary,
                            Grow = 1f, Basis = 0f,
                            Wrap = TextWrap.Wrap, MaxLines = _maxLines,
                        },
                    // Pencil pinned to the first text line (18px line box), fading with the hover state.
                    new BoxEl
                    {
                        Width = 16f, Height = 18f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        Opacity = Prop.Of(() => _hovered.Value && _status.Value == StatusIdle ? 1f : 0f),
                        Transition = MotionTok.ControlFast,
                        Children = [Ui.Icon(Icons.Edit, 13f, Tok.TextSecondary)],
                    },
                ],
            };
            // Same invariant as the title editor: status feedback is vertical chrome, never an inline sibling that
            // changes the wrapping width of live rich text during the save transition.
            return AnimatedSwap(_width, new BoxEl
            {
                Direction = 1, Width = _width, Gap = status == StatusIdle ? 0f : 4f,
                Children =
                [
                    descriptionRow,
                    status != StatusIdle
                        ? new BoxEl { Direction = 0, Width = _width, Justify = FlexJustify.End, Children = [StatusChip(status)] }
                        : new BoxEl { Width = _width, Height = 0f },
                ],
            });
        }

        void EndEdit(LibraryBridge lib, bool commit)
        {
            if (!_editing.Peek()) return;
            _editing.Value = false;
            var cur = _full.Value.Peek();
            if (!commit) { _draft.Value = cur.Description ?? ""; return; }
            if (cur.ContextUri is not { } uri) return;
            string next = _draft.Peek().Trim();
            if (next == (cur.Description ?? "")) return;
            _ = RunSaveAsync(async () =>
            {
                bool saved = await SaveDetailsAsync(lib, uri, null, next, null).ConfigureAwait(false);
                if (saved) PatchDetail(_full, m => m with { Description = next.Length == 0 ? null : next });
                return saved;
            }, _status, () => ++_saveEpoch, () => _saveEpoch);
        }
    }

    // ── owner row + invite ───────────────────────────────────────────────────────────────────────────────────────

    sealed class PlaylistOwnerRow : Component
    {
        readonly Loadable<DetailModel> _full;
        readonly float _width;

        public PlaylistOwnerRow(Loadable<DetailModel> full, float width) { _full = full; _width = width; }

        public override Element Render()
        {
            var m = _full.Value.Value;
            string owner = m.OwnerName ?? "";
            // Flex row: avatar + invite are Shrink=0, the name Grows/trims into whatever is left — nothing ever clips at
            // rail width (the old hand-computed MaxWidth = _width - 120f under-reserved for the pill and clipped).
            return new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MaxWidth = _width,
                Children =
                [
                    PersonPicture.Create("", 24f, displayName: owner, imageSourcePath: m.OwnerImage?.Url),
                    WaveeType.TrackTitle(owner) with { Grow = 1f, Basis = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    InviteButton(_full, _width),
                ],
            };
        }
    }

    // ── share ────────────────────────────────────────────────────────────────────────────────────────────────────

    sealed class PlaylistShareButton : Component
    {
        readonly Loadable<DetailModel> _full;
        readonly float _size;
        readonly Signal<bool> _copied = new(false);
        TimerHandle _copyReset;   // one-shot: flips the "copied ✓" glyph back after 1600ms (frame-clock UseTimeout, generation-guarded)
        public PlaylistShareButton(Loadable<DetailModel> full, float size) { _full = full; _size = size; }

        public override Element Render()
        {
            _copyReset = UseTimeout(() => _copied.Value = false, 1600f);
            var m = _full.Value.Value;
            bool copied = _copied.Value;
            return new BoxEl
            {
                Width = _size, Height = _size,
                Children =
                [
                    new BoxEl
                    {
                        Width = _size, Height = _size, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        Corners = CornerRadius4.All(_size * 0.5f),
                        HoverScale = WaveeMotion.ScaleSubtle.Hover, PressScale = WaveeMotion.ScaleSubtle.Press,
                        Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.Button,
                        OnClick = () => Share(m),
                        Children = [new BoxEl
                        {
                            Key = copied ? "share-check" : "share-link",
                            Animate = MotionRecipes.IconSwap,
                            Children = [Icon(copied ? Icons.Accept : Icons.Share, 16f,
                                copied ? Tok.AccentTextPrimary : Tok.TextSecondary)],
                        }],
                    }.Interactive(Interaction.Subtle),
                ],
            };
        }

        void Share(DetailModel m)
        {
            if (m.ContextUri is not { } uri) return;
            var url = m.ShareUrl ?? DetailPage.SpotifyPlaylistWebUrl(uri);
            if (InputHooks.Current.Default.Clipboard is { } clip)
            {
                try { clip.SetText(url); }
                catch (Exception ex) { PlaylistEditErrors.Toast(ex); return; }
                InputHooks.Current.Default.Announce?.Invoke(Loc.Get(Strings.Auth.Copied), false);
                _copied.Value = true;
                _copyReset.Restart();   // reset the ✓ back to the share glyph after 1600ms (generation-guarded handle)
            }
            else InputHooks.Current.Default.OpenUri?.Invoke(url);
        }
    }

    internal static async Task<bool> CopyContributorInviteAsync(LibraryBridge lib, DetailModel m, Loadable<DetailModel>? full = null, Services? svc = null)
    {
        if (m.ContextUri is not { } uri) return false;
        try
        {
            var token = await lib.CreateContributorInviteAsync(uri).ConfigureAwait(false);
            var url = $"{m.ShareUrl ?? DetailPage.SpotifyPlaylistWebUrl(uri)}?pt={token}";
            var clip = InputHooks.Current.Default.Clipboard;
            bool copied = clip is not null;
            if (clip is not null)
            {
                clip.SetText(url);
                InputHooks.Current.Default.Announce?.Invoke(Loc.Get(Strings.Auth.Copied), false);
            }
            else InputHooks.Current.Default.OpenUri?.Invoke(url);
            if (full is not null) await RefreshPlaylistDetailAsync(svc, full, uri).ConfigureAwait(false);
            return copied;
        }
        catch (Exception ex) { PlaylistEditErrors.Toast(ex); return false; }
    }

    // ── the two permission writes (P2.9) ─────────────────────────────────────────────────────────────────────────
    // Both are OPTIMISTIC-ONLY. The re-read that used to follow each of them is gone: the STORE header is now the
    // canonical permission state, so the ack (and any concurrent flip from another device, which arrives as a dealer
    // permission push) bumps StoreChange(uri) and the open page reloads itself in place — the flyout re-skins off that
    // model with no request of its own. Re-fetching here bought nothing and raced the push: whichever landed second won.

    static async Task<bool> SetCollaborativeAsync(LibraryBridge lib, Loadable<DetailModel> full, string uri, bool collaborative)
    {
        if (!await SaveDetailsAsync(lib, uri, null, null, collaborative).ConfigureAwait(false)) return false;
        PatchDetail(full, m => m with { Capabilities = m.Capabilities with { IsCollaborative = collaborative } });
        return true;
    }

    static async Task<bool> SetVisibilityAsync(LibraryBridge lib, Loadable<DetailModel> full, string uri, bool isPublic)
    {
        try
        {
            await lib.SetPlaylistVisibilityAsync(uri, isPublic).ConfigureAwait(false);
            PatchDetail(full, m => m with { IsPublic = isPublic });
            return true;
        }
        catch (Exception ex) { PlaylistEditErrors.Toast(ex); return false; }
    }

    // ── invite affordance + access flyout ────────────────────────────────────────────────────────────────────────

    /// <summary>Open (or toggle) the Invite &amp; access WinUI-style FlyoutPresenter anchored to
    /// <paramref name="anchor"/>; focus-trapped and light-dismissable.</summary>
    static void OpenAccessFlyout(IOverlayService? overlay, LibraryBridge lib, Services? svc, Loadable<DetailModel> full,
        Func<NodeHandle> anchor, Ref<OverlayHandle?> handle, FlyoutPlacement placement)
    {
        if (overlay is null) return;
        if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
        handle.Value = overlay.Open(
            anchor,
            () => Embed.Comp(() => new PlaylistAccessFlyout(full, lib, svc)),
            placement,
            new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
            { ConstrainToRootBounds = false });
        handle.Value.ClosedAction = () => handle.Value = null;
    }

    /// <summary>The shared adaptive invite affordance. Wide rows get a bordered pill (glyph + label); narrow rows get a
    /// 28px round icon button + tooltip, so the owner name always keeps room. Both open the access flyout on click.</summary>
    sealed class PlaylistInviteControl : Component
    {
        readonly Loadable<DetailModel> _full;
        readonly float _availableWidth;

        public PlaylistInviteControl(Loadable<DetailModel> full, float availableWidth)
        { _full = full; _availableWidth = availableWidth; }

        public override Element Render()
        {
            var lib = UseContext(LibraryBridge.Slot);
            var svc = UseContext(Services.Slot);
            var overlay = UseContext(Overlay.Service);
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);

            var m = _full.Value.Value;
            if (lib is null || !SpotifyEditsLive(svc) || !Live(m) || !m.Capabilities.CanAdministratePermissions || m.ContextUri is null)
                return new BoxEl { Shrink = 0f };

            void Toggle() => OpenAccessFlyout(overlay, lib, svc, _full, () => anchor.Value, handle, FlyoutPlacement.BottomEdgeAlignedLeft);

            // Below ~260px a full pill would starve the owner name → collapse to a round icon (still readable via tooltip).
            bool compact = _availableWidth < 260f;
            if (compact)
                return ToolTip.Wrap(new BoxEl
                {
                    Width = 28f, Height = 28f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Corners = CornerRadius4.All(14f),
                    HoverScale = WaveeMotion.ScaleSubtle.Hover, PressScale = WaveeMotion.ScaleSubtle.Press,
                    Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.Button,
                    OnClick = Toggle, OnRealized = h => anchor.Value = h,
                    Children = [Icon(Icons.Friends, 14f, Tok.TextSecondary)],
                }.Interactive(Interaction.Subtle), Loc.Get(Strings.Detail.Edit.InviteCollaborators));

            return new BoxEl
            {
                Direction = 0, Shrink = 0f, Height = 28f, Gap = Spacing.XS, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Padding = new Edges4(10f, 0f, 12f, 0f), Corners = CornerRadius4.All(14f),
                BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
                HoverScale = WaveeMotion.ScaleSubtle.Hover, PressScale = WaveeMotion.ScaleSubtle.Press,
                Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.Button,
                OnClick = Toggle, OnRealized = h => anchor.Value = h,
                Children =
                [
                    Icon(Icons.Friends, 12f, Tok.TextPrimary),
                    new TextEl(Loc.Get(Strings.Detail.Edit.InviteCollaborators)) { Size = 12f, Weight = 600, Color = Tok.TextPrimary },
                ],
            }.Interactive(Interaction.Subtle);
        }
    }

    /// <summary>The "Invite &amp; access" flyout body: copy-invite CTA + collaborative/public toggles. A Component that
    /// reads live state from the <see cref="Loadable{T}"/> (component props freeze at mount), so the controlled toggles
    /// re-render when the optimistic <c>PatchDetail</c> lands. <paramref name="lib"/>/<paramref name="svc"/> are stable
    /// service instances — safe to capture at mount.</summary>
    sealed class PlaylistAccessFlyout : Component
    {
        readonly Loadable<DetailModel> _full;
        readonly LibraryBridge _lib;
        readonly Services? _svc;
        readonly Signal<int> _inviteStatus = new(StatusIdle);
        readonly Signal<int> _collaborativeStatus = new(StatusIdle);
        readonly Signal<int> _publicStatus = new(StatusIdle);
        int _inviteEpoch, _collaborativeEpoch, _publicEpoch;

        public PlaylistAccessFlyout(Loadable<DetailModel> full, LibraryBridge lib, Services? svc)
        { _full = full; _lib = lib; _svc = svc; }

        public override Element Render()
        {
            var m = _full.Value.Value;              // live — re-renders on PatchDetail (controlled toggles)
            var caps = m.Capabilities;
            string? uri = m.ContextUri;
            int inviteStatus = _inviteStatus.Value;
            int collaborativeStatus = _collaborativeStatus.Value;
            int publicStatus = _publicStatus.Value;

            var content = new List<Element>(5)
            {
                new BoxEl
                {
                    Direction = 1, Gap = 4f,
                    Children =
                    [
                        new TextEl(Loc.Get(Strings.Detail.Edit.InviteCollaborators)) { Size = 14f, Weight = 700, Color = Tok.TextPrimary },
                        new TextEl(Loc.Get(Strings.Detail.Edit.InviteHint)) { Size = 12f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 3 },
                    ],
                },
            };

            if (caps.CanAdministratePermissions && uri is not null)
                content.Add(CopyInviteCta(inviteStatus));

            if (caps.CanEditMetadata && caps.CanAdministratePermissions && uri is { } u)
            {
                content.Add(new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault, Margin = new Edges4(0f, 2f, 0f, 2f) });
                content.Add(ToggleRow(
                    Loc.Get(Strings.Detail.Edit.Collaborative), Loc.Get(Strings.Detail.Edit.CollaborativeHint),
                    caps.IsCollaborative, collaborativeStatus,
                    () =>
                    {
                        var c = _full.Value.Peek();
                        _ = RunSaveAsync(
                            () => SetCollaborativeAsync(_lib, _full, u, !c.Capabilities.IsCollaborative),
                            _collaborativeStatus, () => ++_collaborativeEpoch, () => _collaborativeEpoch);
                    }));
                content.Add(ToggleRow(
                    Loc.Get(Strings.Detail.Edit.PublicPlaylist),
                    // The caption states what the CURRENT setting means rather than what the toggle is for: "public"
                    // and "private" are the two halves of one sentence about who can reach this playlist, and a static
                    // hint left the off state unexplained (the reachable audience of a private playlist is exactly its
                    // collaborators, which is the fact people are actually asking about when they open this flyout).
                    Loc.Get(m.IsPublic ? Strings.Detail.Access.PublicCaption : Strings.Detail.Access.PrivateCaption),
                    m.IsPublic, publicStatus,
                    () =>
                    {
                        var c = _full.Value.Peek();
                        _ = RunSaveAsync(
                            () => SetVisibilityAsync(_lib, _full, u, !c.IsPublic),
                            _publicStatus, () => ++_publicEpoch, () => _publicEpoch);
                    }));
            }

            return new BoxEl
            {
                Direction = 1, Width = 300f, Gap = 12f, Padding = Edges4.All(16f),
                Children = content.ToArray(),
            };
        }

        Element CopyInviteCta(int status)
        {
            var accent = Tok.AccentDefault;
            var ink = ColorContrast.PickContrast(accent);
            return new BoxEl
            {
                Direction = 0, Height = 34f, Gap = Spacing.S, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = CornerRadius4.All(17f), Fill = accent,
                HoverFill = Tok.AccentSecondary, PressedFill = Tok.AccentTertiary,
                HoverScale = WaveeMotion.ScaleSubtle.Hover, PressScale = WaveeMotion.ScaleSubtle.Press,
                Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.Button,
                IsEnabled = status != StatusSaving,
                OnClick = status == StatusSaving ? null : () => _ = RunSaveAsync(
                    () => CopyContributorInviteAsync(_lib, _full.Value.Peek(), _full, _svc),
                    _inviteStatus, () => ++_inviteEpoch, () => _inviteEpoch),
                Children =
                [
                    new BoxEl
                    {
                        Key = "invite-icon:" + status,
                        Animate = MotionRecipes.IconSwap,
                        Children = [status == StatusSaving
                            ? ProgressRing.Indeterminate(14f, true, ink)
                            : Icon(status == StatusSaved ? Icons.Accept : Icons.Link, 14f, ink)],
                    },
                    new BoxEl
                    {
                        Key = "invite-text:" + status,
                        Animate = MotionRecipes.TextSwap,
                        Children = [new TextEl(status == StatusSaved
                            ? Loc.Get(Strings.Auth.Copied)
                            : Loc.Get(Strings.Detail.Edit.CopyInviteLink))
                        { Size = 13f, Weight = 600, Color = ink }],
                    },
                ],
            };
        }

        static Element ToggleRow(string label, string caption, bool isOn, int status, Action onToggle) => new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
            Children =
            [
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, Gap = 2f,
                    Children =
                    [
                        new TextEl(label) { Size = 13f, Weight = 600, Color = Tok.TextPrimary },
                        new TextEl(caption) { Size = 11.5f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 2 },
                    ],
                },
                status != StatusIdle ? StatusChip(status) : new BoxEl { Width = 0f },
                ToggleSwitch.Create(new Signal<bool>(isOn), onChange: _ => onToggle(), isEnabled: status != StatusSaving, style: SettingsCard.CompactToggleStyle()),
            ],
        };
    }

    // ── owner overflow (invite / delete) ─────────────────────────────────────────────────────────────────────────

    sealed class OwnerOverflowMenu : Component
    {
        readonly Loadable<DetailModel> _full;
        readonly DetailHandlers _h;

        public OwnerOverflowMenu(Loadable<DetailModel> full, DetailHandlers h) { _full = full; _h = h; }

        public override Element Render()
        {
            var lib = UseContext(LibraryBridge.Slot);
            var svc = UseContext(Services.Slot);
            var overlay = UseContext(Overlay.Service);
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            var accessHandle = UseRef<OverlayHandle?>(null);

            var m = _full.Value.Value;
            if (lib is null || !SpotifyEditsLive(svc) || m.ContextUri is not { } uri || !Live(m) || !m.Capabilities.IsOwner)
                return new BoxEl();

            void Toggle()
            {
                if (overlay is null) return;
                if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
                var items = new List<MenuFlyoutItem>();
                AppendOwnerItems(items, overlay, lib, svc, _full, _h, () => anchor.Value, accessHandle);
                if (items.Count == 0) return;
                handle.Value = overlay.Open(
                    () => anchor.Value,
                    () => MenuFlyout.Create(items, () => handle.Value?.Close()),
                    FlyoutPlacement.BottomEdgeAlignedRight,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
                handle.Value.ClosedAction = () => handle.Value = null;
            }

            return new BoxEl
            {
                Width = 40f, Height = 40f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = CornerRadius4.All(20f),
                HoverScale = WaveeMotion.ScaleSubtle.Hover, PressScale = WaveeMotion.ScaleSubtle.Press,
                OnClick = Toggle,
                OnRealized = h => anchor.Value = h,
                Children = [Icon(Icons.More, 16f, Tok.TextSecondary)],
            }.Interactive(Interaction.Subtle);
        }
    }

    /// <summary>Append the playlist-owner overflow items (Invite collaborators · Delete playlist) to <paramref name="items"/>.
    /// Fully capability-gated (SpotifyEditsLive ∧ IsOwner), so both the rail's owner ⋯ menu and the vertical hero's More
    /// menu call it unconditionally and get identical rows. Invite reuses the shared access flyout; delete confirms then
    /// navigates home via <paramref name="h"/>. Every row carries a glyph so the menu's icon column stays consistent.
    ///
    /// <para>The sidebar row menus fold their permission verbs into an <c>Access ▸</c> submenu (<c>Menus.AccessItem</c>)
    /// because they carry fourteen-plus verbs; this menu carries TWO, and the page it hangs off already exposes the
    /// visibility/collaborative pair as an inline access flyout beside it. Grouping two rows into a cascade would add a
    /// click and a second door onto the same state, so this one stays flat — the shared rule the sidebar and this menu
    /// do keep is that <b>Rename is never hidden in a submenu or a strip</b>, and destructive is last.</para></summary>
    internal static void AppendOwnerItems(List<MenuFlyoutItem> items, IOverlayService? overlay, LibraryBridge? lib,
        Services? svc, Loadable<DetailModel> full, DetailHandlers h, Func<NodeHandle> anchor, Ref<OverlayHandle?> accessHandle)
    {
        var m = full.Value.Peek();
        if (overlay is null || lib is null || !SpotifyEditsLive(svc) || m.ContextUri is not { } uri || !Live(m) || !m.Capabilities.IsOwner)
            return;
        if (m.Capabilities.CanAdministratePermissions)
        {
            items.Add(new MenuFlyoutItem(Loc.Get(Strings.Detail.Edit.InviteCollaborators), Icons.Friends,
                Invoke: () => OpenAccessFlyout(overlay, lib, svc, full, anchor, accessHandle, FlyoutPlacement.BottomEdgeAlignedRight)));
            items.Add(MenuFlyoutItem.Separator);
        }
        items.Add(new MenuFlyoutItem(Loc.Get(Strings.Detail.Edit.DeletePlaylist), Icons.Delete,
            Invoke: () => SettingsShared.Confirm(overlay,
                Loc.Get(Strings.Detail.Edit.DeletePlaylist),
                Loc.Get(Strings.Detail.Edit.DeletePlaylistConfirm),
                Loc.Get(Strings.Detail.Edit.DeletePlaylist),
                () => _ = DeletePlaylistAsync(lib, h, uri))));
    }

    static async Task DeletePlaylistAsync(LibraryBridge lib, DetailHandlers h, string uri)
    {
        try
        {
            await lib.DeletePlaylistAsync(uri).ConfigureAwait(false);
            h.Go("home", null);
        }
        catch (Exception ex) { PlaylistEditErrors.Toast(ex); }
    }

    // ── shared hooks/plumbing ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Drive the saving→saved→idle status lifecycle around one save call; a newer save (epoch bump)
    /// abandons the stale tail so overlapping edits never fight over the chip.</summary>
    static async Task RunSaveAsync(Func<Task<bool>> save, Signal<int> status, Func<int> bumpEpoch, Func<int> curEpoch)
    {
        int epoch = bumpEpoch();
        status.Value = StatusSaving;
        bool ok = await save().ConfigureAwait(false);
        if (curEpoch() != epoch) return;
        if (!ok) { status.Value = StatusIdle; return; }
        status.Value = StatusSaved;
        await Task.Delay(1800).ConfigureAwait(false);
        if (curEpoch() == epoch && status.Peek() == StatusSaved) status.Value = StatusIdle;
    }
}
