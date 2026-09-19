using System.Security.Cryptography;
using System.Text.Json;
using FluentGpu.Signals;
using Google.Protobuf;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Podcasts
    {
        static readonly Api.Query SavedLibraryQuery = new("libraryV3",
            "390c78e5b951029bad359785e69b07b536a509c581cbcd0aded5e5067f187455", true);
        static readonly SemaphoreSlim SavedNetwork = new(1, 1);
        static readonly HashSet<EntityId> SavedIds = [];
        static Scope? s_savedScope;
        static string s_savedUri = "";
        static EntityId[] s_savedEpisodes = [];
        static bool s_savedReady, s_savedBusy, s_savedRefreshAgain;
        static int s_savedStatus;
        static Timer? s_savedPushTimer;

        public static readonly Signal<uint> SavedChanged = new(0);
        public static bool SavedReady { get { _ = SavedChanged.Value; return IsSavedScope && s_savedReady; } }
        public static bool SavedBusy { get { _ = SavedChanged.Value; return IsSavedScope && s_savedBusy; } }
        public static int SavedStatus { get { _ = SavedChanged.Value; return IsSavedScope ? s_savedStatus : 0; } }
        public static string SavedPlaylistUri { get { _ = SavedChanged.Value; return IsSavedScope ? s_savedUri : ""; } }
        public static EntityId[] SavedEpisodeIds { get { _ = SavedChanged.Value; return IsSavedScope ? (EntityId[])s_savedEpisodes.Clone() : []; } }
        static bool IsSavedScope => ReferenceEquals(s_savedScope, Entities.Current);

        public static bool IsSaved(Episode episode)
        {
            _ = SavedChanged.Value;
            return IsSavedScope && episode.IsValid && SavedIds.Contains(episode.Id);
        }

        static void BindSaved(Scope scope)
        {
            if (ReferenceEquals(s_savedScope, scope)) return;
            s_savedScope = scope; s_savedUri = ""; s_savedReady = false; s_savedBusy = false;
            s_savedRefreshAgain = false; s_savedStatus = 0; s_savedEpisodes = []; SavedIds.Clear();
            SavedChanged.Value++;
        }

        public static byte[] SavedLibraryBody(int offset)
        {
            var vars = new Api.Vars(SavedLibraryQuery);
            vars.W.WriteStartArray("filters"); vars.W.WriteEndArray();
            vars.W.WriteNull("order"); vars.W.WriteString("textFilter", "");
            vars.W.WriteStartArray("features");
            foreach (string feature in new[] { "LIKED_SONGS", "YOUR_EPISODES_V2", "PRERELEASES", "PRERELEASES_V2", "CLIPS", "EVENTS" })
                vars.W.WriteStringValue(feature);
            vars.W.WriteEndArray();
            vars.W.WriteNumber("limit", 50); vars.W.WriteNumber("offset", offset);
            vars.W.WriteBoolean("flatten", false);
            vars.W.WriteStartArray("expandedFolders"); vars.W.WriteEndArray();
            vars.W.WriteNull("folderUri"); vars.W.WriteBoolean("includeFoldersWhenFlattening", true);
            return vars.Finish();
        }

        public readonly record struct SavedDiscovery(string Uri, int Count, int Total);

        public static SavedDiscovery DecodeSavedDiscovery(byte[] body)
        {
            using var document = JsonDocument.Parse(body);
            JsonElement library = At(document.RootElement, "data", "me", "libraryV3");
            JsonElement items = At(library, "items");
            if (items.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Missing libraryV3 items.");
            string uri = "";
            foreach (var item in items.EnumerateArray())
            {
                var data = At(item, "data");
                if (Text(data, "__typename") == "Playlist" && Text(data, "format") == "listen-later")
                    uri = Text(data, "uri");
            }
            return new(uri, items.GetArrayLength(), Number(library, "totalCount"));
        }

        sealed record SavedSnapshot(string Uri, PlaylistOps.WireItem[] Items, string Revision, byte[] Header);

        static SavedSnapshot ReadSavedNetwork(string knownUri, CancellationToken ct)
        {
            string uri = knownUri;
            if (uri.Length == 0)
            {
                int offset = 0;
                while (true)
                {
                    var result = Api.Pathfinder(SavedLibraryQuery, SavedLibraryBody(offset), ct);
                    if (!result.Ok) throw new SavedReadException(result.Status);
                    var discovery = DecodeSavedDiscovery(result.Body);
                    if (discovery.Uri.Length > 0) { uri = discovery.Uri; break; }
                    offset += discovery.Count;
                    if (offset >= discovery.Total || discovery.Count == 0) throw new SavedReadException(404);
                }
            }
            const string prefix = "spotify:playlist:";
            if (!uri.StartsWith(prefix, StringComparison.Ordinal)) throw new SavedReadException(502);
            string id = uri[prefix.Length..];
            var items = new List<PlaylistOps.WireItem>();
            Pl.SelectedListContent? full = null;
            string? head = null;
            int offsetRows = 0;
            do
            {
                var route = Api.ListRoute(Api.ListKind.Playlist, id);
                var result = Api.Send(route with { Path = route.Path + "&from=" + offsetRows + "&length=300" }, [], ct);
                if (!result.Ok) throw new SavedReadException(result.Status);
                if (!PlaylistOps.TryDecodeContents(result.Bytes, out var page, out int position, out bool truncated, out string? revision)
                    || revision is null || position != offsetRows)
                    throw new SavedReadException(502);
                if (head is not null && head != revision) throw new SavedReadException(409);
                head = revision;
                var decoded = Pl.SelectedListContent.Parser.ParseFrom(result.Bytes);
                if (full is null) full = decoded;
                else
                {
                    full.Contents.Items.AddRange(decoded.Contents.Items);
                    full.Contents.MetaItems.AddRange(decoded.Contents.MetaItems);
                }
                items.AddRange(page);
                offsetRows += page.Length;
                if (!truncated) break;
                if (page.Length == 0) throw new SavedReadException(502);
            } while (true);
            full!.Contents.Pos = 0;
            full.Contents.Truncated = false;
            return new(uri, items.ToArray(), head!, full.ToByteArray());
        }

        sealed class SavedReadException(int status) : Exception("Your Episodes read failed: " + status)
        {
            public int Status { get; } = status;
        }

        /// <summary>UI entrypoint: discover and read the account's canonical synthetic playlist.</summary>
        public static Task<Mutation> ReadSavedAsync(CancellationToken ct)
        {
            Scope scope = Entities.Current;
            BindSaved(scope);
            if (Platform.Args.Fake) { s_savedStatus = 501; SavedChanged.Value++; return Task.FromResult(new Mutation(false, 501)); }
            if (s_savedBusy) { s_savedRefreshAgain = true; return Task.FromResult(new Mutation(false, 202)); }
            s_savedBusy = true; SavedChanged.Value++;
            string uri = s_savedUri;
            uint epoch = scope.Epoch;
            var done = new TaskCompletionSource<Mutation>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!Api.Run(() =>
            {
                using var accountRequest = Api.ForAccount(scope.Key.Account);
                bool held = false;
                try
                {
                    SavedNetwork.Wait(ct); held = true;
                    var snapshot = ReadSavedNetwork(uri, ct);
                    Post(() => LandSaved(scope, epoch, snapshot, 200));
                    done.TrySetResult(new Mutation(true, 200));
                }
                catch (Exception ex)
                {
                    int status = ex is SavedReadException read ? read.Status : 0;
                    Log.Warn("podcast", "Your Episodes could not be read", ex);
                    Post(() => LandSaved(scope, epoch, null, status));
                    done.TrySetResult(new Mutation(false, status));
                }
                finally { if (held) SavedNetwork.Release(); }
            }))
            {
                LandSaved(scope, epoch, null, 503);
                done.TrySetResult(new Mutation(false, 503));
            }
            return done.Task;
        }

        /// <summary>Build a keyed remove against server-returned item ids, or an add with a newly minted id.</summary>
        public static PlaylistOp[] SavedMutationOps(string episodeUri, bool save, PlaylistOps.WireItem[] current,
            long now, string mintedId)
        {
            var matches = current.Where(item => item.Uri == episodeUri).ToArray();
            if (save)
                return matches.Length > 0 ? [] :
                    [new PlaylistOp(PlaylistOpKind.Add, AddFirst: true, Items: [new PlaylistMember(episodeUri, mintedId, now)])];
            if (matches.Any(item => string.IsNullOrEmpty(item.ItemId)))
                throw new InvalidDataException("Cannot remove an episode without its canonical item id.");
            return matches.Select(item => new PlaylistOp(PlaylistOpKind.Remove, ItemsAsKey: true,
                Items: [new PlaylistMember(item.Uri, item.ItemId!)])).ToArray();
        }

        public static void ToggleSaved(Episode episode)
        {
            if (Platform.Args.Fake || !episode.IsValid) return;
            Scope scope = Entities.Current;
            BindSaved(scope);
            if (s_savedBusy) return;
            if (!s_savedReady) { _ = ReadSavedAsync(CancellationToken.None); return; }
            string episodeUri = episode.Id.Text;
            bool save = !SavedIds.Contains(episode.Id);
            string uri = s_savedUri;
            uint epoch = scope.Epoch;
            string account = scope.Key.Account;
            s_savedBusy = true; SavedChanged.Value++;
            if (!Api.Run(() =>
            {
                using var accountRequest = Api.ForAccount(account);
                SavedNetwork.Wait();
                try
                {
                    SavedSnapshot snapshot = ReadSavedNetwork(uri, CancellationToken.None);
                    var ops = SavedMutationOps(episodeUri, save, snapshot.Items, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(10)));
                    int status = 200;
                    if (ops.Length > 0)
                    {
                        if (!ReferenceEquals(Entities.Current, scope) || scope.Epoch != epoch)
                            throw new OperationCanceledException("Account changed before Your Episodes write.");
                        Span<byte> revision = stackalloc byte[Encode.MaxRevisionBytes];
                        int length = Encode.ResultingRevision(snapshot.Header, revision);
                        if (length == 0) throw new SavedReadException(502);
                        string id = snapshot.Uri["spotify:playlist:".Length..];
                        var result = Api.PlaylistChanges(id,
                            Encode.PlaylistChanges(revision[..length], ops, account, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Encode.NewNonce()),
                            CancellationToken.None);
                        status = result.Status;
                        // A response may canonicalize item_id. Always read it back before the next keyed mutation.
                        // Ambiguous failures are read back too; never blindly repeat a potentially accepted ADD.
                        snapshot = ReadSavedNetwork(snapshot.Uri, CancellationToken.None);
                        bool present = snapshot.Items.Any(item => item.Uri == episodeUri);
                        if (present == save) status = 200;
                    }
                    Post(() => LandSaved(scope, epoch, snapshot, status));
                }
                catch (Exception ex)
                {
                    int status = ex is SavedReadException read ? read.Status : 0;
                    Log.Warn("podcast", "Your Episodes mutation did not settle", ex);
                    Post(() => LandSaved(scope, epoch, null, status));
                }
                finally { SavedNetwork.Release(); }
            }))
                LandSaved(scope, epoch, null, 503);
        }

        static void LandSaved(Scope scope, uint epoch, SavedSnapshot? snapshot, int status)
        {
            if (!ReferenceEquals(Entities.Current, scope) || scope.Epoch != epoch || !ReferenceEquals(scope, s_savedScope)) return;
            s_savedBusy = false; s_savedStatus = status;
            if (snapshot is not null)
            {
                s_savedUri = snapshot.Uri; s_savedReady = true;
                SavedIds.Clear();
                var ids = new List<EntityId>();
                foreach (var item in snapshot.Items)
                    if (EntityId.TryParseGid(item.Uri.AsSpan(), out var id) && id.Kind == EntityKind.Episode && SavedIds.Add(id)) ids.Add(id);
                s_savedEpisodes = ids.ToArray();
                Staging staging = Staging.Rent(); staging.Epoch = epoch;
                Api.PlaylistAnswer(snapshot.Header, snapshot.Uri, staging);
                Entities.Commit(staging); Entities.Publish();
                if (!Store.WriteBehind(staging)) Staging.Return(staging);
                Episode[] episodes = s_savedEpisodes.Select(Entities.Episode).ToArray();
                Entities.Ensure(episodes, EpisodeFields.Row, FetchPriority.Visible);
            }
            SavedChanged.Value++;
            if (s_savedRefreshAgain)
            {
                s_savedRefreshAgain = false;
                _ = ReadSavedAsync(CancellationToken.None);
            }
        }

        /// <summary>Paired legacy/json aliases invalidate the canonical playlist; they are never used for writes.</summary>
        public static bool OnSavedEpisodesPush(ReadOnlySpan<byte> topic)
        {
            bool alias = topic.StartsWith("hm://collection/listenlater/"u8);
            string uri = s_savedUri;
            bool playlist = uri.Length > 0 && topic.EndsWith(System.Text.Encoding.UTF8.GetBytes("/" + uri["spotify:playlist:".Length..]));
            if (!alias && !playlist) return false;
            var timer = LazyInitializer.EnsureInitialized(ref s_savedPushTimer,
                static () => new Timer(static state => Post(() => { _ = ReadSavedAsync(CancellationToken.None); }), null,
                    Timeout.Infinite, Timeout.Infinite));
            timer.Change(Library.PushSettleMs, Timeout.Infinite);
            return alias; // Normal playlist pushes still feed the shared list synchronizer.
        }
    }
}
