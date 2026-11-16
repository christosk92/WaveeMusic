// ── Home/Board/Hosts/HomeBoardMount.cs — the board's mount and its inspector seam (pinned-home-tech.md 1.5, 3.2) ──────────────────
//
// Role: CORE. The page mounts `Home.Board` through this component, so every theme's board registers the same way: while it is mounted it is
// `HomeBoardInspect.Current` (the `home do=` verbs, pin-from-anywhere's confirmation and reveal on Home) and its grid is the "home:board" pack
// the `pack.hole` lint reads. It draws nothing of its own (WVTH008).

using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using Wavee.Theming.Contracts;
using Slots = FluentGpu.Theming.Slots;

namespace Wavee;

/// <summary>The board's props as the mount sees them (by reference: the host builds one record per binding).</summary>
public sealed record HomeBoardMountProps(HomeBoardProps Board, HomeBoardHost Host);

/// <summary>Mounts `Home.Board` and registers the board with the inspector while it is mounted.</summary>
[CoreHost]
public sealed class HomeBoardMount : Component
{
    public override Element Render()
    {
        var p = UseProps<HomeBoardMountProps>();
        var host = p.Host;
        var grid = p.Board.Grid;
        UseLayoutEffect(() =>
        {
            HomeBoardInspect.Register(host.Inspector);
            HomeBoardInspect.RegisterPack(HomeBoardKeys.Board, grid);
            return () =>
            {
                HomeBoardInspect.Unregister(host.Inspector);
                HomeBoardInspect.UnregisterPack(grid);
            };
        }, DepKey.Empty);
        return Slots.View(ViewKeys.HomeBoard, p.Board);
    }
}

/// <summary>The board as the inspector drives it: the view state, the live pack and the same verbs the themes call.</summary>
internal sealed class HomeBoardInspector : IHomeBoardInspect
{
    readonly HomeBoardHost _host;

    internal HomeBoardInspector(HomeBoardHost host) => _host = host;

    HomeBoardView View => _host.View;
    IHomeBoardVerbs Verbs => _host.Verbs;
    PackGridController Grid => _host.Grid;

    public string Theme => Diagnostics.InspectKit.ActiveThemeId;
    public bool Editing => View.Editing.Peek();
    public string? SelectedId => View.SelectedId.Peek();
    public string? OpenId => View.OpenId.Peek();

    public string AddSurface => View.Add.Peek().Kind switch
    {
        HomeAddSurfaceKind.Band => "band", HomeAddSurfaceKind.Row => "row", HomeAddSurfaceKind.AllItems => "allitems",
        HomeAddSurfaceKind.Find => "find", _ => "closed",
    };

    public string AddKind => View.Add.Peek().Arg;
    public string Query => View.Query.Peek();

    public IReadOnlyList<HomeCandidate> Results
    {
        get
        {
            var all = HomeCatalog.All(HomeBoardStore.Instance.Doc.Peek(), Model, HomeSourceRegistry.Default);
            string q = Query.Trim();
            return q.Length > 0 ? HomeCatalog.Find(all, q) : all;
        }
    }

    public BoardModel Model => _host.ModelNow;

    public int Columns => Grid.Metrics.Columns;

    public bool TryRect(string entryId, out RectF rect) => Grid.TryRect(HomeBoardKeys.Tile(entryId), out rect);

    public bool IsRealized(string entryId) => Grid.IsRealized(HomeBoardKeys.Tile(entryId));

    public bool TrySpan(string entryId, out int cols, out float rows, out string form)
    {
        cols = 0; rows = 0f; form = "";
        string key = HomeBoardKeys.Tile(entryId);
        var keys = Grid.Entries;
        var r = Grid.Layout;
        for (int i = 0; i < keys.Length && i < r.Count; i++)
        {
            if (Grid.KeyOf(i) != key) continue;
            cols = r.SpanCols[i];
            rows = r.SpanRows[i];
            form = HomeTileHost.FormOf(entryId) is { } f ? f.ToString() : "";
            return true;
        }
        return false;
    }

    public int Verify(List<string> faults)
    {
        Span<PackFault> buf = stackalloc PackFault[32];
        int n = Grid.Verify(buf);
        for (int i = 0; i < n && i < buf.Length; i++)
        {
            var f = buf[i];
            faults.Add(f.Kind + " group " + f.Group + " band " + f.Band + " entry " + (Grid.KeyOf(f.Entry) ?? f.Entry.ToString(System.Globalization.CultureInfo.InvariantCulture))
                       + " at " + f.Where.X.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "," + f.Where.Y.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
        }
        // Settled: every realized tile at its packed rect (more than half a DIP off is a fault).
        int count = Grid.Entries.Length;
        for (int i = 0; i < count; i++)
        {
            if (Grid.KeyOf(i) is not { } key || !Grid.IsRealized(key)) continue;
            if (!Grid.TryRect(key, out var packed) || !Grid.TryNodeRect(key, out var node) || !Grid.TryOrigin(out var o)) continue;
            float dx = node.X - o.X - packed.X, dy = node.Y - o.Y - packed.Y, dw = node.W - packed.W, dh = node.H - packed.H;
            if (MathF.Abs(dx) > 0.5f || MathF.Abs(dy) > 0.5f || MathF.Abs(dw) > 0.5f || MathF.Abs(dh) > 0.5f)
                faults.Add("OffRect " + key + " packed " + packed + " node " + node);
        }
        return faults.Count;
    }

    public string? Edit(bool on, string? itemId)
    {
        if (View.NoEditMode is { } refusal) return refusal;
        View.Editing.Value = on;
        View.SelectedId.Value = on ? itemId : null;
        return null;
    }

    public string? Surface(bool open, string kind, string? arg)
    {
        if (!open) { View.Add.Value = HomeAddSurface.Closed; View.Query.Value = ""; return null; }
        var k = kind switch
        {
            "band" => HomeAddSurfaceKind.Band, "allitems" => HomeAddSurfaceKind.AllItems, "addrow" or "row" => HomeAddSurfaceKind.Row,
            "find" => HomeAddSurfaceKind.Find, _ => HomeAddSurfaceKind.Closed,
        };
        if (k == HomeAddSurfaceKind.Closed) return "unknown surface '" + kind + "'";
        View.Add.Value = new HomeAddSurface(k, arg ?? "");
        return null;
    }

    public (IReadOnlyList<HomeCandidate> Hits, string Spoken) Search(string query)
    {
        View.Query.Value = query;
        var hits = Results;
        // The board's add surface speaks the count itself once typing pauses (each theme words it): the inspector only reports the sentence.
        string spoken = query.Trim().Length > 0 ? HomeCatalog.Spoken(query.Trim(), hits.Count) : "";
        return (hits, spoken);
    }

    public void Pin(string pinId, string name, string cover, string? groupId = null) => Verbs.Pin(pinId, name, cover, groupId);
    public void Unpin(string pinId) => Verbs.Unpin(pinId);
    public void Add(HomeCandidate candidate, int atEntryIndex = -1, HomeSize size = HomeSize.Auto) => Verbs.Add(candidate, atEntryIndex, size);
    public void Remove(string entryId) => Verbs.Remove(entryId);
    public void SetSize(string entryId, HomeSize size) => Verbs.SetSize(entryId, size);
    public void CycleSize(string entryId) => Verbs.CycleSize(entryId);
    public void MoveOnto(string entryId, string targetEntryId) => Verbs.MoveOnto(entryId, targetEntryId);
    public void MoveBy(string entryId, int delta) => Verbs.MoveBy(entryId, delta);
    public void MoveToEdge(string entryId, bool toStart) => Verbs.MoveToEdge(entryId, toStart);

    public void MoveToward(string entryId, int dx, int dy)
    {
        if (dy == 0) { if (dx != 0) Verbs.MoveBy(entryId, Math.Sign(dx)); return; }
        if (Grid.Neighbour(HomeBoardKeys.Tile(entryId), 0, Math.Sign(dy)) is { } key && key.StartsWith("home:tile:", StringComparison.Ordinal))
            Verbs.MoveOnto(entryId, key["home:tile:".Length..]);
    }

    public void Open(string entryId) => Verbs.Open(entryId);
    public void Close(bool refocus) => Verbs.Close(refocus);
    public void SetOption(string key, string value) => Verbs.SetOption(key, value);
    public void Undo() => Verbs.Undo();
    public void Redo() => Verbs.Redo();
    public void Reset(HomeResetScope scope, bool confirm) => Verbs.Reset(scope);
}
