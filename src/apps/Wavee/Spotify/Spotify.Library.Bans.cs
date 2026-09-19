using Google.Protobuf;
using Col = Wavee.Protocol.Collection;

namespace Wavee;

/// <summary>Account collection exclusions. Pure membership rules shared by queue and autoplay filtering.</summary>
public static class SpotifyBanRules
{
    public static bool Blocked(string uri, ReadOnlySpan<string> artists, IReadOnlySet<string> bans, IReadOnlySet<string> artistBans)
    {
        if (bans.Contains(uri) || artistBans.Contains(uri)) return true;
        foreach (string artist in artists)
            if (artistBans.Contains(artist)) return true;
        return false;
    }
}

public static partial class Spotify
{
    public static partial class Library
    {
        sealed record BanSnapshot(Scope Scope, HashSet<string> Items, HashSet<string> Artists, string ItemToken, string ArtistToken);
        static BanSnapshot? s_bans;
        static Scope? s_banReading;
        static bool s_banReadAgain;
        static Timer? s_banPushTimer;

        /// <summary>UI thread: include the hydrated track's artists when testing an automatic queue candidate.</summary>
        public static bool IsBanned(EntityId id)
        {
            var snapshot = Volatile.Read(ref s_bans);
            Scope scope = Entities.Current;
            if (snapshot is null || !ReferenceEquals(snapshot.Scope, scope)) return false;
            if (snapshot.Items.Contains(id.Text) || snapshot.Artists.Contains(id.Text)) return true;
            if (id.Kind != EntityKind.Track || !scope.Tracks.TryGetSlot(id, out int slot)) return false;
            foreach (int artist in scope.Edges.TrackArtists.Targets(slot))
                if (artist > Table.None && artist < scope.Artists.Count && snapshot.Artists.Contains(scope.Artists.Id[artist].Text))
                    return true;
            return false;
        }

        /// <summary>Worker-safe form for a response whose artist URIs are already decoded.</summary>
        public static bool IsBanned(string uri, ReadOnlySpan<string> artists)
        {
            var snapshot = Volatile.Read(ref s_bans);
            return snapshot is not null && ReferenceEquals(snapshot.Scope, Entities.Current)
                && SpotifyBanRules.Blocked(uri, artists, snapshot.Items, snapshot.Artists);
        }

        /// <summary>UI thread. Reads capture-proven ban and artistban sets, with incremental reconnects.</summary>
        public static void SyncBans(Scope scope)
        {
            if (ReferenceEquals(s_banReading, scope)) { s_banReadAgain = true; return; }
            var previous = Volatile.Read(ref s_bans);
            if (previous is not null && !ReferenceEquals(previous.Scope, scope))
            {
                Volatile.Write(ref s_bans, null);
                previous = null;
            }
            if (scope.Key.Account.Length == 0) return;
            s_banReading = scope;
            uint epoch = scope.Epoch;
            string account = scope.Key.Account;
            if (!Api.Run(() =>
            {
                using var accountRequest = Api.ForAccount(account);
                try
                {
                    var items = ReadBanSet(account, "ban", previous?.Items, previous?.ItemToken ?? "");
                    var artists = ReadBanSet(account, "artistban", previous?.Artists, previous?.ArtistToken ?? "");
                    var snapshot = new BanSnapshot(scope, items.Items, artists.Items, items.Token, artists.Token);
                    Post(() => LandBans(scope, epoch, snapshot));
                }
                catch (Exception ex)
                {
                    Log.Warn("library", "ban collections could not synchronize; existing exclusions retained", ex);
                    Post(() => LandBans(scope, epoch, null));
                }
            }))
                LandBans(scope, epoch, null);
        }

        static (HashSet<string> Items, string Token) ReadBanSet(string account, string set, HashSet<string>? old, string token)
        {
            if (old is not null && token.Length > 0)
            {
                var result = Api.CollectionDelta(new Col.DeltaRequest
                { Username = account, Set = set, LastSyncToken = token }.ToByteArray(), CancellationToken.None);
                if (!result.Ok) throw new IOException("Ban delta status=" + result.Status);
                var delta = Col.DeltaResponse.Parser.ParseFrom(result.Bytes);
                if (delta.DeltaUpdatePossible)
                {
                    var updated = new HashSet<string>(old, StringComparer.Ordinal);
                    ApplyBanItems(updated, delta.Items);
                    return (updated, delta.SyncToken.Length > 0 ? delta.SyncToken : token);
                }
            }
            var items = new HashSet<string>(StringComparer.Ordinal);
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            string page = "", sync = "";
            do
            {
                var result = Api.CollectionPage(Api.CollectionPageBody(account, set, page, 300), CancellationToken.None);
                if (!result.Ok) throw new IOException("Ban collection status=" + result.Status);
                var answer = Col.PageResponse.Parser.ParseFrom(result.Bytes);
                ApplyBanItems(items, answer.Items);
                page = answer.NextPageToken;
                if (answer.SyncToken.Length > 0) sync = answer.SyncToken;
                if (page.Length > 0 && !cursors.Add(page)) throw new InvalidDataException("Repeated ban collection cursor.");
            } while (page.Length > 0);
            // Collection pages can span an edit. The official client follows the terminal page with a delta.
            if (sync.Length > 0)
            {
                var result = Api.CollectionDelta(new Col.DeltaRequest
                { Username = account, Set = set, LastSyncToken = sync }.ToByteArray(), CancellationToken.None);
                if (!result.Ok) throw new IOException("Ban post-page delta status=" + result.Status);
                var delta = Col.DeltaResponse.Parser.ParseFrom(result.Bytes);
                if (!delta.DeltaUpdatePossible) throw new InvalidDataException("Ban snapshot expired during paging.");
                ApplyBanItems(items, delta.Items);
                if (delta.SyncToken.Length > 0) sync = delta.SyncToken;
            }
            return (items, sync);
        }

        public static void ApplyBanItems(HashSet<string> into, IEnumerable<Col.CollectionItem> changes)
        {
            foreach (var item in changes)
            {
                if (string.IsNullOrEmpty(item.Uri)) continue;
                if (item.IsRemoved) into.Remove(item.Uri); else into.Add(item.Uri);
            }
        }

        static void LandBans(Scope scope, uint epoch, BanSnapshot? snapshot)
        {
            if (ReferenceEquals(s_banReading, scope)) s_banReading = null;
            if (!ReferenceEquals(Entities.Current, scope) || scope.Epoch != epoch) return;
            if (snapshot is not null)
            {
                Volatile.Write(ref s_bans, snapshot);
                Log.Info("library", "ban collections synchronized items=" + snapshot.Items.Count + " artists=" + snapshot.Artists.Count);
            }
            if (s_banReadAgain) { s_banReadAgain = false; SyncBans(scope); }
        }

        public static bool OnBanPush(ReadOnlySpan<byte> topic)
        {
            if (!topic.StartsWith("hm://collection/ban/"u8) && !topic.StartsWith("hm://collection/artistban/"u8)) return false;
            var timer = LazyInitializer.EnsureInitialized(ref s_banPushTimer,
                static () => new Timer(static state => Post(() => SyncBans(Entities.Current)), null, Timeout.Infinite, Timeout.Infinite));
            timer.Change(PushSettleMs, Timeout.Infinite);
            return true;
        }
    }
}
