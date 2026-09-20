using System.Text;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Library
    {
        // A push is an invalidation hint. The native list read supplies a coherent
        // revision and all members before the persisted baseline is replaced.
        static bool OnShowPush(ReadOnlySpan<byte> topic, ReadOnlySpan<byte> payload)
        {
            const string prefix = "hm://playlist/v2/show/";
            if (!topic.StartsWith("hm://playlist/v2/show/"u8)) return false;
            string id = Encoding.UTF8.GetString(topic[prefix.Length..]);
            int end = id.IndexOfAny(['?', '/']);
            if (end >= 0) id = id[..end];
            string uri = "spotify:show:" + id;
            uint epoch = Entities.Current.Epoch;
            var push = DecodePushBody(payload);
            Post(() =>
            {
                var scope = Entities.Current;
                if (scope.Epoch != epoch || !IsAccountScope(scope)) return;
                if (scope.Shows.TryGetSlot(uri.AsSpan(), out int slot) && slot > Table.None)
                {
                    string held = Entities.Strings.Resolve(scope.Shows.ListRevision[slot]);
                    if (push.Ok && held.Length > 0 && held == push.NewRevision) return;
                    if (!s_pushReadGuard.Accept(uri, push.NewRevision, ListStamps.NowMs())) return;
                    Api.NoteListPush(Api.ListKind.Show, id);
                    ListStamps.MarkDirty(uri);
                    if (OpenShow(scope) == slot && scope.Edges.ShowEpisodes.State(slot) != EdgeState.Unknown)
                        Entities.RefreshEdge(FetchEdge.ShowEpisodes, slot, FetchPriority.Visible);
                }
                else ListStamps.MarkDirty(uri);
            });
            return true;
        }

        static int OpenShow(Scope scope)
        {
            Shell.Route route = Shell.Current.Peek();
            return route.Kind == Shell.RouteKind.Show && route.Subject.IsValid
                && scope.Shows.TryGetSlot(route.Subject.Id, out int slot) ? slot : Table.None;
        }

        static int RevalidateShowsAfterReconnect(Scope scope)
        {
            int asked = 0;
            int open = OpenShow(scope);
            for (int slot = 1; slot < scope.Shows.Count; slot++)
            {
                string uri = scope.Shows.Id[slot].Text;
                if ((slot != open && !ListStamps.IsDirty(uri))
                    || scope.Edges.ShowEpisodes.State(slot) != EdgeState.Complete) continue;
                s_keptLists.Add(uri);
                Entities.RefreshEdge(FetchEdge.ShowEpisodes, slot, FetchPriority.Prefetch);
                asked++;
            }
            return asked;
        }
    }
}
