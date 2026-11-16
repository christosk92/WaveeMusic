// ── Screens/Diagnostics.Inspect.Home.Explain.cs ────────────────────────────────────────────────────────────────────────
// `home do=explain`: why Home shows what it shows. Per section the board renders (a feed section, a placed section, a fold), Spotify's
// identity (typename, section uri, the raw title and its template, the facet) and what the app did to it: the group it sits in, the fold
// it joined and why the run of one-card sections began and ended where it did, SectionIdentity (key, fingerprint, family), the chooser's
// template, reason and alternates, the rhythm pass (kept, or moved off the template its neighbour already draws), the tier form and span,
// and the pager's pages. Then the findings: two sections drawn under one title, or one family twice. The lint rule `home.duplicate`
// (todo, error: `--gate error --gate-todo` counts it) reports the same duplicate titles on any lint of Home.
//
// Role: SHELL (UI thread; reads the projection's answer, the chooser, the board model and the theme's board inspect seam)
// Spec: docs/guide/agent-inspect.md "home do=explain"; the skill's verify/debug-home.md.

using System.Globalization;
using System.Text.Json;
using FluentGpu.Foundation;
using Wavee.HomeUi;

namespace Wavee;

public static partial class Diagnostics
{
    public sealed partial class InspectSession
    {
        /// <summary>One rendered section as explain and the lint see it: the entry, the title it is drawn under, its families.</summary>
        internal readonly record struct ShownSection(string EntryId, BoardEntryKind Kind, string Title, string[] Families, string[] Keys);

        /// <summary>The sections the board renders now, in reading order: feed sections, placed sections and folds (not hidden ones).</summary>
        internal static List<ShownSection> ShownSections(BoardModel model, IReadOnlyList<SectionInput> answer)
        {
            var byKey = new Dictionary<string, SectionInput>(StringComparer.Ordinal);
            foreach (var s in answer) byKey.TryAdd(s.Uri, s);
            var list = new List<ShownSection>();
            foreach (var e in model.Entries)
            {
                if (e.Hidden) continue;
                if (e.Kind == BoardEntryKind.Fold)
                {
                    var fams = new List<string>();
                    var keys = new List<string>();
                    foreach (var m in e.Members) { keys.Add(m.Key); if (!fams.Contains(m.Family)) fams.Add(m.Family); }
                    list.Add(new ShownSection(e.Id, e.Kind, e.Zone?.Title ?? e.Title, [.. fams], [.. keys]));
                }
                else if (e.Raw is SpotifySectionSource ss && e.Kind is BoardEntryKind.FeedSection or BoardEntryKind.Item)
                {
                    string title = e.Zone?.Title ?? e.Title;
                    if (byKey.TryGetValue(ss.Ref.Key, out var input) && input.Title is { Length: > 0 } t) title = t;
                    list.Add(new ShownSection(e.Id, e.Kind, title, [ss.Ref.Family], [ss.Ref.Key]));
                }
            }
            return list;
        }

        /// <summary>Folded for comparison: case, runs of white space.</summary>
        internal static string FoldTitle(string s) => string.Join(' ', s.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        static void WriteExplain(Utf8JsonWriter w, string facet, HomeBoardStore store, IHomeBoardInspect? board)
        {
            var answer = HomeProjection.AnswerOnHand(facet);
            var model = board?.Model ?? store.ShownNow();
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var byKey = new Dictionary<string, (SectionInput Input, int Index)>(StringComparer.Ordinal);
            for (int i = 0; i < answer.Count; i++) byKey.TryAdd(answer[i].Uri, (answer[i], i));
            w.WriteString("facet", facet.Length == 0 ? "all" : facet);
            w.WriteString("via", board is null ? "store" : "board");
            w.WriteNumber("answerSections", answer.Count);
            w.WriteStartArray("sections");
            var entries = model.Entries;
            for (int i = 0; i < entries.Length; i++)
            {
                var e = entries[i];
                bool isFold = e.Kind == BoardEntryKind.Fold;
                if (!isFold && !(e.Raw is SpotifySectionSource && e.Kind is BoardEntryKind.FeedSection or BoardEntryKind.Item)) continue;
                w.WriteStartObject();
                w.WriteString("entry", e.Id);
                w.WriteString("key", "home:tile:" + e.Id);
                w.WriteString("kind", e.Kind.ToString());
                w.WriteString("group", e.GroupId);
                w.WriteString("drawnTitle", e.Zone?.Title ?? e.Title);
                if (e.Hidden) w.WriteBoolean("hidden", true);
                w.WriteString("template", e.Template.ToString());
                if (e.Zone is { } z) { w.WriteString("zone", z.Key); w.WriteString("zoneKind", z.Kind.ToString()); }
                if (isFold)
                {
                    w.WriteStartObject("fold");
                    w.WriteNumber("members", e.Members.Length);
                    string before = Neighbour(entries, i, -1), after = Neighbour(entries, i, +1);
                    w.WriteString("why", e.Members.Length + " one-card baseline sections of the run (SpineShelf, 1 card each, auto size) are gathered into one "
                        + e.Template + " under the app's own heading '" + (e.Zone?.Title ?? e.Title) + "' (HomeProjection.Fold): Spotify sends no such title");
                    w.WriteString("runStartsAfter", before);
                    w.WriteString("runEndsBefore", after);
                    w.WriteString("rule", "a fold gathers EVERY foldable entry of its run (one-card baseline sections at their auto size, wherever they stand in it) at the first one's place; a run is bounded by a placed non-section item (a widget, a pin), and the entries between members keep their order after the shelf");
                    w.WriteEndObject();
                    w.WriteStartArray("sections");
                    foreach (var m in e.Members) WriteSectionCore(w, m, byKey, now, null);
                    w.WriteEndArray();
                }
                else
                {
                    var ss = (SpotifySectionSource)e.Raw!;
                    WriteSectionCore(w, ss.Ref, byKey, now, e);
                    w.WriteString("why", e.Kind == BoardEntryKind.Item ? "placed by the user (a document item)" : "the feed item expanded to it (Spotify's order)");
                }
                if (board is not null)
                {
                    if (board.TrySpan(e.Id, out int cols, out float rows, out string form))
                    {
                        w.WriteString("form", form);
                        w.WriteStartObject("span");
                        w.WriteNumber("cols", cols);
                        Inspect.Reply.Num(w, "rows", rows);
                        w.WriteEndObject();
                    }
                    w.WriteBoolean("realized", board.IsRealized(e.Id));
                }
                if (e.Zone is { } zz && GridFill.Pagers.TryGetValue(zz.Key, out FluentGpu.Controls.ShelfController? pager))
                {
                    w.WriteStartObject("pager");
                    w.WriteNumber("page", pager.Page.Peek() + 1);
                    w.WriteNumber("pages", pager.PageCount.Peek());
                    w.WriteEndObject();
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();

            // the findings: one title drawn twice, one family twice
            var shown = ShownSections(model, answer);
            var findings = new List<(string Kind, string What, List<string> Entries, string Hint)>();
            var byTitle = new Dictionary<string, List<ShownSection>>(StringComparer.Ordinal);
            foreach (var s in shown)
            {
                string t = FoldTitle(s.Title);
                if (t.Length == 0) continue;
                if (!byTitle.TryGetValue(t, out var l)) byTitle[t] = l = [];
                l.Add(s);
            }
            foreach (var (t, l) in byTitle)
                if (l.Count > 1)
                    findings.Add(("duplicate-title", "'" + l[0].Title + "' is drawn " + l.Count + " times", l.ConvertAll(s => s.EntryId),
                        l.TrueForAll(s => s.Kind == BoardEntryKind.Fold)
                            ? "every copy is a fold: the one-card runs are split by an entry that is not foldable between them, and each run takes the same app heading"
                            : "two sections carry one title: compare their typename, family and header in sections[]"));
            var byFamily = new Dictionary<string, List<ShownSection>>(StringComparer.Ordinal);
            foreach (var s in shown)
                foreach (string f in s.Families)
                {
                    if (f.Length == 0 || f == "|") continue;
                    if (!byFamily.TryGetValue(f, out var l)) byFamily[f] = l = [];
                    if (!l.Exists(x => x.EntryId == s.EntryId)) l.Add(s);
                }
            foreach (var (f, l) in byFamily)
                if (l.Count > 1)
                    findings.Add(("duplicate-family", "family '" + f + "' is drawn in " + l.Count + " entries", l.ConvertAll(s => s.EntryId),
                        "one family (typename | title template) split over entries: a run broken in two, or Spotify sending the family twice"));
            w.WriteStartArray("findings");
            foreach (var (kind, what, ids, hint) in findings)
            {
                w.WriteStartObject();
                w.WriteString("kind", kind);
                w.WriteString("what", what);
                w.WriteStartArray("entries");
                foreach (string id in ids) w.WriteStringValue(id);
                w.WriteEndArray();
                w.WriteString("hint", hint);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }

        /// <summary>The entry beside <paramref name="i"/> in reading order (dir -1 before, +1 after): its id, kind and template, or the group edge.</summary>
        static string Neighbour(System.Collections.Immutable.ImmutableArray<BoardEntry> entries, int i, int dir)
        {
            int j = i + dir;
            if (j < 0 || j >= entries.Length) return "(the board's " + (dir < 0 ? "start" : "end") + ")";
            var n = entries[j];
            if (n.Kind == BoardEntryKind.GroupHead) return "(the head of group " + n.Id + ")";
            string why = n.Hidden ? "hidden" : n.Size != HomeSize.Auto ? "sized by the user" : n.Kind == BoardEntryKind.Fold ? "another fold" : "not a one-card baseline section";
            return n.Id + " (" + n.Kind + ", " + n.Template + "; " + why + ")";
        }

        /// <summary>One section's identity and the chooser's verdict; <paramref name="entry"/> (not a fold member) adds the rhythm's.</summary>
        static void WriteSectionCore(Utf8JsonWriter w, SectionRef r, Dictionary<string, (SectionInput Input, int Index)> byKey, long now, BoardEntry? entry)
        {
            if (entry is null) w.WriteStartObject();
            w.WriteStartObject("spotify");
            w.WriteString("uri", r.Key);
            if (byKey.TryGetValue(r.Key, out var hit))
            {
                SectionInput s = hit.Input;
                w.WriteNumber("index", hit.Index);
                w.WriteString("typename", s.TypeName ?? "");
                if (s.Title is null) w.WriteNull("title"); else w.WriteString("title", s.Title);
                w.WriteString("titleBase", s.TitleBase ?? "");
                if (s.HeaderUri is { Length: > 0 } hu) w.WriteString("headerUri", hu);
                w.WriteString("kind", s.Kind.ToString());
                w.WriteNumber("cards", s.Cards.Count);
                w.WriteNumber("total", s.TotalCount);
                if (s.Cards.Count > 0) w.WriteString("firstCard", s.Cards[0].Kind.ToString());
                w.WriteEndObject();
                w.WriteStartObject("identity");
                w.WriteString("key", r.Key);
                w.WriteString("fp", r.Fp.ToString("x16", CultureInfo.InvariantCulture));
                w.WriteString("family", r.Family);
                w.WriteEndObject();
                var choice = SectionChooser.Choose(s, now);
                w.WriteStartObject("chooser");
                w.WriteString("template", choice.Template.ToString());
                w.WriteString("zone", choice.Zone.ToString());
                w.WriteString("reason", choice.Reason);
                w.WriteStartArray("alternates");
                if (!choice.Alternates.IsDefault) foreach (var t in choice.Alternates) w.WriteStringValue(t.ToString());
                w.WriteEndArray();
                w.WriteEndObject();
                if (entry is not null)
                {
                    w.WriteStartObject("rhythm");
                    bool moved = entry.Template != choice.Template;
                    w.WriteString("decision", moved ? "moved" : "kept");
                    if (moved) w.WriteString("why", "SectionRhythm moved it off " + choice.Template + " (" + SectionRhythm.LookOf(choice.Template)
                        + "'s look) to " + entry.Template + ": its neighbour already drew that look");
                    w.WriteString("final", entry.Template.ToString());
                    w.WriteEndObject();
                }
            }
            else
            {
                w.WriteBoolean("inAnswer", false);
                w.WriteEndObject();
                w.WriteStartObject("identity");
                w.WriteString("key", r.Key);
                w.WriteString("fp", r.Fp.ToString("x16", CultureInfo.InvariantCulture));
                w.WriteString("family", r.Family);
                w.WriteEndObject();
            }
            if (entry is null) w.WriteEndObject();
        }
    }

    public static partial class InspectLint
    {
        /// <summary><c>home.duplicate</c> (todo, error; promote it to gate once the fixtures are clean): two sections the Home board renders under one title (case and spaces folded). The owner
        /// saw two "Because you like" folds on a live answer that no check caught; `home do=explain` says why each copy is there.</summary>
        static int HomeDuplicateRule(Snapshot s, List<Finding> into)
        {
            if (s.HomeSections.Count == 0) return 0;
            var byTitle = new Dictionary<string, List<(string Entry, string Title)>>(StringComparer.Ordinal);
            foreach (var hs in s.HomeSections)
            {
                string t = InspectSession.FoldTitle(hs.Title);
                if (t.Length == 0) continue;
                if (!byTitle.TryGetValue(t, out var l)) byTitle[t] = l = [];
                l.Add(hs);
            }
            foreach (var (t, l) in byTitle)
            {
                if (l.Count < 2) continue;
                var keys = l.ConvertAll(x => "home:tile:" + x.Entry);
                into.Add(new Finding(Rules.HomeDuplicate, Severity.Error,
                    $"'{l[0].Title}' titles {l.Count} Home sections ({string.Join(", ", l.ConvertAll(x => x.Entry))}): a person reads one section twice; home do=explain names why each is there",
                    keys)
                {
                    Pattern = "home:tile",
                    Cause = "home:duplicate-title",
                });
            }
            return s.HomeSections.Count;
        }
    }

    public static partial class InspectLintScene
    {
        /// <summary>The Home board's rendered sections (entry, drawn title) when the page the capture saw is Home; empty otherwise.</summary>
        internal static IReadOnlyList<(string Entry, string Title)> CaptureHomeSections(string route)
        {
            if (!string.Equals(route, "home", StringComparison.Ordinal) || HomeBoardInspect.Current is not { } board) return [];
            var list = new List<(string, string)>();
            foreach (var s in InspectSession.ShownSections(board.Model, HomeProjection.AnswerOnHand(""))) list.Add((s.EntryId, s.Title));
            return list;
        }
    }

    public static partial class InspectLintStore
    {
        static void WriteHomeSections(Utf8JsonWriter w, IReadOnlyList<(string Entry, string Title)> sections)
        {
            if (sections.Count == 0) return;
            w.WriteStartArray("homeSections");
            foreach (var (entry, title) in sections)
            {
                w.WriteStartObject();
                w.WriteString("entry", entry);
                w.WriteString("title", title);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }

        static IReadOnlyList<(string Entry, string Title)> ReadHomeSections(JsonElement root)
        {
            if (!root.TryGetProperty("homeSections", out JsonElement list) || list.ValueKind != JsonValueKind.Array) return [];
            var sections = new List<(string, string)>();
            foreach (JsonElement e in list.EnumerateArray())
                if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("entry", out JsonElement id) && e.TryGetProperty("title", out JsonElement t))
                    sections.Add((id.GetString() ?? "", t.GetString() ?? ""));
            return sections;
        }
    }
}
