// ── Entities/Entities.Fake.Answer.cs — the inspector's SCRIPTED ANSWER: a page's real request, settled the way a transport can ──────────────────
//
// Every --fake row is seeded whole, so no fake page ever asks the fetch layer for anything and none of its settle paths can be watched. The scripted
// answer puts a detail page's HEAD through the real path and lets an agent settle it one command at a time:
//
//   `state kind=head uri=<album|playlist|show|episode|artist> answer=hold`   the head (an artist: its overview and chart bit) is taken away
//        (StageHead) and a scripted transport is swapped in for the subject's provider (with the send gate open for it): the page's own ask goes
//        Plan → bucket → Drain → Send and is HELD here, in flight, exactly as a slow server leaves it;
//   `state kind=head-land answer=<mode>`                             the held ask is settled:
//        fill      the head's fields back, then Answer (the group is Known: the page reveals), and the original transport and gate go back;
//        omit      Answer with nothing for the row: the fetch layer's own miss retries go out, are omitted again here, and the group ends Failed;
//        unfilled  Answer naming the groups as a route that did not answer: Failed at once (never a silent un-ask);
//        fail      a terminal 404: Failed at once;
//        cancel    a transport failure (status 0): the fetch layer re-queues it behind its backoff, still in flight, and the retry is held again.
//
// No page timer takes part in any of it: the page resolves on the publication the settle makes, and its Retry re-asks through the same door (the
// re-ask is held here again, ready for the next `head-land`). The next mount (InspectKit.RestoreColdPlaylist) puts the transport and gate back too.
// Asks for other rows the scripted transport took while it was in place are handed back on restore as a retryable 503 (re-queued with their marks,
// held by the restored gate as before).

namespace Wavee;

public static partial class Entities
{
    /// <summary>How the scripted transport settles the held head ask (file header).</summary>
    public enum ScriptedAnswer : byte { Hold, Fill, Omit, Unfilled, Fail, Cancel }

    /// <summary>The word `answer=` takes, or null for another one.</summary>
    public static ScriptedAnswer? ScriptedAnswerOf(string word) => word switch
    {
        "hold" => ScriptedAnswer.Hold,
        "fill" => ScriptedAnswer.Fill,
        "omit" => ScriptedAnswer.Omit,
        "unfilled" => ScriptedAnswer.Unfilled,
        "fail" => ScriptedAnswer.Fail,
        "cancel" => ScriptedAnswer.Cancel,
        _ => null,
    };

    /// <summary>The transport the scripted answer swaps in: it holds every batch it is handed (a slow server), except that in
    /// <see cref="ScriptedAnswer.Omit"/> mode a batch naming the subject is answered at once with nothing for it (the miss retries).</summary>
    sealed class ScriptedTransport(EntityProvider provider, Table table, int slot) : FetchProvider
    {
        public override EntityProvider Provider => provider;
        public ScriptedAnswer Mode = ScriptedAnswer.Hold;
        public readonly List<(uint Ticket, uint Wanted, bool Subject)> Held = new();

        public override void Start(FetchBatch batch)
        {
            bool subject = Names(batch);
            if (subject && Mode == ScriptedAnswer.Omit) { Fetch.Answer(batch.Ticket, null); return; }
            Held.Add((batch.Ticket, batch.Wanted, subject));
        }

        bool Names(FetchBatch batch)
        {
            if (batch.Subject != FetchSubject.Entity || batch.Kind != table.Kind) return false;
            for (int i = 0; i < batch.Count; i++)
                if (batch.Slots[i] == slot) return true;
            return false;
        }
    }

    static ScriptedTransport? s_scripted;
    static FetchProvider? s_scriptedWas;
    static Func<EntityProvider, bool>? s_scriptedGate;

    /// <summary>After <see cref="StageHead"/>: swap the scripted transport in for <paramref name="uri"/>'s provider and open the send gate for it, so the
    /// page's ask for its head is sent and held. False for a uri the fake does not hold.</summary>
    public static bool BeginScripted(string uri)
    {
        EndScripted();
        var kind = EntityUri.KindOf(uri.AsSpan());
        int slot = ResolveSeedSlot(kind, uri);
        if (slot == Table.None || TableFor(kind) is not { } table) return false;
        EntityProvider provider = table.Id[slot].Provider;
        s_scriptedWas = Fetch.ProviderOf(provider);
        s_scriptedGate = Fetch.CanSend;
        s_scripted = new ScriptedTransport(provider, table, slot);
        Fetch.Register(s_scripted);
        var gate = s_scriptedGate;
        Fetch.CanSend = p => p == provider || gate is null || gate(p);
        return true;
    }

    /// <summary>Settle the held head ask (file header). Returns how many asks for the head it settled (0 when none was held: the page has not asked,
    /// or a Retry is still to come).</summary>
    public static int LandScripted(ScriptedAnswer mode)
    {
        if (s_scripted is not { } s)
        {
            RestoreHead();
            return 0;
        }
        s.Mode = mode;
        if (mode == ScriptedAnswer.Fill) RestoreHead();   // the fields first: the answer then finds the group known (no miss to retry)
        int settled = 0;
        for (int i = s.Held.Count - 1; i >= 0; i--)
        {
            var (ticket, wanted, subject) = s.Held[i];
            if (!subject || mode == ScriptedAnswer.Hold) continue;
            s.Held.RemoveAt(i);
            settled++;
            switch (mode)
            {
                case ScriptedAnswer.Fill:
                case ScriptedAnswer.Omit: Fetch.Answer(ticket, null); break;
                case ScriptedAnswer.Unfilled: Fetch.Answer(ticket, null, wanted); break;
                case ScriptedAnswer.Fail: Fetch.Failed(ticket, 404, 0); break;
                case ScriptedAnswer.Cancel: Fetch.Failed(ticket, 0, 0); break;
            }
        }
        if (mode == ScriptedAnswer.Fill) EndScripted();
        Current.PublishAll();
        return settled;
    }

    /// <summary>The asks the scripted transport holds right now (the reply's evidence).</summary>
    public static int ScriptedHeld => s_scripted?.Held.Count ?? 0;

    /// <summary>Put the original transport and send gate back; the asks for other rows it still holds go back to their buckets as a retryable 503.
    /// Nothing when no scripted answer is in place.</summary>
    public static void EndScripted()
    {
        if (s_scripted is not { } s) return;
        s_scripted = null;
        if (s_scriptedWas is { } was) Fetch.Register(was);
        else Fetch.Unregister(s.Provider);
        Fetch.CanSend = s_scriptedGate;
        s_scriptedWas = null;
        s_scriptedGate = null;
        var held = s.Held.ToArray();
        s.Held.Clear();
        foreach (var (ticket, _, _) in held) Fetch.Failed(ticket, 503, 0);
    }
}
