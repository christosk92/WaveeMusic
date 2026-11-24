// ── Spotify/Spotify.Api.Mix.cs — #48 Mix, the write side that is captured (plan §3.2) ─────────────────────────────────
//
// ONE write: turning Mix ON for a playlist the user owns. The capture (report §3.1, sessions #057 / #526) is a POST to
// /playlist/v2/playlist/{id}/signals with signal `mix-state`, the server's own args {1:"mix"}, the payload {1:1}, the lens
// header `mix,auto` and sync-reason CBAQAQ== ({1:16,2:1}); the reply is the whole list with the mix lens applied, folded as
// a list read (the Tune path). Turning Mix OFF on Spotify is not captured (O1): it is the local "listen without Mix"
// (MixListenWithout), which sends nothing. Saving a transition (`set-transition`) is phase 7's.

namespace Wavee;

public static partial class Spotify
{
    public static partial class Api
    {
        /// <summary>The general ApplyPlaylistSignals body (report §3.2, "AvailableSignal shape"): f1 the 24-byte revision, f2 one
        /// signal { f1 identifier, f2 the server-supplied args (bytes of the args message, copied), f3 the signal-specific payload }.
        /// PURE. Returns the byte count written into <paramref name="into"/>.</summary>
        public static int SignalsBody(ReadOnlySpan<byte> revision, string identifier, ReadOnlySpan<byte> args, ReadOnlySpan<byte> payload, Span<byte> into)
        {
            var w = new Decode.ProtoWriter(into);
            w.Utf8Always(1, revision);
            int signal = w.Open(2);
            w.Str(1, identifier);
            w.Utf8Always(2, args);
            w.Utf8Always(3, payload);
            w.Close(signal);
            return w.Length;
        }

        /// <summary>The `mix-state` body that turns Mix on: args {1:"mix"}, payload {1:1} (report §3.1). PURE.</summary>
        public static byte[] MixStateOnBody(ReadOnlySpan<byte> revision)
        {
            Span<byte> args = stackalloc byte[8];
            var a = new Decode.ProtoWriter(args);
            a.Utf8(1, "mix"u8);
            Span<byte> payload = stackalloc byte[4];
            var p = new Decode.ProtoWriter(payload);
            p.U(1, 1);
            Span<byte> body = stackalloc byte[revision.Length + 64];
            int n = SignalsBody(revision, "mix-state", args[..a.Length], payload[..p.Length], body);
            return body[..n].ToArray();
        }

        public static Result PlaylistMixState(string playlistId, byte[] body, CancellationToken ct)
        {
            var args = new RequestArgs { Id = playlistId, Body = body, Flag = true };   // Flag: the mix-state route (CBAQAQ==, mix,auto)
            return Unwrapped(Send(RequestKind.PlaylistSignals, args, ct));
        }
    }

    public static class Mixes
    {
        /// <summary>Turn Mix on for <paramref name="p"/> (the user's own playlist): read its head, POST `mix-state`, fold the reply as
        /// the list (it carries the lens, the list attributes and every item's set-transition). <paramref name="settled"/> hears whether
        /// it landed. <c>--fake</c> marks the seed's row mixed locally: there is no account to write with.</summary>
        public static void TurnOn(Playlist p, Action<bool>? settled)
        {
            if (!p.IsValid || !p.IsOwner) { settled?.Invoke(false); return; }
            if (Platform.Args.Fake)
            {
                Entities.FakeMixOn(p);
                settled?.Invoke(true);
                return;
            }
            if (!PlaylistEdits.CanWrite(out var scope, out _)) { settled?.Invoke(false); return; }
            string id = Entities.MixPlaylistId(p.Slot), uri = p.Uri.Text;
            if (id.Length == 0) { settled?.Invoke(false); return; }
            MixData.SetLens(id, true);
            if (!Net.Run(() =>
                {
                    Span<byte> head = stackalloc byte[Encode.MaxRevisionBytes];
                    Api.Result read = Api.Playlist(id, CancellationToken.None);
                    int length = read.Ok ? Encode.ResultingRevision(read.Bytes, head) : 0;
                    Api.Result r = length == 0 ? read : Api.PlaylistMixState(id, Api.MixStateOnBody(head[..length]), CancellationToken.None);
                    Staging? staging = null;
                    if (length > 0 && r.Ok && r.Body.Length > 0)
                    {
                        staging = Staging.Rent();
                        staging.Epoch = scope.Epoch;
                        Api.PlaylistAnswer(r.Bytes, uri, staging);
                    }
                    Log.Info("mix", "mix-state on " + (staging is not null ? "landed" : "failed status=" + r.Status));
                    Post(() =>
                    {
                        bool ok = staging is not null;
                        if (ok && ReferenceEquals(scope, Entities.Current)) { Entities.Commit(staging!); Entities.Publish(); }
                        if (staging is not null) Staging.Return(staging);
                        if (!ok) MixData.SetLens(id, false);
                        settled?.Invoke(ok);
                    });
                }))
            {
                MixData.SetLens(id, false);
                settled?.Invoke(false);
            }
        }

        /// <summary>The local "listen without Mix" (decision D3): this PC plays and reads the playlist without the lens; nothing is
        /// sent. Turning it back on asks the rows again through the lens.</summary>
        public static void SetListenWithoutMix(Playlist p, bool without)
        {
            string id = Entities.MixPlaylistId(p.Slot);
            MixListenWithout.Set(MixListenWithout.KeyOf(p), without);
            if (!without && id.Length > 0 && p.MixOn)
            {
                MixData.SetLens(id, true);
                Entities.RefreshEdge(FetchEdge.PlaylistTracks, p.Slot);
            }
            Entities.Current.Playlists.Bump(p.Slot);
        }
    }
}
