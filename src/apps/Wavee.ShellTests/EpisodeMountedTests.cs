// ── Wavee.ShellTests/EpisodeMountedTests.cs — the episode family's views, mounted (themes plan 6.1, T9c) ─────────────────────────
//
// The episode page's slots, the reader and the discussion are the structure's since T9c; the page components, `PodcastReaderUI` and their signals,
// resources and verbs stay core (`Wavee.Tests` pins the pure ones: `DiscussionLayoutTests`, `PodcastReactionTests`, `EpisodeContractTests`).
// What only a mounted tree can say:
//
//   registered   Classic is the registered template of every key
//   thread       the thread stands its comments, then "Load more" in order; a comment card mounts the reply thread under the disclosure only
//                when the host handed it one
//   reader       the reader draws its scroll under the pivot band, hands every section's anchor to the host, and keeps the section order
//   chapters     one row per chapter, in order

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using Wavee.Themes.Classic;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.ShellTests;

public sealed class EpisodeMountedTests(ShellHost host)
{
    static ViewHarnessOptions UnderClassic() => new() { Composition = ClassicTheme.Create() };

    static Element Marker(string key) => new BoxEl { Key = key, Height = 24f };

    // ── registered ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Classic_is_the_registered_template_of_every_episode_key()
        => host.Run(() =>
        {
            (string Id, string Template)[] expected =
            [
                (ViewKeys.EpisodePrimary.Id, "classic.episode.primary"),
                (ViewKeys.EpisodeReader.Id, "classic.episode.reader"),
                (ViewKeys.EpisodeChapters.Id, "classic.episode.chapters"),
                (ViewKeys.EpisodeDiscussion.Id, "classic.episode.discussion"),
                (ViewKeys.EpisodeCommentCard.Id, "classic.episode.comment-card"),
                (ViewKeys.EpisodeReactionFlyout.Id, "classic.episode.reaction-flyout"),
            ];
            foreach (var (id, template) in expected)
                Assert.Equal(template, ThemeHost.Registry.TemplateIdOf(id));
        });

    // ── the thread ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_thread_stands_its_comments_then_load_more_in_order()
        => ViewHarness.With(host, ViewKeys.EpisodeDiscussion, new DiscussionThreadProps(
            "Comments (2)", [Marker("c:1"), Marker("c:2")], null, Marker("more"), null, null, null, Continuation: false), harness =>
        {
            harness.RunFrames(4);
            var first = harness.AbsoluteBounds(harness.Node("c:1"));
            var second = harness.AbsoluteBounds(harness.Node("c:2"));
            var more = harness.AbsoluteBounds(harness.Node("more"));
            Assert.True(second.Y >= first.Y + first.H - 0.5f, "the second comment stands under the first");
            Assert.True(more.Y >= second.Y + second.H - 0.5f, "load more stands under the last comment");
        }, UnderClassic());

    [Fact]
    public void A_continuation_page_is_the_list_alone()
        => ViewHarness.With(host, ViewKeys.EpisodeDiscussion, new DiscussionThreadProps(
            "Comments (9)", [Marker("c:1")], "No comments yet", null, null, "Comments are off", "Could not post", Continuation: true), harness =>
        {
            harness.RunFrames(4);
            Assert.True(harness.Contains("c:1"));
            // a closed note and the error are quiet lines under the list, never a composer
            Assert.Single(harness.FindPrefix("c:"));
        }, UnderClassic());

    [Fact]
    public void A_comment_card_mounts_the_reply_thread_only_when_the_host_hands_it_one()
    {
        var comment = new DiscussionComment("c:u", "Hello", "Ada", "", "", false, false, false, 2, [], false);
        var pill = new ReactionPillModel([PodcastReactionRules.NeutralFace], 0, false, true);
        CommentCardProps Card(Element? thread) => new(comment, false, false, static () => { }, pill, static () => { },
            static _ => { }, static () => false, static () => { }, static () => { }, thread);

        ViewHarness.With(host, ViewKeys.EpisodeCommentCard, Card(null), harness =>
        {
            harness.RunFrames(4);
            Assert.False(harness.Contains("replies"), "closed: no reply thread");
        }, UnderClassic());
        ViewHarness.With(host, ViewKeys.EpisodeCommentCard, Card(Marker("thread:body")), harness =>
        {
            harness.RunFrames(60);   // the drawer reveal runs out
            Assert.True(harness.Contains("replies"), "open: the reply thread's rail");
            Assert.True(harness.Contains("thread:body"));
        }, UnderClassic());
    }

    // ── the reader ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_reader_hands_every_section_anchor_to_the_host_and_keeps_the_section_order()
    {
        var anchors = new NodeHandle[4];
        var realized = new Action<NodeHandle>[4];
        for (int i = 0; i < 4; i++) { int index = i; realized[i] = h => anchors[index] = h; }
        var props = new EpisodeReaderProps(
            [static () => { }, static () => { }, static () => { }, static () => { }], new Signal<int>(0), realized,
            Marker("s:about"), Marker("s:related"), Marker("s:chapters"), Marker("s:transcript"), Marker("s:comments"),
            "k", "scroll:k", static _ => { }, new ScrollHandle());
        ViewHarness.With(host, ViewKeys.EpisodeReader, props, harness =>
        {
            harness.RunFrames(10);
            Assert.False(harness.Find("episode-scroll:k").IsNull, "the sections scroll under the band");
            Assert.All(anchors, anchor => Assert.False(anchor.IsNull));
            var about = harness.AbsoluteBounds(harness.Node("s:about"));
            var chapters = harness.AbsoluteBounds(harness.Node("s:chapters"));
            Assert.True(chapters.Y >= about.Y + about.H - 0.5f, "Chapters stands under About");
        }, UnderClassic());
    }

    // ── the chapters ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_chapters_list_has_one_row_per_chapter_in_order()
        => ViewHarness.With(host, ViewKeys.EpisodeChapters, new EpisodeChaptersProps(
            [new("ch:1", 0, "Opening", false), new("ch:2", 60000, "", true)], static _ => { }, new Signal<bool>(false)), harness =>
        {
            harness.RunFrames(4);
            var first = harness.AbsoluteBounds(harness.Node("ch:1"));
            var second = harness.AbsoluteBounds(harness.Node("ch:2"));
            Assert.True(second.Y >= first.Y + first.H - 0.5f, "the second chapter stands under the first");
        }, UnderClassic());
}
