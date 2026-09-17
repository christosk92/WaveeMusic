// ── Wavee.Tests/RootlistSlotToOpTests.cs — a resolved (target, placement) → the op → where the row ACTUALLY lands ───
//
// Restored from 0.2.9 (gap batch B2, G-043). The published drop slot resolves to a `(RootlistItemRef,
// RootlistDropPlacement)` pair, and this pins what that pair DOES to the marker stream — by building the op with the
// production writer (`Spotify.Encode.TryBuildMove`), applying it with the production local apply
// (`Spotify.Encode.ApplyLocally`, the server's positional MOV semantics) and reading the order back, because the op
// alone says nothing about where a row lands. 0.3's `SidebarDropTests` had to drop these ORDER facts while no writer
// existed; the writer exists now, and the last fact pins that it cannot disagree with the pane's legality authority
// (`RootlistOps.CheckMove`, Shell/Sidebar.cs) about any pair over this stream.
//
// D2 is why the file exists: "after the last child of a folder" expressed against the CHILD stays inside the folder,
// while the same gesture expressed against the FOLDER takes it out. Pure: no scope, no engine.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class RootlistSlotToOpTests
{
    //  a · [g "Chill": b, c] · d · [h "Trailing": e]
    static List<RootlistEntry> Entries() => Spotify.Encode.EntriesFromUris(
    [
        "spotify:playlist:a",
        "spotify:start-group:g:Chill",
        "spotify:playlist:b",
        "spotify:playlist:c",
        "spotify:end-group:g",
        "spotify:playlist:d",
        "spotify:start-group:h:Trailing",
        "spotify:playlist:e",
        "spotify:end-group:h",
    ]);

    static RootlistItemRef Pl(string slug) => new("spotify:playlist:" + slug, IsFolder: false);
    static RootlistItemRef Folder(string id) => new(id, IsFolder: true);

    static string[] Apply(RootlistItemRef source, RootlistItemRef target, RootlistDropPlacement placement)
    {
        var entries = Entries();
        Assert.True(Spotify.Encode.TryBuildMove(entries, source, target, placement, out var op, out var reason),
                    $"expected a buildable move, got {reason}");
        return Spotify.Encode.ApplyLocally(entries, [op]).Select(Short).ToArray();
    }

    static string Short(RootlistEntry e) => e.Kind switch
    {
        1 => "[" + Spotify.Encode.GroupIdOf(e.Uri),
        2 => Spotify.Encode.GroupIdOf(e.Uri) + "]",
        _ => e.Uri.Split(':')[2],
    };

    // ── the D2 fix ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OutdentSlot_LandsOutsideTheFolder_NotBackInsideIt()
    {
        // After(c) at a REDUCED depth ⇒ the target is the ANCESTOR FOLDER — the same shape as "Move out of Chill".
        Assert.Equal(new[] { "a", "[g", "b", "g]", "c", "d", "[h", "e", "h]" },
                     Apply(Pl("c"), Folder("g"), RootlistDropPlacement.After));
    }

    [Fact]
    public void ExpressingTheSameGestureAgainstTheChild_KeepsItInside()
    {
        Assert.False(Spotify.Encode.TryBuildMove(Entries(), Pl("c"), Pl("c"), RootlistDropPlacement.After, out _, out var reason));
        Assert.Equal(RootlistMoveCheck.SameItem, reason);
        // A SIBLING filed after c stays inside the folder — the correct reading of that slot at full depth.
        Assert.Equal(new[] { "a", "[g", "b", "c", "d", "g]", "[h", "e", "h]" },
                     Apply(Pl("d"), Pl("c"), RootlistDropPlacement.After));
    }

    // ── the end of the list ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EndOfListSlot_OnATrailingFolder_LandsAfterItsEndMarker()
    {
        Assert.Equal(new[] { "[g", "b", "c", "g]", "d", "[h", "e", "h]", "a" },
                     Apply(Pl("a"), Folder("h"), RootlistDropPlacement.After));
    }

    [Fact]
    public void EndOfListSlot_MovingAWholeFolder_TakesItsSubtreeWithIt()
    {
        Assert.Equal(new[] { "a", "d", "[h", "e", "h]", "[g", "b", "c", "g]" },
                     Apply(Folder("g"), Folder("h"), RootlistDropPlacement.After));
    }

    // ── the expanded header's bottom band ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ExpandedHeaderBottomBand_IsBeforeTheFoldersFirstChild()
    {
        Assert.Equal(new[] { "a", "[g", "d", "b", "c", "g]", "[h", "e", "h]" },
                     Apply(Pl("d"), Pl("b"), RootlistDropPlacement.Before));
        Assert.Equal(new[] { "a", "[g", "b", "c", "d", "g]", "[h", "e", "h]" },
                     Apply(Pl("d"), Folder("g"), RootlistDropPlacement.Inside));
    }

    // ── the reasons ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("b", "c", RootlistDropPlacement.Before, RootlistMoveCheck.NoOp)]
    [InlineData("c", "b", RootlistDropPlacement.After, RootlistMoveCheck.NoOp)]
    [InlineData("a", "d", RootlistDropPlacement.After, RootlistMoveCheck.Ok)]
    [InlineData("missing", "d", RootlistDropPlacement.After, RootlistMoveCheck.Missing)]
    [InlineData("a", "missing", RootlistDropPlacement.After, RootlistMoveCheck.Missing)]
    public void TryBuildMove_NamesWhyItRefused(string source, string target, RootlistDropPlacement placement,
                                               RootlistMoveCheck expected)
    {
        Spotify.Encode.TryBuildMove(Entries(), Pl(source), Pl(target), placement, out _, out var reason);
        Assert.Equal(expected, reason);
    }

    [Fact]
    public void TryBuildMove_DistinguishesACycleFromANoOp()
    {
        static RootlistMoveCheck Why(RootlistItemRef s, RootlistItemRef t, RootlistDropPlacement p)
        {
            Spotify.Encode.TryBuildMove(Entries(), s, t, p, out _, out var reason);
            return reason;
        }
        Assert.Equal(RootlistMoveCheck.Cycle, Why(Folder("g"), Pl("b"), RootlistDropPlacement.Before));
        Assert.Equal(RootlistMoveCheck.Cycle, Why(Folder("g"), Pl("c"), RootlistDropPlacement.After));
        Assert.Equal(RootlistMoveCheck.SameItem, Why(Folder("g"), Folder("g"), RootlistDropPlacement.Inside));
        Assert.Equal(RootlistMoveCheck.Invalid, Why(Pl("a"), Pl("d"), RootlistDropPlacement.Inside));
    }

    [Fact]
    public void AFolderMovesItsWholeSubtree_MarkersAndAll()
    {
        Assert.True(Spotify.Encode.TryBuildMove(Entries(), Folder("g"), Pl("d"), RootlistDropPlacement.After, out var op, out _));
        Assert.Equal((1, 4, 6), (op.FromIndex, op.Length, op.ToIndex));    // start marker, two rows, end marker
    }

    // ── the writer and the legality authority cannot disagree ────────────────────────────────────────────────────────

    [Fact]
    public void EveryPairOverTheStream_TheWriterAndCheckMoveGiveTheSameVerdict()
    {
        var entries = Entries();
        var refs = new List<RootlistItemRef> { Folder("g"), Folder("h") };
        foreach (var slug in new[] { "a", "b", "c", "d", "e" }) refs.Add(Pl(slug));
        foreach (var source in refs)
            foreach (var target in refs)
                foreach (var placement in new[] { RootlistDropPlacement.Before, RootlistDropPlacement.After, RootlistDropPlacement.Inside })
                {
                    Spotify.Encode.TryBuildMove(entries, source, target, placement, out _, out var built);
                    Assert.Equal(RootlistOps.CheckMove(entries, source, target, placement), built);
                }
    }
}
