// ── Wavee.Tests/HomeUi/PodcastFx.cs — podcast card fixtures, staged through the real commit ─────────────────────
//
// `HomeCard` is a live handle over the entity + section-fact tables (`Wavee.Tests.HomeFixtures`'s own header
// explains why no shortcut can fake one). This is the same stage → commit → read-back technique, local to
// `PodcastPlannerTests` because it needs facts `HomeFixtures.Spec` does not carry: an episode's release date
// (`Episode.PublishedAt`, entity-level) and the podcast-only per-card facts `PodcastPlanner` reads through
// `HomeCard` — `VideoThumbUrl`, `PlayedState`, the `Explicit`/`Unplayable` flag bits (`StagedCardFact`,
// Entities/Home.cs). It builds one committed `Section` row per call and hands back a `SectionInput` — the exact
// shape `PodcastPlanner.Plan` consumes — reading the cards straight off the committed section (`Section.CardSlots`
// / `CardKinds`), never through a presentation-layer view.

using System.Threading;
using Wavee;
using Wavee.HomeUi;

namespace Wavee.Tests.HomeUi;

static class PodcastFx
{
    static int s_seq;
    static string NextUri(string kind) => "spotify:" + kind + ":fx-" + Interlocked.Increment(ref s_seq);

    public readonly record struct EpisodeSpec(
        string Title,
        long ReleasedAtMs = 0,
        long DurationMs = 0,
        long ResumeMs = 0,
        byte PlayedState = 0,
        string? VideoThumbUrl = null,
        bool Explicit = false,
        bool Unplayable = false,
        string? ShowUri = null,
        string? Uri = null);

    public readonly record struct ShowSpec(
        string Title,
        bool Audiobook = false,
        bool Unplayable = false,
        string? Uri = null);

    /// <summary>One "New episode from {show}" baseline section — the real capture's shape: one episode, one section,
    /// titled by the show it came from (§4.4 rule 1).</summary>
    public static SectionInput NewEpisodeSection(string show, EpisodeSpec spec)
        => Section("New episode from " + show, SectionKind.HomeBaseline, spec);

    /// <summary>One section of episode cards under <paramref name="title"/>.</summary>
    public static SectionInput Section(string title, SectionKind kind, params EpisodeSpec[] specs)
    {
        var s = Staging.Rent();
        string sectionUri = NextUri("section");
        var sectionId = new StagedId(s.Text(sectionUri));
        int mark = s.Edges.PendingMark;
        int factStart = s.CardFacts.Count;

        foreach (var spec in specs)
        {
            var cid = new StagedId(s.Text(spec.Uri ?? NextUri("episode")));

            ref var e = ref s.Episodes.RowFor(cid, Authority.Full, (uint)EpisodeFields.Identity);
            e.Title = s.Text(spec.Title);
            e.DurationMs = (int)spec.DurationMs;
            e.PublishedAt = (int)(spec.ReleasedAtMs / 1000);
            if (spec.ShowUri is not null) e.ShowUri = new StagedId(s.Text(spec.ShowUri));

            StageFact(s, in cid, spec.DurationMs, spec.ResumeMs, spec.PlayedState, spec.VideoThumbUrl,
                spec.Explicit, spec.Unplayable, video: spec.VideoThumbUrl is not null, audiobook: false);
            s.Edges.Push().Target = cid;
        }

        return Commit(s, sectionId, sectionUri, title, kind, specs.Length, factStart, mark);
    }

    /// <summary>One section of show cards under <paramref name="title"/> (§4.4 "Your shows" / "Shows you might
    /// like" / a rule-5 shows footer shelf).</summary>
    public static SectionInput ShowSection(string title, params ShowSpec[] specs)
    {
        var s = Staging.Rent();
        string sectionUri = NextUri("section");
        var sectionId = new StagedId(s.Text(sectionUri));
        int mark = s.Edges.PendingMark;
        int factStart = s.CardFacts.Count;

        foreach (var spec in specs)
        {
            var cid = new StagedId(s.Text(spec.Uri ?? NextUri("show")));

            ref var sh = ref s.Shows.RowFor(cid, Authority.Full, (uint)ShowFields.Identity);
            sh.Title = s.Text(spec.Title);

            StageFact(s, in cid, 0, 0, 0, null, false, spec.Unplayable, video: false, audiobook: spec.Audiobook);
            s.Edges.Push().Target = cid;
        }

        return Commit(s, sectionId, sectionUri, title, SectionKind.HomeGeneric, specs.Length, factStart, mark);
    }

    static void StageFact(Staging s, in StagedId target, long durationMs, long resumeMs, byte playedState,
        string? videoThumbUrl, bool explicitRating, bool unplayable, bool video, bool audiobook)
    {
        ref var f = ref s.CardFacts.Add();
        f.Target = target;
        f.DurationMs = (int)durationMs;
        f.ResumeMs = (int)resumeMs;
        f.PlayedState = playedState;
        f.VideoThumbUrl = videoThumbUrl is null ? default : s.Text(videoThumbUrl);
        byte flags = 0;
        if (explicitRating) flags |= (byte)HomeCardFlags.Explicit;
        if (unplayable) flags |= (byte)HomeCardFlags.Unplayable;
        if (video) flags |= (byte)HomeCardFlags.HasVideo;
        if (audiobook) flags |= (byte)HomeCardFlags.Audiobook;
        f.Flags = flags;
    }

    static SectionInput Commit(Staging s, StagedId sectionId, string sectionUri, string title, SectionKind kind,
        int cardCount, int factStart, int mark)
    {
        ref var row = ref s.Sections.RowFor(sectionId, Authority.Full, (uint)SectionFields.Identity);
        row.Title = s.Text(title);
        row.Kind = (byte)kind;
        row.Total = cardCount;
        row.Raw = cardCount;
        row.Cards = cardCount;
        row.NextOffset = SectionPaging.NoCursor;
        row.HasFacts = true;
        row.FactStart = factStart;
        row.FactCount = s.CardFacts.Count - factStart;

        s.Edges.Close(Relation.SectionCards, in sectionId, mark);
        TestScope.CommitAndPublish(s);

        var section = Entities.Section(sectionUri.AsSpan());
        return ToInput(section, title);
    }

    static SectionInput ToInput(Section section, string title)
    {
        var slots = section.CardSlots;
        var kinds = section.CardKinds;
        var cards = new HomeCard[slots.Length];
        for (int i = 0; i < slots.Length; i++)
            cards[i] = new HomeCard(new EntityRef(kinds[i].Kind, slots[i]), section.Slot);
        return new SectionInput(section.Slot, section.Id.Text, title, null, section.Kind, cards, section.Total, []);
    }
}
