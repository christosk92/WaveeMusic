// ── Wavee.ShellTests/FactIsolationTests.cs — what the host puts back between facts (ShellHost.ResetBetweenFacts) ───────────
//
// xUnit runs the classes in a new order on every run and the host is one process, so a fact starts from whatever the fact
// before it left. Production keeps some of that on purpose and the host puts it back before every fact. These facts leave
// that state behind themselves and run the reset, so a reset that stops covering it fails here, not as one random fact per run:
//
//   the deck    a stop keeps the stopped row (the bar holds its face): the next fact's bar drew the previous fact's track, and
//               tr26's video added the bar's video split (PlayerBarMountedTests' Classic template fact)
//   the queue   the played context's rows
//   the plays   a play stamps the recency the library panes sort on and the Up next page's history

using Wavee;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed class FactIsolationTests(ShellHost host)
{
    const string VideoTrack = "spotify:track:tr26";   // the fake album track with a video (the bar's video split)

    [Fact]
    public void A_fact_starts_on_the_boots_deck_whatever_the_fact_before_played()
    {
        host.Mounted(shell =>
        {
            shell.RunFrames(5);
            Play(VideoTrack, fromMs: 20_000);
            shell.RunFrames(10);
            Assert.False(Playback.Current.Peek().IsNone, "the fact played " + VideoTrack);
        });
        host.Run(() =>
        {
            Assert.False(Playback.Current.Peek().IsNone, "a stop keeps the played row: the state the reset exists for");
            Assert.NotEmpty(Shell.PlayLog.Entries);
        });

        host.ResetBetweenFacts();

        host.Run(() =>
        {
            Assert.True(Playback.Current.Peek().IsNone, "the next fact starts with nothing on the deck");
            Assert.True(Playback.CurrentId.Peek().IsEmpty);
            Assert.Equal(0, Queue.Count);
            Assert.Empty(Shell.PlayLog.Entries);
        });
        host.Mounted(shell =>
        {
            shell.RunFrames(10);
            Until(shell, () => !shell.Find("queue").IsNull, "the bar's row");
            Assert.True(shell.Find("video").IsNull, "the next fact's bar draws no video split");
        });
    }
}
