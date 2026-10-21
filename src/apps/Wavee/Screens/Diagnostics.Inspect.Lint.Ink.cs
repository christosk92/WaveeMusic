// ── Screens/Diagnostics.Inspect.Lint.Ink.cs — the report-only "what shows" rules: text.duplicate, target.ink, space.dead ───────────
// what a person sees that the scene does not say is wrong: one label shown twice within a glance, a click target that draws nothing, and
// a slot that keeps its room while nothing fills it
//
// Role: CORE (pure: reads a prepared `InspectLint.Snapshot`). Level `todo`: reported, never counted in the summary, until a rule earns
// `gate` on the replay set (docs/guide/agent-lint.md, "Promotion"). The flaws the P1 verifier saw by eye on the reference mounts: the Zune
// album aside's heading "About the artist" repeated as the card's eyebrow, the album list header's number column (a 28 x 36 sort target
// that paints nothing), and "Nothing playing" beside an empty art slot in the play bar.

using System.Text;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee;

public static partial class Diagnostics
{
    public static partial class InspectLint
    {
        /// <summary>Per node: it or something in its subtree paints (ink, or a drawn visual a fill does not describe: a progress ring's arc).</summary>
        static bool[] Draws(Snapshot s)
        {
            IReadOnlyList<Node> nodes = s.Nodes;
            var draws = new bool[nodes.Count];
            for (int i = nodes.Count - 1; i >= 0; i--)
            {
                Node n = nodes[i];
                if (n.Ink || n.Visual.Length > 0) draws[i] = true;
                if (draws[i] && n.Parent >= 0) draws[n.Parent] = true;
            }
            return draws;
        }

        /// <summary>The page a node sits in: its nearest keep-alive page slot (<see cref="TrySlotRoute"/>), -1 outside every page (the title bar,
        /// the band, the play bar: chrome around the page).</summary>
        static int PageOf(Snapshot s, Node n)
        {
            for (int i = n.Index; i >= 0; i = s.Nodes[i].Parent)
                if (s.Nodes[i].Key is { } key && TrySlotRoute(key, out _, out _)) return i;
            return -1;
        }

        // ── text.duplicate ──────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The farthest apart (DIP, the vertical gap between the two boxes) two copies of a label may sit and still be read together:
        /// about a card's height, the about card's art between a heading and its eyebrow.</summary>
        public const float DuplicateReach = 240f;

        /// <summary>A label as a person compares it: case folded, runs of white space made one, trimmed (Zune lowercases a heading on screen
        /// that a view wrote capitalised).</summary>
        static string Folded(string text)
        {
            var b = new StringBuilder(text.Length);
            bool space = false;
            foreach (char c in text.Trim())
            {
                if (char.IsWhiteSpace(c)) { space = true; continue; }
                if (space && b.Length > 0) b.Append(' ');
                space = false;
                b.Append(char.ToLowerInvariant(c));
            }
            return b.ToString();
        }

        /// <summary>The run is a control's label (a button's or a link's words, not a content card's): <c>duplicate.action</c> owns a verb seen
        /// twice, and a "See all" per shelf is one link per shelf.</summary>
        static bool ControlLabel(Snapshot s, Node n, TextRun t)
        {
            for (int i = n.Parent; i >= 0; i = s.Nodes[i].Parent)
            {
                Node a = s.Nodes[i];
                if (!a.Interactive) continue;
                return a.TextCount < 2 && a.ImageCount == 0 && string.Equals(a.Label, t.String, StringComparison.Ordinal);
            }
            return false;
        }

        /// <summary>The nearest click target at or above <paramref name="n"/> (-1: none).</summary>
        static int TargetOf(Snapshot s, Node n)
        {
            for (int i = n.Index; i >= 0; i = s.Nodes[i].Parent)
                if (s.Nodes[i].Interactive) return i;
            return -1;
        }

        /// <summary>The two runs repeat ONE STRUCTURE: below their lowest common ancestor, each sits at the same child-ordinal path inside its own
        /// branch (two rows of a list, two release-note sections, two rails that show the same album card): row content, not a label said twice.</summary>
        static bool SameStructure(Snapshot s, Node a, Node b, int[] ordinal)
        {
            var up = new HashSet<int>();
            for (int i = a.Index; i >= 0; i = s.Nodes[i].Parent) up.Add(i);
            int lca = b.Index;
            while (lca >= 0 && !up.Contains(lca)) lca = s.Nodes[lca].Parent;
            if (lca < 0 || lca == a.Index || lca == b.Index) return false;
            var pa = new List<int>();
            for (int i = a.Index; s.Nodes[i].Parent != lca; i = s.Nodes[i].Parent) pa.Add(ordinal[i]);
            var pb = new List<int>();
            for (int i = b.Index; s.Nodes[i].Parent != lca; i = s.Nodes[i].Parent) pb.Add(ordinal[i]);
            // Two runs that are direct children of one stack are no repeated structure (both paths empty): a heading and a same-words eyebrow
            // under it in one column, the core case.
            return pa.Count > 0 && pa.SequenceEqual(pb);
        }

        sealed class DuplicateGroup
        {
            public readonly List<Node> Members = [];
            public string Text = "";
            public float Gap;
        }

        /// <summary><c>text.duplicate</c> (warn, todo): the same visible words twice in one column within <see cref="DuplicateReach"/>, the Zune
        /// album aside's heading "About the artist" and, under the card's art, its eyebrow "About the artist". Algorithm:
        /// 1. runs: in-scope word runs (not glyphs, not mid-animation, at least half opaque) of 4 or more characters with a letter, folded
        ///    (<see cref="Folded"/>); not a control's label (<see cref="ControlLabel"/>: a verb seen twice is <c>duplicate.action</c>'s, a "See
        ///    all" per shelf is one link per shelf), not in `allowDuplicate`;
        /// 2. pairs: two runs of one folded text in one scroll region whose boxes share x (one column) and stand at most
        ///    <see cref="DuplicateReach"/> apart vertically; not a glow copy (the same box), and NOT REPEATED CONTENT: two runs in different
        ///    instances of one repeated key family (two track rows, two shelves' "See all", one album in two shelves) are rows, not a repeat, and
        ///    neither are two runs at the same child path below their lowest common ancestor (<see cref="SameStructure"/>: unkeyed rows, sections
        ///    that each carry a "closed" tag) or two runs that both sit in click targets (cards and rows: a show named like its publisher, one
        ///    album in two rails; their words are content). A heading outside the card and its eyebrow inside it is the class;
        /// 3. one finding per (region, text), its members the copies in reading order; named by the later copy's pattern, its cause the first
        ///    copy's pattern (`repeats:zune.album.about:head`). A guide <see cref="LintIntent.Allow"/> for the rule makes it intended.</summary>
        static int DuplicateTextRule(Snapshot s, Options o, List<Finding> into)
        {
            int looked = 0;
            var byText = new Dictionary<(int Region, int Page, string Text), List<Node>>();
            foreach (Node n in s.Nodes)
            {
                if (!n.InScope || n.Moving || n.Opacity < o.IncidentalOpacity || n.Text is not { Glyph: false } t || t.String.Length == 0) continue;
                if (KeyedUnder(s, n, o.IgnoreUnder)) continue;   // the title bar's crumbs and the play bar repeat the page's words by design
                string folded = Folded(t.String);
                if (folded.Length < 4 || !folded.Any(char.IsLetter) || ControlLabel(s, n, t)) continue;
                bool allowed = false;
                foreach (string label in o.AllowDuplicate)
                    if (string.Equals(Folded(label), folded, StringComparison.Ordinal)) { allowed = true; break; }
                if (allowed) continue;
                looked++;
                var key = (n.Region, PageOf(s, n), folded);
                if (!byText.TryGetValue(key, out List<Node>? list)) byText[key] = list = [];
                list.Add(n);
            }
            var ordinal = new int[s.Nodes.Count];
            var children = new int[s.Nodes.Count];
            foreach (Node n in s.Nodes)
                if (n.Parent >= 0) ordinal[n.Index] = children[n.Parent]++;
            var groups = new List<DuplicateGroup>();
            foreach (var (_, list) in byText)
            {
                if (list.Count < 2) continue;
                list.Sort(static (a, b) => a.Rect.Y != b.Rect.Y ? a.Rect.Y.CompareTo(b.Rect.Y) : a.Rect.X.CompareTo(b.Rect.X));
                DuplicateGroup? g = null;
                for (int i = 0; i < list.Count; i++)
                    for (int j = i + 1; j < list.Count; j++)
                    {
                        Node a = list[i], b = list[j];
                        if (MathF.Abs(a.Rect.X - b.Rect.X) <= 1f && MathF.Abs(a.Rect.Y - b.Rect.Y) <= 1f) continue;   // a glow copy: one label
                        if (MathF.Min(a.Rect.Right, b.Rect.Right) - MathF.Max(a.Rect.X, b.Rect.X) <= 0f) continue;   // side by side: two columns
                        float gap = MathF.Max(a.Rect.Y, b.Rect.Y) - MathF.Min(a.Rect.Bottom, b.Rect.Bottom);
                        if (gap > DuplicateReach) continue;
                        if (a.Family.Length > 0 && string.Equals(a.Family, b.Family, StringComparison.Ordinal) && a.Instance != b.Instance) continue;   // rows
                        if (SameStructure(s, a, b, ordinal)) continue;   // one structure repeated: rows, sections, two cards of one album
                        if (TargetOf(s, a) >= 0 && TargetOf(s, b) >= 0) continue;   // both inside cards or rows: their words are content (a show and its publisher)
                        g ??= new DuplicateGroup { Text = a.Text!.String, Gap = gap };
                        if (!g.Members.Contains(a)) g.Members.Add(a);
                        if (!g.Members.Contains(b)) g.Members.Add(b);
                        g.Gap = MathF.Min(g.Gap, gap);
                    }
                if (g is not null) groups.Add(g);
            }
            foreach (DuplicateGroup g in groups)
            {
                g.Members.Sort(static (a, b) => a.Index.CompareTo(b.Index));
                Node first = g.Members[0], repeat = g.Members[^1];
                var (refs, rects) = Members(g.Members);
                into.Add(new Finding(Rules.TextDuplicate, Severity.Warn,
                    $"{Q(g.Text)} shows {g.Members.Count} times in one column, {F(MathF.Max(0f, g.Gap))} apart: {first.Ref} ({first.Pattern}) and {repeat.Ref} ({repeat.Pattern})", refs)
                {
                    Measured = [M("copies", g.Members.Count), M("gap", g.Gap)],
                    Expected = [M("copies", 1)],
                    Pattern = repeat.Pattern,
                    Cause = "repeats:" + first.Pattern,
                    Count = g.Members.Count,
                    Rects = rects,
                    Nodes = Ids(g.Members),
                });
            }
            return looked;
        }

        // ── target.ink ──────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The share of the window a target with no ink may cover and still be judged: past it, a light-dismiss catcher (the click outside
        /// a popup), not a control.</summary>
        const float CatcherShare = 0.5f;

        /// <summary>The share of a target's box that content drawn by another node (text, a glyph, an image, a gradient, a drawn visual: a chart's
        /// curve) must cover for the target to show through it (a hit band laid over a chart).</summary>
        const float ContentUnder = 0.25f;

        /// <summary>Content another node draws (not its ancestors' or its own subtree's) covers at least <see cref="ContentUnder"/> of the target's box
        /// where it shows, and fits the target's group (its first larger ancestor): the target is the clickable part of something a person sees (the
        /// tempo chart's bands). A page backdrop that spreads past the group (a material bleed, a wash) excuses nothing.</summary>
        static bool OverContent(Snapshot s, Node t, bool[] draws)
        {
            float area = t.Rect.W * t.Rect.H;
            if (area <= 0f) return false;
            // The target's GROUP: its first ancestor whose box is larger than its own (the tempo chart's band row). Content that shows through a
            // target belongs to that group and fits its box (the chart's curve); a page backdrop (the artist page's material bleed, a wash
            // gradient) spreads far past it and excuses nothing.
            RectF group = t.Rect;
            for (int i = t.Parent; i >= 0; i = s.Nodes[i].Parent)
            {
                RectF r = s.Nodes[i].Rect;
                if (r.W > t.Rect.W + 0.5f || r.H > t.Rect.H + 0.5f) { group = r; break; }
            }
            foreach (Node m in s.Nodes)
            {
                if (m.Index == t.Index || !(m.Text is not null || m.Image || m.Gradient || m.Visual.Length > 0) || m.Visible.IsEmpty) continue;
                RectF both = m.Visible.Intersect(t.Rect);
                if (both.IsEmpty || both.W * both.H < ContentUnder * area || Related(s, m, t)) continue;
                if (m.Rect.X < group.X - Slack || m.Rect.Y < group.Y - Slack || m.Rect.Right > group.Right + Slack || m.Rect.Bottom > group.Bottom + Slack)
                    continue;   // a backdrop behind the group, not its content
                return true;
            }
            return false;
        }

        /// <summary><c>target.ink</c> (warn, todo): a click target a person cannot see, the Zune album list header's number column (<c>c.num</c>,
        /// 28 x 36, sorts by track number) that paints nothing at rest. Algorithm: in-scope, enabled, pressable targets (the target rule's set;
        /// the track controls it exempts too) whose subtree holds no ink (no text, image, gradient, visible fill or border: a hover fill is not
        /// ink at rest), unless the click shows elsewhere (an ancestor that runs the same click holds ink: the row a target sits in), content
        /// another node draws covers a quarter of it or more (<see cref="OverContent"/>: the tempo chart's hit bands; never a page backdrop), it is a drag bar (a splitter shows its cursor, not ink), or it covers half the window or more (a light-dismiss catcher). Grouped by the target
        /// family (its parent's pattern), as `target.size` groups. Fix: give the target ink at rest (its label or glyph), or take the click away
        /// from a slot that draws nothing (never hide a feature).</summary>
        static int TargetInkRule(Snapshot s, Options o, List<Finding> into)
        {
            int looked = 0;
            var groups = new Dictionary<string, List<Node>>(StringComparer.Ordinal);
            float window = s.Width * s.Height;
            bool[] draws = Draws(s);
            foreach (Node n in s.Nodes)
            {
                if (!n.InScope || !n.Interactive || n.Disabled || !n.Pressable || ExemptFromTarget(n)) continue;
                looked++;
                if (draws[n.Index] || n.Rect.W * n.Rect.H >= CatcherShare * window) continue;
                if (n.ClickId == 0 && Drags(s, n)) continue;   // a drag bar: a splitter shows its cursor
                if (IsBar(s, n, EffectiveArea(s, n), s.Tier == WidthTier.Compact ? o.TargetFloorCompact : o.TargetFloor)) continue;
                bool shows = false;
                int click = n.ClickId;
                for (int i = n.Parent; i >= 0 && !shows; i = s.Nodes[i].Parent)
                {
                    Node a = s.Nodes[i];
                    if (!a.Interactive) continue;
                    if (click == 0 && a.ClickId != 0) click = a.ClickId;
                    else if (a.ClickId != click) break;
                    shows = draws[i];
                }
                if (shows || OverContent(s, n, draws)) continue;
                string family = TargetFamily(s, n);
                if (!groups.TryGetValue(family, out List<Node>? list)) groups[family] = list = [];
                list.Add(n);
            }
            foreach (var (pattern, list) in groups)
            {
                var (refs, rects) = Members(list);
                string sizes = string.Join(", ", list.Take(4).Select(static n => n.Ref + " " + F(n.Rect.W) + " x " + F(n.Rect.H) + (n.Role.Length > 0 ? " (" + n.Role + ")" : "")))
                    + (list.Count > 4 ? ", ..." : "");
                string head = list.Count > 1 ? $"{list.Count} click targets in {pattern} paint nothing at rest, yet take clicks" : $"click target {list[0].Ref} (in {pattern}) paints nothing at rest, yet takes clicks";
                into.Add(new Finding(Rules.TargetInk, Severity.Warn, $"{head}: {sizes}", refs)
                {
                    Measured = [M("w", list.Min(static n => n.Rect.W)), M("h", list.Min(static n => n.Rect.H)), M("count", list.Count)],
                    Expected = [M("ink", 1)],
                    Pattern = pattern,
                    Cause = "no-ink",
                    Count = list.Count,
                    Rects = rects,
                    Nodes = Ids(list),
                });
            }
            return looked;
        }

        // ── space.dead ──────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The smallest declared width and height (DIP) of an empty slot <c>space.dead</c> reports: a hole a person sees, not a spacer.</summary>
        public const float DeadSlotMin = 24f;

        /// <summary><c>space.dead</c> (info, todo; a report rule, never promoted): a slot that keeps its room while nothing fills it, "Nothing
        /// playing" at x=72 beside the play bar's empty 44 x 44 art slot with no plate (Zune Home at 400; 48 x 48 at 1440). Algorithm: an in-scope
        /// box, not moving, not a target, with a declared width AND height of at least <see cref="DeadSlotMin"/> and nothing drawn in its subtree
        /// (<see cref="Draws"/>), that LEADS a row (no ZStack): nothing before it in the row draws and a sibling after it does. Not reported: a gap
        /// between two groups (the title bar's drag room before the caption buttons), and a cell of a repeated family that another instance fills
        /// (a column reserved across rows: the expand chevron some recents rows carry, the explicit badge). A slot reserved ON PURPOSE (the no-jump rule: room kept for art
        /// that arrives) is declared in the theme's guide as an <see cref="LintIntent.Allow"/> for <c>space.dead</c> on its key prefix, with the
        /// board that draws it: the finding is then suppressed by intent. Fix: give the empty slot its plate (a placeholder fill or glyph at
        /// rest) or let the row start where its content does; never drop the reservation that keeps the row from jumping.</summary>
        static int DeadSpaceRule(Snapshot s, List<Finding> into)
        {
            IReadOnlyList<Node> nodes = s.Nodes;
            bool[] draws = Draws(s);
            // Which (family, cell) another instance fills with ink: a reserved column, not a hole.
            var filled = new HashSet<string>(StringComparer.Ordinal);
            foreach (Node n in nodes)
                if (n.Family.Length > 0 && draws[n.Index]) filled.Add(n.Family + "/" + n.CellPath);
            int looked = 0;
            var groups = new Dictionary<string, List<(Node Slot, Node Beside)>>(StringComparer.Ordinal);
            foreach (Node n in nodes)
            {
                if (!n.InScope || n.Moving || n.Interactive || n.Parent < 0 || draws[n.Index]) continue;
                Declared l = n.Layout;
                if (!(float.IsFinite(l.W) && float.IsFinite(l.H) && l.W >= DeadSlotMin && l.H >= DeadSlotMin)) continue;
                Node row = nodes[n.Parent];
                if (row.Column || row.ZStack) continue;
                looked++;
                if (n.Family.Length > 0 && filled.Contains(n.Family + "/" + n.CellPath)) continue;
                // It LEADS its row: nothing before it in the row draws, and content after it does (a gap between two groups, the title bar's
                // drag room before the caption buttons, is spacing, not a hole).
                Node? beside = null;
                bool before = false;
                foreach (Node m in nodes)
                {
                    if (m.Parent != row.Index || m.Index == n.Index || !draws[m.Index]) continue;
                    if (m.Index < n.Index) { before = true; break; }
                    if (m.InScope) { beside = m; break; }
                }
                if (before || beside is null) continue;
                string pattern = n.Pattern;
                if (!groups.TryGetValue(pattern, out var list)) groups[pattern] = list = [];
                list.Add((n, beside));
            }
            foreach (var (pattern, list) in groups)
            {
                var (refs, rects) = Members(list.Select(static e => e.Slot));
                var (slot, beside) = list[0];
                Node? words = null;
                for (int i = beside.Index; i < nodes.Count && words is null; i++)
                {
                    if (i != beside.Index && !Under(s, i, beside.Index)) break;
                    if (IsWords(nodes[i])) words = nodes[i];
                }
                string what = words?.Text is { } t ? $"{TextName(t)} at x={F(words.Rect.X)}" : $"{beside.Ref} at x={F(beside.Rect.X)}";
                string head = list.Count > 1 ? $"{list.Count} empty slots in {pattern}" : $"an empty {F(slot.Rect.W)} x {F(slot.Rect.H)} slot {slot.Ref}";
                into.Add(new Finding(Rules.SpaceDead, Severity.Info, $"{head} keeps its room with nothing in it and no plate; {what} starts after it", refs)
                {
                    Measured = [M("w", slot.Rect.W), M("h", slot.Rect.H), M("count", list.Count)],
                    Pattern = pattern,
                    Cause = "empty-slot",
                    Count = list.Count,
                    Rects = rects,
                    Nodes = Ids(list.Select(static e => e.Slot)),
                });
            }
            return looked;
        }
    }
}
