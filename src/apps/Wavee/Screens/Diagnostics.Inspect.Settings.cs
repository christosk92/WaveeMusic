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

        /// <summary>`settings {q?, focus?, limit?, model?, press?, + the settle args}`.</summary>
        string SettingsSearchVerb(int id, string name, Inspect.Args a)
        {
            if (a.Has("press")) return SettingsPress(id, name, a);
            if (a.Has("model")) return SettingsModel(id, name, a);
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

        // ── the registry page's model (contract 1.116) ─────────────────────────────────────────────────────────────────

        /// <summary>`settings model=<category> [q=<text>]`: what the registry's page is handed (the props the page host last pushed while
        /// Settings is mounted, else the same members built now): the category list, the open category and the arrival, the home's facts, one
        /// category's body, and with <c>q=</c> the page box's result set (the query is written to the box's cell and ranked as a keystroke).</summary>
        string SettingsModel(int id, string name, Inspect.Args a)
        {
            Wavee.Theming.Contracts.SettingsPageProps? page = _shell is not null && Wavee.Shell.Current.Peek().Kind == Wavee.Shell.RouteKind.Settings
                ? Settings.LastPublished : null;
            var categories = page?.Categories ?? SettingsPageModel.Categories;
            int open = page?.Category ?? Settings.Category.Peek();
            var arrival = page?.Focus ?? SettingsPageModel.ArrivalOf(Settings.Focus.Peek(), ShellPolicy.Now.SettingsPage.HighlightMs);
            SettingsSearchModel search = page?.Search ?? SettingsSearchModel.Current;
            SettingsHomeSource home = page?.Home ?? SettingsHomeSource.Shared;
            SettingsBodySource bodies = page?.Body ?? SettingsBodySource.Shared;
            string slug = a.Str("model");
            int index = slug.Length == 0 ? open : SettingsPageModel.IndexOf(slug);
            Wavee.Theming.Contracts.SettingsBody body = bodies.Of(index);
            Wavee.Theming.Contracts.SettingsHomeFacts facts = home.Read();
            long allocBytes = -1;
            if (a.Has("q"))
            {
                string q = a.Str("q");
                search.Query.Value = q;
                search.Refresh(q, force: true);   // the first use builds the index: not part of a keystroke's cost
                long before = GC.GetAllocatedBytesForCurrentThread();
                search.Refresh(q, force: true);
                allocBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            }
            return Inspect.Reply.Ok(id, name, w =>
            {
                w.WriteString("source", page is null ? "built" : "page");
                w.WriteNumber("category", open);
                w.WriteStartArray("categories");
                foreach (var c in categories)
                {
                    w.WriteStartObject();
                    w.WriteNumber("index", c.Index);
                    w.WriteString("slug", c.Slug);
                    w.WriteString("title", c.Title);
                    w.WriteString("strip", c.Strip);
                    w.WriteString("glyph", c.Glyph);
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                w.WriteStartObject("focus");
                w.WriteString("dial", arrival.DialId);
                w.WriteNumber("seq", arrival.Seq);
                if (!arrival.IsNone)
                {
                    w.WriteString("category", arrival.Category);
                    if (arrival.Group is { } group) w.WriteString("group", group);
                    if (arrival.Parent is { } parent) w.WriteString("parent", parent);
                    w.WriteStartArray("expand");
                    foreach (string e in arrival.Expand) w.WriteStringValue(e);
                    w.WriteEndArray();
                }
                w.WriteNumber("highlightMs", arrival.HighlightMs);
                w.WriteEndObject();

                w.WriteStartObject("home");
                w.WriteStartArray("glance");
                foreach (var g in facts.Glance)
                {
                    w.WriteStartObject();
                    w.WriteString("id", g.Id);
                    w.WriteString("title", g.Title);
                    w.WriteString("value", g.Value);
                    w.WriteString("dial", g.DialId);
                    w.WriteString("glyph", g.Glyph);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartArray("suggestions");
                foreach (var s in facts.Suggestions)
                {
                    w.WriteStartObject();
                    w.WriteString("id", s.Id);
                    w.WriteString("text", s.Text);
                    w.WriteString("action", s.Action.ToString().ToLowerInvariant());
                    if (s.ActionLabel is { } label) w.WriteString("actionLabel", label);
                    if (s.DialId is { } dial) w.WriteString("dial", dial);
                    w.WriteBoolean("act", s.Act is not null);
                    w.WriteBoolean("dismiss", s.Dismiss is not null);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                w.WriteStartArray("recent");
                foreach (var r in facts.Recent)
                {
                    w.WriteStartObject();
                    w.WriteString("dial", r.DialId);
                    w.WriteString("title", r.Title);
                    w.WriteString("value", r.Value);
                    w.WriteString("from", r.From);
                    w.WriteNumber("agoMs", now - r.AtMs);
                    w.WriteBoolean("door", r.Open is not null);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();

                w.WriteStartObject("body");
                w.WriteString("category", body.Category.Slug);
                w.WriteString("title", body.Category.Title);
                w.WriteString("subtitle", body.Category.Subtitle);
                w.WriteStartArray("groups");
                foreach (var group in body.Groups)
                {
                    w.WriteStartObject();
                    w.WriteString("id", group.Id);
                    w.WriteString("title", group.Title);
                    if (group.Subtitle is { } sub) w.WriteString("subtitle", sub);
                    w.WriteStartArray("rows");
                    foreach (var row in group.Rows) WriteBodyRow(w, row);
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();

                w.WriteStartObject("search");
                w.WriteString("q", search.Ranked);
                w.WriteBoolean("active", search.Active);
                w.WriteNumber("count", search.Count);
                w.WriteBoolean("close", search.Close);
                w.WriteNumber("highlight", search.Highlight.Peek());
                w.WriteNumber("focusTicket", search.FocusTicket.Peek());
                w.WriteBoolean("box", search.HasBox);
                if (allocBytes >= 0) w.WriteNumber("allocBytes", allocBytes);
                if (search.Close) w.WriteString("note", search.CloseLine);
                w.WriteStartArray("results");
                for (int i = 0; i < search.Count; i++)
                {
                    SettingsHit hit = search.Hit(i);
                    w.WriteStartObject();
                    w.WriteString("id", hit.Id);
                    w.WriteString("key", Wavee.Theming.Contracts.SettingsKeys.Result(hit.Id));
                    w.WriteString("title", hit.Title);
                    w.WriteString("breadcrumb", hit.Place);
                    ReadOnlySpan<Wavee.Theming.Contracts.SettingsTextSpan> spans = search.Spans(i);
                    w.WriteStartArray("spans");
                    foreach (var span in spans)
                    {
                        w.WriteStartArray();
                        w.WriteNumberValue(span.Start);
                        w.WriteNumberValue(span.Length);
                        w.WriteEndArray();
                    }
                    w.WriteEndArray();
                    w.WriteString("marked", Marked(hit.Title, spans));
                    if (hit.Under >= 0) w.WriteString("under", search.Hit(hit.Under).Id);
                    if (hit.Pointer is not null) w.WriteBoolean("pointer", true);
                    else if (hit.Dial is { } dial && bodies.Row(dial) is { } row)
                    {
                        w.WriteStartObject("row");
                        w.WriteString("kind", row.Kind.ToString().ToLowerInvariant());
                        WriteValue(w, row.Value);
                        w.WriteBoolean("enabled", row.Enabled);
                        if (row.Children.Length > 0) w.WriteNumber("children", row.Children.Length);
                        w.WriteEndObject();
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                if (search.NoMatch)
                {
                    w.WriteString("noMatch", search.NoMatchLine());
                    w.WriteString("didYouMeanLabel", search.DidYouMeanLabel);
                    w.WriteStartArray("didYouMean");
                    for (int i = 0; i < search.DidYouMeanCount; i++) w.WriteStringValue(search.DidYouMean(i));
                    w.WriteEndArray();
                    w.WriteString("searchApp", search.SearchAppLine());
                }
                w.WriteEndObject();
            });
        }

        /// <summary>`settings press=<down|up|enter|esc>`: the page box's key handler, as a key in the bound box runs it (Down/Up through the
        /// results, Enter opens the highlighted dial in its category, Escape clears then leaves); settles, replies the box's state and the route.</summary>
        string SettingsPress(int id, string name, Inspect.Args a)
        {
            string key = a.Str("press").ToLowerInvariant();
            int code = key switch
            {
                "down" => FluentGpu.Foundation.Keys.Down,
                "up" => FluentGpu.Foundation.Keys.Up,
                "enter" => FluentGpu.Foundation.Keys.Enter,
                "esc" or "escape" => FluentGpu.Foundation.Keys.Escape,
                _ => -1,
            };
            if (code < 0) return Inspect.Reply.Fail(id, name, "press wants down, up, enter or esc");
            SettingsSearchModel search = SettingsSearchModel.Current;
            bool handled = search.PreviewKey(new FluentGpu.Foundation.KeyEventArgs(code));
            void Body(Utf8JsonWriter w)
            {
                w.WriteString("press", key);
                w.WriteBoolean("handled", handled);
                w.WriteString("q", search.Query.Peek());
                w.WriteNumber("count", search.Count);
                w.WriteNumber("highlight", search.Highlight.Peek());
                if (search.Highlight.Peek() is var h and >= 0) w.WriteString("highlighted", search.Hit(h).Id);
                SettingsFocus focus = Settings.Focus.Peek();
                w.WriteStartObject("focus");
                w.WriteString("dial", focus.DialId);
                w.WriteNumber("seq", focus.Seq);
                w.WriteEndObject();
                w.WriteNumber("category", Settings.Category.Peek());
                w.WriteString("route", RouteText());
            }
            return _shell is null ? Inspect.Reply.Ok(id, name, Body) : Settled(id, name, a, Body);
        }

        static void WriteBodyRow(Utf8JsonWriter w, Wavee.Theming.Contracts.SettingsBodyRow row)
        {
            w.WriteStartObject();
            w.WriteString("id", row.Id);
            w.WriteString("kind", row.Kind.ToString().ToLowerInvariant());
            w.WriteString("label", row.Label);
            if (row.Description is { } description) w.WriteString("description", description);
            if (row.Subgroup is { } subgroup) w.WriteString("subgroup", subgroup);
            if (row.Glyph is { } glyph) w.WriteString("glyph", glyph);
            WriteValue(w, row.Value);
            if (row.Choices.Length > 0)
            {
                w.WriteStartArray("choices");
                foreach (string c in row.Choices) w.WriteStringValue(c);
                w.WriteEndArray();
            }
            w.WriteString("writer", row.Writer.Verb);
            if (!row.Enabled)
            {
                w.WriteBoolean("enabled", false);
                if (row.Reason is { } reason) w.WriteString("reason", reason);
                if (row.Silent) w.WriteBoolean("silent", true);
            }
            if (row.Tier != SettingsTier.Common) w.WriteString("tier", row.Tier.ToString().ToLowerInvariant());
            if (row.Explain is { } explain)
            {
                w.WriteStartObject("explain");
                w.WriteString("clip", explain.ClipId);
                w.WriteString("caption", explain.Caption);
                if (explain.ClipPath is { } path) w.WriteString("path", path);
                else w.WriteNull("path");
                w.WriteEndObject();
            }
            if (row.AlsoIn.Length > 0)
            {
                w.WriteString("alsoInLine", row.AlsoInLine);
                w.WriteStartArray("alsoIn");
                foreach (var place in row.AlsoIn)
                {
                    w.WriteStartObject();
                    w.WriteString("surface", place.Surface);
                    w.WriteString("place", place.Place);
                    w.WriteBoolean("door", place.Open is not null);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            if (row.Option is { } option) w.WriteString("option", option.QualifiedId);
            if (row.NoteBefore is { } before) w.WriteString("noteBefore", before.Title);
            if (row.NoteAfter is { } after) w.WriteString("noteAfter", after.Title);
            if (row.Children.Length > 0)
            {
                w.WriteStartArray("children");
                foreach (var child in row.Children) WriteBodyRow(w, child);
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }

        static void WriteValue(Utf8JsonWriter w, object? value)
        {
            switch (value)
            {
                case null: break;
                case bool b: w.WriteBoolean("value", b); break;
                case int i: w.WriteNumber("value", i); break;
                case float f: w.WriteNumber("value", Math.Round(f, 3)); break;
                case double d: w.WriteNumber("value", Math.Round(d, 3)); break;
                default: w.WriteString("value", Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)); break;
            }
        }

        /// <summary>The title with its matched runs in brackets ("[Cros]sfade"): the spans as a reader sees them.</summary>
        static string Marked(string title, ReadOnlySpan<Wavee.Theming.Contracts.SettingsTextSpan> spans)
        {
            if (spans.IsEmpty) return title;
            var sb = new System.Text.StringBuilder(title.Length + spans.Length * 2);
            int at = 0;
            foreach (var span in spans)
            {
                if (span.Start < at || span.Start + span.Length > title.Length) continue;
                sb.Append(title, at, span.Start - at).Append('[').Append(title, span.Start, span.Length).Append(']');
                at = span.Start + span.Length;
            }
            return sb.Append(title, at, title.Length - at).ToString();
        }
    }
}
