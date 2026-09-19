namespace Wavee;

public static partial class Spotify
{
    public static partial class Api
    {
        // Playlist4 owns show membership; metadata owns episode facts. Pages are assembled only
        // while their revisions agree, so a persisted baseline always describes the whole list.
        static void ShowEdge(string uri, string? revision, ListRow[]? baseline, Staging s, ref FetchOutcome outcome)
        {
            string id = IdOf(uri);
            ListRead read = ReadList(ListKind.Show, id, revision, baseline, ref outcome);
            if (read.Rows is { } replay)
            {
                if (!Store.StageList(s, EdgeRelation.ShowEpisodes, uri, replay, read.Revision!))
                { var invalid = new Result(503, []); outcome.Note(in invalid); }
                return;
            }
            if (read.Body is null) return;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                byte[] body = attempt == 0 ? read.Body : FullRead(ListKind.Show, id, ref outcome) ?? [];
                var rows = new List<ListRow>();
                string? head = null;
                bool retry = false;
                while (true)
                {
                    if (!PlaylistOps.TryDecodeContents(body, out var items, out int offset, out bool truncated, out string? nextHead)
                        || !ListWrite.IsWellFormedRevision(nextHead) || offset != rows.Count
                        || (head is not null && head != nextHead)) { retry = true; break; }
                    var content = Wavee.Protocol.Playlist.SelectedListContent.Parser.ParseFrom(body);
                    head = nextHead;
                    foreach (var item in items)
                        rows.Add(ListPushReplay.RowOf(item));
                    if (!truncated && (!content.HasLength || rows.Count >= content.Length))
                    {
                        if (content.HasLength && rows.Count != content.Length) { retry = true; break; }
                        if (!Store.StageList(s, EdgeRelation.ShowEpisodes, uri, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(rows), head!))
                        { var invalid = new Result(503, []); outcome.Note(in invalid); }
                        return;
                    }
                    if (items.Length == 0) { retry = true; break; }
                    Route route = ListRoute(ListKind.Show, id);
                    Result page = Send(route with { Path = route.Path + "?from=" + rows.Count + "&length=300" }, [], CancellationToken.None);
                    outcome.Note(in page);
                    if (!page.Ok) return;
                    body = page.Body;
                }
                if (!retry) break;
            }
            var failed = new Result(503, []);
            outcome.Note(in failed);
        }
    }
}
