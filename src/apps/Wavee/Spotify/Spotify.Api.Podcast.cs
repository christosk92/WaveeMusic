// ── Spotify/Spotify.Api.Podcast.cs ────────────────────────────────────────────────────────────────────────────────
// The podcast rework's pathfinder table + the wire check (§6.0): one Diagnostics click that fires every op the show
// and episode pages will need, ONCE each, and reports whether the wire answered the shape the plan assumes — before
// wave P5 writes a single decoder line against a guess.
//
// Role: SHELL (the sends) + CORE (the bodies, the forward-only root-key verdict)
// Owner: D1
// Wave: P1
// Spec: docs/plans/wavee/podcast-show-rework-implementation.md §5.9, §6.0
//
// Contracts are captured in verycomplex3/4 (2026-09-19), or source-confirmed in the shipped UI.
// Show facts and chapters use native metadata/list routes; the episode detail query is web-verified.
// Do not infer response completeness from a shared GraphQL root name.
//
// THREADING (C9). Every call below blocks (HttpClient.Send, through Pathfinder/PostEncoded/GetText) — this file never
// runs on the UI thread. RunWireCheckAsync hands the whole sequential walk to Api.RunAsync, which drains it on one of
// the four named `wavee-spotify-api-*` worker threads, same as every other blocking call in this file; the UI awaits
// the Task and marshals the result back with UsePost, same as every other background action in Screens/*.UI.cs.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Rs = Wavee.Protocol.Resumption;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Api
    {
        // Request identities follow the captured operation.
        public static class PodcastQueries
        {
            public static readonly Query SimilarShows = new("internalLinkRecommenderShow",
                "6c369ff272a666b31fef1629c169925a1bd80f372195396c82304142cacd89e8", true);
            public static readonly Query Episode = new("getEpisodeOrChapter",
                "3416929067571ac4b79db16716be3c6ea5f6265f7975a0ee94b1fc5ee1dc1e9d", true);
            public static readonly Query SimilarEpisodes = new("internalLinkRecommenderEpisode",
                "122f5c777aae5c0918baec11cd646b7034b8f213f260097b4d229ad947ec7f93", true);
            public static readonly Query Comments = new("getCommentsForEntity",
                "bba34fe5f2da3aaa25ab5c90eef1fe2036d325bf32e791ae462b637665185d83", false);
            public static readonly Query Replies = new("getReplies",
                "a2018b23184ee9c8f355f5bcb0584aa3afbacaed6912195a367aa1bb807359f6", false);
            public static readonly Query Reactions = new("getReactions",
                "0d209bf9507779887fe2b3032d1afd8f35de8425b01aead094698ff1abecda71", false);

            // ── 1. the per-op request builders (§5.9) — reusable by wave P5's decoder, not just the wire check ──────

            /// <summary><c>internalLinkRecommenderShow</c> — the show's "more like this" shelf. Root:
            /// <c>data.seoRecommendedPodcast</c>.</summary>
            public static Result SimilarShowsQuery(string showUri, CancellationToken ct)
            {
                var vars = new Vars(SimilarShows);
                vars.W.WriteString("uri", showUri);
                return Pathfinder(SimilarShows, vars.Finish(), ct);
            }

            /// <summary><c>getEpisodeOrChapter</c> — the episode page's identity/about/facts groups. Root:
            /// <c>data.episodeUnionV2</c>.</summary>
            public static Result EpisodeQuery(string episodeUri, CancellationToken ct)
            {
                var vars = new Vars(Episode);
                vars.W.WriteString("uri", episodeUri);
                vars.W.WriteBoolean("includeEpisodeContentRatingsV2", true);
                return Pathfinder(Episode, vars.Finish(), ct);
            }

            /// <summary><c>internalLinkRecommenderEpisode</c> — "more from this show" / around-this-episode shelves.
            /// Root: <c>data.seoRecommendedEpisode</c>.</summary>
            public static Result SimilarEpisodesQuery(string episodeUri, CancellationToken ct)
            {
                var vars = new Vars(SimilarEpisodes);
                vars.W.WriteString("uri", episodeUri);
                vars.W.WriteBoolean("includeEpisodeContentRatingsV2", true);
                return Pathfinder(SimilarEpisodes, vars.Finish(), ct);
            }

            /// <summary><c>getCommentsForEntity</c> — the episode's comment thread, one page. <paramref name="token"/>
            /// is the wire's own paging token, "" for the first page. Root: <c>data.comments</c> — an ARRAY of pages
            /// (0d0429a0's <c>EntityCommentsData.Comments</c> is a <c>List&lt;EntityCommentPage&gt;</c>); the first
            /// page is <c>[0]</c>.</summary>
            public static Result CommentsQuery(string episodeUri, string token, CancellationToken ct)
            {
                var vars = new Vars(Comments);
                vars.W.WriteString("uri", episodeUri);
                if (token.Length > 0) vars.W.WriteString("token", token); else vars.W.WriteNull("token");   // the wire sends null for the first page (2026-09-19 capture)
                return Pathfinder(Comments, vars.Finish(), ct);
            }

            /// <summary><c>getReplies</c> — one comment's replies, one page. Root: <c>data.commentReplies[0]</c>.</summary>
            public static Result RepliesQuery(string commentUri, string pageToken, CancellationToken ct)
            {
                var vars = new Vars(Replies);
                vars.W.WriteString("commentUri", commentUri);
                if (pageToken.Length > 0) vars.W.WriteString("pageToken", pageToken); else vars.W.WriteNull("pageToken");
                return Pathfinder(Replies, vars.Finish(), ct);
            }

            /// <summary><c>getReactions</c> — one comment's reactions, one page; <paramref name="reactionUnicode"/>
            /// filters to one emoji, "" for all. Root: <c>data.commentReactions[0]</c>.</summary>
            public static Result ReactionsQuery(string commentUri, string token, string reactionUnicode, CancellationToken ct)
            {
                var vars = new Vars(Reactions);
                vars.W.WriteString("uri", commentUri);
                if (token.Length > 0) vars.W.WriteString("token", token); else vars.W.WriteNull("token");   // the wire sends null for the first page (2026-09-19 capture)
                if (reactionUnicode.Length > 0) vars.W.WriteString("reactionUnicode", reactionUnicode); else vars.W.WriteNull("reactionUnicode");
                return Pathfinder(Reactions, vars.Finish(), ct);
            }

            // ── 2. the wire check (§6.0) ──────────────────────────────────────────────────────────────────────────────

            /// <summary>Where "Save captures" writes each op's raw body: <c>Platform.LogFolder\podcast-wire\</c>.</summary>
            public const string CaptureFolderName = "podcast-wire";

            /// <summary>The herodotus route the login hydrate calls (<see cref="Telemetry.CurrentStates"/>) — a fresh literal
            /// here, because that file's own route constant is private to its <c>Telemetry</c> class.</summary>
            const string ListCurrentStatesRoute = "/herodotus/spotify.resumption.v1.CurrentStateService/ListCurrentStates";
            const int ListCurrentStatesLimit = 10;
            const int ListCurrentStatesLookbackDays = 30;

            /// <summary>One fired op's verdict, for the Diagnostics result list. <see cref="Status"/> is the real HTTP
            /// status for every pathfinder and herodotus row; the transcript row's status is a stand-in (see
            /// <see cref="RunTranscript"/> — <see cref="GetText"/> does not expose the real one). <see cref="Status"/>
            /// -1 marks a row the check never fired at all (a blank uri, or replies/reactions with no comment to
            /// anchor them) — rendered "skip" rather than a fake status.</summary>
            public readonly record struct WireCheckResult(string Op, int Status, int Bytes, bool RootFound, string? RootKey, string? Error);

            /// <summary>Fires the 8 pathfinder ops + herodotus <c>ListCurrentStates</c> + the transcript GET, ONCE each,
            /// sequentially, and reports whether each answered the shape the plan assumes (§6.0). The whole walk runs on
            /// an api worker thread (<see cref="RunAsync{T}"/>) — safe to await from the UI thread. A blank
            /// <paramref name="showUri"/>/<paramref name="episodeUri"/> skips the ops that need it rather than firing a
            /// call that can only 400. <paramref name="saveCaptures"/> writes every raw body under
            /// <see cref="CaptureFolderName"/> in <c>Platform.LogFolder</c> — scrubbed of account data, those files
            /// become wave P5's decoder fixtures (the plan's <c>ConcertDecodeTests</c> precedent). Never logs a body —
            /// one Info line per op, by name.</summary>
            public static Task<IReadOnlyList<WireCheckResult>> RunWireCheckAsync(string showUri, string episodeUri, bool saveCaptures, CancellationToken ct)
                => RunAsync(() => RunWireCheck(showUri?.Trim() ?? "", episodeUri?.Trim() ?? "", saveCaptures, ct));

            static IReadOnlyList<WireCheckResult> RunWireCheck(string showUri, string episodeUri, bool saveCaptures, CancellationToken ct)
            {
                string? dir = null;
                if (saveCaptures)
                {
                    dir = Path.Combine(Platform.LogFolder, CaptureFolderName);
                    try { Directory.CreateDirectory(dir); }
                    catch (Exception ex) { Log.Warn("podcast", "wire check could not create the capture folder", ex); dir = null; }
                }

                var results = new List<WireCheckResult>(10);
                bool haveShow = showUri.Length > 0, haveEpisode = episodeUri.Length > 0;

                results.Add(haveShow
                    ? RunNativeShow(showUri, dir, ct)
                    : Skip("show", "no show uri"));

                results.Add(haveShow
                    ? Evaluate(SimilarShows.Op, SimilarShowsQuery(showUri, ct), "seoRecommendedPodcast", dir)
                    : Skip(SimilarShows.Op, "no show uri"));

                results.Add(haveEpisode
                    ? Evaluate(Episode.Op, EpisodeQuery(episodeUri, ct), "episodeUnionV2", dir)
                    : Skip(Episode.Op, "no episode uri"));

                results.Add(haveEpisode
                    ? Evaluate(SimilarEpisodes.Op, SimilarEpisodesQuery(episodeUri, ct), "seoRecommendedEpisode", dir)
                    : Skip(SimilarEpisodes.Op, "no episode uri"));

                results.Add(haveEpisode
                    ? RunChapterList(episodeUri, dir, ct)
                    : Skip("chapters", "no episode uri"));

                // Comments → replies/reactions: the wire's own comment uri has to come back before either can fire at
                // all (§6.0) — there is no "first comment" without one round trip first.
                string? commentUri = null;
                if (!haveEpisode)
                    results.Add(Skip(Comments.Op, "no episode uri"));
                else
                {
                    Result result = CommentsQuery(episodeUri, "", ct);
                    SaveCapture(dir, Comments.Op, result.Body, ".json");
                    bool rootFound = false;
                    if (result.Ok && TryFindDataRoot(result.Bytes, "comments", out Utf8JsonReader atComments))
                    {
                        rootFound = true;
                        commentUri = FirstUriInFirstPage(atComments);
                    }
                    results.Add(Row(Comments.Op, result.Status, result.Body.Length, rootFound, "comments",
                        result.Status == 400 ? "hash probably rotated" : null));
                }

                if (commentUri is null)
                {
                    results.Add(Skip(Replies.Op, "no comment returned by getCommentsForEntity"));
                    results.Add(Skip(Reactions.Op, "no comment returned by getCommentsForEntity"));
                }
                else
                {
                    results.Add(Evaluate(Replies.Op, RepliesQuery(commentUri, "", ct), "commentReplies", dir));
                    results.Add(Evaluate(Reactions.Op, ReactionsQuery(commentUri, "", "", ct), "commentReactions", dir));
                }

                results.Add(RunListCurrentStates(dir, ct));
                results.Add(RunTranscript(episodeUri, haveEpisode, dir, ct));

                return results;
            }

            /// <summary>Every resume point touched in the last <see cref="ListCurrentStatesLookbackDays"/> days, in ONE
            /// herodotus call (0d0429a0 <c>SpClient.cs</c>'s CEL filter shape, ported to a 30-day/10-row wire-check
            /// probe rather than the real sync's 1000/180-day one, <see cref="Telemetry.CurrentStates"/>). Same headers and
            /// host as that call. RootFound = the protobuf parses into the 2026-09-19 shape (repeated revisions per state,
            /// the Duration/marker/context oneof); there is no <c>data.*</c> key to check.</summary>
            static WireCheckResult RunListCurrentStates(string? dir, CancellationToken ct)
            {
                var request = new Rs.ListCurrentStatesRequest
                {
                    Limit = ListCurrentStatesLimit,
                    Filter = "cs.resume_point_revisions.exists(revision, revision.update_time > timestamp('"
                           + DateTimeOffset.UtcNow.AddDays(-ListCurrentStatesLookbackDays)
                                 .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) + "'))",
                };
                Result result = PostEncoded(ListCurrentStatesRoute, ApiHost.Spclient,
                    HeaderSet.Bearer | HeaderSet.ClientToken | HeaderSet.Identity | HeaderSet.ContentProtobuf | HeaderSet.AcceptLanguage,
                    request.ToByteArray(), "application/x-protobuf", null, ct);
                SaveCapture(dir, "ListCurrentStates", result.Body, ".bin");

                bool parsed = false;
                if (result.Ok)
                {
                    try { Rs.ListCurrentStatesResponse.Parser.ParseFrom(result.Bytes); parsed = true; }
                    catch (InvalidProtocolBufferException) { parsed = false; }
                }
                return Row("ListCurrentStates", result.Status, result.Body.Length, parsed, "states",
                    parsed ? null : result.Ok ? "protobuf parse failed" : null);
            }

            /// <summary>Resolve extension-21 availability first, then request its actual language/variant locator.
            /// Reports the real HTTP status and validates the title-or-sentence transcript grammar.</summary>
            static WireCheckResult RunTranscript(string episodeUri, bool haveEpisode, string? dir, CancellationToken ct)
            {
                if (!haveEpisode) return Skip("transcript", "no episode uri");
                Result availability = MetadataPost(MetadataBody(episodeUri, [(Xm.ExtensionKind)21], Market, Catalogue), ct);
                if (!availability.Ok) return Row("transcript", availability.Status, 0, false, null, "transcript availability failed");
                string url = "";
                bool readAlong = false;
                Staging staging = Staging.Rent();
                try
                {
                    Decode.ExtendedMetadata(availability.Bytes, staging);
                    if (staging.Episodes.Count > 0)
                    {
                        ref var episode = ref staging.Episodes[0];
                        url = Encoding.UTF8.GetString(staging.Utf8(episode.TranscriptUrl));
                        readAlong = episode.TranscriptReadAlong;
                    }
                }
                finally { Staging.Return(staging); }
                if (url.Length == 0) return Skip("transcript", "the episode has no transcript locator");
                Result response = TranscriptDocument(Podcasts.TranscriptRequestUrl(url, readAlong), null, ct);
                SaveCapture(dir, "transcript", response.Body, ".json");
                bool valid = false;
                if (response.Ok)
                    try { valid = Podcasts.DecodeTranscript(response.Body).Ok; }
                    catch (JsonException) { }
                return Row("transcript", response.Status, response.Body.Length, valid, null,
                    response.Ok ? null : "transcript document request failed");
            }

            // ── 3. the forward-only, no-DOM root-key scan (§6.0) ─────────────────────────────────────────────────────

            static WireCheckResult RunNativeShow(string uri, string? dir, CancellationToken ct)
            {
                Result result = MetadataPost(MetadataBody(uri, [(Xm.ExtensionKind)11], Market, Catalogue), ct);
                SaveCapture(dir, "show", result.Body, ".bin");
                bool valid = result.Ok && Xm.BatchedExtensionResponse.Parser.ParseFrom(result.Body).ExtendedMetadata.Count > 0;
                return Row("show", result.Status, result.Body.Length, valid, null, null);
            }

            static WireCheckResult RunChapterList(string uri, string? dir, CancellationToken ct)
            {
                var route = new Route(Verb.Get, ApiHost.Spclient,
                    "/playlist/v2/list/podcast-chapters/" + Uri.EscapeDataString(uri),
                    HeaderSet.Bearer | HeaderSet.ClientToken | HeaderSet.Identity | HeaderSet.AcceptProtobuf
                    | HeaderSet.ApplyLenses | HeaderSet.AcceptListItems, SyncReason: "CAwQAQ==", Zstd: true);
                Result result = Send(route, [], ct);
                SaveCapture(dir, "chapters", result.Body, ".bin");
                bool valid = result.Ok && Wavee.Protocol.Playlist.SelectedListContent.Parser.ParseFrom(result.Body).Contents is not null;
                return Row("chapters", result.Status, result.Body.Length, valid, null, null);
            }

            /// <summary>Scans the CURRENT object's immediate children only, skipping every value it does not want, for
            /// a property named <paramref name="name"/>. On a hit <paramref name="r"/> is left positioned AT that
            /// property's value; on a miss it is left at the object's closing brace.</summary>
            static bool SeekProperty(ref Utf8JsonReader r, string name)
            {
                while (r.Read())
                {
                    if (r.TokenType == JsonTokenType.PropertyName)
                    {
                        if (r.ValueTextEquals(name)) { r.Read(); return true; }
                        r.Skip();
                        continue;
                    }
                    if (r.TokenType == JsonTokenType.EndObject) return false;
                }
                return false;
            }

            /// <summary>Does <c>data.&lt;rootKey&gt;</c> exist and answer non-null? On a hit, <paramref name="atValue"/>
            /// is positioned AT the root key's own value (a comments-shaped root continues scanning from there for the
            /// first comment's uri). This is every pathfinder op's whole verdict (§6.0).</summary>
            static bool TryFindDataRoot(ReadOnlySpan<byte> json, string rootKey, out Utf8JsonReader atValue)
            {
                // A 2xx body that is not JSON (or is truncated) is THIS op's "root=no", never an exception that aborts
                // the whole walk and leaves the card with no rows at all.
                try
                {
                    var reader = new Utf8JsonReader(json);
                    if (json.Length > 0 && reader.Read() && reader.TokenType == JsonTokenType.StartObject
                        && SeekProperty(ref reader, "data") && reader.TokenType == JsonTokenType.StartObject
                        && SeekProperty(ref reader, rootKey) && reader.TokenType != JsonTokenType.Null)
                    {
                        atValue = reader;
                        return true;
                    }
                }
                catch (JsonException) { }
                atValue = default;
                return false;
            }

            /// <summary>The first comment's own <c>uri</c>: <c>data.comments</c> is an ARRAY of pages (<paramref name="atComments"/>
            /// positioned at it by <see cref="TryFindDataRoot"/>), page [0] holds <c>items[]</c>, and the comment's OWN
            /// <c>uri</c> is a top-level key of <c>items[0]</c>. The wire's keys are alphabetical, so the nested
            /// <c>author{…uri}</c> comes FIRST — an any-depth scan returned the commenter's user uri (2026-09-19
            /// capture: page keys __typename/eligibilityStatus/entityUri/items/nextPageToken/totalCount; item keys
            /// author … uri). Null for an empty first page or any other shape.</summary>
            static string? FirstUriInFirstPage(Utf8JsonReader atComments)
            {
                try
                {
                    if (atComments.TokenType != JsonTokenType.StartArray
                        || !atComments.Read() || atComments.TokenType != JsonTokenType.StartObject
                        || !SeekProperty(ref atComments, "items") || atComments.TokenType != JsonTokenType.StartArray
                        || !atComments.Read() || atComments.TokenType != JsonTokenType.StartObject)
                        return null;
                    return FindFirstStringInObject(atComments, "uri");
                }
                catch (JsonException) { return null; }   // a truncated page: replies/reactions are then "skip"
            }

            /// <summary>The first string-valued property named <paramref name="propertyName"/> at the TOP LEVEL of the
            /// object <paramref name="r"/> is positioned AT (nested objects are skipped over) — forward-only, no DOM, bounded to
            /// exactly that object by manual bracket counting (a copy of the reader; the caller's own position is never
            /// disturbed).</summary>
            static string? FindFirstStringInObject(Utf8JsonReader r, string propertyName)
            {
                if (r.TokenType != JsonTokenType.StartObject) return null;
                int depth = 0;
                bool wantValue = false;
                while (r.Read())
                {
                    switch (r.TokenType)
                    {
                        case JsonTokenType.StartObject:
                        case JsonTokenType.StartArray:
                            depth++;
                            wantValue = false;
                            break;
                        case JsonTokenType.EndObject:
                        case JsonTokenType.EndArray:
                            if (depth == 0) return null;   // the object we started scanning just closed
                            depth--;
                            wantValue = false;
                            break;
                        case JsonTokenType.PropertyName:
                            // DEPTH 0 ONLY: the wire's keys are alphabetical, so a comment's nested `author{…uri}` comes
                            // BEFORE its own `uri` — any-depth matching returned the commenter's user uri (2026-09-19 capture).
                            wantValue = depth == 0 && r.ValueTextEquals(propertyName);
                            break;
                        case JsonTokenType.String:
                            if (wantValue) return r.GetString();
                            wantValue = false;
                            break;
                        default:
                            wantValue = false;
                            break;
                    }
                }
                return null;
            }

            // ── 4. the shared row/log/capture plumbing ───────────────────────────────────────────────────────────────

            /// <summary>A pathfinder result whose only interesting shape is "does <paramref name="rootKey"/> exist" —
            /// every op except comments (which also has to hand back a comment uri).</summary>
            static WireCheckResult Evaluate(string op, Result result, string rootKey, string? dir)
            {
                SaveCapture(dir, op, result.Body, ".json");
                bool rootFound = result.Ok && TryFindDataRoot(result.Bytes, rootKey, out _);
                return Row(op, result.Status, result.Body.Length, rootFound, rootKey,
                    result.Status == 400 ? "hash probably rotated" : null);
            }

            static WireCheckResult Skip(string op, string reason) => Row(op, -1, 0, false, null, reason);

            /// <summary>The one Info line every fired (or skipped) op gets — never a body.</summary>
            static WireCheckResult Row(string op, int status, int bytes, bool rootFound, string? rootKey, string? error)
            {
                string statusText = status == -1 ? "skip" : status.ToString(CultureInfo.InvariantCulture);
                Log.Info("podcast", "podcast.wire op=" + op + " status=" + statusText
                    + " bytes=" + bytes.ToString(CultureInfo.InvariantCulture) + " root=" + (rootFound ? "yes" : "no"));
                return new WireCheckResult(op, status, bytes, rootFound, rootKey, error);
            }

            static void SaveCapture(string? dir, string op, byte[] body, string ext)
            {
                if (dir is null) return;
                try { File.WriteAllBytes(Path.Combine(dir, op + ext), body); }
                catch (Exception ex) { Log.Warn("podcast", "wire check could not save the " + op + " capture", ex); }
            }
        }
    }
}
