using System.Collections.Generic;
using System.Linq;
using Wavee;
using Wavee.Backend;
using Wavee.Backend.Playlists;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// SLOT → MUTATION. The published <c>SidebarDropSlot</c> resolves to a <c>(RootlistItemRef, RootlistDropPlacement)</c>
/// pair, and this pins what that pair actually DOES to the marker stream — by applying the built op and reading the
/// resulting order back, because the op alone says nothing about where a row lands. The anchor does.
///
/// <para>D2 is the reason this file exists: "after the last child of a folder" used to be expressed against the CHILD,
/// whose exclusive range end is still inside the folder — so the gesture that visibly meant "take it out" put it back
/// in, while the visually adjacent gesture one pixel lower took it out. Same neighbourhood, opposite result, identical
/// cue, and no test anywhere.</para>
/// </summary>
public class RootlistSlotToOpTests
{
    //  a · [g "Chill": b, c] · d · [h "Trailing": e]
    static IReadOnlyList<RootlistEntry> Entries() => RootlistTreeBuilder.EntriesFromUris(new[]
    {
        "spotify:playlist:a",
        "spotify:start-group:g:Chill",
        "spotify:playlist:b",
        "spotify:playlist:c",
        "spotify:end-group:g",
        "spotify:playlist:d",
        "spotify:start-group:h:Trailing",
        "spotify:playlist:e",
        "spotify:end-group:h",
    });

    static RootlistItemRef Pl(string slug) => new("spotify:playlist:" + slug, IsFolder: false);
    static RootlistItemRef Folder(string id) => new(id, IsFolder: true);

    /// <summary>Apply the move to the marker stream and return the resulting uris, shortened for readability.</summary>
    static List<string> Apply(RootlistItemRef source, RootlistItemRef target, RootlistDropPlacement placement)
    {
        var entries = Entries();
        Assert.True(RootlistOps.TryBuildMove(entries, source, target, placement, out var op, out var reason),
                    $"expected a buildable move, got {reason}");
        var list = entries.Select(e => new PlaylistMember("", e.Uri, null, 0)).ToList();
        PlaylistDiffApplier.Apply(list, new[] { op! });
        return list.Select(Short).ToList();
    }

    static string Short(PlaylistMember m) => m.ItemUri switch
    {
        var u when u.StartsWith("spotify:start-group:") => "[" + u.Split(':')[2],
        var u when u.StartsWith("spotify:end-group:") => u.Split(':')[2] + "]",
        var u => u.Split(':')[2],
    };

    // ── the D2 fix ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OutdentSlot_LandsOutsideTheFolder_NotBackInsideIt()
    {
        // The slot: After(c) at a REDUCED depth ⇒ the target is the ANCESTOR FOLDER, not the child. Identical shape to
        // FolderActions.MoveOut, which is exactly the point — the drag and the menu verb do the same thing.
        Assert.Equal(new[] { "a", "[g", "b", "g]", "c", "d", "[h", "e", "h]" },
                     Apply(Pl("c"), Folder("g"), RootlistDropPlacement.After));
    }

    [Fact]
    public void ExpressingTheSameGestureAgainstTheChild_KeepsItInside()
    {
        // The bug, pinned as a fact: After(c) expressed against C ITSELF resolves to c's own position, which is why the
        // old gesture appeared to do nothing at all. This is the shape the resolver must NEVER produce for an outdent —
        // and it is named now instead of failing silently.
        Assert.Equal(RootlistMoveCheck.SameItem,
                     RootlistOps.CheckMove(Entries(), Pl("c"), Pl("c"), RootlistDropPlacement.After));
        // A SIBLING filed after c stays inside the folder, which is the correct reading of that slot at full depth.
        Assert.Equal(new[] { "a", "[g", "b", "c", "d", "g]", "[h", "e", "h]" },
                     Apply(Pl("d"), Pl("c"), RootlistDropPlacement.After));
    }

    // ── the end of the list ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EndOfListSlot_OnATrailingFolder_LandsAfterItsEndMarker()
    {
        // The TreeEnd row files against the last TOP-LEVEL entry — here a folder — and After uses its EXCLUSIVE range
        // end, so the item clears the whole subtree instead of becoming its last child.
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
        // The precise "make it the folder's FIRST item" slot. `Inside` cannot express it — that appends last.
        Assert.Equal(new[] { "a", "[g", "d", "b", "c", "g]", "[h", "e", "h]" },
                     Apply(Pl("d"), Pl("b"), RootlistDropPlacement.Before));
        Assert.Equal(new[] { "a", "[g", "b", "c", "d", "g]", "[h", "e", "h]" },
                     Apply(Pl("d"), Folder("g"), RootlistDropPlacement.Inside));
    }

    // ── the reasons, which used to be one silent `false` ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("b", "c", RootlistDropPlacement.Before, RootlistMoveCheck.NoOp)]   // before the row right after me
    [InlineData("c", "b", RootlistDropPlacement.After, RootlistMoveCheck.NoOp)]    // after the row right before me
    [InlineData("a", "d", RootlistDropPlacement.After, RootlistMoveCheck.Ok)]
    [InlineData("missing", "d", RootlistDropPlacement.After, RootlistMoveCheck.Missing)]
    [InlineData("a", "missing", RootlistDropPlacement.After, RootlistMoveCheck.Missing)]
    public void CheckMove_NamesWhyItRefused(string source, string target, RootlistDropPlacement placement,
                                            RootlistMoveCheck expected)
    {
        Assert.Equal(expected, RootlistOps.CheckMove(Entries(), Pl(source), Pl(target), placement));
    }

    [Fact]
    public void CheckMove_DistinguishesACycleFromANoOp()
    {
        // Both used to be the same `return false` three layers below the pointer, so a folder dropped into its own
        // child showed "Move into B" and then did nothing at all (D8).
        Assert.Equal(RootlistMoveCheck.Cycle,
                     RootlistOps.CheckMove(Entries(), Folder("g"), Pl("b"), RootlistDropPlacement.Before));
        Assert.Equal(RootlistMoveCheck.Cycle,
                     RootlistOps.CheckMove(Entries(), Folder("g"), Pl("c"), RootlistDropPlacement.After));
        Assert.Equal(RootlistMoveCheck.SameItem,
                     RootlistOps.CheckMove(Entries(), Folder("g"), Folder("g"), RootlistDropPlacement.Inside));
        // `Inside` a leaf is not a placement the marker stream can express at all.
        Assert.Equal(RootlistMoveCheck.Invalid,
                     RootlistOps.CheckMove(Entries(), Pl("a"), Pl("d"), RootlistDropPlacement.Inside));
    }

    [Fact]
    public void TheFourArgOverloadStillAnswersTheSameQuestion()
    {
        // Kept verbatim for PlaylistMutationSource and PlaylistMoveOpsTests: the reason is additive, never a rewrite.
        Assert.True(RootlistOps.TryBuildMove(Entries(), Pl("a"), Pl("d"), RootlistDropPlacement.After, out var op));
        Assert.NotNull(op);
        Assert.False(RootlistOps.TryBuildMove(Entries(), Pl("b"), Pl("c"), RootlistDropPlacement.Before, out var none));
        Assert.Null(none);
    }
}
