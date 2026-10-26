// ── Theming/Contracts/SurfaceStyle.cs — what a structure supplies for the media surface (themes plan 6.1, T8c) ──────────────────
//
// `Controls.Surface(CardData, SurfaceShape, width)` is THE card, tile and row of the app (Home's shelves, the library grids, search, the
// queue's rows, the rail's tiles). Its ONE host (`Controls.SurfaceHost`, Platform\Surface.Host.cs) keeps everything that is not a
// picture: the hot fold (hover, focus-within, the slot's focus bit), what is mounted when (the lazy chrome and the now-playing overlay),
// who owns the click, the focus and the roving tab stop, the drag, the drop, the menu, the title tooltip and every handler as a
// trampoline into the newest props. The structure registers a `SurfaceStyle` under `StyleKeys.Surface` and the host composes the
// tree from it while it renders, so its subscriptions, keys and effect timing are what they were.
//
// WHAT STAYED CORE. The seed face, the shelf gutter, the drag-arm barrier, a card's inner width and the row title's rung
// (`SurfaceParts`): host-free callers build them with no registry (a skeleton source, a page's placeholder, `Controls.Surface`'s
// `SkeletonOverride`), and the seed IS the live surface's measured extent in bones (`SurfaceGeometry`). A host mounted where no
// registry provides the key draws nothing (logged once, `ListStyleLookup`).

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>Draws the shell: a ZStack of [plate, <paramref name="content"/>] with the corners, the stroke, the role, the tab stop and
/// the cursor the ownership rule decides. The host adds the handlers with <c>with</c>.</summary>
internal delegate BoxEl SurfaceShellTemplate(Element content, in SurfaceShape s, in SurfaceOwnership mode, string uri);

/// <summary>Draws the selected skin on the shell root (<paramref name="accent"/>, read now: the host calls this in its render, so a palette
/// landing repaints the one selected surface; null is the theme accent).</summary>
internal delegate BoxEl SurfaceSelectedTemplate(BoxEl shell, Func<ColorF>? accent);

/// <summary>Draws the STACK body (a shelf or grid card): the cover stack (art, the now-playing overlay, the corner "...") over the label
/// block. <paramref name="labels"/> is the host's cached label block (built here when null). <paramref name="liveWidth"/> is the shelf
/// card's width as the host's signal (null on a grid card): a shelf body BINDS its sizes to it, so a new card width (every tick of a
/// docking pane's reflow) re-lays the card out and re-renders nothing (audit B3bc). <paramref name="width"/> is its value now.</summary>
internal delegate Element SurfaceStackBodyTemplate(Controls.CardData d, in SurfaceShape s, float width, IReadSignal<float>? liveWidth,
                                                   bool overlay, bool chrome, bool hasMenu, Action? onPlay, Element? labels);

/// <summary>Draws the ROW body: the art square, the text column and the trailing cluster, with the "..." as a hover overlay or a
/// reserved lane. <paramref name="text"/> is the host's cached text column (built here when null).</summary>
internal delegate Element SurfaceRowBodyTemplate(Controls.CardData d, in SurfaceShape s, bool overlay, bool chrome, bool hasMenu,
                                                 Action? onPlay, Element? text);

/// <summary>Draws a stack's label block (<paramref name="inner"/> is the shelf's inner width, NaN on a fluid grid card).</summary>
internal delegate Element SurfaceLabelsTemplate(Controls.CardData d, in SurfaceShape s, float inner);

/// <summary>Draws a row's text column; null for a label-less shape.</summary>
internal delegate Element? SurfaceRowTextTemplate(Controls.CardData d, in SurfaceShape s);

/// <summary>The structure's style for the media surface (<c>StyleKeys.Surface</c>).</summary>
public sealed class SurfaceStyle
{
    internal SurfaceStyle(SurfaceShellTemplate shell, SurfaceSelectedTemplate selected, SurfaceStackBodyTemplate stackBody,
                          SurfaceRowBodyTemplate rowBody, SurfaceLabelsTemplate labels, SurfaceRowTextTemplate rowText)
    {
        Shell = shell;
        Selected = selected;
        StackBody = stackBody;
        RowBody = rowBody;
        Labels = labels;
        RowText = rowText;
    }

    /// <summary>The shell.</summary>
    internal SurfaceShellTemplate Shell { get; }

    /// <summary>The selected skin.</summary>
    internal SurfaceSelectedTemplate Selected { get; }

    /// <summary>The stack body.</summary>
    internal SurfaceStackBodyTemplate StackBody { get; }

    /// <summary>The row body.</summary>
    internal SurfaceRowBodyTemplate RowBody { get; }

    /// <summary>The stack's label block.</summary>
    internal SurfaceLabelsTemplate Labels { get; }

    /// <summary>The row's text column.</summary>
    internal SurfaceRowTextTemplate RowText { get; }
}
