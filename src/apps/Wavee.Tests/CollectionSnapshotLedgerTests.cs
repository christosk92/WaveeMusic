using System;
using System.Collections.Generic;
using Wavee.Backend.Collections;
using Xunit;

namespace Wavee.Tests;

// The verified-snapshot ledger: a page walk has to prove its own completeness before anything may be swept or a sync
// token stored. Pure — no store, no wire.
public class CollectionSnapshotLedgerTests
{
    static CollectionItem Item(string uri, long at = 1000, bool removed = false) => new(uri, removed, at);
    static IReadOnlyList<CollectionItem> Items(params string[] uris) { var l = new List<CollectionItem>(); foreach (var u in uris) l.Add(Item(u)); return l; }

    [Fact]
    public void SinglePage_TerminalWithToken_IsVerified()
    {
        var ledger = new CollectionSnapshotLedger("collection", 5_000);
        ledger.AddPage(Items("spotify:track:a", "spotify:album:b"), nextPageToken: "", syncToken: "tok");

        Assert.Equal(SnapshotVerdict.Verified, ledger.Verdict);
        Assert.True(ledger.IsVerified);
        Assert.Equal(1, ledger.Pages);
        Assert.Equal(2, ledger.ItemCount);
        Assert.Equal("tok", ledger.Token);
        Assert.Equal("first", ledger.TokenSource);
        Assert.Equal(5_000, ledger.StartedAtMs);
        Assert.Equal("collection", ledger.WireSet);
    }

    [Fact]
    public void NoTerminalPage_IsNoTerminal()
    {
        var ledger = new CollectionSnapshotLedger("collection", 0);
        ledger.AddPage(Items("spotify:track:a"), nextPageToken: "p2", syncToken: "tok");

        Assert.Equal(SnapshotVerdict.NoTerminal, ledger.Verdict);
        Assert.False(ledger.TerminalSeen);
    }

    [Fact]
    public void TerminalWithoutAnyToken_IsNoToken()
    {
        var ledger = new CollectionSnapshotLedger("collection", 0);
        ledger.AddPage(Items("spotify:track:a"), nextPageToken: "p2", syncToken: null);
        ledger.AddPage(Items("spotify:track:b"), nextPageToken: "", syncToken: "");

        Assert.Equal(SnapshotVerdict.NoToken, ledger.Verdict);
        Assert.Null(ledger.Token);
        Assert.Equal("none", ledger.TokenSource);
    }

    [Fact]
    public void UriOnTwoPages_IsOverlap_AndCountsDuplicates()
    {
        var ledger = new CollectionSnapshotLedger("collection", 0);
        ledger.AddPage(Items("spotify:track:a", "spotify:track:b"), "p2", "tok");
        ledger.AddPage(Items("spotify:track:b", "spotify:track:c"), "", "tok2");

        Assert.Equal(SnapshotVerdict.Overlap, ledger.Verdict);
        Assert.Equal(1, ledger.Duplicates);
        Assert.Equal(3, ledger.ItemCount);            // b counted once
        Assert.Equal(3, ledger.Items.Count);
    }

    [Fact]
    public void Token_IsTheFirstPagesToken_NotTheLast()
    {
        // The earliest token is the conservative cursor: the next delta re-ships anything that changed while later
        // pages were walking, and re-applying is idempotent.
        var ledger = new CollectionSnapshotLedger("collection", 0);
        ledger.AddPage(Items("spotify:track:a"), "p2", "tok-first");
        ledger.AddPage(Items("spotify:track:b"), "p3", "tok-mid");
        ledger.AddPage(Items("spotify:track:c"), "", "tok-last");

        Assert.Equal("tok-first", ledger.Token);
        Assert.Equal(1, ledger.TokenPage);
        Assert.Equal("first", ledger.TokenSource);
        Assert.Equal(3, ledger.Pages);
    }

    [Fact]
    public void TokenStampedLate_IsStillAccepted_AndSaysWhichPage()
    {
        var ledger = new CollectionSnapshotLedger("collection", 0);
        ledger.AddPage(Items("spotify:track:a"), "p2", null);
        ledger.AddPage(Items("spotify:track:b"), "", "tok-2");

        Assert.Equal(SnapshotVerdict.Verified, ledger.Verdict);
        Assert.Equal("tok-2", ledger.Token);
        Assert.Equal("page2", ledger.TokenSource);
    }

    [Fact]
    public void EmptyNonTerminalPages_AreCounted_ButNotAVerdict()
    {
        var ledger = new CollectionSnapshotLedger("collection", 0);
        ledger.AddPage(Items("spotify:track:a"), "p2", "tok");
        ledger.AddPage(Array.Empty<CollectionItem>(), "p3", null);
        ledger.AddPage(Items("spotify:track:b"), "", null);

        Assert.Equal(1, ledger.EmptyNonTerminalPages);
        Assert.Equal(SnapshotVerdict.Verified, ledger.Verdict);
    }

    [Fact]
    public void RemovedItems_AreNeverPartOfTheSnapshot()
    {
        var ledger = new CollectionSnapshotLedger("collection", 0);
        ledger.AddPage(new[] { Item("spotify:track:a"), Item("spotify:track:gone", removed: true) }, "", "tok");

        Assert.Equal(1, ledger.ItemCount);
        Assert.False(ledger.Contains("spotify:track:gone"));
        Assert.True(ledger.Contains("spotify:track:a"));
    }

    [Fact]
    public void UrisFor_SplitsTheSharedWireSetByPrefix_AndReturnsEverythingForNull()
    {
        var ledger = new CollectionSnapshotLedger("collection", 0);
        ledger.AddPage(Items("spotify:track:a", "spotify:album:b", "spotify:track:c"), "", "tok");

        Assert.Equal(new HashSet<string> { "spotify:track:a", "spotify:track:c" }, ledger.UrisFor("spotify:track:"));
        Assert.Equal(new HashSet<string> { "spotify:album:b" }, ledger.UrisFor("spotify:album:"));
        Assert.Equal(3, ledger.UrisFor(null).Count);
    }

    [Fact]
    public void AddPage_AfterTerminal_Throws()
    {
        var ledger = new CollectionSnapshotLedger("collection", 0);
        ledger.AddPage(Items("spotify:track:a"), "", "tok");
        Assert.Throws<InvalidOperationException>(() => ledger.AddPage(Items("spotify:track:b"), "", "tok"));
    }
}
