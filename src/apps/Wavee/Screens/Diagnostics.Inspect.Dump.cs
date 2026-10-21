// ── Screens/Diagnostics.Inspect.Dump.cs ────────────────────────────────────────────────────────────────────────────────
// the `--inspect` scene dump (`dump`, `find`, `tiers`); text trimming is the engine's TextOverflow query (lane A1)
//
// Role: SHELL (UI thread; reads the engine's public scene inspection only: SceneStore, NodeDescriber, AnimEngine)
// Spec: handoff/themes-2026-10-17/agent-inspect-plan.md lane A2 (`dump`), lane A1 (AppHost.QueryTextOverflow)
//
// THE NODE SHAPE (one flat array in depth-first child order; `parent` is the index in that array, -1 for a root):
//   {"i","parent","depth","key"?,"path","kind","visual","rect":{x,y,w,h},"opacity","fill"?,"border"?:{w,color},
//    "corners"?:[tl,tr,br,bl],"clip"?:true|{x,y,w,h},"text"?:{...},"flags":{...},"role"?,"handlers"?:[...],
//    "scroll"?:{offset,max,viewport,content,axis},"anim"?:true,"children"}
// Defaults are omitted (no fill when transparent, no border when 0 wide...), so a dump of a page stays small enough to read.
// `rect` is the PRESENTED absolute rect in window DIP (`SceneStore.PresentedAbsoluteRect`): what paint and hit-testing use.
// An icon glyph prints as `glyph:<name>` (the engine Icons name or the app's own; dump and find alike), alone or inside words, so a label never holds an invisible
// private-use character.

using System.Text.Json;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using FluentGpu.Text;
using Wavee.Theming;

namespace Wavee;

public static partial class Diagnostics
{
    /// <summary>The dump / find / tiers writers. UI thread.</summary>
    public static class InspectDump
    {
        /// <summary>What `dump` was asked for.</summary>
        public readonly record struct Request(int MaxDepth = int.MaxValue, bool KeysOnly = false, bool VisibleOnly = true,
                                              int MaxNodes = 4000, bool Text = true);

        /// <summary>What the trim answers rest on: `dwrite` (the engine's TextOverflow query over real DirectWrite metrics) or
        /// `headless-model` (the same query over the 0.55 x size font model: not truthful).</summary>
        public static string TrimSource(Shell.HeadlessShell shell) => IsRealText(shell) ? "dwrite" : "headless-model";

        /// <summary>True when the mount measures with a real font system (not the headless 0.55 x size model).</summary>
        public static bool IsRealText(Shell.HeadlessShell shell) => shell.Fonts is not FluentGpu.Text.Headless.HeadlessFontSystem;

        /// <summary>Write `"nodes":[...]` (and `"truncated"` when the cap stopped it) for the subtrees under
        /// <paramref name="roots"/>.</summary>
        public static int Write(Utf8JsonWriter w, Shell.HeadlessShell shell, ReadOnlySpan<NodeHandle> roots, in Request request)
        {
            SceneStore scene = shell.Host.Scene;
            var stack = new Stack<(NodeHandle Node, int Parent, int Depth)>();
            var children = new List<NodeHandle>();
            Span<char> path = stackalloc char[256];
            int written = 0;
            bool truncated = false;
            w.WriteStartArray("nodes");
            for (int r = roots.Length - 1; r >= 0; r--) stack.Push((roots[r], -1, 0));
            while (stack.Count > 0)
            {
                var (node, parent, depth) = stack.Pop();
                if (node.IsNull || !scene.IsLive(node)) continue;
                NodeFlags flags = scene.Flags(node);
                ref NodePaint paint = ref scene.Paint(node);
                if (request.VisibleOnly && ((flags & NodeFlags.Visible) == 0 || (flags & NodeFlags.Parked) != 0 || paint.Opacity <= 0f))
                    continue;   // hidden, parked or transparent: its subtree paints nothing
                string? key = scene.DebugKeyOf(node);
                RectF rect = scene.PresentedAbsoluteRect(node);
                bool emit = (!request.KeysOnly || key is not null) && (!request.VisibleOnly || (rect.W > 0f && rect.H > 0f));
                int self = parent;
                if (emit)
                {
                    if (written >= request.MaxNodes) { truncated = true; break; }
                    self = written++;
                    WriteNode(w, shell, node, key, rect, flags, ref paint, self, parent, depth, path, request.Text);
                }
                if (depth >= request.MaxDepth) continue;
                children.Clear();
                for (var c = scene.FirstChild(node); !c.IsNull; c = scene.NextSibling(c)) children.Add(c);
                for (int i = children.Count - 1; i >= 0; i--) stack.Push((children[i], self, emit ? depth + 1 : depth));
            }
            w.WriteEndArray();
            w.WriteNumber("count", written);
            if (truncated) w.WriteBoolean("truncated", true);
            w.WriteString("trimSource", TrimSource(shell));
            return written;
        }

        static void WriteNode(Utf8JsonWriter w, Shell.HeadlessShell shell, NodeHandle node, string? key, in RectF rect, NodeFlags flags,
                              ref NodePaint paint, int self, int parent, int depth, Span<char> path, bool text)
        {
            SceneStore scene = shell.Host.Scene;
            w.WriteStartObject();
            w.WriteNumber("i", self);
            w.WriteNumber("parent", parent);
            w.WriteNumber("depth", depth);
            if (key is not null) w.WriteString("key", key);
            int pathLength = NodeDescriber.Describe(scene, (int)node.Raw.Index, node.Raw.Gen, path);
            w.WriteString("path", path[..pathLength]);
            w.WriteString("kind", NodeDescriber.ElementTypeName(scene.ElementTypeId(node)));
            if (paint.VisualKind != VisualKind.None) w.WriteString("visual", paint.VisualKind.ToString());
            Inspect.Reply.Rect(w, "rect", rect);
            if (paint.Opacity < 1f) Inspect.Reply.Num(w, "opacity", paint.Opacity);
            if (paint.Fill.A > 0f && paint.VisualKind != VisualKind.Text) w.WriteString("fill", Inspect.Reply.Hex(paint.Fill));
            if (paint.BorderWidth > 0f)
            {
                w.WriteStartObject("border");
                Inspect.Reply.Num(w, "w", paint.BorderWidth);
                w.WriteString("color", Inspect.Reply.Hex(paint.BorderColor));
                w.WriteEndObject();
            }
            CornerRadius4 k = paint.Corners;
            if (k.TopLeft > 0f || k.TopRight > 0f || k.BottomRight > 0f || k.BottomLeft > 0f)
            {
                w.WriteStartArray("corners");
                w.WriteNumberValue(Math.Round(k.TopLeft, 2));
                w.WriteNumberValue(Math.Round(k.TopRight, 2));
                w.WriteNumberValue(Math.Round(k.BottomRight, 2));
                w.WriteNumberValue(Math.Round(k.BottomLeft, 2));
                w.WriteEndArray();
            }
            if (!paint.ClipRect.IsInfinite) Inspect.Reply.Rect(w, "clip", paint.ClipRect);
            else if ((flags & NodeFlags.ClipsToBounds) != 0) w.WriteBoolean("clip", true);
            if (text && paint.VisualKind == VisualKind.Text) WriteText(w, shell, node, rect, ref paint);
            WriteFlags(w, flags);
            ref InteractionInfo interaction = ref scene.Interaction(node);
            if (interaction.Role != AutomationRole.None) w.WriteString("role", interaction.Role.ToString());
            if (interaction.HandlerMask != 0) WriteHandlers(w, interaction.HandlerMask);
            if (scene.TryGetScroll(node, out ScrollState s))
            {
                w.WriteStartObject("scroll");
                Inspect.Reply.Num(w, "offset", (float)s.Offset);
                Inspect.Reply.Num(w, "max", s.MaxOffset);
                Inspect.Reply.Num(w, "viewport", s.ViewportMain);
                Inspect.Reply.Num(w, "content", s.ContentMain);
                w.WriteString("axis", s.Orientation == 1 ? "x" : "y");
                if (s.ItemCount > 0) w.WriteNumber("items", s.ItemCount);
                w.WriteEndObject();
            }
            if (shell.Host.Animation.HasTracks(node)) w.WriteBoolean("anim", true);
            w.WriteNumber("children", scene.ChildCount(node));
            w.WriteEndObject();
        }

        static void WriteText(Utf8JsonWriter w, Shell.HeadlessShell shell, NodeHandle node, in RectF rect, ref NodePaint paint)
        {
            SceneStore scene = shell.Host.Scene;
            TextStyle style = scene.Layout(node).TextStyle;
            StringTable strings = scene.Strings ?? Entities.Strings;
            string value = paint.Text.Value > 0 ? strings.Resolve(paint.Text) : "";
            w.WriteStartObject("text");
            if (GlyphLabel(value) is { } glyph)
            {
                w.WriteString("string", glyph);
                w.WriteString("codepoint", Codepoints(value, privateUseOnly: true));
            }
            else w.WriteString("string", value);
            if (value.Length == 0 && scene.TryGetSpanText(node, out ReadOnlySpan<TextSpan> spans)) w.WriteNumber("spans", spans.Length);
            Inspect.Reply.Num(w, "sizeDip", style.SizeDip);
            if (float.IsFinite(style.LineHeight)) Inspect.Reply.Num(w, "lineHeight", style.LineHeight);
            w.WriteNumber("weight", style.Weight);
            if (style.FontFamily.Value > 0) w.WriteString("family", strings.Resolve(style.FontFamily));
            w.WriteString("color", Inspect.Reply.Hex(paint.TextColor));
            if (style.Case != TextCase.AsWritten) w.WriteString("case", style.Case.ToString());
            if (style.MaxLines > 0) w.WriteNumber("maxLines", style.MaxLines);
            if (style.Wrap != TextWrap.NoWrap) w.WriteString("wrap", style.Wrap.ToString());
            if (style.Trim != TextTrim.None) w.WriteString("trim", style.Trim.ToString());
            if (style.CharSpacing != 0f) Inspect.Reply.Num(w, "charSpacing", style.CharSpacing);
            // The engine's per-node overflow query (AppHost.QueryTextOverflow, lane A1): the run re-measured uncut through the
            // host's own font system against its laid-out box.
            // (Read-only: MeasureCacheRef is a write-intent accessor, so the dump never touches the cache itself.)
            TextOverflow overflow = shell.Host.QueryTextOverflow(node);
            w.WriteBoolean("trimmed", overflow.Trimmed);
            if (overflow.IsText)
            {
                w.WriteStartObject("natural");
                Inspect.Reply.Num(w, "w", overflow.NaturalSize.Width);
                Inspect.Reply.Num(w, "h", overflow.NaturalSize.Height);
                w.WriteEndObject();
                if (overflow.HorizontalOverflow) w.WriteBoolean("overflowX", true);
                if (overflow.VerticalOverflow) w.WriteBoolean("overflowY", true);
            }
            w.WriteEndObject();
        }

        static void WriteFlags(Utf8JsonWriter w, NodeFlags f)
        {
            w.WriteStartObject("flags");
            w.WriteBoolean("visible", (f & NodeFlags.Visible) != 0);
            if ((f & NodeFlags.Focusable) != 0) w.WriteBoolean("focusable", true);
            if ((f & NodeFlags.Focused) != 0) w.WriteBoolean("focused", true);
            if ((f & NodeFlags.Hovered) != 0) w.WriteBoolean("hovered", true);
            if ((f & NodeFlags.Pressed) != 0) w.WriteBoolean("pressed", true);
            if ((f & NodeFlags.Disabled) != 0) w.WriteBoolean("disabled", true);
            if ((f & NodeFlags.ClipsToBounds) != 0) w.WriteBoolean("clipsToBounds", true);
            if ((f & NodeFlags.Scrollable) != 0) w.WriteBoolean("scrollable", true);
            if ((f & NodeFlags.HitTestVisible) != 0) w.WriteBoolean("hitTest", true);
            w.WriteEndObject();
        }

        static readonly (uint Bit, string Name)[] s_handlers =
        [
            (InteractionInfo.ClickBit, "click"), (InteractionInfo.KeyBit, "key"), (InteractionInfo.PointerBit, "pointer"),
            (InteractionInfo.CharBit, "char"), (InteractionInfo.RepeatBit, "repeat"), (InteractionInfo.PressedBit, "pressed"),
            (InteractionInfo.ContextBit, "context"), (InteractionInfo.FocusBit, "focus"), (InteractionInfo.DragBit, "drag"),
            (InteractionInfo.WheelBit, "wheel"), (InteractionInfo.SelectableTextBit, "selectableText"),
            (InteractionInfo.SpanLinksBit, "spanLinks"), (InteractionInfo.GestureBit, "gesture"),
            (InteractionInfo.ClickRequestsContextBit, "clickRequestsContext"), (InteractionInfo.RowCellsBit, "rowCells"),
        ];

        static void WriteHandlers(Utf8JsonWriter w, uint mask)
        {
            w.WriteStartArray("handlers");
            foreach (var (bit, name) in s_handlers)
                if ((mask & bit) != 0) w.WriteStringValue(name);
            w.WriteEndArray();
        }

        /// <summary>`"nodes":[{key, rect, shown, onScreen, actionable, nth?, why?, kind, text?}]` for every live node keyed exactly
        /// <paramref name="key"/> (or, with <paramref name="key"/> empty, starting with <paramref name="prefix"/>), in reading order.
        /// `onScreen` is the visible share of the rect (0..1, after the window and every clipping ancestor); `actionable` is what
        /// `click`/`hover`/`scroll` check, and `nth` the index a pointer verb's `nth` takes for this key (actionable matches only).</summary>
        public static int WriteFind(Utf8JsonWriter w, Shell.HeadlessShell shell, string key, string prefix)
        {
            SceneStore scene = shell.Host.Scene;
            List<(NodeHandle Node, string Key)> found = key.Length > 0 ? Exact(shell, key) : shell.FindPrefix(prefix);
            InReadingOrder(scene, found);
            RectF window = InspectKit.WindowRect(shell);
            var nth = new Dictionary<string, int>(StringComparer.Ordinal);
            w.WriteStartArray("nodes");
            foreach (var (node, k) in found)
            {
                Actionability.Verdict v = InspectKit.Assess(shell, node, window);
                w.WriteStartObject();
                w.WriteString("key", k);
                Inspect.Reply.Rect(w, "rect", scene.PresentedAbsoluteRect(node));
                w.WriteBoolean("shown", scene.IsShown(node));
                Inspect.Reply.Num(w, "onScreen", v.Fraction);
                w.WriteBoolean("actionable", v.Ok);
                if (v.Ok)
                {
                    int index = nth.GetValueOrDefault(k);
                    nth[k] = index + 1;
                    w.WriteNumber("nth", index);
                    if (v.Note.Length > 0) w.WriteString("note", v.Note);
                }
                else w.WriteString("why", v.Why);
                w.WriteString("kind", NodeDescriber.ElementTypeName(scene.ElementTypeId(node)));
                string? text = FirstText(scene, node);
                if (text is not null) w.WriteString("text", GlyphLabel(text) ?? text);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteNumber("count", found.Count);
            w.WriteString("order", "reading order (top, then left); nth counts the actionable matches of a key");
            if (key.Length > 0 && found.Count == 0 && shell.FindPrefix(key).Count is > 0 and int near)   // `find <x>` was a prefix search once
                w.WriteString("hint", "0 nodes keyed exactly '" + key + "'; " + near + " keys start with it: find prefix=" + Inspect.Quote(key));
            return found.Count;
        }

        /// <summary>Every live node keyed exactly <paramref name="key"/>, in slot order.</summary>
        public static List<(NodeHandle Node, string Key)> Exact(Shell.HeadlessShell shell, string key)
        {
            var all = shell.FindPrefix(key);
            all.RemoveAll(e => !string.Equals(e.Key, key, StringComparison.Ordinal));
            return all;
        }

        /// <summary>Sort <paramref name="found"/> into reading order by presented rect (<see cref="Actionability.ReadingOrder"/>).</summary>
        public static void InReadingOrder(SceneStore scene, List<(NodeHandle Node, string Key)> found)
        {
            var rects = new Dictionary<NodeHandle, RectF>(found.Count);
            foreach (var (node, _) in found) rects[node] = scene.PresentedAbsoluteRect(node);
            found.Sort((a, b) => Actionability.ReadingOrder(rects[a.Node], rects[b.Node]));
        }

        /// <summary>The first text string at or under <paramref name="node"/> (a button's label), or null.</summary>
        static string? FirstText(SceneStore scene, NodeHandle node)
        {
            StringTable strings = scene.Strings ?? Entities.Strings;
            foreach (var n in InspectKit.Subtree(scene, node))
            {
                ref NodePaint p = ref scene.Paint(n);
                if (p.VisualKind == VisualKind.Text && p.Text.Value > 0) return strings.Resolve(p.Text);
            }
            return null;
        }

        // ── glyphs ──────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>`glyph:<name>` for an icon glyph (one or two private-use characters; the name is the engine's <c>Icons</c> constant or
        /// the app's own, <see cref="TryGlyphName"/>), `glyph:U+XXXX` for one with no name; text that mixes words and glyphs with each glyph so named
        /// (`Afterglow glyph:U+E8B2`: a token stands apart from the words beside it by a blank); null for text with no glyph.</summary>
        public static string? GlyphLabel(string text)
        {
            if (IsGlyph(text)) return GlyphToken(text);
            if (text.AsSpan().IndexOfAnyInRange(FirstPrivateUse, LastPrivateUse) < 0) return null;
            var sb = new System.Text.StringBuilder(text.Length + 16);
            for (int i = 0; i < text.Length;)
            {
                if (!IsPrivateUse(text[i])) { sb.Append(text[i++]); continue; }
                int start = i;
                while (i < text.Length && IsPrivateUse(text[i])) i++;
                string run = text[start..i];
                if (sb.Length > 0 && !char.IsWhiteSpace(sb[^1])) sb.Append(' ');
                // A run of two may be one glyph (a layered icon); a longer run, or two the table does not name together, is a token per glyph.
                if (run.Length <= 2 && TryGlyphName(run, out _)) sb.Append(GlyphToken(run));
                else
                    for (int k = 0; k < run.Length; k++)
                    {
                        if (k > 0) sb.Append(' ');
                        sb.Append(GlyphToken(run[k].ToString()));
                    }
                if (i < text.Length && !char.IsWhiteSpace(text[i])) sb.Append(' ');
            }
            return sb.ToString();
        }

        /// <summary>`glyph:<name>` (<see cref="TryGlyphName"/>), or `glyph:U+XXXX` for a glyph nothing names.</summary>
        static string GlyphToken(string glyph)
            => "glyph:" + (TryGlyphName(glyph, out string? name) ? name : "U+" + Codepoints(glyph));

        /// <summary>The app's core marks that are not the engine's (<see cref="WaveeIcons"/>, its own font; <see cref="SegoeGlyphs"/>, the
        /// Segoe marks the engine's table lacks), by their constant names.</summary>
        static readonly (string Name, string Glyph)[] s_coreGlyphs =
        [
            (nameof(WaveeIcons.PlayNext), WaveeIcons.PlayNext), (nameof(WaveeIcons.PlayAfter), WaveeIcons.PlayAfter),
            (nameof(WaveeIcons.Lyrics), WaveeIcons.Lyrics), (nameof(SegoeGlyphs.AddFriend), SegoeGlyphs.AddFriend),
        ];

        /// <summary>The name of an icon glyph, as dump, find and lint print it: the engine's <c>Icons</c> constant first, then the name a theme
        /// gave a glyph it declares itself (<c>ThemeInspection.RegisterGlyphNames</c>, in its composition file), then the app's core marks.</summary>
        public static bool TryGlyphName(string glyph, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? name)
        {
            if (FluentGpu.Controls.Icons.TryGetName(glyph, out name) || Wavee.Theming.ThemeInspection.TryGetGlyphName(glyph, out name)) return true;
            foreach ((string n, string g) in s_coreGlyphs)
                if (string.Equals(g, glyph, StringComparison.Ordinal)) { name = n; return true; }
            name = null;
            return false;
        }

        const char FirstPrivateUse = '\uE000', LastPrivateUse = '\uF8FF';

        static bool IsPrivateUse(char c) => c is >= FirstPrivateUse and <= LastPrivateUse;

        /// <summary>One or two private-use characters (U+E000..U+F8FF): an icon glyph, not words.</summary>
        static bool IsGlyph(string text)
        {
            if (text.Length is 0 or > 2) return false;
            foreach (char c in text)
                if (!IsPrivateUse(c)) return false;
            return true;
        }

        /// <summary>The UTF-16 units of <paramref name="text"/> as hex (`E768`, or `E768 E769` for two); with
        /// <paramref name="privateUseOnly"/>, only the private-use ones (the glyphs inside words).</summary>
        static string Codepoints(string text, bool privateUseOnly = false)
        {
            var sb = new System.Text.StringBuilder(text.Length * 5);
            foreach (char c in text)
            {
                if (privateUseOnly && !IsPrivateUse(c)) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>The window's tier facts (`ThemeHost.Facts.Tiers`) plus the active theme and kind.</summary>
        public static void WriteTiers(Utf8JsonWriter w)
        {
            TierFacts t = ThemeHost.Facts.Peek().Tiers;
            w.WriteStartObject("tiers");
            w.WriteString("window", t.Window.ToString());
            w.WriteString("height", t.Height.ToString());
            w.WriteString("content", t.Content.ToString());
            Inspect.Reply.Num(w, "contentWidth", t.ContentWidth);
            Inspect.Reply.Num(w, "viewportWidth", t.ViewportWidth);
            Inspect.Reply.Num(w, "viewportHeight", t.ViewportHeight);
            w.WriteBoolean("resizing", t.Resizing);
            WriteRungs(w, in t);
            w.WriteEndObject();
            w.WriteString("theme", InspectKit.ActiveThemeId);
            w.WriteString("kind", FluentGpu.Dsl.Tok.Theme == FluentGpu.Dsl.ThemeKind.Light ? "light" : "dark");
        }

        /// <summary>`"rungs":[ids]`: the crossed rungs of the tier policy, by id (`tiers`, `mount`, `status` and `crossing` alike).</summary>
        public static void WriteRungs(Utf8JsonWriter w, in TierFacts t)
        {
            w.WriteStartArray("rungs");
            if (t.Policy is { } policy && !policy.Rungs.IsDefaultOrEmpty)
                for (int i = 0; i < policy.Rungs.Length && i < 64; i++)
                    if (((t.Rungs >> i) & 1UL) != 0UL) w.WriteStringValue(policy.Rungs[i].Id);
            w.WriteEndArray();
        }
    }
}
