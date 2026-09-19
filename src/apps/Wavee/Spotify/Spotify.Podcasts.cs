using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Pl = Wavee.Protocol.Playlist;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee;

public static partial class Spotify
{
    /// <summary>Return-only discussion and reader data. Catalog facts still enter through Fetch/Staging.</summary>
    public static partial class Podcasts
    {
        public sealed record Comment(string Uri, string Text, string Author, string Avatar, string Created,
            bool Pending, bool Sensitive, bool Pinned, int Replies, int Reactions, string MyReaction, bool ReplyLimit);
        public sealed record Page<T>(T[] Items, string NextToken, int Total, string Eligibility, int Status)
        {
            public bool Ok => Status is >= 200 and < 300;
        }
        public sealed record Chapter(string Uri, string Title, int StartMs, int EndMs);
        public sealed record TranscriptLine(int StartMs, string Text, bool Heading);
        public sealed record Transcript(string Language, TranscriptLine[] Lines, int Status)
        {
            public bool Ok => Status is >= 200 and < 300;
        }
        public sealed record Recommendation(string Uri, string Title, string Image, string Subtitle);
        public sealed record Reaction(string Emoji, string Author, string Avatar, string Created);
        public readonly record struct Mutation(bool Success, int Status, bool Ambiguous = false);
        public readonly record struct TranscriptKey(EntityId Episode, string Language, bool ReadAlong);

        static readonly Api.Query AddComment = new("addComment", "504a54dcb144fc345869c93887152f53450bd41298b6edb57d9e3b1c5aae92e6", false);
        static readonly Api.Query AddReply = new("addCommentReply", "d4046f61457dad19e9315f8829a92e6d162592729d08ed0f9fe70f4c04cd09ae", false);
        static readonly Api.Query AddReaction = new("addCommentReaction", "0af9821ff5cc680412c61c470d55493fe26e6cf938101a93b18c7cc8e5590acb", false);
        static readonly Api.Query DeleteReaction = new("deleteCommentReaction", "b93a40eaa470fcaaa886352e3268e3e5c6e303e11e8b849490e98ba158796cca", false);
        static readonly Lock CacheGate = new();
        static readonly Dictionary<(uint Epoch, string Uri, string Token, bool Replies), (long At, Page<Comment> Page)> CommentCache = [];

        static readonly Dictionary<(uint Epoch, string Uri, string Token, bool Replies), Task<Page<Comment>>> CommentRequests = [];
        static uint s_discussionGeneration;

        public static Task<Page<Comment>> CommentsAsync(uint epoch, string uri, string token, bool replies, CancellationToken ct)
        {
            Scope scope = Entities.Current;
            var key = (epoch, uri, token, replies);
            lock (CacheGate)
            {
                if (scope.Epoch != epoch) return Task.FromResult(new Page<Comment>([], "", 0, "", 409));
                if (CommentCache.TryGetValue(key, out var cached) && Environment.TickCount64 - cached.At < 30_000)
                    return Task.FromResult(cached.Page);
                if (CommentRequests.TryGetValue(key, out var pending)) return pending;
                uint generation = s_discussionGeneration;
                var task = Api.RunAsync(() =>
                {
                    using var accountRequest = Api.ForAccount(scope.Key.Account);
                    ct.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(scope, Entities.Current)) return new Page<Comment>([], "", 0, "", 409);
                    Api.Result result = replies ? Api.PodcastQueries.RepliesQuery(uri, token, ct)
                        : Api.PodcastQueries.CommentsQuery(uri, token, ct);
                    var page = result.Ok ? DecodeComments(result.Body, replies) : new Page<Comment>([], "", 0, "", result.Status);
                    lock (CacheGate)
                    {
                        if (generation != s_discussionGeneration || !ReferenceEquals(scope, Entities.Current) || scope.Epoch != epoch)
                            return new Page<Comment>([], "", 0, "", 409);
                        if (page.Ok)
                        {
                            if (CommentCache.Count >= 128) CommentCache.Clear();
                            CommentCache[key] = (Environment.TickCount64, page);
                        }
                    }
                    return page;
                });
                CommentRequests[key] = task;
                _ = task.ContinueWith(completed =>
                {
                    lock (CacheGate)
                        if (CommentRequests.TryGetValue(key, out var current) && ReferenceEquals(current, completed))
                            CommentRequests.Remove(key);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return task;
            }
        }

        public static void InvalidateDiscussion()
        {
            lock (CacheGate)
            {
                s_discussionGeneration++;
                CommentCache.Clear();
                CommentRequests.Clear();
            }
        }

        public static Page<Comment> DecodeComments(byte[] bytes, bool replies)
        {
            using var document = JsonDocument.Parse(bytes);
            JsonElement pages = At(document.RootElement, "data", replies ? "commentReplies" : "comments");
            if (HasGraphQlErrors(document.RootElement)) return new([], "", 0, "", 502);
            if (pages.ValueKind != JsonValueKind.Array || pages.GetArrayLength() == 0) return new([], "", 0, "", 502);
            var page = pages[0];
            var rows = new List<Comment>();
            foreach (var item in Array(At(page, "items")))
            {
                var author = At(item, "author", "data"); var reactions = At(item, "reactionsMetadata");
                rows.Add(new(Text(item, "uri"), Text(item, replies ? "replyString" : "commentString"),
                    Text(author, "name"), FirstImage(At(author, "avatar", "sources")), Text(At(item, "createDate"), "isoString"),
                    Bool(item, "isPendingReview"), Bool(item, "isSensitive"), Bool(item, "isPinned"),
                    Number(item, "numberOfRepliesWithThreads"), Number(reactions, "numberOfReactions"),
                    Text(reactions, "usersReactionUnicode"), Bool(item, "hasUserReachedReplyLimit")));
            }
            return new(rows.ToArray(), Text(page, "nextPageToken"), Number(page, "totalCount"), Text(page, "eligibilityStatus"), 200);
        }

        public static Task<Mutation> CommentAsync(string entityUri, string text, bool reply, CancellationToken ct)
            => RunAccountMutation(() =>
            {
                if (string.IsNullOrWhiteSpace(text)) return new Mutation(false, 400);
                var query = reply ? AddReply : AddComment;
                var vars = new Api.Vars(query);
                vars.W.WriteString(reply ? "commentUri" : "entityUri", entityUri);
                vars.W.WriteString("text", text);
                var result = Api.Pathfinder(query, vars.Finish(), ct);
                InvalidateDiscussion();
                return CommentMutationOf(result, reply);
            });

        public static Task<Mutation> ReactionAsync(string commentUri, string emoji, bool remove, CancellationToken ct)
            => RunAccountMutation(() =>
            {
                var query = remove ? DeleteReaction : AddReaction;
                var vars = new Api.Vars(query);
                vars.W.WriteString("commentUri", commentUri);
                if (!remove) vars.W.WriteString("reactionUnicode", emoji);
                var result = Api.Pathfinder(query, vars.Finish(), ct);
                InvalidateDiscussion();
                return ReactionMutationOf(result, remove);
            });

        public static Task<Page<Reaction>> ReactionsAsync(string commentUri, string token, string emoji, CancellationToken ct)
            => Api.RunAsync(() =>
            {
                var result = Api.PodcastQueries.ReactionsQuery(commentUri, token, emoji, ct);
                if (!result.Ok) return new Page<Reaction>([], "", 0, "", result.Status);
                using var doc = JsonDocument.Parse(result.Body);
                var pages = At(doc.RootElement, "data", "commentReactions");
                if (HasGraphQlErrors(doc.RootElement)) return new Page<Reaction>([], "", 0, "", 502);
                if (pages.ValueKind != JsonValueKind.Array || pages.GetArrayLength() == 0) return new Page<Reaction>([], "", 0, "", 502);
                var page = pages[0]; var rows = new List<Reaction>();
                foreach (var item in Array(At(page, "items")))
                {
                    var author = At(item, "author", "data");
                    rows.Add(new(Text(item, "reactionUnicode"), Text(author, "name"), FirstImage(At(author, "avatar", "sources")), Text(At(item, "createDate"), "isoString")));
                }
                int total = 0;
                foreach (var count in Array(At(page, "reactionCounts"))) total += Number(count, "numberOfReactions");
                return new Page<Reaction>(rows.ToArray(), Text(page, "nextPageToken"), total, "", 200);
            });

        public static Mutation CommentMutationOf(in Api.Result result, bool reply)
            => MutationOf(result, reply ? "createCommentReply" : "createComment",
                reply ? "SuccessCreateCommentReply" : "SuccessCreateComment");

        public static Mutation ReactionMutationOf(in Api.Result result, bool remove)
            => MutationOf(result, remove ? "deleteCommentReaction" : "putCommentReaction",
                remove ? "SuccessDeleteCommentReaction" : "SuccessPutCommentReaction", requireSuccess: !remove);

        public static Mutation MutationOf(in Api.Result result, string root, string successType, bool requireSuccess = true)
        {
            if (!result.Ok) return new(false, result.Status, result.Status == 0 || result.Status >= 500);
            try
            {
                using var doc = JsonDocument.Parse(result.Body);
                var value = At(doc.RootElement, "data", root);
                bool success = !HasGraphQlErrors(doc.RootElement) && Text(value, "__typename") == successType
                    && (!requireSuccess || Bool(value, "success"));
                return new(success, success ? result.Status : 422);
            }
            catch (JsonException) { return new(false, result.Status, true); }
        }

        public static Task<Mutation> RateAsync(string showUri, int rating, CancellationToken ct)
            => RunAccountMutation(() =>
            {
                if (rating is < 1 or > 5 || !EntityId.TryParseGid(showUri.AsSpan(), out var show) || show.Kind != EntityKind.Show)
                    return new Mutation(false, 400);
                var route = new Api.Route(Verb.Post, ApiHost.SpclientWg,
                    "/ratings/v1/rating/show/" + showUri + "?market=from_token",
                    HeaderSet.Bearer | HeaderSet.ClientToken | HeaderSet.PathfinderDesktop | HeaderSet.XpuiOrigin
                    | HeaderSet.AcceptLanguage | HeaderSet.ContentJson | HeaderSet.AcceptJson);
                var result = Api.Send(route, Encoding.UTF8.GetBytes("{\"rating\":" + rating + "}"), ct);
                if (result.Ok) Api.InvalidateMetadata(showUri);
                return new(result.Ok, result.Status, result.Status == 0 || result.Status >= 500);
            });

        public static Task<Page<Recommendation>> RecommendationsAsync(string uri, bool show, CancellationToken ct)
            => Api.RunAsync(() =>
            {
                var result = show ? Api.PodcastQueries.SimilarShowsQuery(uri, ct) : Api.PodcastQueries.SimilarEpisodesQuery(uri, ct);
                if (!result.Ok) return new Page<Recommendation>([], "", 0, "", result.Status);
                using var doc = JsonDocument.Parse(result.Body);
                var root = At(doc.RootElement, "data", show ? "seoRecommendedPodcast" : "seoRecommendedEpisode");
                if (HasGraphQlErrors(doc.RootElement) || At(root, "items").ValueKind != JsonValueKind.Array)
                    return new Page<Recommendation>([], "", 0, "", 502);
                var rows = new List<Recommendation>();
                foreach (var item in Array(At(root, "items")))
                {
                    var data = At(item, "data");
                    if (Text(data, "__typename") != (show ? "Podcast" : "Episode")) continue;
                    rows.Add(new(Text(data, "uri"), Text(data, "name"), FirstImage(At(data, "coverArt", "sources")),
                        show ? Text(At(data, "publisher"), "name") : Text(At(data, "podcastV2", "data"), "name")));
                }
                return new Page<Recommendation>(rows.ToArray(), "", Number(root, "totalCount"), "", 200);
            });

        public static Task<Page<Chapter>> ChaptersAsync(string episodeUri, CancellationToken ct)
            => Api.RunAsync(() =>
            {
                var route = new Api.Route(Verb.Get, ApiHost.Spclient,
                    "/playlist/v2/list/podcast-chapters/" + Uri.EscapeDataString(episodeUri),
                    HeaderSet.Bearer | HeaderSet.ClientToken | HeaderSet.Identity | HeaderSet.AcceptLanguage | HeaderSet.AcceptProtobuf
                    | HeaderSet.ApplyLenses | HeaderSet.AcceptListItems, SyncReason: "CAwQAQ==", Zstd: true);
                var items = new List<Pl.Item>();
                ByteString? revision = null;
                int position = 0;
                do
                {
                    var result = Api.Send(route with { Path = route.Path + "?from=" + position + "&length=300" }, [], ct);
                    if (!result.Ok) return new Page<Chapter>([], "", 0, "", result.Status);
                    var list = Pl.SelectedListContent.Parser.ParseFrom(result.Body);
                    if (list.Contents is null)
                        return new Page<Chapter>([], "", 0, "", list.Length == 0 ? 200 : 502);
                    if (list.Contents.Pos != position || (revision is not null && !revision.Equals(list.Revision)))
                        return new Page<Chapter>([], "", 0, "", 409);
                    revision = list.Revision;
                    items.AddRange(list.Contents.Items);
                    position += list.Contents.Items.Count;
                    if (!list.Contents.Truncated) break;
                    if (list.Contents.Items.Count == 0) return new Page<Chapter>([], "", 0, "", 502);
                } while (true);
                var chapters = new List<Chapter>();
                foreach (var item in items)
                {
                    int start = 0, end = 0;
                    if (item.Attributes is { } attrs)
                        foreach (var attribute in attrs.FormatAttributes)
                            if (int.TryParse(attribute.Value, out int value))
                            {
                                if (attribute.Key == "chapter.start_position_in_milliseconds") start = value;
                                if (attribute.Key == "chapter.end_position_in_milliseconds") end = value;
                            }
                    chapters.Add(new(item.Uri, "", start, end));
                }
                for (int offset = 0; offset < chapters.Count; offset += 300)
                {
                    var uris = chapters.Skip(offset).Take(300).Select(x => x.Uri).ToArray();
                    var metadata = Api.MetadataPost(Api.MetadataBody(uris, [(Xm.ExtensionKind)178], Api.Market, Api.Catalogue), ct);
                    if (!metadata.Ok) return new Page<Chapter>([], "", 0, "", metadata.Status);
                    var response = Xm.BatchedExtensionResponse.Parser.ParseFrom(metadata.Body);
                    foreach (var group in response.ExtendedMetadata)
                        foreach (var entity in group.ExtensionData)
                        {
                            if (group.ExtensionKind != (Xm.ExtensionKind)178 || entity.ExtensionData is null
                                || (entity.Header is { StatusCode: >= 400 })) continue;
                            string title = DecodeChapterTitle(entity.ExtensionData);
                            int index = chapters.FindIndex(x => x.Uri == entity.EntityUri);
                            if (index >= 0) chapters[index] = chapters[index] with { Title = title };
                        }
                }
                return new Page<Chapter>(chapters.ToArray(), "", chapters.Count, "", 200);
            });

        public static string DecodeChapterTitle(Google.Protobuf.WellKnownTypes.Any value)
            => Encoding.UTF8.GetString(new Decode.ProtoReader(value.Value.Span).Bytes(2));

        public static Transcript DecodeTranscript(byte[] bytes)
        {
            using var doc = JsonDocument.Parse(bytes);
            var lines = new List<TranscriptLine>();
            var sections = At(doc.RootElement, "section");
            if (sections.ValueKind != JsonValueKind.Array) return new("", [], 502);
            foreach (var section in Array(sections))
            {
                int start = Number(section, "startMs");
                if (section.ValueKind != JsonValueKind.Object) continue;
                if (section.TryGetProperty("title", out var title)) lines.Add(new(start, Text(title, "title"), true));
                else
                {
                    var sentence = At(section, "text", "sentence");
                    string text = Text(sentence, "text");
                    if (text.Length > 0) lines.Add(new(Number(sentence, "startMs", start), text, false));
                }
            }
            return new(Text(doc.RootElement, "language"), lines.ToArray(), 200);
        }

        static Task<Mutation> RunAccountMutation(Func<Mutation> work)
        {
            Scope scope = Entities.Current;
            uint epoch = scope.Epoch;
            return Api.RunAsync(() =>
            {
                using var accountRequest = Api.ForAccount(scope.Key.Account);
                return ReferenceEquals(scope, Entities.Current) && scope.Epoch == epoch ? work() : new Mutation(false, 409);
            });
        }

        static bool HasGraphQlErrors(JsonElement root)
            => At(root, "errors") is { ValueKind: JsonValueKind.Array } errors && errors.GetArrayLength() > 0;

        public static JsonElement At(JsonElement node, params string[] keys)
        {
            foreach (string key in keys)
                if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(key, out node)) return default;
            return node;
        }
        public static string Text(JsonElement node, string key) => At(node, key) is var value && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        public static int Number(JsonElement node, string key, int fallback = 0) => At(node, key) is var value && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int n) ? n : fallback;
        public static bool Bool(JsonElement node, string key) => At(node, key).ValueKind == JsonValueKind.True;
        public static IEnumerable<JsonElement> Array(JsonElement node) => node.ValueKind == JsonValueKind.Array ? node.EnumerateArray() : [];
        static string FirstImage(JsonElement node)
        {
            foreach (var image in Array(node)) { string url = Text(image, "url"); if (url.Length > 0) return url; }
            return "";
        }
    }
}
