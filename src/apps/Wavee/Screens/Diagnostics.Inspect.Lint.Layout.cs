// ── Screens/Diagnostics.Inspect.Lint.Layout.cs — the report-only layout rules: align.inset, align.baseline, space.scale ────────────
// the owner's #1 complaint class ("inconsistent spacing", "misaligned"), judged from the app's own data: ink leads per scroll region and
// plate, rows of one key family against each other, and the DECLARED spacing against the theme's scale
//
// Role: CORE (pure: reads a prepared `InspectLint.Snapshot`). Level `todo`: every finding here is reported, never counted in the lint's
// summary, until the rule earns `gate` on the replay set (PLAN 5.6). Spec: PLAN 5.3; algorithms and false-positive classes: design-qa.md
// A2 (align.inset), A4 (align.baseline), B1 (space.scale); prototypes: scratch/agentic-e2e/design/proto/leads.py and columns.py.
//
// INK, NOT BOXES, for relations; DECLARED VALUES, not measured gaps, for "is this on the scale" (45-53 % of measured insets are off the
// 4-grid by centring residues and line boxes, design-qa 1.2). Every rule reads only nodes in the lint's scope.

using System.Globalization;
using FluentGpu.Foundation;
using Wavee.Theming;

namespace Wavee;

public static partial class Diagnostics
{
    public static partial class InspectLint
    {
        // ── align.inset ─────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The smallest and largest offset (DIP, either side) <c>align.inset</c> calls an inset drift; under it is a near miss (a later rule),
        /// over it a different column.</summary>
        public const float InsetMin = 4f, InsetMax = 16f;

        /// <summary>How close a lead may sit to a declared hang or indent and still be the intended one.</summary>
        const float IntentTolerance = 2f;

        /// <summary>Without a guide column, a column line left of the best-supported x must hold at least this share of its support (see step 4).</summary>
        public const float LineShare = 0.5f;

        static bool OffStart(string align) => align is "Center" or "End";

        /// <summary>Its x is set by centring or end alignment, not by a lead: the node or an ancestor up to its section root is placed Center or End
        /// by itself or by its parent (a column's AlignItems, a row's Justify), or its text is aligned off the start.</summary>
        static bool PlacedOffStart(Snapshot s, Node n)
        {
            if (n.Text is { } t && t.Align is "Center" or "End") return true;
            for (int i = n.Index; i >= 0; i = s.Nodes[i].Parent)
            {
                Node m = s.Nodes[i];
                if (OffStart(m.Layout.AlignSelf) || OffStart(m.Layout.JustifySelf)) return true;
                if (m.Parent >= 0)
                {
                    Node p = s.Nodes[m.Parent];
                    if (p.Column ? (m.Layout.AlignSelf == "Auto" && OffStart(p.Layout.AlignItems)) : p.Layout.Justify is "Center" or "End" or "SpaceAround" or "SpaceEvenly")
                        return true;
                }
                if (m.Key is { Length: > 0 }) break;   // the section root: its own placement was the last checked
            }
            return false;
        }

        /// <summary>The nearest keyed node at or above <paramref name="n"/> (-1: none): the section a lead belongs to.</summary>
        static int SectionOf(Snapshot s, Node n)
        {
            for (int i = n.Index; i >= 0; i = s.Nodes[i].Parent)
                if (s.Nodes[i].Key is { Length: > 0 }) return i;
            return -1;
        }

        static bool Under(Snapshot s, int node, int ancestor)
        {
            for (int i = node; i >= 0; i = s.Nodes[i].Parent)
                if (i == ancestor) return true;
            return false;
        }

        sealed class InsetGroup
        {
            public readonly List<Node> Leads = [];
            public readonly List<Node> FirstPerSection = [];
            public readonly HashSet<int> Sections = [];
            public float Line, X, D;
            public int Support;
            public string LinePatterns = "", Source = "";
            public LintPolicy? Intent;
        }

        /// <summary>A member that can lead the column: scrolling reaches it and no modal covers it (the column line is read over the whole
        /// scroll content, so it does not move while the page scrolls).</summary>
        static bool Reachable(Node n) => !n.Covered && !n.Reach.IsEmpty && InspectVisible.InScope(InspectVisible.Scope.Content, n.OnScreen, n.InContent, n.Cover);

        /// <summary>A fill, image or gradient as wide as its scroll region (or the window): a ground, not a lead.</summary>
        static bool FullBleed(Snapshot s, Node n)
        {
            if (n.Text is not null) return false;
            float width = n.Region >= 0 ? s.Nodes[n.Region].Rect.W : s.Width;
            return n.Rect.W >= 0.95f * width;
        }

        /// <summary><c>align.inset</c> (port of design-qa proto/leads.py). Algorithm:
        /// 1. members: ink (text, image, gradient, visible fill or border) that scrolling reaches (the WHOLE scroll content, not only the screen),
        ///    not a glyph, not in an animating subtree (a page sliding in is not where it lands), not inside a horizontal scroller (a rail's x is content-driven), not a PLATE (an ink node another ink
        ///    node sits on: it leads its own content, not the column), not a ground as wide as its region, not placed by centring or end alignment,
        ///    and not an end-aligned column (cells of one family and cell path whose right or centre edges agree while their left edges do not:
        ///    right-aligned numerals);
        /// 2. groups: (scroll region x backdrop), so a card's content is never compared with the page column;
        /// 3. leads: members that nothing of their group STARTS to the left of on an overlapping line (vertical overlap over 2 DIP), on reach rects.
        ///    "Nothing" counts every ink node of the group, the ones step 1 left out included: a label beside a play disc (a plate) or an icon
        ///    leads nothing, and neither does the back layer of stacked or offset art (the front layer starts left of it);
        /// 4. the column line: on the page's own ground (backdrop: the page) the guide's <c>Column</c> edge when at least 3 leads sit on it (Zune
        ///    and WinUI: the page title's x); else the x (to 0.1 DIP) the most leads share, at least 3, unless an x left of it holds at least
        ///    <see cref="LineShare"/> of that support (then the leftmost such x): read over the whole scroll content, so it does not move with the
        ///    scroll position, inset rows do not outvote the headers they sit under, and three outdented leads (a hang without a policy) do not move
        ///    a column of twenty;
        /// 5. a lead IN SCOPE 4 &lt; |d| &lt;= 16 off the line is a finding when its section (nearest keyed node) has no lead on the line (an inset
        ///    INSIDE a section that also leads on the line is the section's own design); one finding per (pattern, d rounded to 1 DIP), listing one
        ///    member per section.
        /// A guide <see cref="LintIntent.Hang"/> or <see cref="LintIntent.Indent"/> within 2 DIP of d makes it intended (suppressed).</summary>
        static int AlignInsetRule(Snapshot s, List<Finding> into)
        {
            IReadOnlyList<Node> nodes = s.Nodes;
            var plate = new bool[nodes.Count];
            foreach (Node n in nodes)
                if (n.Ink && n.Backdrop >= 0) plate[n.Backdrop] = true;
            var members = new List<Node>();
            var blockers = new Dictionary<(int Region, int Backdrop), List<Node>>();
            foreach (Node n in nodes)
            {
                if (!n.Ink || n.Moving || !Reachable(n) || FullBleed(s, n)) continue;
                var at = (n.Region, n.Backdrop);
                if (!blockers.TryGetValue(at, out List<Node>? ink)) blockers[at] = ink = [];
                ink.Add(n);
                if (n.InRail || plate[n.Index] || n.Text is { Glyph: true } || PlacedOffStart(s, n)) continue;
                members.Add(n);
            }
            ExcludeEndAligned(members);

            var columns = new List<LintEdge>();
            foreach (LintEdge e in s.Edges)
                if (e.Column && e.Side == LintSide.Left) columns.Add(e);

            var groups = new Dictionary<(int Region, int Backdrop), List<Node>>();
            foreach (Node n in members)
            {
                var key = (n.Region, n.Backdrop);
                if (!groups.TryGetValue(key, out List<Node>? list)) groups[key] = list = [];
                list.Add(n);
            }
            var found = new Dictionary<string, InsetGroup>(StringComparer.Ordinal);
            int judged = 0;
            foreach (var ((region, backdrop), list) in groups)
            {
                var leads = new List<Node>();
                List<Node> ink = blockers[(region, backdrop)];
                foreach (Node n in list)
                {
                    if (n.InScope) judged++;
                    RectF r = n.Reach;
                    bool led = false;
                    foreach (Node m in ink)
                    {
                        if (ReferenceEquals(m, n) || Under(s, m.Index, n.Index) || Under(s, n.Index, m.Index)) continue;
                        RectF o = m.Reach;
                        if (o.X < r.X - Slack && MathF.Min(r.Bottom, o.Bottom) - MathF.Max(r.Y, o.Y) > 2f) { led = true; break; }
                    }
                    if (!led) leads.Add(n);
                }
                float line = float.NaN, tolerance = Slack;
                int best = 0;
                string source = "";
                if (backdrop < 0)
                    foreach (LintEdge e in columns)
                    {
                        float tol = MathF.Max(Slack, e.Tolerance);
                        int c = 0;
                        foreach (Node n in leads)
                            if (MathF.Abs(n.Rect.X - e.Expected) <= tol) c++;
                        if (c >= 3 && c > best) { line = e.Expected; tolerance = tol; best = c; source = "the guide's " + e.Id + "; "; }
                    }
                if (float.IsNaN(line))
                {
                    var support = new Dictionary<float, int>();
                    foreach (Node n in leads)
                    {
                        float x = MathF.Round(n.Rect.X, 1);
                        support[x] = support.TryGetValue(x, out int c) ? c + 1 : 1;
                    }
                    int most = 0;
                    foreach (var (_, c) in support) most = Math.Max(most, c);
                    float need = MathF.Max(3f, LineShare * most);
                    foreach (var (x, c) in support)
                        if (c >= need && (float.IsNaN(line) || x < line)) { line = x; best = c; }
                    if (float.IsNaN(line)) continue;
                    source = best == most ? "the x most leads share; " : "the leftmost x holding half the best support; ";
                }
                var onLine = new List<Node>();
                foreach (Node n in leads)
                    if (MathF.Abs(n.Rect.X - line) <= tolerance) onLine.Add(n);
                string linePatterns = string.Join(", ", onLine.Select(static n => n.Pattern).Distinct().Take(4));
                foreach (Node n in leads)
                {
                    if (!n.InScope) continue;
                    float d = n.Rect.X - line;
                    if (MathF.Abs(d) <= InsetMin || MathF.Abs(d) > InsetMax) continue;
                    int section = SectionOf(s, n);
                    bool nested = false;
                    if (section >= 0)
                        foreach (Node m in onLine)
                            if (Under(s, m.Index, section)) { nested = true; break; }
                    if (nested) continue;
                    float bucket = MathF.Round(d);
                    string key = n.Pattern + "|" + F(bucket);
                    if (!found.TryGetValue(key, out InsetGroup? g))
                    {
                        found[key] = g = new InsetGroup { Line = line, X = MathF.Round(n.Rect.X, 1), D = bucket, Support = best, LinePatterns = linePatterns, Source = source };
                        LintPolicy? hang = PolicyFor(s, n, LintIntent.Hang) ?? PolicyFor(s, n, LintIntent.Indent);
                        if (hang is not null && MathF.Abs(d - hang.Value) <= IntentTolerance) g.Intent = hang;
                    }
                    g.Leads.Add(n);
                    if (g.Sections.Add(section)) g.FirstPerSection.Add(n);
                }
            }
            foreach (var (key, g) in found)
            {
                var (refs, rects) = Members(g.FirstPerSection);
                string pattern = key[..key.LastIndexOf('|')];
                int sections = g.Sections.Count;
                string what = sections > 1 ? $"{pattern} ({sections} {Plural(sections, "section", "sections")})" : pattern;
                var f = new Finding(Rules.AlignInset, Severity.Warn,
                    $"{what} leads at x={F(g.X)}; its region's column line is x={F(g.Line)} ({g.Source}{g.Support} leads on it: {g.LinePatterns}): {Signed(g.D)}", refs)
                {
                    Measured = [M("x", g.X), M("d", g.D)],
                    Expected = [M("x", g.Line), M("support", g.Support)],
                    Pattern = pattern,
                    Cause = "lead:" + Signed(g.D),
                    Count = sections,
                    Rects = rects,
                    Nodes = Ids(g.Leads),
                };
                if (g.Intent is { } intent)
                    f = f with { Outcome = Outcome.Suppressed, SuppressedBy = "intent", Reason = intent.Reason };
                into.Add(f);
            }
            return judged;
        }

        /// <summary>Drop the cells of an end-aligned column: members of one family and cell path (3 or more) whose left edges spread over 0.5 DIP while
        /// their right or centre edges agree within it (right-aligned numerals lead nothing).</summary>
        static void ExcludeEndAligned(List<Node> members)
        {
            var cells = new Dictionary<string, List<Node>>(StringComparer.Ordinal);
            foreach (Node n in members)
            {
                if (n.Family.Length == 0) continue;
                string key = n.Family + "/" + n.CellPath;
                if (!cells.TryGetValue(key, out List<Node>? list)) cells[key] = list = [];
                list.Add(n);
            }
            var drop = new HashSet<Node>(ReferenceEqualityComparer.Instance);
            foreach (var (_, list) in cells)
            {
                if (list.Count < 3) continue;
                float minL = float.MaxValue, maxL = float.MinValue, minR = float.MaxValue, maxR = float.MinValue, minC = float.MaxValue, maxC = float.MinValue;
                foreach (Node n in list)
                {
                    float l = n.Rect.X, r = n.Rect.Right, c = (l + r) / 2f;
                    minL = MathF.Min(minL, l); maxL = MathF.Max(maxL, l);
                    minR = MathF.Min(minR, r); maxR = MathF.Max(maxR, r);
                    minC = MathF.Min(minC, c); maxC = MathF.Max(maxC, c);
                }
                if (maxL - minL > Slack && (maxR - minR <= Slack || maxC - minC <= Slack))
                    foreach (Node n in list) drop.Add(n);
            }
            if (drop.Count > 0) members.RemoveAll(drop.Contains);
        }

        // ── align.baseline ──────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The first in-scope word text at or under <paramref name="root"/> (depth-first), or null.</summary>
        static Node? FirstText(Snapshot s, int root, int[] last)
        {
            for (int i = root; i <= last[root]; i++)
            {
                Node m = s.Nodes[i];
                if (m.InScope && IsWords(m)) return m;
            }
            return null;
        }

        /// <summary>The nearest instance root above <paramref name="n"/> (itself excluded) whose family differs from <paramref name="family"/>: its row.</summary>
        static int RowOf(Snapshot s, Node n, string family)
        {
            for (int i = n.Parent; i >= 0; i = s.Nodes[i].Parent)
            {
                Node m = s.Nodes[i];
                if (m.Instance == i && !string.Equals(m.Family, family, StringComparison.Ordinal)) return i;
            }
            return -1;
        }

        sealed class Drift
        {
            public readonly List<(Node Text, float Offset)> Cells = [];
            public readonly List<float> Bottoms = [];
            public readonly List<float> Centres = [];

            /// <summary>Each row's height, and the height and width of the box the text is measured in (the keyed cell, or the row itself).</summary>
            public readonly List<float> Heights = [];
            public readonly List<float> BoxH = [];
            public readonly List<float> BoxW = [];

            public void Add(Node text, RectF row, RectF box)
            {
                Cells.Add((text, text.Rect.Y - row.Y));
                Bottoms.Add(text.Rect.Bottom - row.Y);
                Centres.Add(text.Rect.Y + text.Rect.H / 2f - row.Y);
                Heights.Add(row.H);
                BoxH.Add(box.H);
                BoxW.Add(box.W);
            }
            public readonly HashSet<int> Rows = [];
            public readonly List<int> RowIds = [];
            public string Row = "";
            public string Style = "";
        }

        /// <summary>Do the drifts follow the boxes' heights? Every cell off the most common offset sits off it by as much (within 1 DIP) as its box
        /// is taller or shorter than the boxes at that offset: a block above the text grew or shrank (an optional header, a wide card's shorter
        /// art), which moves the text with it. Not a drift inside one row design.</summary>
        static bool PushedByBox(Drift d, float mode)
        {
            var reference = new List<float>();
            for (int i = 0; i < d.Cells.Count; i++)
                if (MathF.Abs(d.Cells[i].Offset - mode) <= Slack) reference.Add(d.BoxH[i]);
            if (reference.Count == 0) return false;
            reference.Sort();
            float href = reference[reference.Count / 2];
            bool any = false;
            for (int i = 0; i < d.Cells.Count; i++)
            {
                float off = d.Cells[i].Offset - mode;
                if (MathF.Abs(off) <= Slack) continue;
                any = true;
                if (MathF.Abs(off - (d.BoxH[i] - href)) > 1f) return false;
            }
            return any;
        }

        static float Spread(List<float> values)
        {
            float min = float.MaxValue, max = float.MinValue;
            foreach (float v in values) { min = MathF.Min(min, v); max = MathF.Max(max, v); }
            return values.Count == 0 ? 0f : max - min;
        }

        /// <summary><c>align.baseline</c> (port of design-qa proto/columns.py "row-offset", without its false positive). Algorithm:
        /// (a) KEYED CELLS: for every instance of a repeated family F that sits inside an instance of ANOTHER repeated family G (its row), the offset of
        ///     F's first text from the row's top; grouped by (F, G), one cell per row (the cells of ONE row stack: their offsets differ by design);
        /// (b) UNKEYED CELLS: for every instance of a repeated family G that is a row (no other family's instance above it), the offset of each text
        ///     whose nearest instance is that row, from the row's top; grouped by (G, cell path).
        /// A group of 3 or more whose offsets spread over 0.5 DIP drifts: the text sits at different heights from row to row (a centred stack whose
        /// optional line comes and goes), unless its bottoms or its centres agree (a bottom-anchored or centred caption whose line count varies),
        /// or its "rows" are no rows: instances whose heights differ by more than max(2 DIP, 10 %) are sections of a page (a key family is a
        /// static key part, so `zs:title` and `zs:link:*` are never one family; settings sections whose first one has no top rule differ in
        /// height), or its keyed cells come in different widths (a shelf of wide cards beside shelves of square ones: another card form), or every
        /// drift follows its box's height (<see cref="PushedByBox"/>: an optional block above the text). The rows themselves are never measured
        /// against the list they sit in (that offset only steps by the row height: the prototype's false positive). When several groups hold the
        /// SAME texts (a card's title measured against its strip, its shelf and its zone), only the innermost row's finding stays. An unkeyed cell
        /// is named by its type style (<c>text 14/400</c>), never by a dump index path. Offsets are line-box tops, not true baselines, so a finding
        /// is a `review`: look at the rows.</summary>
        static int AlignBaselineRule(Snapshot s, List<Finding> into)
        {
            IReadOnlyList<Node> nodes = s.Nodes;
            int count = nodes.Count;
            var last = new int[count];
            for (int i = 0; i < count; i++) last[i] = i;
            for (int i = count - 1; i >= 0; i--)
            {
                int p = nodes[i].Parent;
                if (p >= 0 && last[i] > last[p]) last[p] = last[i];
            }
            var drifts = new Dictionary<string, Drift>(StringComparer.Ordinal);
            int looked = 0;
            foreach (Node n in nodes)
            {
                if (!n.InScope || n.Instance != n.Index) continue;
                int row = RowOf(s, n, n.Family);
                if (row >= 0)
                {
                    // (a) a keyed cell inside a row
                    if (FirstText(s, n.Index, last) is not { } text) continue;
                    looked++;
                    string key = "a|" + PatternOfKey(n.Key!) + "|" + nodes[row].Family;
                    if (!drifts.TryGetValue(key, out Drift? d)) drifts[key] = d = new Drift { Row = PatternOfKey(nodes[row].Key!) };
                    if (d.Rows.Add(row))   // the first such cell of each row: cells of one row stack
                    {
                        d.Add(text, nodes[row].Rect, n.Rect);
                        d.RowIds.Add(row);
                    }
                    continue;
                }
                // (b) a row: its own texts by cell path
                for (int i = n.Index + 1; i <= last[n.Index]; i++)
                {
                    Node m = nodes[i];
                    if (!m.InScope || m.Instance != n.Index || !IsWords(m)) continue;
                    looked++;
                    string key = "b|" + PatternOfKey(n.Key!) + "/" + m.CellPath;
                    if (!drifts.TryGetValue(key, out Drift? d))
                        drifts[key] = d = new Drift { Row = PatternOfKey(n.Key!), Style = "text " + F(m.Text!.Size) + "/" + m.Text.Weight.ToString(CultureInfo.InvariantCulture) };
                    if (d.Rows.Add(n.Index))
                    {
                        d.Add(m, n.Rect, n.Rect);
                        d.RowIds.Add(n.Index);
                    }
                }
            }
            // Drifting groups first; the same texts measured against nested rows (a card's title against its strip, its shelf and its zone) are one
            // drift, judged once at the innermost row (the deepest row is a descendant of the outer ones: the larger index). Only then the tests
            // that excuse a drift, so a drift a card form explains at the card is not reported again through the shelf.
            var drifting = new List<(string Key, Drift Drift, string Members, int Row)>();
            foreach (var (key, d) in drifts)
            {
                if (d.Cells.Count < 3 || Spread(d.Cells.Select(static c => c.Offset).ToList()) <= Slack) continue;   // three rows at least, and a drift
                drifting.Add((key, d, string.Join(",", d.Cells.Select(static c => c.Text.Index).Order()), d.RowIds.Max()));
            }
            foreach (var (key, d, members, row) in drifting)
            {
                bool outer = false;
                foreach (var other in drifting)
                    if (!ReferenceEquals(other.Drift, d) && other.Members == members && other.Row > row) { outer = true; break; }
                if (outer) continue;
                float min = float.MaxValue, max = float.MinValue;
                var modes = new Dictionary<float, int>();
                foreach (var (_, offset) in d.Cells)
                {
                    min = MathF.Min(min, offset);
                    max = MathF.Max(max, offset);
                    float b = MathF.Round(offset * 2f) / 2f;
                    modes[b] = modes.TryGetValue(b, out int c) ? c + 1 : 1;
                }
                if (Spread(d.Bottoms) <= Slack || Spread(d.Centres) <= Slack) continue;   // anchored at its bottom or centre: a line count, not a drift
                if (Spread(d.Heights) > MathF.Max(2f, 0.1f * d.Heights.Min())) continue;  // not rows: sections of different heights (a first one without its top rule)
                bool keyed = key[0] == 'a';
                if (keyed && Spread(d.BoxW) > MathF.Max(2f, 0.1f * d.BoxW.Min())) continue;   // cells of different widths: another card form
                float mode = modes.OrderByDescending(static kv => kv.Value).ThenBy(static kv => kv.Key).First().Key;
                if (PushedByBox(d, mode)) continue;   // an optional block above the text moved it: the box grew by as much
                string cell = keyed ? key[2..key.LastIndexOf('|')] : d.Style;
                string spread = string.Join(", ", modes.OrderByDescending(static kv => kv.Value).ThenBy(static kv => kv.Key)
                    .Take(4).Select(static kv => F(kv.Key) + " in " + kv.Value.ToString(CultureInfo.InvariantCulture)));
                var (refs, rects) = Members(d.Cells.Select(static c => c.Text));
                into.Add(new Finding(Rules.AlignBaseline, Severity.Warn,
                    $"{(keyed ? cell + "'s first text" : cell + " in " + d.Row)} sits at different heights below its row top ({d.Row}): {spread} rows; it drifts {F(max - min)} DIP from row to row", refs)
                {
                    Measured = [M("spread", max - min), M("min", min), M("max", max), M("rows", d.Cells.Count)],
                    Expected = [M("spread", 0)],
                    Pattern = cell,
                    Cause = "rows:" + d.Row,
                    Count = d.Cells.Count,
                    Outcome = Outcome.Review,
                    Reason = "line-box tops, not measured baselines: look at the rows",
                    Rects = rects,
                    Nodes = Ids(d.Cells.Select(static c => c.Text)),
                });
            }
            return looked;
        }

        // ── space.scale ─────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The padding values engine controls use inside themselves (WinUI's button, toggle, combo, menu item, chip insets: design-qa 3.5): a
        /// value in this set under a control with a role is the control's, not the view's.</summary>
        static readonly float[] s_controlInternal = [3f, 5f, 6f, 7f, 9f, 10f, 11f, 14f, 15f];

        /// <summary>The largest multiple of the 4-DIP grid that is on the scale by itself.</summary>
        const float GridMax = 64f;

        static bool Near(float a, float b) => MathF.Abs(a - b) <= 0.01f;

        /// <summary>On the design scale: 2, a multiple of 4 up to 64, or a value of the theme's scale (<see cref="Snapshot.Scale"/>).</summary>
        static bool OnScale(Snapshot s, float v)
        {
            if (Near(v, 2f)) return true;
            if (v <= GridMax + 0.01f && Near(v, MathF.Round(v / 4f) * 4f)) return true;
            foreach (ScaleEntry e in s.Scale)
                if (Near(e.Value, v)) return true;
            return false;
        }

        /// <summary>The scale value nearest <paramref name="v"/>: a named spacing token of the theme (<c>Spacing.*</c>, <c>R.Space.*</c>; radii count as
        /// on the scale but are never offered as a spacing), else the 4-grid. A multiple of 4 past 64 says so.</summary>
        static string NearestToken(Snapshot s, float v)
        {
            if (v > GridMax && Near(v, MathF.Round(v / 4f) * 4f)) return "none: a multiple of 4 past " + F(GridMax) + " (a derived value? take it from the tier record)";
            string name = "";
            float best = float.MaxValue, value = 0f;
            foreach (ScaleEntry e in s.Scale)
            {
                if (e.Name.StartsWith("R.Shape.", StringComparison.Ordinal)) continue;
                float d = MathF.Abs(e.Value - v);
                if (d < best - 0.001f) { best = d; name = e.Name; value = e.Value; }
            }
            float grid = MathF.Max(4f, MathF.Round(v / 4f) * 4f);
            if (grid <= GridMax && MathF.Abs(grid - v) < best - 0.001f) return F(grid) + " (the 4-grid)";
            return name.Length > 0 ? F(value) + " = " + name : F(grid) + " (the 4-grid)";
        }

        static bool UnderControl(Snapshot s, Node n)
        {
            for (int i = n.Index; i >= 0; i = s.Nodes[i].Parent)
            {
                Node m = s.Nodes[i];
                if (m.Interactive && m.Role.Length > 0) return true;
            }
            return false;
        }

        sealed class ScaleGroup
        {
            public readonly List<Node> Nodes = [];
            public readonly List<string> Props = [];
            public LintPolicy? Intent;
        }

        /// <summary><c>space.scale</c> (design-qa B1). Algorithm: for every in-scope node with ink under it (a value nothing shows is moot), each
        /// DECLARED gap (when it has two or more children), padding side and margin side that is not 0 must be on the theme's scale: 2, a multiple
        /// of 4 up to 64, or a value of the active definition's <c>Spacing.*</c>, <c>R.Space</c> or <c>R.Shape</c> (the capture lists them). A value
        /// of the control-internal set under a control with a role is the control's. Grouped by (value, the node's nearest keyed pattern: not a
        /// family counted over the scope, so the fingerprint holds at every scope and scroll position); one info per group naming the nearest
        /// token. A guide <see cref="LintIntent.OffScaleOk"/> makes it intended.</summary>
        static int SpaceScaleRule(Snapshot s, List<Finding> into)
        {
            IReadOnlyList<Node> nodes = s.Nodes;
            var children = new int[nodes.Count];
            foreach (Node n in nodes)
                if (n.Parent >= 0) children[n.Parent]++;
            var groups = new Dictionary<string, ScaleGroup>(StringComparer.Ordinal);
            int looked = 0;
            Span<float> values = stackalloc float[9];
            Span<int> props = stackalloc int[9];
            string[] names = ["gap", "padding-left", "padding-top", "padding-right", "padding-bottom", "margin-left", "margin-top", "margin-right", "margin-bottom"];
            foreach (Node n in nodes)
            {
                if (!n.InScope || n.InkCount == 0) continue;
                Declared l = n.Layout;
                int k = 0;
                if (children[n.Index] >= 2 && l.Gap != 0f) { values[k] = l.Gap; props[k++] = 0; }
                if (l.Padding.Left != 0f) { values[k] = l.Padding.Left; props[k++] = 1; }
                if (l.Padding.Top != 0f) { values[k] = l.Padding.Top; props[k++] = 2; }
                if (l.Padding.Right != 0f) { values[k] = l.Padding.Right; props[k++] = 3; }
                if (l.Padding.Bottom != 0f) { values[k] = l.Padding.Bottom; props[k++] = 4; }
                if (l.Margin.Left != 0f) { values[k] = l.Margin.Left; props[k++] = 5; }
                if (l.Margin.Top != 0f) { values[k] = l.Margin.Top; props[k++] = 6; }
                if (l.Margin.Right != 0f) { values[k] = l.Margin.Right; props[k++] = 7; }
                if (l.Margin.Bottom != 0f) { values[k] = l.Margin.Bottom; props[k++] = 8; }
                for (int j = 0; j < k; j++)
                {
                    float v = MathF.Abs(values[j]);
                    if (!float.IsFinite(v)) continue;
                    looked++;
                    if (OnScale(s, v)) continue;
                    bool control = false;
                    foreach (float c in s_controlInternal)
                        if (Near(c, v)) { control = true; break; }
                    if (control && UnderControl(s, n)) continue;
                    string pattern = n.Pattern;
                    string key = F(MathF.Round(v, 2)) + "|" + pattern;
                    if (!groups.TryGetValue(key, out ScaleGroup? g)) groups[key] = g = new ScaleGroup { Intent = PolicyFor(s, n, LintIntent.OffScaleOk) };
                    if (!g.Nodes.Contains(n)) g.Nodes.Add(n);
                    if (!g.Props.Contains(names[props[j]])) g.Props.Add(names[props[j]]);
                }
            }
            foreach (var (key, g) in groups)
            {
                float v = float.Parse(key[..key.IndexOf('|')], CultureInfo.InvariantCulture);
                string pattern = key[(key.IndexOf('|') + 1)..];
                var (refs, rects) = Members(g.Nodes);
                var f = new Finding(Rules.SpaceScale, Severity.Info,
                    $"{F(v)} is off the {(s.Theme.Length > 0 ? s.Theme : "theme")} scale: {string.Join(", ", g.Props)} on {pattern} ({g.Nodes.Count} {Plural(g.Nodes.Count, "node", "nodes")}); nearest {NearestToken(s, v)}", refs)
                {
                    Measured = [M("value", v), M("count", g.Nodes.Count)],
                    Pattern = pattern,
                    Cause = "value:" + F(v),
                    Count = g.Nodes.Count,
                    Rects = rects,
                    Nodes = Ids(g.Nodes),
                };
                if (g.Intent is { } intent)
                    f = f with { Outcome = Outcome.Suppressed, SuppressedBy = "intent", Reason = intent.Reason };
                into.Add(f);
            }
            return looked;
        }
    }
}
