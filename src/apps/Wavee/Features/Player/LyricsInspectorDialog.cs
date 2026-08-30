using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Backend.Lyrics;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The lyrics-source inspector: the answer to "these lyrics are out of sync — is that the provider's timings or our
// parser?". Opened from the rail's Lyrics header, it shows, for the playing track:
//
//   1. PROVIDERS — every source that ran, whether it was chosen, and (for the ones that were not) the concrete reason:
//      the reranker score it lost by, the timeout, the miss, or the fact that a faster match cut it short.
//   2. RAW       — each provider's payload EXACTLY as it arrived (decrypted, for the CJK formats, since that is what the
//      parser is handed), copyable per payload including for the losing providers.
//   3. PARSED    — the LyricsDocument each provider parsed to, and the FINAL document handed to the UI, with the timing
//      anomalies that matter for desync called out (out-of-order lines, missing timestamps, ends before starts,
//      syllables outside their line).
//
// Everything it reads is published by AggregatingLyricsProvider into the static LyricsDiagnostics store — this file adds
// no fetching of its own. The one exception is "Re-fetch from providers", which exists because the winner cache (memory
// AND disk) means a track played in any earlier session answers with no round-trip, so there IS no raw payload until the
// fan-out is forced to run again.
//
// This is a developer surface on a theme PLATE (a ContentDialog card), so it uses the theme's text rungs and a monospace
// face throughout rather than the reading-surface ink seam LyricsView follows.

/// <summary>The header glyph button that opens the inspector. Its own component so the rail header does not have to
/// subscribe to anything: the track id is <c>Peek</c>ed at click time, so this never re-renders on a track change.</summary>
sealed class LyricsInspectorButton : Component
{
    public override Element Render()
    {
        var overlay = UseContext(Overlay.Service);
        var bridge = UseContext(PlaybackBridge.Slot);

        return RightRail.HeaderButton(Icons.Code, Loc.Get(Strings.Player.InspectLyrics), () =>
        {
            if (overlay is null) return;
            string trackId = bridge?.Identity.Peek().Track?.Id ?? "";
            LyricsInspector.Open(overlay, trackId);
        });
    }
}

static class LyricsInspector
{
    public static void Open(IOverlayService overlay, string trackId) => ContentDialog.Show(overlay, d =>
    {
        d.Title = Loc.Get(Strings.Player.LyricsInspector);
        d.DialogWidth = 548f;              // the WinUI ContentDialog maximum — a raw payload wants every DIP of it
        d.PrimaryText = "";                // no command; the dialog only reports
        d.CloseText = Loc.Get(Strings.Common.Close);
        d.DefaultButton = ContentDialog.DefaultBtn.Close;
        d.Content = Embed.Comp(() => new LyricsInspectorBody(trackId));
    });
}

sealed class LyricsInspectorBody : Component
{
    /// <summary>How much of a payload is RENDERED. The capture cap is two orders of magnitude larger — Copy always hands
    /// over everything that was captured, because laying out 128k characters of wrapped monospace is what would actually
    /// hurt, not holding them.</summary>
    const int OnScreenRawChars = 4000;
    /// <summary>How many parsed lines are rendered. Beyond this, Copy is the way to see the rest.</summary>
    const int OnScreenLines = 300;

    const float ContentW = 492f;           // 548 card − 2×24 padding, minus room for the dialog's scrollbar
    const string MonoFont = "Cascadia Code";

    static readonly ColorF Good = new(0.30f, 0.78f, 0.45f, 1f);
    static readonly ColorF Warn = new(0.92f, 0.70f, 0.25f, 1f);
    static readonly ColorF Bad = new(0.90f, 0.35f, 0.38f, 1f);
    static readonly ColorF Dim = new(0.40f, 0.42f, 0.50f, 1f);

    readonly string _trackId;
    readonly Signal<int> _tab = new(0);         // 0 providers · 1 raw · 2 parsed
    readonly Signal<string> _focus = new("");   // which provider the raw/parsed tabs are showing ("" = the final document)
    readonly Signal<int> _epoch = new(0);       // bump to re-read the static diagnostics store
    readonly Signal<bool> _syllables = new(false);
    readonly Signal<bool> _refetching = new(false);
    readonly Signal<string> _status = new("");

    public LyricsInspectorBody(string trackId) => _trackId = trackId;

    public override Element Render()
    {
        var hooks = UseContext(InputHooks.Current);
        var svc = UseContext(Services.Slot);
        var post = UsePost();
        _ = _epoch.Value;   // a refresh / a completed re-fetch republishes into the store — re-read on the bump

        if (_trackId.Length == 0)
            return Body(Note("Nothing is playing. Start a track and reopen this dialog."));

        var report = LyricsDiagnostics.ForTrack(_trackId);
        var insp = LyricsDiagnostics.InspectionFor(_trackId);

        Element tab = _tab.Value switch
        {
            1 => RawTab(hooks, insp),
            2 => ParsedTab(hooks, insp),
            _ => ProvidersTab(report, insp),
        };

        return Body(
            Toolbar(hooks, svc, post, report, insp),
            SelectorBar.Create(["Providers", "Raw response", "Parsed"], _tab),
            tab);
    }

    static Element Body(params Element[] kids) => new BoxEl
    {
        Direction = 1, Gap = 12f, Width = ContentW, Children = kids,
    };

    // ── toolbar ───────────────────────────────────────────────────────────────────────────────────────────────────────

    Element Toolbar(InputHooks hooks, Services? svc, Action<Action> post, LyricsSearchReport? report, LyricsInspection? insp)
    {
        bool busy = _refetching.Value;
        string status = _status.Value;

        var kids = new List<Element>
        {
            HStack(8f,
                Button.Standard("Copy full report", () =>
                {
                    hooks.Clipboard?.SetText(LyricsInspectionExport.BuildReport(_trackId, report, insp));
                    Toast.Show("Lyrics report copied", new ToastOptions { Severity = InfoBarSeverity.Success });
                }),
                // The report is what fits in a clipboard; the BUNDLE is what an investigation needs — every payload
                // byte-for-byte, every candidate's parse as a TSV, in a folder that can be diffed, grepped, or replayed
                // through the parser as a test fixture.
                Button.Standard(busy ? "Working…" : "Save bundle…", () => SaveBundle(report, insp, svc, post), isEnabled: !busy),
                Button.Standard("Refresh", () => _epoch.Value = _epoch.Peek() + 1),
                Button.Accent(busy ? "Re-fetching…" : "Re-fetch from providers",
                    () => Refetch(svc, post), isEnabled: !busy)),
            Caption("Re-fetch drops this track from the memory AND disk lyric caches and runs the fan-out again — the only "
                + "way to get a raw response for a track that was already cached. It repopulates this dialog; the lyrics "
                + "already on screen are left alone."),
        };
        if (status.Length > 0)
            kids.Add(new TextEl(status) { Size = 12f, LineHeight = 16f, Color = Warn, Wrap = TextWrap.Wrap });

        return new BoxEl { Direction = 1, Gap = 6f, Children = kids.ToArray() };
    }

    /// <summary>Save, fetching first if there is nothing to save. A cache hit carries NO payload, so a bundle written
    /// from one is the useless artifact this button exists to avoid — and "click Re-fetch, then click Save" is an
    /// ordering the user has no way to know about and every reason to get wrong (a re-fetch re-persists the winner, so
    /// the very next open is a cache hit again). Chain it instead: the button always produces a usable bundle.</summary>
    void SaveBundle(LyricsSearchReport? report, LyricsInspection? insp, Services? svc, Action<Action> post)
    {
        if (insp is null || insp.Raw.Count == 0) Refetch(svc, post, thenSave: true);
        else WriteBundleNow(report, insp);
    }

    void WriteBundleNow(LyricsSearchReport? report, LyricsInspection? insp)
    {
        string? folder = LyricsInspectionExport.WriteBundle(_trackId, report, insp);
        if (folder is null)
        {
            _status.Value = "Nothing to save — no search has been recorded for this track.";
            return;
        }
        int payloads = insp?.Raw.Count ?? 0;
        // Say so loudly rather than handing over a folder that silently contains no evidence.
        _status.Value = payloads == 0
            ? "Saved to " + folder + " — but with NO provider payloads: the answer still came from a cache."
            : $"Saved {payloads} payload(s) to {folder}";
        Toast.Show("Lyrics evidence bundle saved", new ToastOptions { Severity = InfoBarSeverity.Success });
        SettingsShared.OpenFolder(folder);
    }

    void Refetch(Services? svc, Action<Action> post, bool thenSave = false)
    {
        if (_refetching.Peek()) return;
        if (svc?.Lyrics is not ILyricsRefetch refetchable)
        {
            _status.Value = "This session has no re-fetchable lyrics provider (offline / not logged in).";
            return;
        }

        _refetching.Value = true;
        _status.Value = thenSave
            ? "Nothing cached to save — re-fetching from the providers first…"
            : "";
        _ = RunAsync();

        async Task RunAsync()
        {
            string? error = null;
            try { await refetchable.RefetchAsync(_trackId).ConfigureAwait(false); }
            catch (Exception e) { error = e.GetType().Name + ": " + e.Message; }

            post(() =>
            {
                _refetching.Value = false;
                _epoch.Value = _epoch.Peek() + 1;
                if (error is not null) { _status.Value = "Re-fetch failed — " + error; return; }
                _status.Value = "";
                // Re-read the store rather than trusting the snapshot this render closed over — the fetch is what just
                // populated it.
                if (thenSave)
                    WriteBundleNow(LyricsDiagnostics.ForTrack(_trackId), LyricsDiagnostics.InspectionFor(_trackId));
            });
        }
    }

    // ── 1. providers ──────────────────────────────────────────────────────────────────────────────────────────────────

    Element ProvidersTab(LyricsSearchReport? report, LyricsInspection? insp)
    {
        var kids = new List<Element>();

        if (report is null)
        {
            kids.Add(Note("No lyrics search has been recorded for this track yet — the fetch may still be in flight, or "
                + "this is a local/fake track. Hit Refresh, or Re-fetch to force one."));
            if (insp?.Note is { Length: > 0 } onlyNote) kids.Add(Caption(onlyNote));
            return VStack(10f, kids.ToArray());
        }

        kids.Add(new TextEl(report.Summary)
        { Size = 13f, LineHeight = 18f, Weight = 600, Color = Tok.AccentTextPrimary, Wrap = TextWrap.Wrap });
        kids.Add(new TextEl($"“{Or(report.Title, "(no title)")}” — {Or(report.Artist, "(no artist)")}")
        { Size = 12f, LineHeight = 16f, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap });
        kids.Add(Caption($"album {Or(report.Album, "—")} · {report.DurationMs / 1000}s · ISRC {Or(report.Isrc, "—")} · id {_trackId}"));
        if (insp?.Note is { Length: > 0 } note) kids.Add(Caption(note));
        kids.Add(Divider());

        double winnerScore = 0d;
        foreach (var t in report.Sources) if (t.Winner) winnerScore = t.Score;

        if (report.Sources.Count == 0)
            kids.Add(Note("No source ran for this track."));
        foreach (var t in report.Sources)
            kids.Add(ProviderCard(t, insp, winnerScore));

        return VStack(10f, kids.ToArray());
    }

    Element ProviderCard(LyricsSourceTrace t, LyricsInspection? insp, double winnerScore)
    {
        ColorF dot = t.Outcome switch
        {
            LyricsOutcome.Hit => Good,
            LyricsOutcome.Timeout => Warn,
            LyricsOutcome.Error => Bad,
            LyricsOutcome.Skipped => Dim,
            _ => new ColorF(0.55f, 0.57f, 0.62f, 1f),
        };

        int rawCount = 0;
        if (insp is not null)
            foreach (var r in insp.Raw)
                if (StringComparer.Ordinal.Equals(r.SourceId, t.SourceId)) rawCount++;
        var parsed = CandidateFor(insp, t.SourceId);

        var rows = new List<Element>
        {
            HStack(8f,
                new BoxEl { Width = 8f, Height = 8f, Corners = Radii.Circle(8f), Fill = dot, AlignSelf = FlexAlign.Center },
                new TextEl(t.SourceId)
                { Size = 13f, LineHeight = 18f, Weight = 700, Color = t.Winner ? Tok.AccentTextPrimary : Tok.TextPrimary },
                new TextEl($"{t.Outcome.ToString().ToUpperInvariant()} · {t.ElapsedMs}ms")
                { Size = 11f, LineHeight = 18f, Weight = 600, Color = Tok.TextTertiary, Grow = 1f, MinWidth = 0f }),
            new TextEl(Verdict(t, winnerScore))
            {
                Size = 12f, LineHeight = 16f, Wrap = TextWrap.Wrap,
                Color = t.Winner ? Good : Tok.TextSecondary,
            },
        };

        if (t.Detail.Length > 0)
            rows.Add(Caption(t.Detail));
        if (parsed is not null)
        {
            rows.Add(Caption($"parsed: {parsed.Document.Sync}, {parsed.Document.Lines.Count} lines, "
                + $"{SyllableCount(parsed.Document)} syllables, matched by {parsed.Basis}, prior {parsed.Prior:F2}"));
            string timing = LyricsTiming.Describe(parsed.Document);
            rows.Add(new TextEl("timing: " + timing)
            {
                Size = 11f, LineHeight = 15f, Wrap = TextWrap.Wrap,
                Color = timing.StartsWith("clean", StringComparison.Ordinal) ? Tok.TextTertiary : Warn,
            });
        }

        rows.Add(HStack(6f,
            Button.Standard(rawCount > 0 ? $"Raw ({rawCount})" : "Raw (none)",
                () => { _focus.Value = t.SourceId; _tab.Value = 1; }, isEnabled: rawCount > 0),
            Button.Standard(parsed is not null ? "Parsed" : "Parsed (none)",
                () => { _focus.Value = t.SourceId; _tab.Value = 2; }, isEnabled: parsed is not null)));

        return new BoxEl
        {
            Direction = 1, Gap = 5f,
            Padding = Edges4.All(10f), Corners = CornerRadius4.All(6f),
            Fill = Tok.FillSubtleSecondary,
            BorderWidth = 1f, BorderColor = t.Winner ? Tok.AccentDefault : Tok.StrokeCardDefault,
            Children = rows.ToArray(),
        };
    }

    /// <summary>The one line the whole tab exists for: why this provider is (not) the one you are listening to.</summary>
    static string Verdict(LyricsSourceTrace t, double winnerScore)
    {
        if (t.Winner)
            return $"★ CHOSEN — reranker score {t.Score:F2}"
                + (t.RerankReason.Length > 0 ? $" ({t.RerankReason})" : "");

        return t.Outcome switch
        {
            LyricsOutcome.Hit =>
                $"not chosen — it returned lyrics but lost the rerank: score {t.Score:F2} against the winner's {winnerScore:F2}"
                + (t.RerankReason.Length > 0 ? $" ({t.RerankReason})" : ""),
            LyricsOutcome.Miss => "not chosen — the provider had nothing for this track",
            LyricsOutcome.Timeout => "not chosen — it did not answer inside the per-source budget",
            LyricsOutcome.Error => "not chosen — the request failed",
            LyricsOutcome.Skipped => "not chosen — it never ran to completion (a faster match closed the window)",
            _ => "not chosen",
        };
    }

    // ── 2. raw ────────────────────────────────────────────────────────────────────────────────────────────────────────

    Element RawTab(InputHooks hooks, LyricsInspection? insp)
    {
        if (insp is null || insp.Raw.Count == 0)
            return VStack(10f,
                Note("No provider payload was captured for this track."),
                Caption(insp?.Note ?? "Nothing has been recorded yet — hit Refresh."),
                Caption("Payloads are only captured when a fan-out actually runs. A cached track (memory or disk) never "
                    + "contacts a provider, so use “Re-fetch from providers” above to force one."));

        // The distinct sources that actually produced a payload, in capture order.
        var sources = new List<string>();
        foreach (var r in insp.Raw)
            if (!sources.Contains(r.SourceId, StringComparer.Ordinal)) sources.Add(r.SourceId);

        string focus = _focus.Value;
        if (!sources.Contains(focus, StringComparer.Ordinal)) focus = sources[0];

        var chips = new List<Element>(sources.Count);
        foreach (string s in sources)
        {
            string id = s;
            int n = 0;
            foreach (var r in insp.Raw) if (StringComparer.Ordinal.Equals(r.SourceId, id)) n++;
            chips.Add(Pill($"{id} ({n})", StringComparer.Ordinal.Equals(id, focus), () => _focus.Value = id));
        }

        var kids = new List<Element> { Wrap(6f, chips.ToArray()) };
        foreach (var payload in insp.Raw)
        {
            if (!StringComparer.Ordinal.Equals(payload.SourceId, focus)) continue;
            kids.Add(PayloadCard(hooks, payload));
        }
        return VStack(10f, kids.ToArray());
    }

    Element PayloadCard(InputHooks hooks, LyricsRawPayload p)
    {
        string shown = p.Text.Length <= OnScreenRawChars ? p.Text : p.Text[..OnScreenRawChars];
        var head = new List<Element>
        {
            new TextEl(p.Label)
            { Size = 11f, LineHeight = 15f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, FontFamily = MonoFont },
            HStack(8f,
                Caption($"{p.Format} · {p.OriginalLength:N0} chars"
                    + (p.Truncated ? $" · CAPTURE-TRUNCATED to {p.Text.Length:N0}" : "")
                    + (shown.Length < p.Text.Length ? $" · showing the first {shown.Length:N0}" : "")),
                new BoxEl { Grow = 1f },
                Button.Standard("Copy", () =>
                {
                    hooks.Clipboard?.SetText(p.Text);
                    Toast.Show($"{p.SourceId} payload copied", new ToastOptions { Severity = InfoBarSeverity.Success });
                })),
        };

        return new BoxEl
        {
            Direction = 1, Gap = 6f, Padding = Edges4.All(10f), Corners = CornerRadius4.All(6f),
            Fill = Tok.FillControlSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
            Children =
            [
                new BoxEl { Direction = 1, Gap = 4f, Children = head.ToArray() },
                new BoxEl { Height = 1f, Fill = Tok.StrokeCardDefault },
                new TextEl(shown)
                {
                    Size = 11f, LineHeight = 15f, FontFamily = MonoFont,
                    Color = Tok.TextPrimary, Wrap = TextWrap.Wrap,
                },
            ],
        };
    }

    // ── 3. parsed ─────────────────────────────────────────────────────────────────────────────────────────────────────

    Element ParsedTab(InputHooks hooks, LyricsInspection? insp)
    {
        if (insp is null || (insp.Final is null && insp.Candidates.Count == 0))
            return VStack(10f,
                Note("No parsed document has been recorded for this track."),
                Caption(insp?.Note ?? "Nothing has been recorded yet — hit Refresh."));

        // A focus carried over from the Providers/Raw tab may name a source that produced no DOCUMENT (a payload that
        // parsed to nothing, or a plain miss). Resolve it once here so the chip highlight and the body can never
        // disagree about what is on screen.
        string focus = _focus.Value;
        var cand = CandidateFor(insp, focus);
        LyricsDocument? doc = cand?.Document ?? insp.Final;
        string who = cand is null ? "final" : focus;

        var chips = new List<Element>();
        if (insp.Final is not null)
            chips.Add(Pill("Final (what the UI got)", cand is null, () => _focus.Value = ""));
        foreach (var c in insp.Candidates)
        {
            string id = c.SourceId;
            chips.Add(Pill(id, cand is not null && StringComparer.Ordinal.Equals(id, focus), () => _focus.Value = id));
        }

        var kids = new List<Element> { Wrap(6f, chips.ToArray()) };
        if (doc is null)
        {
            kids.Add(Note("That provider produced no document."));
            return VStack(10f, kids.ToArray());
        }

        var d = doc;
        bool showSyllables = _syllables.Value;

        kids.Add(new TextEl(who == "final"
                ? "The document handed to LyricsView — the winner AFTER the reranker's offset correction."
                : $"“{who}” as parsed, BEFORE the reranker's offset correction.")
        { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap });
        kids.Add(Caption($"provider {Or(d.Provider, who)} · sync {d.Sync} · isSynced {d.IsSynced} · {d.Lines.Count} lines · "
            + $"{SyllableCount(d)} syllables · offset applied {d.OffsetMsApplied}ms"));

        string anomalies = LyricsTiming.Describe(d);
        kids.Add(new TextEl("timing check: " + anomalies)
        {
            Size = 12f, LineHeight = 16f, Wrap = TextWrap.Wrap,
            Color = anomalies.StartsWith("clean", StringComparison.Ordinal) ? Good : Warn,
        });

        kids.Add(HStack(6f,
            Button.Standard(showSyllables ? "Hide syllables" : "Show syllables",
                () => _syllables.Value = !_syllables.Peek()),
            Button.Standard("Copy parsed", () =>
            {
                hooks.Clipboard?.SetText(LyricsInspectionExport.BuildParsed(who, d));
                Toast.Show("Parsed lyrics copied", new ToastOptions { Severity = InfoBarSeverity.Success });
            })));
        kids.Add(Divider());

        int count = Math.Min(d.Lines.Count, OnScreenLines);
        long prev = long.MinValue;
        for (int i = 0; i < count; i++)
        {
            var l = d.Lines[i];
            kids.Add(LineRow(i, l, prev, LyricsTiming.IsSquashed(d, i), showSyllables));
            prev = l.StartMs;
        }
        if (d.Lines.Count > count)
            kids.Add(Caption($"…{d.Lines.Count - count} more lines — use Copy parsed to see them all."));

        return VStack(8f, kids.ToArray());
    }

    static Element LineRow(int i, LyricLine l, long prevStart, bool squashed, bool showSyllables)
    {
        bool outOfOrder = prevStart != long.MinValue && l.StartMs < prevStart;
        bool noStamp = i > 0 && l.StartMs == 0;
        bool badEnd = l.EndMs is { } e && e < l.StartMs;
        long dur = (l.EndMs ?? l.StartMs) - l.StartMs;
        ColorF timeColor = outOfOrder || badEnd ? Bad : noStamp || squashed ? Warn : Tok.TextSecondary;

        var kids = new List<Element>
        {
            HStack(8f,
                new TextEl(i.ToString())
                { Size = 11f, LineHeight = 16f, FontFamily = MonoFont, Color = Tok.TextTertiary, Width = 26f, Shrink = 0f },
                new TextEl($"{Ts(l.StartMs)}→{(l.EndMs is { } e2 ? Ts(e2) : "  --:--.---")}")
                { Size = 11f, LineHeight = 16f, FontFamily = MonoFont, Color = timeColor, Width = 130f, Shrink = 0f },
                // The DURATION, spelled out: a squashed line is invisible in two absolute timestamps but obvious the
                // moment you can read "217ms" next to nine words.
                new TextEl(l.EndMs is null ? "" : dur + "ms")
                { Size = 11f, LineHeight = 16f, FontFamily = MonoFont, Color = squashed ? Warn : Tok.TextTertiary, Width = 52f, Shrink = 0f },
                new TextEl(l.Text.Length == 0 ? "(blank)" : l.Text)
                {
                    Size = 12f, LineHeight = 16f, Wrap = TextWrap.Wrap, Grow = 1f, MinWidth = 0f,
                    Color = l.Text.Length == 0 ? Tok.TextTertiary : Tok.TextPrimary,
                }),
        };

        if (l.Translation is { Length: > 0 } tr) kids.Add(SubLine("tr", tr));
        if (l.Romanization is { Length: > 0 } ro) kids.Add(SubLine("ro", ro));
        if (showSyllables && l.Syllables.Count > 0)
        {
            var sb = new StringBuilder();
            foreach (var s in l.Syllables)
            {
                if (sb.Length > 0) sb.Append("  ");
                sb.Append(s.Text.Length == 0 ? "␣" : s.Text).Append('[').Append(s.StartMs).Append('-').Append(s.EndMs).Append(']');
            }
            kids.Add(new TextEl(sb.ToString())
            {
                Size = 10.5f, LineHeight = 14f, FontFamily = MonoFont, Color = Tok.TextTertiary,
                Wrap = TextWrap.Wrap, Margin = new Edges4(34f, 0f, 0f, 0f),
            });
        }

        return new BoxEl { Direction = 1, Gap = 2f, Children = kids.ToArray() };
    }

    static Element SubLine(string tag, string text) => new TextEl($"[{tag}] {text}")
    {
        Size = 11f, LineHeight = 15f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap,
        Margin = new Edges4(34f, 0f, 0f, 0f),
    };

    // ── small helpers ─────────────────────────────────────────────────────────────────────────────────────────────────

    static LyricsParsedCandidate? CandidateFor(LyricsInspection? insp, string sourceId)
    {
        if (insp is null || sourceId.Length == 0) return null;
        foreach (var c in insp.Candidates)
            if (StringComparer.Ordinal.Equals(c.SourceId, sourceId)) return c;
        return null;
    }

    static int SyllableCount(LyricsDocument doc)
    {
        int n = 0;
        foreach (var l in doc.Lines) n += l.Syllables.Count;
        return n;
    }

    static string Ts(long ms)
    {
        if (ms < 0) ms = 0;
        long m = ms / 60000, rest = ms % 60000;
        return $"{m:00}:{rest / 1000:00}.{rest % 1000:000}";
    }

    static string Or(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value!;

    static Element Note(string text) => new TextEl(text)
    { Size = 12f, LineHeight = 17f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap };

    static Element Caption(string text) => new TextEl(text)
    { Size = 11f, LineHeight = 15f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap };
}
