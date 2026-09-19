using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Input;
using FluentGpu.Scene;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// F.1.4 — the 56-DIP compact rail's shared 40×40 TILE.
//
// Locked decision 7 makes the rail's CONTENT mode-specific (Classic shows pins → shortcuts → playlist covers; V3 shows
// its filter/entity strip; Curated shows the sections that opted into the rail). The TILE is shared, so all three rails
// have the same hit box, the same 6/8-DIP corner ladder, the same selected treatment and the same tooltip behaviour.
//
// Every visual below is copied verbatim from the landed WaveeSidebar CompactIcon / CompactArt / CompactDivider /
// CompactSkeleton so relocating those call sites is a pure refactor.

static class SidebarRailItem
{
    /// <summary>The rail tile box (40×40 inside the 56-DIP rail).</summary>
    public const float Box = 40f;

    /// <summary>The art edge inside an <see cref="Art"/> tile (the 2-DIP accent ring needs the 2-DIP inset).</summary>
    public const float ArtEdge = 36f;

    /// <summary>A GLYPH tile — a library shortcut, an app route, a folder pin. Carries the same 4-state
    /// selection-aware ramp as <see cref="SidebarEntityRow"/> (rest/selected × hover × pressed), so a selected rail tile
    /// darkens on hover instead of flattening.</summary>
    /// <param name="drop">Optional deposit destination (a FOLDER tile takes a rootlist filing — Into, and only Into).</param>
    /// <param name="dropActive">Its cue, BOUND: the rail subtree is memoized, so a cue that needed a render would never
    /// appear. An armed folder tile takes the accent plate the selection ladder already owns.</param>
    /// <param name="onRealized">The tile's realized node. A tile that ANCHORS something (a folder tile's side flyout)
    /// needs its handle, and the rail is built by a static helper inside a memoized subtree — it has no hooks, so the
    /// handle is handed back to a caller that outlives the rebuild (<c>SidebarPane.RegisterRailNode</c>).</param>
    /// <param name="menuOverlay">/<paramref name="menu"/> — right-click (and the context-menu key) on the tile. Supplied
    /// as a PAIR: a factory with nowhere to open is dead code, so a half-wired call attaches nothing rather than
    /// swallowing the gesture. The factory is invoked AT OPEN TIME, never at render time.</param>
    public static Element Icon(string key, string glyph, bool selected, Action? onClick, string? tooltip = null,
                               DropTargetSpec? drop = null, Func<bool>? dropActive = null,
                               Action<NodeHandle>? onRealized = null,
                               IOverlayService? menuOverlay = null, Func<ContextMenuModel?>? menu = null)
    {
        var tile = new BoxEl
        {
            OnRealized = onRealized,
            Key = key,
            Width = Box, Height = Box, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(6f),
            // The selection ladder (WaveeColors.SelectedRest): accent plate at rest, states only ever go UP.
            Fill = dropActive is null
                ? (selected ? WaveeColors.SelectedRest : ColorF.Transparent)
                : Prop.Of(() => dropActive() ? Tok.AccentDefault with { A = 0.35f }
                              : selected ? WaveeColors.SelectedRest : ColorF.Transparent),
            HoverFill = selected ? WaveeColors.SelectedHover : Tok.FillSubtleSecondary,
            PressedFill = selected ? WaveeColors.SelectedPressed : Tok.FillSubtleTertiary,
            Role = onClick is null ? AutomationRole.None : AutomationRole.Button,
            OnClick = onClick,
            DropTarget = drop,
            Children = [Ui.Icon(glyph, 16f, selected ? Tok.TextPrimary : Tok.TextSecondary)],
        };
        // ContextMenu.Attach CHAINS onto whatever OnRealized is already there (the SidebarEntityRow contract), so the
        // anchor registration above survives the attach.
        if (menuOverlay is not null && menu is not null) tile = tile.WithContextMenu(menuOverlay, menu);
        return Tip(tile, tooltip);
    }

    /// <summary>An ART tile — an entity pin or a playlist cover. <paramref name="art"/> comes from
    /// <see cref="SidebarCover"/> at <see cref="ArtEdge"/>; the 2-DIP accent border is the rail's selection cue (the
    /// item-owned selection pill is expanded-only).
    /// <para><paramref name="drop"/> makes the tile a real deposit destination while the pane is COLLAPSED — dragging a
    /// song at a 56-DIP rail used to have nowhere to go at all (see <c>SidebarPane._dragPeek</c>). <paramref name="dropActive"/>
    /// is its cue, and it must not be mistaken for selection: selection is a 2-DIP accent ring, so an armed drop adds an
    /// accent WASH inside it. The tile's only label is its tooltip, which a drag never shows — so the chip's caption is
    /// what actually names the target, and that is why it had to become legible in the same pass.</para></summary>
    public static Element Art(string key, Element art, bool selected, Action? onClick, string? tooltip = null,
                              DropTargetSpec? drop = null, Func<bool>? dropActive = null)
    {
        // BOUND, not re-rendered. The whole rail subtree is memoized by SidebarPane on (plan version, route, theme,
        // culture, band) — a drop cue that needed a render would simply never appear, because none of those move when the
        // pointer crosses a tile mid-drag. Prop.Of makes it a compositor-only channel: no render, no reconcile, no alloc.
        var tile = drop is null || dropActive is null
            ? new BoxEl
            {
                Key = key,
                Width = Box, Height = Box, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = CornerRadius4.All(8f),
                BorderColor = selected ? Tok.AccentDefault : ColorF.Transparent, BorderWidth = selected ? 2f : 0f,
                Role = onClick is null ? AutomationRole.None : AutomationRole.Button,
                OnClick = onClick,
                Children = [art],
            }
            : new BoxEl
            {
                Key = key,
                Width = Box, Height = Box, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = CornerRadius4.All(8f),
                // An armed drop borrows selection's accent ring, so a tile that is BOTH keeps one ring, not two. Only the
                // COLOUR is bindable (BorderWidth is a plain float), so the stroke is always 2 DIP and simply paints
                // nothing while transparent — visually identical to width 0, and it keeps the cue render-free.
                BorderColor = Prop.Of(() => dropActive() || selected ? Tok.AccentDefault : ColorF.Transparent),
                BorderWidth = 2f,
                Role = onClick is null ? AutomationRole.None : AutomationRole.Button,
                OnClick = onClick,
                DropTarget = drop,
                ZStack = true,
                Children =
                [
                    art,
                    // The wash sits OVER the cover — a tile is all artwork, so there is no plate to tint — and is
                    // hit-transparent so it can never steal the tile's own drop hit. Always mounted with a BOUND fill
                    // (transparent at rest) rather than conditionally added: a structural change would need a render.
                    new BoxEl
                    {
                        Width = Box, Height = Box, Corners = CornerRadius4.All(8f), HitTestVisible = false,
                        Fill = Prop.Of(() => dropActive() ? Tok.AccentDefault with { A = 0.35f } : ColorF.Transparent),
                    },
                ],
            };
        return Tip(tile.Interactive(Interaction.Subtle), tooltip);
    }

    /// <summary>The short centred rule that separates rail bands (24×1 at <c>Tok.TextTertiary</c> A=0.3).</summary>
    public static Element Divider() => new BoxEl
    {
        Width = 24f, Height = 1f, Margin = new Edges4(0f, 4f, 0f, 4f), Fill = Tok.TextTertiary with { A = 0.3f },
    };

    /// <summary>A pending rail tile (the rail's shimmer placeholder).</summary>
    public static Element Skeleton() => new BoxEl
    {
        Width = Box, Height = Box, Corners = CornerRadius4.All(8f), Fill = Tok.FillSubtleSecondary,
    };

    /// <summary>The tooltip IS the tile's label — a 56-DIP rail has no room for text, so the row label must be reachable
    /// on hover/focus or the rail is unusable for anything but the five icons a user has memorised.</summary>
    static Element Tip(BoxEl tile, string? tooltip)
        => tooltip is { Length: > 0 } t ? ToolTip.Wrap(tile, t) : tile;
}
