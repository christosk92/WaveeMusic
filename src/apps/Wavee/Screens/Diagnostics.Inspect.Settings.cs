// ── Screens/Diagnostics.Inspect.Settings.cs ────────────────────────────────────────────────────────────────────────────
// the `settings` verb: ask the settings search (the ranked dials with their scores and the fields they matched), or open a dial
// through the deep-link path
//
// Role: SHELL (UI thread; reads SettingsSearch and SettingsSpec)
// Spec: handoff/settings-2026-11-18/DESIGN.md phase 2 (4.5 ranking, 4.9 deep links, 4.10 the example table).
//
// `settings q=<text>` runs the query the Settings box and the palette's Settings group run (`SettingsSearch.Run`), into this session's
// own result buffer, and replies with each result's id, score, matched fields, title and place, whether the results are close matches,
// and the bytes the query allocated (measured around a second run, so the first use's index build is not counted). No results: the
// no-match line, "did you mean" and the whole-app search line. `settings focus=<arg>` hands the arg to the deep-link door
// (`wavee://open?route=settings&arg=…`, `Shell.ApplyDeepLink`), exactly as a link would, then settles and reports what the link named,
// the focus it recorded and the route (a feature pointer's id opens the pointer's place). `settings` alone reports the index (its
// size, its build time) and every composed dial whose terms are missing from the loc files.

using System.Text.Json;
using FluentGpu.Localization;

namespace Wavee;

public static partial class Diagnostics
{
    public sealed partial class InspectSession
    {
        readonly SettingsHit[] _searchHits = new SettingsHit[SettingsSearch.PageResults];
        readonly string?[] _searchWords = new string?[3];

        /// <summary>`settings {q?, focus?, limit?, + the settle args}`.</summary>
        string SettingsSearchVerb(int id, string name, Inspect.Args a)
        {
            if (a.Has("focus"))
            {
                if (_shell is null) return Inspect.Reply.Fail(id, name, "nothing is mounted (mount first): focus= navigates");
                string arg = a.Str("focus");
                foreach (SettingsPointer pointer in SettingsSearch.Pointers)
                    if (string.Equals(pointer.Id, arg, StringComparison.OrdinalIgnoreCase))
                    {
                        pointer.Open();   // a pointer result's own door (a settings link, or its place outside Settings)
                        return Settled(id, name, a, w =>
                        {
                            w.WriteString("pointer", pointer.Id);
                            if (pointer.Link is { } target) w.WriteString("link", target);
                            w.WriteString("route", RouteText());
                        });
                    }
                SettingsLink link = SettingsSpec.ParseLink(arg);
                Wavee.Shell.ApplyDeepLink("wavee://open?route=settings&arg=" + Uri.EscapeDataString(arg));
                return Settled(id, name, a, w =>
                {
                    w.WriteString("arg", arg);
                    w.WriteBoolean("known", link.Known);
                    w.WriteString("category", link.Category.Slug);
                    if (link.Dial is { } dial) w.WriteString("dial", dial.Id);
                    SettingsFocus focus = Settings.Focus.Peek();
                    w.WriteStartObject("focus");
                    w.WriteString("dial", focus.DialId);
                    w.WriteNumber("seq", focus.Seq);
                    w.WriteEndObject();
                    w.WriteString("route", RouteText());
                });
            }

            if (!a.Has("q"))
            {
                var stats = SettingsSearch.Stats;
                return Inspect.Reply.Ok(id, name, w =>
                {
                    w.WriteNumber("entries", stats.Entries);
                    w.WriteNumber("tokens", stats.Tokens);
                    w.WriteNumber("postings", stats.Postings);
                    w.WriteNumber("trigrams", stats.Trigrams);
                    w.WriteNumber("phrases", stats.Phrases);
                    w.WriteNumber("buildMs", Math.Round(stats.BuildMs, 2));
                    w.WriteString("culture", stats.Culture);
                    int dials = 0;
                    w.WriteStartArray("missingTerms");
                    foreach (var dial in SettingsSpec.All)
                    {
                        dials++;
                        if (!Localization.Has(dial.TermsKey)) w.WriteStringValue(dial.Id);
                    }
                    foreach (var (_, _, dial) in SettingsSpec.LiveDials)
                    {
                        dials++;
                        if (!Localization.Has(dial.TermsKey)) w.WriteStringValue(dial.Id);
                    }
                    foreach (var pointer in SettingsSearch.Pointers)
                        if (!Localization.Has(pointer.TermsKey)) w.WriteStringValue(pointer.Id);
                    w.WriteEndArray();
                    w.WriteNumber("dials", dials);
                });
            }

            string q = a.Str("q");
            int limit = Math.Clamp(a.Int("limit", SettingsSearch.PopupResults), 1, SettingsSearch.PageResults);
            Span<SettingsHit> hits = _searchHits.AsSpan(0, limit);
            SettingsSearch.Run(q, hits);   // the first use builds the index: not part of a keystroke's cost
            long before = GC.GetAllocatedBytesForCurrentThread();
            SettingsSearchResult result = SettingsSearch.Run(q, hits);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            int suggested = result.Count == 0 ? SettingsSearch.Suggest(q, _searchWords) : 0;
            return Inspect.Reply.Ok(id, name, w =>
            {
                w.WriteString("q", q);
                w.WriteNumber("count", result.Count);
                w.WriteBoolean("close", result.Close);
                w.WriteNumber("allocBytes", allocated);
                w.WriteStartArray("results");
                for (int i = 0; i < result.Count; i++)
                {
                    ref readonly SettingsHit hit = ref _searchHits[i];
                    w.WriteStartObject();
                    w.WriteString("id", hit.Id);
                    if (hit.Pointer is not null) w.WriteBoolean("pointer", true);
                    w.WriteNumber("score", Math.Round(hit.Score, 3));
                    w.WriteStartArray("fields");
                    foreach (SettingsMatch flag in Enum.GetValues<SettingsMatch>())
                        if (flag != SettingsMatch.None && (hit.Fields & flag) != 0) w.WriteStringValue(flag.ToString().ToLowerInvariant());
                    w.WriteEndArray();
                    w.WriteString("title", hit.Title);
                    w.WriteString("place", hit.Place);
                    if (hit.Under >= 0) w.WriteString("under", _searchHits[hit.Under].Id);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                if (result.Close) w.WriteString("note", Loc.Get(Strings.Settings.Search.CloseMatches));
                if (result.Count == 0)
                {
                    w.WriteString("noMatch", Strings.Settings.Search.NoMatch(q));
                    w.WriteString("didYouMeanLabel", Loc.Get(Strings.Settings.Search.DidYouMean));
                    w.WriteStartArray("didYouMean");
                    for (int i = 0; i < suggested; i++) w.WriteStringValue(_searchWords[i]);
                    w.WriteEndArray();
                    w.WriteString("searchApp", Strings.Settings.Search.SearchApp(q));
                }
            });
        }
    }
}
