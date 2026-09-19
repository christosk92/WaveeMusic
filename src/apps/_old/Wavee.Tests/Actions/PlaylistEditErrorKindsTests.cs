using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Wavee;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests.Actions;

/// <summary>
/// The playlist-edit failure map (plan P1.7). Two properties matter and both used to be violated: every failure has a
/// SENTENCE (the old map fell through to <c>ex.Message</c> — engine prose like "rootlist changes failed (409)" shown to
/// a listener as if it were advice), and the classification reads the TYPED failure rather than sniffing message text.
/// </summary>
public class PlaylistEditErrorKindsTests
{
    [Theory]
    [InlineData(PlaylistMutationFailure.Unknown)]
    [InlineData(PlaylistMutationFailure.Conflict)]
    [InlineData(PlaylistMutationFailure.Forbidden)]
    [InlineData(PlaylistMutationFailure.Deleted)]
    [InlineData(PlaylistMutationFailure.Offline)]
    [InlineData(PlaylistMutationFailure.Pending)]
    [InlineData(PlaylistMutationFailure.NotSupported)]
    public void EveryKind_RoundTripsFromItsTypedException(PlaylistMutationFailure kind)
    {
        var ex = new PlaylistMutationException(kind, "engine prose nobody should ever read");
        Assert.Equal(kind, PlaylistEditErrorKinds.KindOf(ex));
    }

    [Theory]
    [InlineData(PlaylistMutationFailure.Unknown)]
    [InlineData(PlaylistMutationFailure.Conflict)]
    [InlineData(PlaylistMutationFailure.Forbidden)]
    [InlineData(PlaylistMutationFailure.Deleted)]
    [InlineData(PlaylistMutationFailure.Offline)]
    [InlineData(PlaylistMutationFailure.Pending)]
    [InlineData(PlaylistMutationFailure.NotSupported)]
    public void EveryKind_HasANonEmptyKeyForEveryVerb(PlaylistMutationFailure kind)
    {
        foreach (PlaylistEditVerb verb in Enum.GetValues<PlaylistEditVerb>())
        {
            string key = PlaylistEditErrorKinds.KeyFor(kind, verb);
            Assert.False(string.IsNullOrWhiteSpace(key), $"{kind}/{verb} has no copy");
            Assert.Contains('.', key);                 // a loc KEY, never a literal sentence
        }
    }

    /// <summary>The one kind×verb cell that differs: a lost reorder race names the edit, so the user knows the ORDER is
    /// what did not stick (nothing was added and nothing was removed).</summary>
    [Fact]
    public void Conflict_HasItsOwnReorderSentence()
    {
        string generic = PlaylistEditErrorKinds.KeyFor(PlaylistMutationFailure.Conflict);
        string reorder = PlaylistEditErrorKinds.KeyFor(PlaylistMutationFailure.Conflict, PlaylistEditVerb.Reorder);
        Assert.NotEqual(generic, reorder);
        Assert.Equal(generic, PlaylistEditErrorKinds.KeyFor(PlaylistMutationFailure.Conflict, PlaylistEditVerb.Remove));
    }

    /// <summary>The second kind×verb cell (P2): a REORDER is the one verb a pending edit can be refused OUTRIGHT for.
    /// The wire names both the moved rows and their landing anchor by membership item_id, so a row whose id has not
    /// landed cannot be moved at all — "Saved on this device, still syncing" would then be a claim about an edit the
    /// backend never accepted. It borrows the drag chip's own refusal sentence so the two channels agree.</summary>
    [Fact]
    public void Pending_HasItsOwnReorderSentence()
    {
        string generic = PlaylistEditErrorKinds.KeyFor(PlaylistMutationFailure.Pending);
        string reorder = PlaylistEditErrorKinds.KeyFor(PlaylistMutationFailure.Pending, PlaylistEditVerb.Reorder);
        Assert.NotEqual(generic, reorder);
        Assert.Equal(Strings.Drag.StillSyncing, reorder);
        // Still informational: nothing was lost, the ids simply have not arrived.
        Assert.True(PlaylistEditErrorKinds.IsInformational(PlaylistMutationFailure.Pending));
        // Every other verb keeps the "kept on this device" sentence.
        Assert.Equal(generic, PlaylistEditErrorKinds.KeyFor(PlaylistMutationFailure.Pending, PlaylistEditVerb.Add));
    }

    /// <summary>THE regression this whole map exists for: an unrecognised exception must never become its own message.
    /// A raw <c>ex.Message</c> is engine prose (a status code, a url, a stack-shaped sentence) and it was the previous
    /// map's fallthrough for every failure the string sniffing did not recognise.</summary>
    [Theory]
    [InlineData("rootlist changes failed (409)")]
    [InlineData("permission base failed (403)")]
    [InlineData("Object reference not set to an instance of an object.")]
    public void Unknown_NeverYieldsTheExceptionMessage(string message)
    {
        var ex = new InvalidOperationException(message);
        Assert.Equal(PlaylistMutationFailure.Unknown, PlaylistEditErrorKinds.KindOf(ex));
        foreach (PlaylistEditVerb verb in Enum.GetValues<PlaylistEditVerb>())
            Assert.NotEqual(message, PlaylistEditErrorKinds.KeyFor(PlaylistMutationFailure.Unknown, verb));
    }

    /// <summary>The wiring stub (a build with no real playlist mutation source) is its own kind, not Unknown: "sign in
    /// to edit Spotify playlists" is actionable where "something went wrong" is not.</summary>
    [Fact]
    public void NotSupported_IsClassifiedFromTheBclType()
        => Assert.Equal(PlaylistMutationFailure.NotSupported,
                        PlaylistEditErrorKinds.KindOf(new NotSupportedException("Playlist editing is not available.")));

    [Fact]
    public void ATypedFailure_SurvivesBeingWrapped()
    {
        var inner = new PlaylistMutationException(PlaylistMutationFailure.Forbidden, "403");
        Assert.Equal(PlaylistMutationFailure.Forbidden, PlaylistEditErrorKinds.KindOf(new InvalidOperationException("wrapped", inner)));
        Assert.Equal(PlaylistMutationFailure.Forbidden, PlaylistEditErrorKinds.KindOf(new AggregateException(inner)));
    }

    [Fact]
    public void NullIsUnknown_NotACrash() => Assert.Equal(PlaylistMutationFailure.Unknown, PlaylistEditErrorKinds.KindOf(null));

    /// <summary>Offline / Pending are kept edits, not lost ones — the toast severity follows this, and dressing a
    /// queued-offline edit as an error told the user their change was gone when it was not.</summary>
    [Fact]
    public void OnlyTheKeptOutcomes_AreInformational()
    {
        Assert.True(PlaylistEditErrorKinds.IsInformational(PlaylistMutationFailure.Offline));
        Assert.True(PlaylistEditErrorKinds.IsInformational(PlaylistMutationFailure.Pending));
        foreach (var kind in new[] { PlaylistMutationFailure.Unknown, PlaylistMutationFailure.Conflict,
                                     PlaylistMutationFailure.Forbidden, PlaylistMutationFailure.Deleted,
                                     PlaylistMutationFailure.NotSupported })
            Assert.False(PlaylistEditErrorKinds.IsInformational(kind), $"{kind} is a lost edit");
    }

    /// <summary>Every key the map can return actually exists in the base catalog. The keys are consts, so a rename is
    /// caught by the compiler — a key DELETED from en-US.json is not, and would ship as a raw key string on screen.</summary>
    [Fact]
    public void EveryKey_ExistsInTheBaseCatalog()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(LocPath()));
        foreach (PlaylistMutationFailure kind in Enum.GetValues<PlaylistMutationFailure>())
            foreach (PlaylistEditVerb verb in Enum.GetValues<PlaylistEditVerb>())
            {
                string key = PlaylistEditErrorKinds.KeyFor(kind, verb);
                var node = doc.RootElement;
                foreach (var segment in key.Split('.'))
                    Assert.True(node.TryGetProperty(segment, out node), $"missing loc key: {key}");
                Assert.Equal(JsonValueKind.String, node.ValueKind);
            }
    }

    static string LocPath([CallerFilePath] string here = "")
    {
        string tests = Path.GetDirectoryName(Path.GetDirectoryName(here)!)!;      // …/Wavee.Tests
        string path = Path.Combine(Path.GetDirectoryName(tests)!, "Wavee", "assets", "loc", "en-US.json");
        Assert.True(File.Exists(path), $"base loc catalog not found: {path}");
        return path;
    }
}
