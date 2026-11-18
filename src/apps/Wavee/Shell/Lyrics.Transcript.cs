using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using ViewRegistry = FluentGpu.Theming.ViewRegistry;

namespace Wavee;

public static partial class Lyrics
{
    public static Doc FromTranscript(string episodeId, Spotify.Podcasts.Transcript transcript, int durationMs = 0)
    {
        var spoken = transcript.Lines.Where(l => !l.Heading && !string.IsNullOrWhiteSpace(l.Text)).ToArray();
        var lines = new List<Line>(spoken.Length);
        bool words = false;
        for (int i = 0; i < spoken.Length; i++)
        {
            var source = spoken[i];
            long end = source.EndMs ?? (i + 1 < spoken.Length ? spoken[i + 1].StartMs : durationMs);
            if (end <= source.StartMs) end = 0;
            var syllables = new List<Syllable>();
            var ranges = source.Highlights ?? [];
            // CORRUPT (bail to a whole-line Line-synced row) is distinct from a plain GAP (an answer that simply
            // skipped some words): a gap becomes one line-synced filler run so the covered words either side still
            // wipe word by word instead of the whole sentence falling back to Line sync.
            bool corrupt = ranges.Length > 0 && end <= ranges[^1].StartMs;
            int offset = 0;
            long gapFromMs = source.StartMs;
            for (int r = 0; !corrupt && r < ranges.Length; r++)
            {
                var span = ranges[r];
                long boundary = r + 1 < ranges.Length ? ranges[r + 1].StartMs : end;
                bool valid = span.Offset >= offset && span.Length > 0 && span.Offset + span.Length <= source.Text.Length
                    && span.StartMs >= source.StartMs && boundary >= span.StartMs
                    && !(span.Offset > 0 && char.IsLowSurrogate(source.Text[span.Offset]))
                    && !(span.Offset + span.Length < source.Text.Length && char.IsLowSurrogate(source.Text[span.Offset + span.Length]));
                if (!valid) { corrupt = true; break; }
                if (span.Offset > offset) AppendGap(syllables, source.Text, offset, span.Offset, gapFromMs, span.StartMs);
                // The window up to `boundary` may belong partly to a GAP after this word (the next covered range
                // does not start right where this one ends): split it char-weighted, so the word's own wipe does not
                // silently swallow the skipped words that follow it.
                int nextOffset = r + 1 < ranges.Length ? ranges[r + 1].Offset : source.Text.Length;
                int gapAfterLen = nextOffset - (span.Offset + span.Length);
                long wordEnd = gapAfterLen > 0
                    ? span.StartMs + (long)Math.Round((boundary - span.StartMs) * (double)span.Length / (span.Length + gapAfterLen))
                    : boundary;
                string text = source.Text.Substring(span.Offset, span.Length);
                // Captures give spaces and the following word the same instant. Keep their exact combined text.
                if (syllables.Count > 0 && syllables[^1].StartMs == span.StartMs)
                    syllables[^1] = syllables[^1] with { Text = syllables[^1].Text + text, EndMs = wordEnd };
                else syllables.Add(new(span.StartMs, wordEnd, text));
                offset = span.Offset + span.Length;
                gapFromMs = wordEnd;
            }
            if (!corrupt && ranges.Length > 0 && offset < source.Text.Length)
                AppendGap(syllables, source.Text, offset, source.Text.Length, gapFromMs, end > 0 ? end : gapFromMs);
            bool valid2 = !corrupt && ranges.Length > 0 && syllables.Count > 0;
            words |= valid2;
            lines.Add(new(source.StartMs, source.Text, valid2 ? syllables : [], end > 0 ? end : null, IsWordByWord: valid2));
        }
        return new(episodeId, lines.Count > 0, lines, words ? SyncKind.Syllable : SyncKind.Line, "spotify-transcript");
    }

    /// <summary>Fill <c>[from, to)</c> — text the answer left without its own highlight — as ONE run that wipes as a
    /// single block over <c>[startMs, endMs)</c>, the time its surrounding real highlights bound it to. A line-synced
    /// island inside an otherwise word-synced line, not a per-character guess.</summary>
    static void AppendGap(List<Syllable> syllables, string text, int from, int to, long startMs, long endMs)
    {
        if (to <= from) return;
        if (endMs < startMs) endMs = startMs;
        string gapText = text.Substring(from, to - from);
        if (syllables.Count > 0 && syllables[^1].StartMs == startMs)
            syllables[^1] = syllables[^1] with { Text = syllables[^1].Text + gapText, EndMs = endMs };
        else syllables.Add(new(startMs, endMs, gapText));
    }

    /// <summary>A row's place relative to the active line, for INK (not opacity — <see cref="Emphasis.OpacityOf"/>
    /// already carries the ring fade).</summary>
    public enum LineRole : byte { Active, Upcoming, Past }

    /// <summary>Which ink RUNG a glyph paints with; <c>ClassicLyricsRow</c> resolves it against the live <c>Ink</c> —
    /// this side never touches a theme token, so it is plain, engine-free and unit-testable.</summary>
    public enum InkRung : byte { Accent, Primary, Secondary }

    /// <summary>The playing episode's transcript shares the lyrics presentation; these are its ink rungs.</summary>
    public static class TranscriptPresentation
    {
        public static void Publish(Loadable<Doc> target, Doc document, Action<Doc> prepare)
        {
            prepare(document);
            target.SetReady(document);
        }

        /// <summary>Beyond this many ring steps PAST the active line, a row's own ink dims too. Inside that ring the
        /// row's OPACITY (<see cref="Emphasis.OpacityOf"/>) already carries the distance — a nearer past row reads the same
        /// full ink as an upcoming one — so a transcript with many rows visible at once doesn't read as a flat grey
        /// wall the moment a line is behind.</summary>
        public const int PastInkRing = 1;

        /// <summary>Classify a row from its PACKED emphasis word (<see cref="Emphasis.Pack"/>) — the same ring the row
        /// already fades its opacity by, so ink and opacity never disagree about where "near" ends.</summary>
        public static LineRole RoleOf(int packedEmphasis)
        {
            int dist = Emphasis.DistOf(packedEmphasis);
            if (dist == 0) return LineRole.Active;
            bool past = (packedEmphasis & Emphasis.PastBit) != 0;
            return past && dist > PastInkRing ? LineRole.Past : LineRole.Upcoming;
        }

        /// <summary>The rung a glyph paints with. <paramref name="sungPart"/> is the glyph-wipe's "Before" state (what
        /// a syllable turns into once sung) — it only ever shows on the active line, so it is the one place the
        /// highlight/accent colour appears; every other rung stays plain ink, never a translucent variant, so a
        /// podcast transcript's un-sung and not-yet-active text reads as full ink instead of a dim wash.</summary>
        public static InkRung InkOf(LineRole role, bool sungPart) => role switch
        {
            LineRole.Active => sungPart ? InkRung.Accent : InkRung.Primary,
            LineRole.Past => InkRung.Secondary,
            _ => InkRung.Primary,
        };
    }

    // UI-thread leases share one resource between the episode reader, rail and expanded player. The final
    // consumer releases it; subsequent visits revalidate through the transcript HTTP/disk-cache authority.
    public readonly record struct TranscriptIdentity(uint Epoch, string Account, Spotify.Podcasts.TranscriptKey Key, string Url);
    static readonly Dictionary<TranscriptIdentity, TranscriptLease> TranscriptLeases = [];
    internal sealed class TranscriptLease(TranscriptIdentity identity, int duration)
    {
        internal readonly Loadable<Doc> Data = Loadable<Doc>.Pending(TranscriptSeed);
        internal int Users;
        internal bool Started;
        CancellationTokenSource? _request;
        internal void Refresh()
        {
            Started = true;
            _request?.Cancel(); _request?.Dispose();
            var request = _request = new CancellationTokenSource();
            if (!Data.IsReady) Data.SetPending(TranscriptSeed);
            _ = Fetch(request);
        }
        async Task Fetch(CancellationTokenSource request)
        {
            try
            {
                var result = Platform.Args.Fake ? await PodcastReaderFixtures.Answer(identity.Key.Episode.Text, PodcastReaderFixtures.TranscriptFor, request.Token).ConfigureAwait(false)
                    : identity.Url.Length == 0 ? new Spotify.Podcasts.Transcript(identity.Key.Language, [], 404)
                    : await Spotify.Podcasts.TranscriptAsync(identity.Key, identity.Url, identity.Account, request.Token).ConfigureAwait(false);
                var doc = FromTranscript(identity.Key.Episode.Text, result, duration);
                Store.ToUi(() =>
                {
                    if (request.IsCancellationRequested || identity.Epoch != Entities.ScopeEpoch.Peek()) return;
                    if (result.Ok || result.Status is 404 or 204) Data.SetReady(doc);
                    else Data.SetFailed(new InvalidOperationException("Transcript response " + result.Status));
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                Store.ToUi(() => { if (!request.IsCancellationRequested && identity.Epoch == Entities.ScopeEpoch.Peek()) Data.SetFailed(error); });
            }
        }
        internal void Dispose() { _request?.Cancel(); _request?.Dispose(); }
    }
    static readonly Doc TranscriptSeed = new("", false,
        [new(0, "Welcome to this episode and today's conversation.", []),
         new(0, "We will explore the ideas behind this story together.", []),
         new(0, "Let us begin with the first question.", [])], SyncKind.Unsynced);

    internal sealed partial class ViewCore
    {
        sealed class TranscriptHost(ViewCore owner, EntityId id) : Component
        {
            public override Element Render()
            {
                uint epoch = Entities.ScopeEpoch.Value;
                _ = Entities.Current.Episodes.Changed.Value;
                var episode = Entities.Episode(id);
                bool known = Platform.Args.Fake || episode.Knows(EpisodeFields.Transcript);
                string url = episode.TranscriptUrl;
                var identity = new TranscriptIdentity(epoch, Entities.Current.Key.Account,
                    new(id, episode.TranscriptLanguage, episode.TranscriptReadAlong), url);
                UseLayoutEffect(() => Entities.Ensure(episode, EpisodeFields.Transcript | EpisodeFields.Duration), DepKey.From((int)epoch));
                if (!known && Entities.Current.Episodes.IsFailed(episode.Slot, (uint)EpisodeFields.Transcript))
                    return PodcastReaderUI.Failed(() => Entities.Refresh(Entities.Current.Episodes, [episode.Slot], (uint)EpisodeFields.Transcript));
                return new BoxEl { Direction = 1, Grow = 1, MinHeight = 0, MinWidth = 0, Children =
                [Embed.Comp(() => new TranscriptDocumentHost(owner, identity, episode.DurationMs, known))
                    with { Key = "transcript-resource:" + epoch + ":" + id.Text + ":" + url + ":" + identity.Key.Language + ":" + identity.Key.ReadAlong + ":" + known }] };
            }
        }
        sealed class TranscriptDocumentHost(ViewCore owner, TranscriptIdentity identity, int duration, bool known) : Component
        {
            readonly Loadable<Doc> _waiting = Loadable<Doc>.Pending(TranscriptSeed);
            readonly Loadable<Doc> _presentation = Loadable<Doc>.Pending(TranscriptSeed);
            TranscriptLease? _lease;
            public override Element Render()
            {
                owner.BindStyle(UseContext(ViewRegistry.Context));
                if (known && _lease is null)
                {
                    if (!TranscriptLeases.TryGetValue(identity, out _lease))
                    {
                        _lease = new(identity, duration);
                        TranscriptLeases.Add(identity, _lease);
                    }
                    _lease.Users++;
                }
                UseLayoutEffect(() =>
                {
                    if (_lease is { Started: false } lease) lease.Refresh();
                }, DepKey.Empty);
                UseEffect(() => () =>
                {
                    if (_lease is not { } lease || --lease.Users != 0) return;
                    lease.Dispose(); TranscriptLeases.Remove(identity);
                }, DepKey.Empty);
                var data = _lease?.Data ?? _waiting;
                UseSignalEffect(() =>
                {
                    // Prepare every owner's stable row signals BEFORE exposing Ready to its virtual list.
                    // Shared resource subscribers have no ordering guarantee relative to a nested skeleton region.
                    var state = (LoadState)data.State.Value;
                    var doc = data.Value.Value;
                    if (state == LoadState.Ready)
                    {
                        TranscriptPresentation.Publish(_presentation, doc,
                            next => owner.PrepareDocument(next, Playback.PositionMs.Peek()));
                    }
                    else if (state == LoadState.Failed)
                        _presentation.SetFailed(data.Error ?? new InvalidOperationException("Transcript unavailable"));
                    else if (!_presentation.IsReady) _presentation.SetPending(TranscriptSeed);
                });
                return Skel.Region(_presentation,
                    content: doc => ReferenceEquals(doc, TranscriptSeed) ? owner.UnsyncedContent(doc) : owner.LyricsContent(doc),
                    isEmpty: static doc => doc.Lines.Count == 0,
                    onEmpty: () => owner.Message(Loc.Get(Strings.Podcast.Reader.NoTranscript)),
                    onFailed: () => PodcastReaderUI.Failed(() => _lease?.Refresh()),
                    reveal: SkelReveal.FadeOnly, smoothResize: false);
            }
        }
    }
}
