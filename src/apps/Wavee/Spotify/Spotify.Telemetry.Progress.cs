using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Col = Wavee.Protocol.Collection;
using Rs = Wavee.Protocol.Resumption;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Telemetry
    {
        // Account-scoped durable intent journal. A successful half of a compound mark is never sent again.
        sealed class CompletionIntent(string uri, bool played, long at)
        {
            public string Uri = uri;
            public bool Played = played;
            public long At = at;
            public bool RevisionDone, CollectionDone;
        }

        static readonly List<CompletionIntent> CompletionQueue = new();
        static string s_outboxAccount = "";
        static string s_completionToken = "";
        static Scope? s_completionScope;
        static readonly Dictionary<string, long> CompletedEpisodes = new(StringComparer.Ordinal);
        static EntityId[] s_recentEpisodes = [];
        const string FinishedSet = "markedasfinished";
        const int JournalVersion = 1;

        public static EntityId[] RecentEpisodes() => (EntityId[])Volatile.Read(ref s_recentEpisodes).Clone();

        static string JournalPath(string account, string name)
            => Path.Combine(Platform.LocalFolder, "telemetry",
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(account))) + "." + name);

        static void ActivateOutbox(string account)
        {
            lock (ResumeGate)
            {
                if (account == s_outboxAccount) return;
                PersistResumeJournal();
                s_outboxAccount = account;
                ResumeQueue.Clear();
                CompletionQueue.Clear();
                Volatile.Write(ref s_recentEpisodes, []);
                if (account.Length == 0) return;
                if (Volatile.Read(ref s_booted) == 0) Boot();
                RestoreGabo(account);
                try
                {
                    string path = JournalPath(account, "progress");
                    byte[]? journal = ReadJournal(path);
                    if (journal is null) return;
                    using var r = new BinaryReader(new MemoryStream(journal));
                    if (r.ReadInt32() != JournalVersion) throw new InvalidDataException("Unknown progress journal.");
                    int count = ReadCount(r, ResumeQueueCap);
                    for (int i = 0; i < count; i++)
                        ResumeQueue.Add(Rs.CreateResumePointRevisionRequest.Parser.ParseFrom(ReadBytes(r)));
                    count = ReadCount(r, ResumeQueueCap);
                    for (int i = 0; i < count; i++)
                        CompletionQueue.Add(new CompletionIntent(r.ReadString(), r.ReadBoolean(), r.ReadInt64())
                        { RevisionDone = r.ReadBoolean(), CollectionDone = r.ReadBoolean() });
                }
                catch (Exception ex) { Log.Warn("spotify", "progress journal could not be restored", ex); }
            }
        }

        static int ReadCount(BinaryReader r, int max)
        {
            int n = r.ReadInt32();
            return n >= 0 && n <= max ? n : throw new InvalidDataException("Invalid journal count.");
        }

        static byte[] ReadBytes(BinaryReader r)
        {
            int n = ReadCount(r, 1_048_576);
            byte[] bytes = r.ReadBytes(n);
            return bytes.Length == n ? bytes : throw new EndOfStreamException();
        }

        // ResumeGate held. Atomic replacement leaves the previous journal valid if the process stops mid-write.
        static void PersistResumeJournal()
        {
            if (s_outboxAccount.Length == 0) return;
            try
            {
                string path = JournalPath(s_outboxAccount, "progress");
                using var stream = new MemoryStream();
                {
                    using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
                    w.Write(JournalVersion);
                    w.Write(ResumeQueue.Count);
                    foreach (var revision in ResumeQueue)
                    {
                        byte[] bytes = revision.ToByteArray();
                        w.Write(bytes.Length); w.Write(bytes);
                    }
                    w.Write(CompletionQueue.Count);
                    foreach (var intent in CompletionQueue)
                    {
                        w.Write(intent.Uri); w.Write(intent.Played); w.Write(intent.At);
                        w.Write(intent.RevisionDone); w.Write(intent.CollectionDone);
                    }
                    w.Flush();
                }
                QueueJournal(path, stream.ToArray());
            }
            catch (Exception ex) { Log.Warn("spotify", "progress journal could not be persisted", ex); }
        }

        public static Rs.CreateResumePointRevisionRequest MarkRevision(string uri, bool played, long at)
            => played ? new Rs.CreateResumePointRevisionRequest
            {
                EntityUri = uri,
                Revision = new Rs.CurrentStateRevision
                {
                    Value = new Rs.CurrentStateValue { Marker8 = new Rs.StateMarker() },
                    CreateTime = Timestamp.FromDateTimeOffset(DateTimeOffset.FromUnixTimeMilliseconds(at)),
                },
            } : ResumeRevision(uri, 0, at);

        public static byte[] CompletionWriteBody(string account, string uri, bool played, long at)
            => new Col.WriteRequest
            {
                Username = account, Set = FinishedSet,
                // Stable across retries. Both collection membership and the timestamped revision are idempotent.
                ClientUpdateId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(uri + ":" + at + ":" + played)))[..32],
                Items = { new Col.CollectionItem
                { Uri = uri, AddedAt = EpisodeProgress.UnixSeconds(at), IsRemoved = !played } },
            }.ToByteArray();

        public static void MarkEpisode(string uri, bool played, long at)
        {
            if (Entities.Current is not { } scope || scope.Key.Account.Length == 0) return;
            ActivateOutbox(scope.Key.Account);
            if (Volatile.Read(ref s_booted) == 0) Boot();
            lock (ResumeGate)
            {
                if (CompletionQueue.Count == ResumeQueueCap)
                {
                    Log.Error("podcast", "completion journal full; mark remains local and must be retried");
                    return;
                }
                CompletionQueue.Add(new CompletionIntent(uri, played, at));
                PersistResumeJournal();
            }
            Api.Run(FlushResumePoints);
        }

        static bool OutboxAccountIsCurrent(string account)
            => account.Length > 0 && string.Equals(account, s_outboxAccount, StringComparison.Ordinal)
                && string.Equals(account, Entities.Current?.Key.Account, StringComparison.Ordinal);

        static void FlushCompletion(string account)
        {
            if (!OutboxAccountIsCurrent(account)) return;
            using var accountRequest = Api.ForAccount(account);
            while (true)
            {
                CompletionIntent? intent;
                lock (ResumeGate)
                {
                    if (account != s_outboxAccount || CompletionQueue.Count == 0) return;
                    intent = CompletionQueue[0];
                }
                if (!intent.RevisionDone)
                {
                    if (!OutboxAccountIsCurrent(account)) return;
                    var result = SendRevision(CreateRoute, MarkRevision(intent.Uri, intent.Played, intent.At).ToByteArray());
                    if (!result.Ok) return;
                    lock (ResumeGate)
                    {
                        if (account != s_outboxAccount) return;
                        intent.RevisionDone = true;
                        PersistResumeJournal();
                    }
                }
                if (!intent.CollectionDone)
                {
                    if (!OutboxAccountIsCurrent(account)) return;
                    var result = Api.CollectionWrite(CompletionWriteBody(account, intent.Uri, intent.Played, intent.At), CancellationToken.None);
                    if (!result.Ok) return;
                    lock (ResumeGate)
                    {
                        if (account != s_outboxAccount) return;
                        intent.CollectionDone = true;
                        PersistResumeJournal();
                    }
                }
                lock (ResumeGate)
                {
                    if (account != s_outboxAccount) return;
                    CompletionQueue.Remove(intent);
                    PersistResumeJournal();
                }
            }
        }

        static Api.Result SendRevision(string route, byte[] body)
            => Api.PostEncoded(route, ApiHost.Spclient,
                HeaderSet.Bearer | HeaderSet.ClientToken | HeaderSet.Identity | HeaderSet.ContentProtobuf | HeaderSet.AcceptLanguage,
                body, "application/x-protobuf", null, CancellationToken.None);

        public static void SyncCompletion(Scope scope)
        {
            if (ReferenceEquals(s_completionScope, scope)) return;
            s_completionScope = scope;
            string account = scope.Key.Account;
            if (account.Length == 0) { s_completionScope = null; return; }
            uint epoch = scope.Epoch;
            string token = s_completionAccount == account ? s_completionToken : "";
            long requestedAt = NowMs();
            if (!Api.Run(() =>
            {
                using var accountRequest = Api.ForAccount(account);
                try
                {
                    var items = new List<Col.CollectionItem>();
                    string nextToken = "";
                    bool full = true;
                    if (token.Length > 0)
                    {
                        var deltaResult = Api.CollectionDelta(new Col.DeltaRequest
                        { Username = account, Set = FinishedSet, LastSyncToken = token }.ToByteArray(), CancellationToken.None);
                        if (deltaResult.Ok)
                        {
                            var delta = Col.DeltaResponse.Parser.ParseFrom(deltaResult.Bytes);
                            if (delta.DeltaUpdatePossible)
                            { full = false; items.AddRange(delta.Items); nextToken = delta.SyncToken; }
                        }
                    }
                    if (full)
                    {
                        string page = "";
                        var seen = new HashSet<string>(StringComparer.Ordinal);
                        do
                        {
                            var result = Api.CollectionPage(Api.CollectionPageBody(account, FinishedSet, page, 300), CancellationToken.None);
                            if (!result.Ok) throw new IOException("Completion read status=" + result.Status);
                            var response = Col.PageResponse.Parser.ParseFrom(result.Bytes);
                            items.AddRange(response.Items); nextToken = response.SyncToken;
                            page = response.NextPageToken;
                            if (page.Length > 0 && !seen.Add(page)) throw new InvalidDataException("Repeated completion cursor.");
                        } while (page.Length > 0);
                    }
                    Post(() => LandCompletion(scope, epoch, account, requestedAt, full, items, nextToken));
                }
                catch (Exception ex)
                {
                    Log.Warn("podcast", "completion sync failed; existing state retained", ex);
                    Post(() => { if (ReferenceEquals(s_completionScope, scope)) s_completionScope = null; });
                }
            })) s_completionScope = null;
        }

        static string s_completionAccount = "";

        static void LandCompletion(Scope scope, uint epoch, string account, long requestedAt, bool full,
            List<Col.CollectionItem> items, string token)
        {
            if (ReferenceEquals(s_completionScope, scope)) s_completionScope = null;
            if (!ReferenceEquals(Entities.Current, scope) || scope.Epoch != epoch) return;
            if (s_completionAccount != account) { CompletedEpisodes.Clear(); s_completionAccount = account; }
            var previous = full ? CompletedEpisodes.Keys.ToArray() : [];
            if (full) CompletedEpisodes.Clear();
            foreach (var item in items)
            {
                if (item.IsRemoved) CompletedEpisodes.Remove(item.Uri);
                else CompletedEpisodes[item.Uri] = item.AddedAt * 1000L;
            }
            Staging s = Staging.Rent(); s.Epoch = epoch;
            foreach (string uri in previous)
                if (!CompletedEpisodes.ContainsKey(uri)) StageCompletion(s, uri, false, requestedAt);
            foreach (var item in items) StageCompletion(s, item.Uri, !item.IsRemoved, requestedAt);
            // Clear cached membership absent from a complete snapshot, including rows restored from disk.
            if (full)
                for (int slot = 1; slot < scope.Episodes.Count; slot++)
                    if (scope.Episodes.ExplicitCompleted[slot] && !CompletedEpisodes.ContainsKey(scope.Episodes.Id[slot].Text))
                        StageCompletion(s, scope.Episodes.Id[slot].Text, false, requestedAt);
            s_completionToken = token;
            Entities.Commit(s); Entities.Publish();
            if (!Store.WriteBehind(s)) Staging.Return(s);
        }

        static void StageCompletion(Staging s, string uri, bool completed, long at)
        {
            if (!EntityId.TryParseGid(uri.AsSpan(), out EntityId id) || id.Kind != EntityKind.Episode) return;
            // Pending local intents win until both servers have acknowledged them.
            lock (ResumeGate)
                if (CompletionQueue.Any(x => x.Uri == uri)) return;
            ref StagedEpisode row = ref s.Episodes.RowFor(id, Authority.Full, (uint)EpisodeFields.Completion);
            row.ExplicitCompleted = completed; row.CompletionAtMs = at;
        }

        public static bool OnDealerProgress(ReadOnlySpan<byte> topic, ReadOnlySpan<byte> payload)
        {
            if (topic.StartsWith("hm://collection/markedasfinished/"u8))
            {
                Post(() => { if (Entities.Current is { } scope) SyncCompletion(scope); });
                return true;
            }
            if (!topic.StartsWith("hm://herodotus/"u8)) return false;
            byte[] bytes = payload.ToArray();
            bool batch = topic.EndsWith("/batch"u8);
            Scope? scope = Entities.Current;
            if (scope is null) return true;
            uint epoch = scope.Epoch;
            try
            {
                var response = DecodeProgressPush(bytes, batch);
                Staging s = Staging.Rent(); s.Epoch = epoch;
                int count = FoldCurrentStates(response, s);
                Post(() => LandProgress(scope, s, count, NowMs(), 0));
            }
            catch (Exception ex) { Log.Warn("podcast", "unreadable progress push; refreshing current state", ex); Post(() => HydrateProgress(scope)); }
            return true;
        }

        public static Rs.ListCurrentStatesResponse DecodeProgressPush(byte[] bytes, bool batch)
        {
            var response = new Rs.ListCurrentStatesResponse();
            if (batch)
            {
                var result = Rs.BatchCreateResumePointRevisionsResponse.Parser.ParseFrom(bytes);
                foreach (var item in result.Results)
                {
                    var state = new Rs.CurrentStateEntry { EntityUri = item.EntityUri };
                    if (item.Result is not null) state.Revisions.AddRange(item.Result.Revisions);
                    response.States.Add(state);
                }
            }
            else
            {
                // vc4 singular topics carry the revision directly, not a CurrentStateEntry wrapper.
                var revision = Rs.CurrentStateRevision.Parser.ParseFrom(bytes);
                response.States.Add(new Rs.CurrentStateEntry
                { EntityUri = revision.Value?.EntityUri ?? "", Revisions = { revision } });
            }
            return response;
        }

        static void HydrateRecentEpisodes(Scope scope)
        {
            uint epoch = scope.Epoch;
            Api.Run(() =>
            {
                using var accountRequest = Api.ForAccount(scope.Key.Account);
                try
                {
                    var ids = new List<EntityId>();
                    var distinct = new HashSet<EntityId>();
                    var cursors = new HashSet<string>(StringComparer.Ordinal);
                    string page = "";
                    do
                    {
                        var request = new Rs.ListResumePointRevisionsRequest
                        { EntityUri = PlayHistoryUri, Limit = 500, PageToken = page };
                        var result = SendRevision("/herodotus/spotify.resumption.v1.ResumePointRevisionService/ListResumePointRevisions",
                            request.ToByteArray());
                        if (!result.Ok) return;
                        var answer = Rs.ListResumePointRevisionsResponse.Parser.ParseFrom(result.Bytes);
                        foreach (var revision in answer.Revisions)
                            if (EntityId.TryParseGid((revision.Value?.ItemUri ?? "").AsSpan(), out var id)
                                && id.Kind == EntityKind.Episode && distinct.Add(id)) ids.Add(id);
                        page = answer.NextPageToken;
                        if (page.Length > 0 && !cursors.Add(page)) break;
                    } while (page.Length > 0 && ids.Count < 100);
                    EntityId[] recent = ids.Take(100).ToArray();
                    Post(() => { if (ReferenceEquals(Entities.Current, scope) && scope.Epoch == epoch) Volatile.Write(ref s_recentEpisodes, recent); });
                }
                catch (Exception ex) { Log.Warn("podcast", "recent episode history unavailable", ex); }
            });
        }
    }
}
