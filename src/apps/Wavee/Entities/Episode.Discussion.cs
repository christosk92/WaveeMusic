using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Scene;
using Contracts = Wavee.Theming.Contracts;
using ViewSlots = FluentGpu.Theming.Slots;

namespace Wavee;

// The discussion's core half (themes plan 6.1, T9c): the thread, a comment card, the reaction flyout and their signals, resources, composer and
// reaction verbs, the reaction session and the overlay the flyout opens in. The PICTURES are Classic's (`Themes\Classic\Structure\Views\Episode\
// ClassicDiscussion.cs`: `Episode.Discussion`, `Episode.CommentCard`, `Episode.CommentSeed`, `Episode.LoadMore`, `Episode.ReactionFlyout`,
// `Episode.ReactionRoster`, `Episode.ReactionPeople`); a host builds one props record per render it makes and mounts the key.
internal static partial class PodcastReaderUI
{
    internal sealed record DiscussionProps(string Uri, bool Replies);
    internal sealed record CommentPageProps(string Uri, bool Replies, string Token, int Generation, Action Refresh);
    internal sealed record CommentProps(Spotify.Podcasts.Comment Comment, bool Reply, Action Refresh);

    /// <summary>The wire row as a template reads it.</summary>
    static Contracts.DiscussionComment Map(Spotify.Podcasts.Comment c) => new(c.Uri, c.Text, c.Author, c.Avatar, c.Created,
        c.Pending, c.Sensitive, c.Pinned, c.Replies, c.ReplyAvatars, c.ReplyLimit);

    static Element CommentSeedView(bool replies) => ViewSlots.View(Contracts.ViewKeys.EpisodeCommentSeed, new Contracts.CommentSeedProps(replies));

    internal sealed class Discussion : Component
    {
        readonly Signal<string> _text = new("");
        readonly Signal<bool> _busy = new(false);
        readonly Signal<string> _error = new("");
        readonly Signal<int> _generation = new(0);
        readonly Signal<bool> _consent = new(false);
        public override Element Render()
        {
            var p = UseProps<DiscussionProps>();
            var post = UsePost(); uint epoch = Entities.ScopeEpoch.Value;
            var consentKey = new SettingKey<bool>("podcast.commentConsent." + Entities.Current.Key.Account, false);
            UseEffect(() => _consent.Value = Platform.Settings.Get(consentKey), DepKey.From((int)epoch));
            var resource = UseResource(ct => Platform.Args.Fake
                    ? p.Replies ? Task.FromResult(PodcastReaderFixtures.Comments) : PodcastReaderFixtures.Answer(p.Uri, PodcastReaderFixtures.Comments, ct)
                    : Spotify.Podcasts.CommentsAsync(epoch, p.Uri, "", p.Replies, ct),
                new Spotify.Podcasts.Page<Spotify.Podcasts.Comment>([], "", 0, "", 0), epoch + ":" + p.Uri);
            return Skel.Region(resource.Loadable, () => CommentSeedView(p.Replies), Build,
                onFailed: () => Failed(Refresh));

            Element Build(Spotify.Podcasts.Page<Spotify.Podcasts.Comment> page)
            {
                if (!page.Ok) return Failed(Refresh);
                var cards = new Element[page.Items.Length];
                for (int i = 0; i < cards.Length; i++)
                    cards[i] = Embed.Comp(new CommentProps(page.Items[i], p.Replies, Refresh), static () => new CommentCard()) with { Key = page.Items[i].Uri };
                Element? more = page.NextToken.Length > 0
                    ? Embed.Comp(new CommentPageProps(p.Uri, p.Replies, page.NextToken, _generation.Value, Refresh), static () => new MoreComments())
                        with { Key = "more:" + _generation.Value + ":" + page.NextToken }
                    : null;
                bool eligible = DiscussionLayout.ShowComposer(p.Replies, Platform.Args.Fake, page.Eligibility);
                Contracts.DiscussionComposerProps? composer = null;
                string? closed = null;
                if (eligible)
                    composer = new Contracts.DiscussionComposerProps(_text,
                        Loc.Get(p.Replies ? Strings.Podcast.Reader.ReplyPlaceholder : Strings.Podcast.Reader.CommentPlaceholder),
                        DiscussionLayout.ShowConsent(eligible, _consent.Value), _consent, value => Platform.Settings.Set(consentKey, value),
                        Loc.Get(p.Replies ? Strings.Podcast.Reader.PostReply : Strings.Podcast.Reader.Submit),
                        DiscussionLayout.CanSubmit(_consent.Value, _busy.Value, _text.Value), Submit);
                else closed = Loc.Get(DiscussionLayout.AlreadyCommented(page.Eligibility) ? Strings.Podcast.Reader.OneComment
                    : DiscussionLayout.CommentsDisabled(page.Eligibility) ? Strings.Podcast.CommentsOff : Strings.Podcast.Reader.Eligibility);
                return ViewSlots.View(Contracts.ViewKeys.EpisodeDiscussion, new Contracts.DiscussionThreadProps(
                    p.Replies ? null : Loc.Get(Strings.Podcast.Reader.Comments) + " (" + page.Total + ")", cards,
                    page.Items.Length == 0 ? Loc.Get(Strings.Podcast.Reader.NoComments) : null, more, composer, closed,
                    _error.Value.Length > 0 ? _error.Value : null, Continuation: false));
            }

            void Refresh()
            {
                Spotify.Podcasts.InvalidateDiscussion();
                _generation.Value++;
                resource.Refresh();
            }
            async void Submit()
            {
                if (!DiscussionLayout.CanSubmit(_consent.Peek(), _busy.Peek(), _text.Peek())) return;
                _busy.Value = true; _error.Value = "";
                Spotify.Podcasts.Mutation result;
                try { result = await Spotify.Podcasts.CommentAsync(p.Uri, _text.Peek().Trim(), p.Replies, CancellationToken.None).ConfigureAwait(false); }
                catch { result = new(false, 0, true); }
                post(() =>
                {
                    if (epoch != Entities.ScopeEpoch.Peek()) return;
                    _busy.Value = false;
                    if (result.Success) _text.Value = "";
                    else _error.Value = Loc.Get(Strings.Podcast.Reader.MutationFailed);
                    // The response has no created ID; reconcile even an ambiguous result rather than retrying the write.
                    Refresh();
                });
            }
        }
    }

    internal sealed class MoreComments : Component
    {
        readonly Signal<bool> _open = new(false);
        public override Element Render()
        {
            var p = UseProps<CommentPageProps>();
            return _open.Value
                ? Embed.Comp(p, static () => new CommentPage())
                : ViewSlots.View(Contracts.ViewKeys.EpisodeLoadMore, new Contracts.LoadMoreProps(() => _open.Value = true, AlignStart: true));
        }
    }

    internal sealed class CommentPage : Component
    {
        public override Element Render()
        {
            var p = UseProps<CommentPageProps>(); uint epoch = Entities.ScopeEpoch.Value;
            var resource = UseResource(ct => Platform.Args.Fake ? Task.FromResult(PodcastReaderFixtures.Comments) : Spotify.Podcasts.CommentsAsync(epoch, p.Uri, p.Token, p.Replies, ct),
                new Spotify.Podcasts.Page<Spotify.Podcasts.Comment>([], "", 0, "", 0), epoch + ":" + p.Uri + ":" + p.Token);
            return Skel.Region(resource.Loadable, () => CommentSeedView(p.Replies), Build,
                onFailed: () => Failed(resource.Refresh));
            Element Build(Spotify.Podcasts.Page<Spotify.Podcasts.Comment> page)
            {
                if (!page.Ok) return Failed(resource.Refresh);
                var cards = new Element[page.Items.Length];
                for (int i = 0; i < cards.Length; i++)
                    cards[i] = Embed.Comp(new CommentProps(page.Items[i], p.Replies, p.Refresh), static () => new CommentCard()) with { Key = page.Items[i].Uri };
                Element? more = page.NextToken.Length > 0 && page.NextToken != p.Token
                    ? Embed.Comp(p with { Token = page.NextToken }, static () => new MoreComments()) with { Key = "more:" + page.NextToken }
                    : null;
                return ViewSlots.View(Contracts.ViewKeys.EpisodeDiscussion, new Contracts.DiscussionThreadProps(
                    null, cards, null, more, null, null, null, Continuation: true));
            }
        }
    }

    internal sealed class CommentCard : Component
    {
        readonly Signal<bool> _replies = new(false), _revealed = new(false);
        public override Element Render()
        {
            var p = UseProps<CommentProps>(); var c = p.Comment;
            uint epoch = Entities.ScopeEpoch.Value;
            var overlay = UseContext(Overlay.Service);
            var reactionAnchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            var sessionRef = UseRef<ReactionSession?>(null);
            var session = sessionRef.Value ??= new ReactionSession(c, epoch);
            UseEffect(() => session.Sync(c), DepKey.FromRef(c));
            UseEffect(() => { return () => handle.Value?.Close(); }, epoch);
            void Open(Ref<NodeHandle> anchor)
            {
                if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
                handle.Value = overlay.Open(() => anchor.Value,
                    () => Embed.Comp(new ReactionProps(session, p.Refresh), static () => new ReactionFlyout()),
                    FlyoutPlacement.BottomLeft,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup));
                handle.Value.ClosedAction = () => handle.Value = null;
            }
            var pill = PodcastReactionRules.Pill(session.Counts.Value, session.Current.Value, session.Total.Value);
            // Reply is the VERB, with no count in it: it opens the thread (where the reply composer lives); the disclosure toggles the same thread
            // for reading. A reply's own replies render flat, so only a top-level comment mounts one.
            Element? thread = _replies.Value && !p.Reply
                ? c.ReplyLimit
                    ? Embed.Comp(new CommentPageProps(c.Uri, true, "", 0, p.Refresh), static () => new CommentPage())
                    : Embed.Comp(new DiscussionProps(c.Uri, true), static () => new Discussion())
                : null;
            return ViewSlots.View(Contracts.ViewKeys.EpisodeCommentCard, new Contracts.CommentCardProps(
                Map(c), p.Reply, _revealed.Value, () => _revealed.Value = true, pill, () => Open(reactionAnchor),
                h => reactionAnchor.Value = h, () => _replies.Value, () => _replies.Value = true, () => _replies.Value = !_replies.Peek(), thread));
        }
    }

    internal sealed class ReactionSession(Spotify.Podcasts.Comment comment, uint epoch)
    {
        public readonly string Uri = comment.Uri;
        public readonly uint Epoch = epoch;
        public readonly Signal<string> Current = new(comment.MyReaction), Error = new("");
        public readonly Signal<int> Total = new(comment.Reactions), Revision = new(0);
        public readonly Signal<bool> Busy = new(false);
        /// <summary>The per-emoji breakdown the PILL stacks. The comments answer carries only a total and your own
        /// reaction, so this starts EMPTY and fills from the reactions roster — the canonical page the picker reads
        /// and every reaction writes back. Until then the pill shows your own emoji (or the neutral face) with the
        /// real total, rather than claiming emoji nobody sent.</summary>
        public readonly Signal<Spotify.Podcasts.ReactionCount[]> Counts = new([]);
        public void Sync(Spotify.Podcasts.Comment value)
        {
            if (Busy.Peek()) return;
            Current.Value = value.MyReaction;
            Total.Value = value.Reactions;
        }
    }

    internal sealed record ReactionProps(ReactionSession Session, Action Refresh);
    internal sealed record ReactionPageProps(string Uri, string Token, string Emoji, uint Epoch);
    internal sealed class ReactionFlyout : Component
    {
        readonly Signal<string> _filter = new("");
        public override Element Render()
        {
            var p = UseProps<ReactionProps>(); var session = p.Session;
            var post = UsePost();
            var viewport = UseContextSignal(Viewport.Size).Value;
            string filter = _filter.Value;
            int revision = session.Revision.Value;
            var resource = UseResource(ct => ReadReactionPage(session.Uri, "", filter, session.Epoch, ct), ReactionSeed,
                session.Epoch + ":" + session.Uri + ":" + filter + ":" + revision);
            UseSignalEffect(() =>
            {
                if (resource.Loadable.State.Value == (byte)LoadState.Ready && resource.Loadable.Value.Value is { Ok: true } page
                    && filter.Length == 0 && Entities.ScopeEpoch.Peek() == session.Epoch)
                {
                    session.Total.Value = page.Total;
                    session.Counts.Value = page.ReactionCounts;   // the pill's emoji, straight from the roster
                }
            });
            var palette = new Contracts.ReactionOption[PodcastReactionRules.Palette.Length];
            for (int index = 0; index < palette.Length; index++)
            {
                string emoji = PodcastReactionRules.Palette[index];
                palette[index] = new Contracts.ReactionOption(emoji, ReactionName(index),
                    PodcastReactionRules.Remove(session.Current.Value, emoji), !session.Busy.Value && !Platform.Args.Fake);
            }
            var roster = Skel.Region(resource.Loadable,
                () => ReactionRoster(ReactionSeed, filter, session, revision),
                page => page.Ok ? ReactionRoster(page, filter, session, revision) : Failed(resource.Refresh),
                onFailed: () => Failed(resource.Refresh));
            return ViewSlots.View(Contracts.ViewKeys.EpisodeReactionFlyout, new Contracts.ReactionFlyoutProps(
                palette, React, session.Error.Value, roster, viewport.Width, viewport.Height));

            async void React(string emoji)
            {
                if (session.Busy.Peek() || Entities.ScopeEpoch.Peek() != session.Epoch) return;
                session.Busy.Value = true; session.Error.Value = "";
                string current = session.Current.Peek();
                bool remove = PodcastReactionRules.Remove(current, emoji);
                Spotify.Podcasts.Mutation result;
                Spotify.Podcasts.Page<Spotify.Podcasts.Reaction>? canonical = null;
                try
                {
                    result = await Spotify.Podcasts.ReactionAsync(session.Uri, emoji, remove, CancellationToken.None).ConfigureAwait(false);
                    if (result.Success && Entities.ScopeEpoch.Peek() == session.Epoch)
                        canonical = await Spotify.Podcasts.ReactionsAsync(session.Uri, "", "", CancellationToken.None).ConfigureAwait(false);
                }
                catch { result = new(false, 0, true); }
                post(() =>
                {
                    if (Entities.ScopeEpoch.Peek() != session.Epoch) return;
                    session.Busy.Value = false;
                    if (result.Success)
                    {
                        session.Current.Value = remove ? "" : emoji;
                        if (canonical is { Ok: true })
                        {
                            session.Total.Value = canonical.Total;
                            session.Counts.Value = canonical.ReactionCounts;
                        }
                    }
                    else session.Error.Value = Loc.Get(Strings.Podcast.Reader.MutationFailed);
                    session.Revision.Value++;
                    p.Refresh();
                });
            }
        }

        Element ReactionRoster(Spotify.Podcasts.Page<Spotify.Podcasts.Reaction> page, string filter, ReactionSession session, int revision)
        {
            var filters = new List<Contracts.ReactionFilter>
            {
                new(Loc.Get(Strings.Podcast.Reader.ReactionAll) + " " + page.Total, "", filter == ""),
            };
            foreach (var count in page.ReactionCounts)
                if (count.Count > 0) filters.Add(new(count.Emoji + " " + count.Count, count.Emoji, filter == count.Emoji));
            return ViewSlots.View(Contracts.ViewKeys.EpisodeReactionRoster, new Contracts.ReactionRosterProps(
                filters.ToArray(), emoji => _filter.Value = emoji,
                ReactionPeople(page, new ReactionPageProps(session.Uri, "", filter, session.Epoch)),
                "reactions:" + session.Uri + ":" + filter + ":" + revision, "participants:" + filter + ":" + revision));
        }
    }

    static readonly Spotify.Podcasts.Page<Spotify.Podcasts.Reaction> ReactionSeed = new(
        [new("❤️", "Listener", "", ""), new("👍", "Listener", "", "")], "", 2, "", 200);

    static Task<Spotify.Podcasts.Page<Spotify.Podcasts.Reaction>> ReadReactionPage(string uri, string token, string emoji, uint epoch, CancellationToken ct)
    {
        if (Entities.ScopeEpoch.Peek() != epoch) return Task.FromResult(new Spotify.Podcasts.Page<Spotify.Podcasts.Reaction>([], "", 0, "", 409));
        return Platform.Args.Fake ? Task.FromResult(new Spotify.Podcasts.Page<Spotify.Podcasts.Reaction>([], "", 0, "", 200))
            : Spotify.Podcasts.ReactionsAsync(uri, token, emoji, ct);
    }

    static Element ReactionPeople(Spotify.Podcasts.Page<Spotify.Podcasts.Reaction> page, ReactionPageProps props)
    {
        var people = new Contracts.ReactionPerson[page.Items.Length];
        for (int i = 0; i < people.Length; i++) people[i] = new(page.Items[i].Emoji, page.Items[i].Author, page.Items[i].Avatar);
        Element? more = page.NextToken.Length > 0 && page.NextToken != props.Token
            ? Embed.Comp(props with { Token = page.NextToken }, static () => new MoreReactions()) with { Key = page.NextToken }
            : null;
        return ViewSlots.View(Contracts.ViewKeys.EpisodeReactionPeople, new Contracts.ReactionPeopleProps(people, more));
    }

    internal sealed class MoreReactions : Component
    {
        readonly Signal<bool> _open = new(false);
        public override Element Render()
        {
            var p = UseProps<ReactionPageProps>();
            return _open.Value ? Embed.Comp(p, static () => new ReactionPage())
                : ViewSlots.View(Contracts.ViewKeys.EpisodeLoadMore, new Contracts.LoadMoreProps(() => _open.Value = true, AlignStart: false));
        }
    }
    internal sealed class ReactionPage : Component
    {
        public override Element Render()
        {
            var p = UseProps<ReactionPageProps>();
            var resource = UseResource(ct => ReadReactionPage(p.Uri, p.Token, p.Emoji, p.Epoch, ct), ReactionSeed,
                p.Epoch + ":" + p.Uri + ":" + p.Token + ":" + p.Emoji);
            return Skel.Region(resource.Loadable, () => ReactionPeople(ReactionSeed, p),
                page => page.Ok ? ReactionPeople(page, p) : Failed(resource.Refresh), onFailed: () => Failed(resource.Refresh));
        }
    }

    static string ReactionName(int index) => Loc.Get(index switch
    {
        0 => Strings.Podcast.Reader.ReactionHeart, 1 => Strings.Podcast.Reader.ReactionLaugh,
        2 => Strings.Podcast.Reader.ReactionSmile, 3 => Strings.Podcast.Reader.ReactionSurprise,
        4 => Strings.Podcast.Reader.ReactionSad, 5 => Strings.Podcast.Reader.ReactionClap,
        6 => Strings.Podcast.Reader.ReactionFire, _ => Strings.Podcast.Reader.ReactionLike,
    });
}

// ── engine-free layout decisions (Wave E, plan §5.4) ────────────────────────────────────────────────────────────
//
// Split out of Discussion.Build so the composer/consent/eligibility branching is unit-tested directly
// (DiscussionLayoutTests.cs, against the captured vc4-275-comments.json fixture) without a live component tree.
// TOP-LEVEL and public: PodcastReaderUI itself is `internal`, and this assembly carries no InternalsVisibleTo
// (Controls.cs / Playlist.UI.cs), so a type Wavee.Tests must reach cannot live nested inside it.

/// <summary>Which rows indent, when the composer shows, and when the consent row shows above it — the three
/// decisions Episode.Discussion.cs's <c>Discussion</c>/<c>CommentCard</c> components read instead of inlining the
/// same branches. No FluentGpu type crosses this boundary.</summary>
public static class DiscussionLayout
{
    /// <summary>A comment row sits one avatar column deep exactly when it renders inside a reply thread
    /// (Discussion/CommentPage mounted with <c>Replies == true</c>) — replies never nest past this ONE indent,
    /// matching <c>ReplyThread</c>'s single rail (a reply's own replies still render flat under it).</summary>
    public static int IndentOf(bool isReplyLevel) => isReplyLevel ? 1 : 0;

    /// <summary>The composer (text box + submit) mounts when the surface is eligible to write: a reply is always
    /// open to whoever can already see the thread, a top-level comment additionally needs the discussion
    /// response's own eligibility (<see cref="PodcastReaderRules.CanComment"/>), and neither ever mounts under
    /// Fake demo data (there is no network to post to).</summary>
    public static bool ShowComposer(bool isReplyLevel, bool fake, string eligibility)
        => !fake && (isReplyLevel || PodcastReaderRules.CanComment(eligibility));

    /// <summary>The consent checkbox row shows above the composer only until the account has consented once —
    /// after that the composer alone is enough (consent is persisted per account, keyed on the Spotify account id).</summary>
    public static bool ShowConsent(bool showComposer, bool consented) => showComposer && !consented;

    /// <summary>"Post"/"Post reply" is enabled only with consent, non-empty text, and no write already in flight.</summary>
    public static bool CanSubmit(bool consented, bool busy, string text)
        => consented && !busy && !string.IsNullOrWhiteSpace(text);

    /// <summary>The account has already used its one comment on this episode — distinct from every other closed
    /// reason (<see cref="CommentsDisabled"/>), so the quiet text never claims comments are off when only THIS
    /// account is capped.</summary>
    public static bool AlreadyCommented(string eligibility) => eligibility == "ELIGIBILITY_STATUS_ALREADY_COMMENTED";

    /// <summary>The show/episode turned comments off entirely — distinct from a transport failure (that path never
    /// reaches this rule: <c>Discussion.Build</c> returns <c>Failed</c> + retry on <c>!page.Ok</c> before eligibility
    /// is ever read) and from <see cref="AlreadyCommented"/> (a per-account cap, not a closed episode).</summary>
    public static bool CommentsDisabled(string eligibility)
        => eligibility is "ELIGIBILITY_STATUS_DISABLED" or "ELIGIBILITY_STATUS_COMMENTS_DISABLED";

    /// <summary>The replies disclosure stacks at most this many repliers' avatars.</summary>
    public const int MaxReplyAvatars = 3;

    /// <summary>The replies disclosure exists only on a TOP-LEVEL comment that actually has replies \u2014 a reply's own
    /// replies render flat under it (<c>ReplyThread</c>'s single rail), and a comment with none shows no affordance.</summary>
    public static bool ShowReplies(bool isReplyLevel, int replies) => !isReplyLevel && replies > 0;

    /// <summary>How many avatars the stack draws: capped at <see cref="MaxReplyAvatars"/>, never more than the answer
    /// gave us, and never more than there are replies (a wire that over-lists must not draw a fourth face).</summary>
    public static int ReplyAvatarCount(int available, int replies)
        => Math.Max(0, Math.Min(MaxReplyAvatars, Math.Min(available, replies)));
}

/// <summary>What ONE reaction pill renders: the emoji it stacks (most-used first), the total beside them, whether it
/// reads as pressed (you reacted) and whether it is the empty CALL TO ACTION ("React" + a single neutral face) rather
/// than a count.</summary>
public readonly record struct ReactionPillModel(string[] Emoji, int Total, bool Pressed, bool CallToAction);

public static class PodcastReactionRules
{
    public static readonly string[] Palette = ["\u2764\uFE0F", "\uD83D\uDE02", "\uD83D\uDE05", "\uD83D\uDE2E", "\uD83D\uDE22", "\uD83D\uDC4F", "\uD83D\uDD25", "\uD83D\uDC4D"];
    public static bool Remove(string current, string picked) => current.Length > 0
        && string.Equals(current.Replace("\uFE0F", "", StringComparison.Ordinal), picked.Replace("\uFE0F", "", StringComparison.Ordinal), StringComparison.Ordinal);

    /// <summary>At most this many emoji overlap inside the pill.</summary>
    public const int PillEmojiCap = 3;

    /// <summary>The one face the pill shows when it has nothing truthful to show: the empty "React" state, and the
    /// stand-in while the per-emoji breakdown is still unknown and you have not reacted yourself.</summary>
    public const string NeutralFace = "\uD83D\uDE42";

    /// <summary>THE PILL's composition (podcast-episode-peek \u00A73) \u2014 a real decision, so it is a pure function with
    /// tests rather than branches inlined in the builder.
    /// <list type="bullet">
    /// <item>Nothing at all (no total, no reaction of yours) \u21D2 one neutral face and the "React" label.</item>
    /// <item>A known breakdown \u21D2 up to <see cref="PillEmojiCap"/> emoji, MOST-USED FIRST, ties keeping the answer's
    /// own order; zero counts and blank emoji never appear.</item>
    /// <item>No breakdown yet (the comments answer carries only a total) \u21D2 your own emoji if you reacted, else the
    /// neutral face \u2014 the pill never claims an emoji nobody sent.</item>
    /// <item>Your own reaction TINTS the pill, and keeps the total at least 1 even if the count has not caught up
    /// with the write yet.</item></list></summary>
    public static ReactionPillModel Pill(IReadOnlyList<Spotify.Podcasts.ReactionCount>? counts, string? myReaction, int total)
    {
        string mine = myReaction ?? "";
        bool pressed = mine.Length > 0;
        int shown = Math.Max(total < 0 ? 0 : total, pressed ? 1 : 0);
        if (shown == 0) return new([NeutralFace], 0, false, true);
        var emoji = TopEmoji(counts);
        if (emoji.Length == 0) emoji = [pressed ? mine : NeutralFace];
        return new(emoji, shown, pressed, false);
    }

    /// <summary>The most-used emoji, capped, stable on ties (a strict &gt; keeps the answer's own order).</summary>
    static string[] TopEmoji(IReadOnlyList<Spotify.Podcasts.ReactionCount>? counts)
    {
        if (counts is null || counts.Count == 0) return [];
        Span<int> chosen = stackalloc int[PillEmojiCap];
        int picked = 0;
        while (picked < PillEmojiCap)
        {
            int best = -1;
            for (int i = 0; i < counts.Count; i++)
            {
                var candidate = counts[i];
                if (candidate is null || candidate.Count <= 0 || string.IsNullOrEmpty(candidate.Emoji)) continue;
                bool taken = false;
                for (int k = 0; k < picked; k++) if (chosen[k] == i) { taken = true; break; }
                if (taken) continue;
                if (best < 0 || candidate.Count > counts[best].Count) best = i;
            }
            if (best < 0) break;
            chosen[picked++] = best;
        }
        if (picked == 0) return [];
        var result = new string[picked];
        for (int i = 0; i < picked; i++) result[i] = counts[chosen[i]].Emoji;
        return result;
    }
}
