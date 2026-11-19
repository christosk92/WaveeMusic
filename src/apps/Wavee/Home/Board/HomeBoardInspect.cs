// ── Home/Board/HomeBoardInspect.cs: the seam the inspector's `home` verb drives a mounted board through ──────────────
//
// The board host (lane W's HomeBoardHost) registers one of these while a board is mounted (Register / Unregister): it is
// the same verbs the themes call (the members below that share IHomeBoardVerbs' signatures are those verbs), plus the
// view state and the live pack the inspector reads back. With no board mounted the inspector falls back to the store
// (the model and its undo are proved; the motion and the view state are not).

using System.Collections.Immutable;
using FluentGpu.Foundation;

namespace Wavee;

public interface IHomeBoardInspect
{
    // ── view state (HomeBoardView) ──
    string Theme { get; }
    bool Editing { get; }
    string? SelectedId { get; }
    string? OpenId { get; }
    /// <summary>"closed" | "band" | "row" | "allitems" | "find".</summary>
    string AddSurface { get; }
    /// <summary>The surface's argument: a selector kind, a pivot, a group id ("" for none).</summary>
    string AddKind { get; }
    string Query { get; }
    /// <summary>The candidates the open add surface lists now (its search applied).</summary>
    IReadOnlyList<HomeCandidate> Results { get; }
    BoardModel Model { get; }

    // ── the live pack ──
    int Columns { get; }
    bool TryRect(string entryId, out RectF rect);
    bool IsRealized(string entryId);
    /// <summary>The span the pack gave the entry (columns, rows) and the form its tile drew, when realized.</summary>
    bool TrySpan(string entryId, out int cols, out float rows, out string form);
    /// <summary>The pack's own proof (PackLayout.Verify over every group at the live width) plus, settled, every realized
    /// rect against its packed rect (a tile more than 0.5 DIP off is a fault). One line per fault.</summary>
    int Verify(List<string> faults);

    // ── acting (the verbs) ──
    /// <summary>Classic's Edit Home / Zune's customize; null when done, else the refusal ("winui has no edit mode").</summary>
    string? Edit(bool on, string? itemId);
    /// <summary>Open or close the theme's add surface (kind: band, allitems, addrow, find; arg: a selector kind, a pivot, a
    /// group id). Null when done, else the refusal.</summary>
    string? Surface(bool open, string kind, string? arg);
    /// <summary>Types into the open add surface's search; the hits and the sentence spoken.</summary>
    (IReadOnlyList<HomeCandidate> Hits, string Spoken) Search(string query);

    void Pin(string pinId, string name, string cover, string? groupId = null);
    void Unpin(string pinId);
    void Add(HomeCandidate candidate, int atEntryIndex = -1, HomeSize size = HomeSize.Auto);
    void Remove(string entryId);
    void SetSize(string entryId, HomeSize size);
    void CycleSize(string entryId);
    void MoveOnto(string entryId, string targetEntryId);
    void MoveBy(string entryId, int delta);
    void MoveToEdge(string entryId, bool toStart);
    /// <summary>Left / right: one step in order; up / down: onto the nearest tile (PackReorder.MoveToward).</summary>
    void MoveToward(string entryId, int dx, int dy);
    void Open(string entryId);
    void Close(bool refocus);
    void SetOption(string key, string value);
    void Undo();
    void Redo();
    /// <summary>Reset; Classic's "everything" asks in its bar: <paramref name="confirm"/> answers it.</summary>
    void Reset(HomeResetScope scope, bool confirm);

    /// <summary>Open <paramref name="entryId"/>'s options where its tile stands (a Settings "Also in" door, settings design 5.1): the board's own
    /// <see cref="Open"/>; a board that can also brings the tile into view.</summary>
    void OpenOptions(string entryId) => Open(entryId);

    /// <summary>The theme's way into changing Home ("Customize Home"): its edit mode (Classic's Edit Home, Zune's customize), or, where the
    /// theme has none (WinUI), its add band.</summary>
    void Customize()
    {
        if (Edit(true, null) is not null) Surface(true, "band", null);
    }
}

public static class HomeBoardInspect
{
    /// <summary>The mounted board, or null.</summary>
    public static IHomeBoardInspect? Current { get; private set; }

    public static void Register(IHomeBoardInspect board)
    {
        Current = board;
        if (OptionsOnMount is { } open)
        {
            OptionsOnMount = null;
            board.OpenOptions(open);
        }
        if (!CustomizeOnMount) return;
        CustomizeOnMount = false;
        board.Customize();
    }

    /// <summary>"Customize Home" asked from another page: the board enters the theme's way into changing Home when it next mounts.</summary>
    public static bool CustomizeOnMount { get; set; }

    /// <summary>A tile's options asked from another page (a Settings door, "Also in: Home › Daylist tile"): the entry whose options the board opens
    /// when it next mounts.</summary>
    public static string? OptionsOnMount { get; set; }

    public static void Unregister(IHomeBoardInspect board)
    {
        if (ReferenceEquals(Current, board)) Current = null;
    }

    static readonly List<(string Key, FluentGpu.Controls.PackGridController Grid)> s_packs = new();

    /// <summary>The pack grids mounted now, by debug key: the lint's pack.hole reads them. The board host registers its grid
    /// ("home:board") at mount and unregisters it at unmount.</summary>
    public static IReadOnlyList<(string Key, FluentGpu.Controls.PackGridController Grid)> Packs => s_packs;

    public static void RegisterPack(string key, FluentGpu.Controls.PackGridController grid)
    {
        UnregisterPack(grid);
        s_packs.Add((key, grid));
    }

    public static void UnregisterPack(FluentGpu.Controls.PackGridController grid)
    {
        for (int i = s_packs.Count - 1; i >= 0; i--) if (ReferenceEquals(s_packs[i].Grid, grid)) s_packs.RemoveAt(i);
    }
}
