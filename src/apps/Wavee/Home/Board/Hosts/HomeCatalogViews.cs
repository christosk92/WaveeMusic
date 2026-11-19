// ── Home/Board/Hosts/HomeCatalogViews.cs — the catalogue as the add surfaces read it (pinned-home-tech.md 1.4) ─────────────────────
//
// Role: CORE. `All` is a subscribing read (the document, the board as shown, the library); `Find` and `Count` are the catalogue's pure functions; a
// candidate's preview is the REAL tile at a size (`HomeTileHost` over a synthetic entry with no packed cell, at a fixed form), so the WinUI band's
// candidates are live tiles, not pictures of them.

using System.Collections.Generic;
using System.Collections.Immutable;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;

namespace Wavee;

/// <summary>Builds the board's <see cref="HomeCatalogView"/>.</summary>
public static class HomeCatalogViews
{
    /// <summary>The catalogue view over the board's model.</summary>
    public static HomeCatalogView Create(IReadSignal<BoardModel> model, IHomeBoardVerbs verbs, IOverlayService? overlay, HomeBoardView view)
    {
        var sources = HomeSourceRegistry.Default;
        return new HomeCatalogView
        {
            All = () => HomeCatalog.All(HomeBoardStore.Instance.Doc.Value, model.Value, sources),
            Find = HomeCatalog.Find,
            Count = HomeCatalog.Count,
            Sizes = c => sources.Of(c.Source)?.Sizes ?? ImmutableArray<HomeSize>.Empty,
            AutoSize = c => sources.Of(c.Source) is { } d && d.CanonicalAuto != HomeSize.Auto ? d.CanonicalAuto : HomeSize.S,
            Preview = (c, size, w, h) => Preview(c, size, sources, verbs, overlay, view),
        };
    }

    static Element Preview(HomeCandidate c, HomeSize size, HomeSourceRegistry sources, IHomeBoardVerbs verbs, IOverlayService? overlay, HomeBoardView view)
    {
        var def = sources.Of(c.Source);
        var kind = c.Source is SpotifySectionSource ? BoardEntryKind.FeedSection : BoardEntryKind.Item;
        var entry = new BoardEntry("preview:" + c.Id, kind, c.TargetGroupId, def, c.Source, size, SectionTemplate.CoverShelf, c.Title,
            false, false, false, c.Source is PinSource, false, -1);
        var form = size switch { HomeSize.L => HomeTileForm.L, HomeSize.S => HomeTileForm.S, _ => HomeTileForm.M };
        return HomeTileHost.For(entry, null, form switch
        {
            HomeTileForm.L => s_l, HomeTileForm.S => s_s, _ => s_m,
        }, verbs, overlay, view);
    }

    static readonly Func<FluentGpu.Controls.PackBox, HomeTileForm> s_s = static _ => HomeTileForm.S;
    static readonly Func<FluentGpu.Controls.PackBox, HomeTileForm> s_m = static _ => HomeTileForm.M;
    static readonly Func<FluentGpu.Controls.PackBox, HomeTileForm> s_l = static _ => HomeTileForm.L;
}
