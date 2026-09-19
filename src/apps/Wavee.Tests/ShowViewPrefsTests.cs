// ── Wavee.Tests/ShowViewPrefsTests.cs — filter + sort per show, in one capped blob (podcast plan §5.2, §10; D-6) ────
//
// `ShowViewPrefs` owns the `podcast.views` format: `id:status:order;…`, most recent first, at most 64 shows. The blob is
// the rule's own OUTPUT, so the facts below read it back freely; malformed input never throws.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class ShowViewPrefsTests
{
    [Fact]
    public void RoundTrip()
    {
        string blob = ShowViewPrefs.Write("", "4rOoJ6Egrf8K2IrywzwOMk", status: 2, order: 1);
        Assert.Equal("4rOoJ6Egrf8K2IrywzwOMk:2:1", blob);
        Assert.Equal((2, 1), ShowViewPrefs.Read(blob, "4rOoJ6Egrf8K2IrywzwOMk"));
    }

    [Fact]
    public void AShowNeverWritten_ReadsTheDefaults()
    {
        Assert.Equal((0, 0), ShowViewPrefs.Read("a:2:1;b:3:0", "c"));
        Assert.Equal((0, 0), ShowViewPrefs.Read("", "c"));
        Assert.Equal((0, 0), ShowViewPrefs.Read(null, "c"));
    }

    [Fact]
    public void Write_MovesTheShowToTheFront_AndReplacesItsOldEntry()
    {
        string blob = "a:1:0;b:2:1;c:3:0";
        blob = ShowViewPrefs.Write(blob, "b", status: 0, order: 0);
        Assert.Equal("b:0:0;a:1:0;c:3:0", blob);
        Assert.Equal((0, 0), ShowViewPrefs.Read(blob, "b"));
        Assert.Equal((3, 0), ShowViewPrefs.Read(blob, "c"));
    }

    [Fact]
    public void Write_TrimsToTheCap_DroppingTheLeastRecent()
    {
        string blob = "";
        for (int i = 0; i < 70; i++) blob = ShowViewPrefs.Write(blob, "show" + i, i % 4, i % 2);

        var entries = blob.Split(';');
        Assert.Equal(ShowViewPrefs.Cap, entries.Length);
        Assert.Equal(64, ShowViewPrefs.Cap);
        Assert.StartsWith("show69:", entries[0]);
        Assert.StartsWith("show6:", entries[^1]);                      // 0-5 fell off the end
        Assert.Equal((0, 0), ShowViewPrefs.Read(blob, "show5"));
        Assert.Equal((69 % 4, 69 % 2), ShowViewPrefs.Read(blob, "show69"));
        Assert.Equal((6 % 4, 6 % 2), ShowViewPrefs.Read(blob, "show6"));
    }

    [Fact]
    public void Write_ToAShowAlreadyStored_AtTheCap_KeepsEveryOtherShow()
    {
        string blob = "";
        for (int i = 0; i < 64; i++) blob = ShowViewPrefs.Write(blob, "show" + i, 1, 0);
        blob = ShowViewPrefs.Write(blob, "show0", 3, 1);                // the oldest, touched again
        Assert.Equal(64, blob.Split(';').Length);
        Assert.StartsWith("show0:3:1;show63:", blob);
        Assert.Equal((1, 0), ShowViewPrefs.Read(blob, "show1"));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData(";;;")]
    [InlineData("abc:x:1")]
    [InlineData("abc:1")]
    [InlineData("abc:1:2:3")]
    [InlineData("abc:-1:0")]
    [InlineData("abc: 1:0")]
    [InlineData("abc:1:")]
    [InlineData(":1:0")]
    public void AMalformedBlob_ReadsTheDefaults(string blob)
        => Assert.Equal((0, 0), ShowViewPrefs.Read(blob, "abc"));

    [Fact]
    public void MalformedEntries_AreSkipped_AndDroppedByTheNextWrite()
    {
        string blob = "junk;abc:x:1;;abc:3:1;:2:2;def:1:1";
        Assert.Equal((3, 1), ShowViewPrefs.Read(blob, "abc"));
        Assert.Equal("xyz:2:0;abc:3:1;def:1:1", ShowViewPrefs.Write(blob, "xyz", 2, 0));
    }

    [Fact]
    public void AMalformedBlob_IsReplacedCleanlyByAWrite()
        => Assert.Equal("abc:1:1", ShowViewPrefs.Write("%%%;::;a:b:c", "abc", 1, 1));

    [Theory]
    [InlineData("spotify:show:4rOoJ6Egrf8K2IrywzwOMk")]                // a uri, not the base62 id
    [InlineData("a;b")]
    [InlineData("")]
    public void AnIdWithASeparator_IsRejected(string id)
    {
        const string blob = "a:1:1;b:2:0";
        Assert.False(ShowViewPrefs.IsValidId(id));
        Assert.Equal((0, 0), ShowViewPrefs.Read(blob, id));
        Assert.Same(blob, ShowViewPrefs.Write(blob, id, 3, 1));
    }

    [Fact]
    public void NegativeValues_AreStoredAsTheDefaults()
    {
        string blob = ShowViewPrefs.Write("", "abc", -3, -1);
        Assert.Equal("abc:0:0", blob);
        Assert.Equal((0, 0), ShowViewPrefs.Read(blob, "abc"));
    }

    [Fact]
    public void IdsCompareExactly_NotAsPrefixes()
    {
        const string blob = "abcd:3:1;abc:1:0";
        Assert.Equal((1, 0), ShowViewPrefs.Read(blob, "abc"));
        Assert.Equal((0, 0), ShowViewPrefs.Read(blob, "ab"));
        Assert.Equal((0, 0), ShowViewPrefs.Read(blob, "ABC"));
    }

    [Fact]
    public void ANullBlob_WritesAFreshOne()
        => Assert.Equal("abc:2:1", ShowViewPrefs.Write(null, "abc", 2, 1));
}
