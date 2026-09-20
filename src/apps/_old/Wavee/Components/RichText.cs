using System;
using System.Collections.Generic;
using System.Text;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;

namespace Wavee;

// Renders a description that may be an HTML FRAGMENT — Spotify playlist/album blurbs carry <a href="spotify:…"> links to
// artists/playlists and <b> bold — as ONE wrapped rich-text paragraph (engine SpanTextEl, which shapes all spans as a
// single flow). Anchors become accent-colored HYPERLINKS that navigate via onNavUri; <b>/<strong> → bold; HTML
// entities are decoded; unknown tags are dropped (their text kept). The shaper has no italic axis, so <i>/<em> render
// upright. Plain text (no markup) → a single span, identical to a TextEl. Colors/size come from the caller so it
// matches whatever caption style the host uses.
public static class RichText
{
    public static Element Of(string? html, float size, ColorF color, ColorF linkColor, float width, int maxLines, Action<string>? onNavUri = null)
    {
        if (string.IsNullOrWhiteSpace(html)) return new BoxEl();
        var spans = Parse(html!, linkColor, onNavUri);
        if (spans.Count == 0) return new BoxEl();
        return new SpanTextEl(spans.ToArray())
        {
            Size = size, Color = color, LineHeight = size <= 12f ? 16f : size <= 14f ? 20f : float.NaN,
            Width = width, MaxLines = maxLines, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis,
        };
    }

    /// <summary>The FLEX twin of <see cref="Of"/>: same parsing/wrapping, but the paragraph GROWS into its flex row
    /// (Grow/Basis=0, no fixed width) and wraps to the width the layout assigns — so siblings (icons, status chips)
    /// take their intrinsic space and the text yields naturally, with NO hand-computed width reservations.</summary>
    public static Element OfFlex(string? html, float size, ColorF color, ColorF linkColor, int maxLines, Action<string>? onNavUri = null)
    {
        if (string.IsNullOrWhiteSpace(html)) return new BoxEl();
        var spans = Parse(html!, linkColor, onNavUri);
        if (spans.Count == 0) return new BoxEl();
        return new SpanTextEl(spans.ToArray())
        {
            Size = size, Color = color, LineHeight = size <= 12f ? 16f : size <= 14f ? 20f : float.NaN,
            Grow = 1f, Basis = 0f, MaxLines = maxLines, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis,
        };
    }

    /// <summary>A rich paragraph with a native inline overflow suffix. The collapsed state reserves “&#x2026; More” on
    /// the final line only when the body actually overflows; the expanded state appends a clickable “Less” span.</summary>
    public static Element Expandable(string? html, float size, ColorF color, ColorF linkColor, float width, int maxLines,
        string contextKey, Action<string>? onNavUri = null)
        => Embed.Comp(() => new ExpandableRichText(html, size, color, linkColor, width, maxLines, onNavUri, flex: false))
            with { Key = $"rich-expand:{contextKey}:{html}:{(int)width}:{maxLines}" };

    /// <summary>Flex-width twin of <see cref="Expandable"/> for inline-edit read states.</summary>
    public static Element ExpandableFlex(string? html, float size, ColorF color, ColorF linkColor, int maxLines,
        string contextKey, Action<string>? onNavUri = null)
        => Embed.Comp(() => new ExpandableRichText(html, size, color, linkColor, float.NaN, maxLines, onNavUri, flex: true))
            with { Key = $"rich-expand-flex:{contextKey}:{html}:{maxLines}" };

    /// <summary>A single-line, FLEX rich caption for a media ROW subtitle: same anchor→hyperlink parsing as <see cref="Of"/>,
    /// but it GROWS into the row's text column (Grow/Basis=0, no fixed width) and ellipsises ONE line. A span whose href
    /// <see cref="RouteForUri"/> can route is an accent hyperlink — so artist/album names are clickable on their own,
    /// independent of the row's click. Plain text (no markup) renders identical to a TrackMeta caption.</summary>
    public static Element OfRow(string? text, float size, ColorF color, ColorF linkColor, Action<string>? onNavUri = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return new BoxEl();
        var spans = Parse(text!, linkColor, onNavUri);
        if (spans.Count == 0) return new BoxEl();
        return new SpanTextEl(spans.ToArray())
        {
            Size = size, Color = color, LineHeight = size <= 12f ? 16f : size <= 14f ? 20f : float.NaN,
            Grow = 1f, Basis = 0f, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
        };
    }

    /// <summary>An entity uri → the app's route key (matches ContentHost): playlist → "pl:…", album → "album:…",
    /// prerelease → "prerelease:…", artist → "artist:…", show → "show:…", saved-tracks → "liked", and a playback
    /// module's own entity → "module:wavee:module:…". Null when it's not a navigable uri.</summary>
    public static string? RouteForUri(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return null;
        // Every liked spelling (the user-namespaced form included) — see EntityUri.IsLikedCollection.
        if (EntityUri.IsLikedCollection(uri)) return "liked";
        // A playback MODULE's own namespace. It is checked before the Spotify parser because it is a different scheme
        // entirely (`wavee:module:<id>:<b64(entityId)>`) and because the route is the uri with the family prefix in
        // front of it — the same `album:spotify:album:…` shape, so pins/tabs/history need no new identity rules.
        if (Wavee.Sdk.ModuleUri.TryDecode(uri, out _, out _)) return Backend.Modules.ModulePages.RoutePrefix + uri;
        // The route TABLE is unchanged; only the discrimination moved to the ONE parser (hydration-facade-design.md §1.1).
        // A prerelease is its own kind, so "the more specific scheme wins" is structural rather than an ordering rule; the
        // prerelease route still resolves to the album's own DetailPage (kind 138, inside the load).
        // A SHOW routes ("show:" → the shared detail surface with Episodes on the right), because an episode row in a
        // playlist carries its show in the album slot (EpisodeAsTrack, design §1.5) and that subtitle has to be a live
        // link to the podcast. An EPISODE still routes NOWHERE: there is no episode page in ContentHost, so a link to
        // one would navigate to a route nothing renders — it stays styled-but-inert until such a page exists.
        return EntityUri.KindOf(uri) switch
        {
            EntityKind.Playlist => "pl:" + uri,
            EntityKind.Prerelease => "prerelease:" + uri,
            EntityKind.Album => "album:" + uri,
            EntityKind.Artist => "artist:" + uri,
            EntityKind.Show => "show:" + uri,
            _ => null,
        };
    }

    static List<TextSpan> Parse(string s, ColorF linkColor, Action<string>? onNavUri)
    {
        var spans = new List<TextSpan>(4);
        var buf = new StringBuilder(s.Length);
        int bold = 0;
        string? href = null;

        void Flush()
        {
            if (buf.Length == 0) return;
            string t = buf.ToString();
            buf.Clear();
            ushort w = (ushort)(bold > 0 ? 700 : 0);
            if (href is { } h && onNavUri is not null && RouteForUri(h) is { } key)
                spans.Add(new TextSpan(t, Weight: w, Color: linkColor, OnClick: () => onNavUri(key)));
            else if (href is not null)
                spans.Add(new TextSpan(t, Weight: w, Color: linkColor));   // a reference we can't route → styled, not clickable
            else
                spans.Add(new TextSpan(t, Weight: w));
        }

        int i = 0;
        while (i < s.Length)
        {
            if (s[i] == '<')
            {
                int gt = s.IndexOf('>', i);
                if (gt < 0) { buf.Append(s[i]); i++; continue; }   // stray '<' — keep it as text
                string tag = s.Substring(i + 1, gt - i - 1).Trim();
                Flush();
                string lower = tag.ToLowerInvariant();
                if (lower == "/a") href = null;
                else if (lower == "a" || lower.StartsWith("a ", StringComparison.Ordinal)) href = ExtractHref(tag);
                else if (lower is "b" or "strong") bold++;
                else if (lower is "/b" or "/strong") bold = Math.Max(0, bold - 1);
                // every other tag (br, i, em, span, p, …) is dropped; its text content is preserved
                i = gt + 1;
            }
            else
            {
                int lt = s.IndexOf('<', i);
                if (lt < 0) lt = s.Length;
                Decode(s, i, lt, buf);
                i = lt;
            }
        }
        Flush();
        return spans;
    }

    sealed class ExpandableRichText : Component
    {
        readonly string? _html;
        readonly float _size;
        readonly ColorF _color;
        readonly ColorF _linkColor;
        readonly float _width;
        readonly int _maxLines;
        readonly Action<string>? _onNavUri;
        readonly bool _flex;
        readonly Signal<bool> _expanded = new(false);

        public ExpandableRichText(string? html, float size, ColorF color, ColorF linkColor, float width, int maxLines,
            Action<string>? onNavUri, bool flex)
        {
            _html = html; _size = size; _color = color; _linkColor = linkColor; _width = width;
            _maxLines = maxLines; _onNavUri = onNavUri; _flex = flex;
        }

        public override Element Render()
        {
            if (string.IsNullOrWhiteSpace(_html)) return new BoxEl();
            var parsed = Parse(_html!, _linkColor, _onNavUri);
            if (parsed.Count == 0) return new BoxEl();

            bool expanded = _expanded.Value;
            if (expanded)
                parsed.Add(new TextSpan(" " + Loc.Get(Strings.Common.Less), Weight: 600, Color: _linkColor,
                    OnClick: () => _expanded.Value = false));

            return new SpanTextEl(parsed.ToArray())
            {
                Size = _size,
                Color = _color,
                LineHeight = _size <= 12f ? 16f : _size <= 14f ? 20f : float.NaN,
                Width = _flex ? float.NaN : _width,
                Grow = _flex ? 1f : 0f,
                Basis = _flex ? 0f : float.NaN,
                MaxLines = expanded ? 0 : _maxLines,
                Wrap = TextWrap.Wrap,
                Trim = TextTrim.CharacterEllipsis,
                OverflowSuffix = expanded
                    ? null
                    : [new TextSpan("… " + Loc.Get(Strings.Common.More), Weight: 600, Color: _linkColor,
                        OnClick: () => _expanded.Value = true)],
            };
        }
    }

    // href value from an anchor tag body ("a href=\"spotify:…\"" or unquoted "a href=spotify:…"). Null if absent.
    static string? ExtractHref(string tag)
    {
        int h = tag.IndexOf("href", StringComparison.OrdinalIgnoreCase);
        if (h < 0) return null;
        int eq = tag.IndexOf('=', h);
        if (eq < 0) return null;
        int v = eq + 1;
        while (v < tag.Length && (tag[v] == ' ' || tag[v] == '"' || tag[v] == '\'')) v++;
        int end = v;
        while (end < tag.Length && tag[end] is not ('"' or '\'' or ' ')) end++;
        return end > v ? tag.Substring(v, end - v) : null;
    }

    // Decode the common HTML entities in s[start,end) into buf (leave unknown ones literal).
    static void Decode(string s, int start, int end, StringBuilder buf)
    {
        int i = start;
        while (i < end)
        {
            char c = s[i];
            if (c == '&')
            {
                int sc = s.IndexOf(';', i);
                if (sc > i && sc < end && sc - i <= 9)
                {
                    string ent = s.Substring(i + 1, sc - i - 1);
                    string? rep = ent switch
                    {
                        "amp" => "&", "lt" => "<", "gt" => ">", "quot" => "\"", "apos" or "#39" => "'", "nbsp" => " ",
                        _ when ent.Length > 1 && ent[0] == '#' && int.TryParse(ent.AsSpan(1), out int cp) && cp > 0 && cp < 0xD800 => char.ConvertFromUtf32(cp),
                        _ => null,
                    };
                    if (rep is not null) { buf.Append(rep); i = sc + 1; continue; }
                }
            }
            buf.Append(c);
            i++;
        }
    }
}
