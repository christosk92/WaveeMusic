// ── Screens/Diagnostics.Inspect.Dj.cs ─────────────────────────────────────────────────────────────────────────────────
// the `dj` verb (AI DJ, plan-dj §2.9): what Wavee holds of a DJ list and of the lexicon session playing it.
//
//   dj                    the DJ list (its attributes as the playlist row holds them), the lexicon session playing (its uri,
//                         url, the pages fetched, the next page's url), and the deck and queue as `transport` reads them
//   dj do=answers         the --fake lexicon service's session and first pages, decoded by the real context-resolve decoder
//   dj do=play            start the DJ list as a click does (Playback.PlayContext): waits for the resolve and the load
//   dj do=expire          the session's lexicon_expiration_time passes now: the next page fetch resolves the session again
//   dj do=jump            "Let DJ pick" as the button presses it (Playback.DjJump): the cursor before and after, the rows it skipped
//                         (now behind the cursor: history), the lexicon answers it caused, and the reducer's refusal when there was none
//   dj do=starve on=true  --fake: the lexicon service answers no more pages (on=false: it answers again), so a jump finds no later segment
//   dj do=seams samples=250  the talk-over and radio plan of every seam from the deck on (`seams`, D3/D3b), each side's gain sampled
//                         every `samples` ms over its window, and the joins the pump scheduled (`joins`)
//
// The session readout carries what its answer stated (dj, infinite, the expiry, the three restrictions, the welcome narration's
// presence), each page's segment and correlation id, the lookahead (pages loaded after the playing one) and the DJ book: per queue
// row its page, segment, jump target, the narration slots present, the subtitle, the decision id and the attribution.
// `locks`: the context's toggle restrictions as the reducer holds them, the user's shuffle and repeat they hold back, and the
// tooltip the shuffle and repeat buttons wear.
//
// Role: SHELL (UI thread; reads the entity tables and the fake answers)

using System.Text.Json;

namespace Wavee;

public static partial class Diagnostics
{
    public sealed partial class InspectSession
    {
        /// <summary>`dj {do?, uri?, pages?}`.</summary>
        string DjVerb(int id, string name, Inspect.Args a)
        {
            string verb = a.Str("do").ToLowerInvariant();
            string uri = a.Str("uri", Entities.LibrarySeed.DjUri);
            switch (verb)
            {
                case "":
                case "status":
                    return Inspect.Reply.Ok(id, name, w => { WriteDjList(w, uri); WriteDjSession(w); });
                case "play":
                    {
                        if (_shell is null) return Inspect.Reply.Fail(id, name, "dj do=play needs a mount");
                        if (!EntityId.TryParse(uri.AsSpan(), out EntityId list)) return Inspect.Reply.Fail(id, name, "not a uri: " + uri);
                        int served = Entities.FakeLexiconServed;
                        Playback.PlayContext(list);
                        string phase = "traced";
                        if (!a.Has("trace"))
                        {
                            // The resolve runs on an api thread: frames until the deck names the list (or the budget runs out), then its load.
                            long until = Environment.TickCount64 + PlayLoadBudgetMs;
                            while (!Playback.Snap().Context.Equals(list) && Environment.TickCount64 < until) { RunFrames(1); Thread.Sleep(2); }
                            phase = AwaitPlaybackLoad(PlayLoadBudgetMs);
                        }
                        return Settled(id, name, a, w =>
                        {
                            w.WriteString("do", verb);
                            w.WriteString("phase", phase);
                            w.WriteNumber("lexiconAnswers", Entities.FakeLexiconServed - served);
                            WriteDjSession(w);
                        });
                    }
                case "answers":
                    {
                        int pages = Math.Clamp(a.Int("pages", 3), 0, 12);
                        return Inspect.Reply.Ok(id, name, w =>
                        {
                            WriteDjList(w, uri);
                            WriteFakeAnswers(w, pages);
                        });
                    }
                case "jump":
                    {
                        if (_shell is null) return Inspect.Reply.Fail(id, name, "dj do=jump needs a mount");
                        Playback.State before = Playback.Snap();
                        int served = Entities.FakeLexiconServed, jumps = Playback.Dj.Jumps, pages = Playback.Dj.Current?.Pages.Count ?? 0;
                        int target = Playback.Dj.NextJumpTarget(global::Wavee.Queue.Rows, before.Cursor.Index);
                        Playback.DjJump("inspect");
                        if (!a.Has("trace"))
                        {
                            long until = Environment.TickCount64 + PlayLoadBudgetMs;
                            RunFrames(2);
                            while (Entities.FakeLexiconServed == served && Environment.TickCount64 < until && Playback.Dj.Jumps > jumps) { RunFrames(1); Thread.Sleep(2); }
                            AwaitPlaybackLoad(PlayLoadBudgetMs);
                            RunFrames(4);
                        }
                        Playback.State after = Playback.Snap();
                        return Settled(id, name, a, w =>
                        {
                            w.WriteString("do", verb);
                            w.WriteBoolean("jumped", Playback.Dj.Jumps > jumps);
                            w.WriteNumber("cursorBefore", before.Cursor.Index);
                            w.WriteNumber("targetBefore", target);
                            w.WriteNumber("cursorAfter", after.Cursor.Index);
                            w.WriteNumber("skippedToHistory", Math.Max(0, after.Cursor.Index - before.Cursor.Index - 1));
                            w.WriteNumber("lexiconAnswers", Entities.FakeLexiconServed - served);
                            w.WriteNumber("pagesBefore", pages);
                            w.WriteNumber("pagesAfter", Playback.Dj.Current?.Pages.Count ?? 0);
                            w.WriteNumber("nextJumpTarget", Playback.Dj.NextJumpTarget(global::Wavee.Queue.Rows, after.Cursor.Index));
                            WriteDjSession(w);
                        });
                    }
                case "seams":
                    {
                        int every = Math.Clamp(a.Int("samples", 250), 10, 5000), count = Math.Clamp(a.Int("n", 12), 1, 40);
                        return Inspect.Reply.Ok(id, name, w =>
                        {
                            w.WriteString("do", verb);
                            WriteSeams(w, count, every);
                            WriteJoins(w);
                        });
                    }
                case "telemetry":
                    {
                        // D5 (plan-dj §2.9): `on=true` arms the tap on what the play report hands Spotify.Telemetry.Enqueue (cleared), `on=false`
                        // drops it; without `on`, the events the tap holds: the RawCoreStream and RawCoreStreamSegment decoded field by field (the
                        // generated descriptor's names), the rest by name and size. Nothing is sent: `--fake` sends no telemetry.
                        if (a.Has("on"))
                        {
                            bool on = a.Str("on") != "false";
                            Spotify.Telemetry.Tap(on);
                            return Inspect.Reply.Ok(id, name, w => { w.WriteString("do", verb); w.WriteBoolean("tapped", on); });
                        }
                        var tapped = Spotify.Telemetry.TapNow();
                        bool all = a.Str("all") == "true";
                        return Inspect.Reply.Ok(id, name, w =>
                        {
                            w.WriteString("do", verb);
                            w.WriteNumber("events", tapped.Length);
                            w.WriteStartArray("payloads");
                            var json = new Google.Protobuf.JsonFormatter(Google.Protobuf.JsonFormatter.Settings.Default.WithFormatDefaultValues(false));
                            foreach ((string evName, byte[] payload) in tapped)
                            {
                                Google.Protobuf.IMessage? decoded = evName switch
                                {
                                    "RawCoreStream" => Wavee.Protocol.EventSender.Events.RawCoreStream.Parser.ParseFrom(payload),
                                    "RawCoreStreamSegment" => Wavee.Protocol.EventSender.Events.RawCoreStreamSegment.Parser.ParseFrom(payload),
                                    _ => null,
                                };
                                if (decoded is null && !all) continue;
                                w.WriteStartObject();
                                w.WriteString("event", evName);
                                w.WriteNumber("bytes", payload.Length);
                                if (decoded is not null)
                                {
                                    w.WritePropertyName("fields");
                                    using JsonDocument doc = JsonDocument.Parse(json.Format(decoded));
                                    doc.RootElement.WriteTo(w);
                                }
                                w.WriteEndObject();
                            }
                            w.WriteEndArray();
                        });
                    }
                case "starve":
                    {
                        Entities.FakeLexiconStarved = a.Str("on", "true") != "false";
                        return Inspect.Reply.Ok(id, name, w => { w.WriteString("do", verb); w.WriteBoolean("starved", Entities.FakeLexiconStarved); });
                    }
                case "expire":
                    {
                        if (!Playback.Dj.ExpireForInspect()) return Inspect.Reply.Fail(id, name, "dj do=expire: no lexicon session plays");
                        return Inspect.Reply.Ok(id, name, w => { w.WriteString("do", verb); WriteDjSession(w); });
                    }
                case "run":
                    {
                        if (_shell is null) return Inspect.Reply.Fail(id, name, "dj do=run needs a mount");
                        // Real time: 16 ms frames paced to the wall clock, so a clip's download (wall time) and its playback (the frame
                        // clock the silent endpoint is paced on) keep the proportions they have live.
                        int ms = Math.Clamp(a.Int("ms", 1000), 16, 600_000);
                        bool untilChange = a.Str("until", "ms") == "change";
                        ulong from = DeckItem();
                        long start = System.Diagnostics.Stopwatch.GetTimestamp();
                        int frames = 0;
                        for (; frames * 16 < ms; frames++)
                        {
                            RunFrames(1);
                            double due = (frames + 1) * 16.0, spent = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                            if (due > spent) Thread.Sleep(TimeSpan.FromMilliseconds(due - spent));
                            if (untilChange && DeckItem() != from) { frames++; break; }
                        }
                        return Inspect.Reply.Ok(id, name, w =>
                        {
                            w.WriteString("do", verb);
                            w.WriteNumber("framesRun", frames);
                            w.WriteNumber("ranMs", frames * 16);
                            w.WriteBoolean("moved", DeckItem() != from);
                            WriteNarration(w);
                        });
                    }
                case "transfer" when !a.Has("file"):   // with file= it is the Connect half's inbound transfer (DjConnectVerb)
                    {
                        int slot = -1;
                        var devices = Playback.Devices.Rows;
                        ulong us = Playback.Snap().Us;
                        for (int k = 0; k < devices.Length && slot < 0; k++) if (devices[k].Hash != us) slot = k;
                        if (slot < 0) return Inspect.Reply.Fail(id, name, "dj do=transfer: no other Connect device (mount connect=nearby)");
                        Playback.TransferTo(slot);
                        RunFrames(2);
                        return Inspect.Reply.Ok(id, name, w =>
                        {
                            w.WriteString("do", verb);
                            w.WriteNumber("slot", slot);
                            w.WriteString("owner", Playback.OwnerSignal.Peek().ToString());
                            WriteNarration(w);
                        });
                    }
                default:
                    return DjConnectVerb(id, name, verb, uri, a)   // the Connect half: Diagnostics.Inspect.DjConnect.cs
                        ?? Inspect.Reply.Fail(id, name, "dj wants do=status|answers|play|jump|seams|starve|expire|run|telemetry|" + DjConnectVerbs + ", not '" + verb + "'");
            }
        }

        /// <summary>The DJ list's row: its title and the two attributes a DJ start reads.</summary>
        static void WriteDjList(Utf8JsonWriter w, string uri)
        {
            w.WriteStartObject("list");
            w.WriteString("uri", uri);
            var eid = EntityId.Parse(uri.AsSpan());
            if (eid.Kind == EntityKind.Playlist && Entities.Current.Playlists.TryGetSlot(eid, out int slot))
            {
                var p = new Playlist(slot);
                w.WriteString("title", Entities.Strings.Resolve(p.TitleId));
                w.WriteBoolean("yourDj", p.IsDj);
                w.WriteString("lexiconUrl", Entities.Strings.Resolve(p.LexiconUrlId));
            }
            else w.WriteBoolean("known", false);
            w.WriteEndObject();
        }

        /// <summary>The lexicon session playing now (none: <c>session: null</c>): what its answer stated, the pages fetched, the next
        /// page's url and the lookahead; the DJ book per queue row; then the deck and the queue as <c>transport</c> reads them.</summary>
        static void WriteDjSession(Utf8JsonWriter w)
        {
            Playback.State st = Playback.Snap();
            if (Playback.Dj.Current is not { } s) w.WriteNull("session");
            else
            {
                Playback.Dj.SessionFacts f = s.Facts;
                w.WriteStartObject("session");
                w.WriteString("context", s.Context.Text);
                w.WriteString("contextUrl", s.ContextUrl);
                w.WriteString("reason", s.Reason);
                w.WriteString("start", s.Start.ToString());
                w.WriteString("uri", f.Uri);
                w.WriteString("url", f.Url);
                w.WriteBoolean("dj", f.IsDj);
                w.WriteBoolean("infinite", f.Infinite);
                w.WriteBoolean("refusesAutoplay", s.RefusesAutoplay);
                w.WriteString("expiresAt", f.ExpiresAt > 0 ? DateTimeOffset.FromUnixTimeSeconds(f.ExpiresAt).ToString("u", System.Globalization.CultureInfo.InvariantCulture) : "");
                w.WriteBoolean("expired", f.ExpiresAt > 0 && DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= f.ExpiresAt);
                w.WriteNumber("renewals", s.Renewals);
                w.WriteStartObject("restrictions");
                w.WriteString("shuffle", f.NoShuffle);
                w.WriteString("repeatContext", f.NoRepeatContext);
                w.WriteString("repeatTrack", f.NoRepeatTrack);
                w.WriteEndObject();
                w.WriteNumber("metadataKeys", f.Metadata.Count);
                w.WriteBoolean("welcomeNarration", f.Metadata.ContainsKey("narration.welcome.ssml"));
                w.WriteString("jumpButton", f.Metadata.TryGetValue("dj.interactivity.localized_jump_button", out string? jump) ? jump : "");
                w.WriteNumber("pagesFetched", s.Pages.Count);
                w.WriteStartArray("pages");
                foreach (Playback.Dj.Page p in s.Pages)
                {
                    w.WriteStartObject();
                    w.WriteNumber("page", p.Ordinal);
                    w.WriteNumber("rows", p.Rows);
                    w.WriteString("segment", p.Segment);
                    w.WriteString("correlation", p.Correlation);
                    w.WriteNumber("metadataKeys", p.Metadata.Count);
                    w.WriteString("url", p.Url);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteString("pendingUrl", s.PendingUrl);
                int ahead = Playback.Dj.PagesAhead(st.Context, global::Wavee.Queue.Rows, st.Cursor.Index);
                w.WriteStartObject("lookahead");
                w.WriteNumber("lastPage", s.LastPage);
                w.WriteNumber("pagesAhead", ahead);
                w.WriteBoolean("wantsPage", ahead >= 0 && ahead < Playback.Dj.MinPagesAhead && s.PendingUrl.Length > 0);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            // The context's toggle restrictions as the reducer holds them, the user's modes they hold back, and what the buttons say.
            w.WriteStartObject("locks");
            w.WriteString("bits", st.Locks.Bits.ToString());
            w.WriteString("reason", st.Locks.Reason.ToString());
            w.WriteString("context", st.Locks.Context.IsEmpty ? "" : st.Locks.Context.Text);
            w.WriteBoolean("holding", st.Locks.Holding);
            w.WriteBoolean("heldShuffle", st.Locks.HeldShuffle);
            w.WriteString("heldRepeat", st.Locks.HeldRepeat.ToString());
            w.WriteString("shuffleTip", PlayerVerbs.ShuffleLock(track: false) ?? "");
            w.WriteString("repeatTip", PlayerVerbs.RepeatLock(track: false) ?? "");
            w.WriteEndObject();
            w.WriteNumber("booked", Playback.Dj.Booked);
            w.WriteStartArray("book");
            ReadOnlySpan<QueueEdge> rows = global::Wavee.Queue.Rows;
            for (int i = 0; i < rows.Length; i++)
            {
                if (Playback.Dj.RowOf(rows[i].ItemId) is not { } row) continue;
                w.WriteStartObject();
                w.WriteNumber("i", i);
                w.WriteString("row", (i == st.Cursor.Index ? ">" : "") + Short(global::Wavee.Queue.RefAt(i).Id));
                w.WriteNumber("page", row.Page);
                w.WriteNumber("track", row.Track);
                w.WriteString("segment", row.Segment);
                w.WriteBoolean("jumpTarget", row.JumpTarget);
                w.WriteString("narration", string.Join('+', Slots(row)));
                w.WriteString("subtitle", row.Subtitle);
                w.WriteString("decision", row.DecisionId);
                w.WriteString("attribution", row.Attribution);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteNumber("cursor", st.Cursor.Index);
            w.WriteNumber("jumps", Playback.Dj.Jumps);
            Playback.Dj.Facts df = Playback.DjNow.Peek();
            w.WriteStartObject("surfaces");
            w.WriteBoolean("active", df.Active);
            w.WriteString("pick", PlayerVerbs.Dj(track: false)?.Pick ?? "");
            w.WriteString("line", PlayerVerbs.Dj(track: false)?.Line ?? "");
            w.WriteBoolean("canJump", df.CanJump);
            w.WriteEndObject();
            w.WriteString("deviceContext", Spotify.Api.DeviceContext);
            WriteNarration(w);
            WriteTransport(w);

            static IEnumerable<string> Slots(Playback.Dj.Row row)
            {
                if (row.Intro is not null) yield return "intro";
                if (row.Jump is not null) yield return "jump";
                if (row.Outro is not null) yield return "outro";
            }
        }

        /// <summary>The deck row's queue item id (0: none).</summary>
        static ulong DeckItem()
        {
            int at = Playback.Snap().Cursor.Index;
            ReadOnlySpan<QueueEdge> rows = global::Wavee.Queue.Rows;
            return (uint)at < (uint)rows.Length ? rows[at].ItemId : 0;
        }

        /// <summary>The narration (plan-dj D2): what is current (the item, its position and length as the reducer holds them, whether it
        /// narrates, the caption), the narration rows the planner laid (slot, the track each belongs to, the key, the clip's state and
        /// length), every narration made this session, the prefetch log and the deck's timeline.</summary>
        static void WriteNarration(Utf8JsonWriter w)
        {
            Playback.State st = Playback.Snap();
            long now = Playback.FrameNowMs();
            ReadOnlySpan<QueueEdge> rows = global::Wavee.Queue.Rows;
            w.WriteStartObject("narration");
            w.WriteNumber("frameMs", now);
            w.WriteStartObject("current");
            int deck = st.Cursor.Index;
            ulong deckItem = (uint)deck < (uint)rows.Length ? rows[deck].ItemId : 0;
            w.WriteString("item", deckItem == 0 ? "" : Playback.Dj.Label(deckItem, global::Wavee.Queue.RefAt(deck)));
            w.WriteString("id", st.CurrentId.IsEmpty ? "" : st.CurrentId.Text);
            w.WriteString("phase", st.Phase.ToString());
            w.WriteNumber("positionMs", st.Position(now));
            w.WriteNumber("durationMs", st.DurationMs);
            w.WriteBoolean("narrating", st.Narrating);
            w.WriteBoolean("clipCurrent", st.ClipCurrent);
            w.WriteBoolean("targetUnder", st.TargetUnder);
            w.WriteBoolean("canSeek", st.CanSeek);
            w.WriteNumber("publishedPositionMs", Playback.PositionMs.Peek());
            w.WriteNumber("publishedDurationMs", Playback.DurationMs.Peek());
            w.WriteString("caption", Playback.Dj.Caption.Peek());
            // What the session document would keep for the next launch: during a clip, the track it belongs to from its start.
            Playback.RestorePoint point = Playback.RestorePointNow();
            w.WriteStartObject("restorePoint");
            w.WriteString("track", point.Track.IsEmpty ? "" : point.Track.Text);
            w.WriteNumber("cursor", point.CursorIndex);
            w.WriteNumber("positionMs", point.PositionMs);
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteStartArray("rows");
            for (int i = 0; i < rows.Length; i++)
            {
                if (Playback.Dj.NarrationOf(rows[i].ItemId) is not { } sp) continue;
                w.WriteStartObject();
                w.WriteNumber("i", i);
                w.WriteBoolean("deck", i == deck);
                WriteSpoken(w, sp, rows);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("first");                    // the deck and the next two rows, as the queue lays them
            for (int i = Math.Max(0, deck); i < rows.Length && i < Math.Max(0, deck) + 3; i++)
                w.WriteStringValue(Playback.Dj.Label(rows[i].ItemId, global::Wavee.Queue.RefAt(i)));
            w.WriteEndArray();
            int jumpAt = Playback.Dj.NextJumpTarget(rows, deck);
            w.WriteString("nextJumpTarget", jumpAt < 0 ? "" : Playback.Dj.Label(rows[jumpAt].ItemId, global::Wavee.Queue.RefAt(jumpAt)));
            w.WriteStartArray("made");
            foreach (Playback.Dj.Spoken sp in Playback.Dj.AllSpoken)
            {
                w.WriteStartObject();
                WriteSpoken(w, sp, rows);
                w.WriteBoolean("queued", global::Wavee.Queue.IndexOfItem(rows, sp.ItemId) >= 0);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("prefetch");
            foreach (Playback.Dj.PrefetchEntry e in Playback.Dj.PrefetchLog)
            {
                w.WriteStartObject();
                w.WriteNumber("atMs", e.AtMs);
                w.WriteString("why", e.Why);
                w.WriteString("slot", Playback.Dj.SlotName(e.Slot));
                w.WriteString("segment", e.Segment);
                w.WriteString("key", e.Key);
                w.WriteString("clipBefore", e.ClipBefore);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            // The play log (what the queue panels' history and Recently played read): the newest entries, and any narration in it (must be 0).
            IReadOnlyList<Shell.PlayEntry> log = Shell.PlayLog.Entries;
            int spokenInLog = 0;
            foreach (Shell.PlayEntry e in log) if (e.Track.Text.StartsWith(Spotify.DjTts.UriPrefix, StringComparison.Ordinal)) spokenInLog++;
            w.WriteNumber("playLogNarrations", spokenInLog);
            w.WriteStartArray("playLog");
            for (int i = Math.Max(0, log.Count - 8); i < log.Count; i++) w.WriteStringValue(log[i].Track.Text);
            w.WriteEndArray();
            WriteSeams(w, 6, 0);
            WriteJoins(w);
            w.WriteStartArray("timeline");
            foreach (Playback.Dj.TimelineEntry e in Playback.Dj.Timeline)
            {
                w.WriteStartObject();
                w.WriteString("item", e.Item);
                w.WriteNumber("startMs", e.StartMs);
                w.WriteNumber("endMs", e.EndMs);
                w.WriteNumber("durationMs", e.DurationMs);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }

        /// <summary>The plan of every seam from the deck on (D3, D3b; <see cref="Playback.Dj.Seam"/>, the call Mix.Follow makes): A and B, the
        /// kind, the timing, and its events on A's clock (ms from A's start): the window (a duck's start, a radio fade's start), B's start and
        /// its position in its own file, the duck's landing, the voice's end, where B becomes current. <paramref name="every"/> &gt; 0 samples
        /// each side's gain over its window.</summary>
        static void WriteSeams(Utf8JsonWriter w, int count, int every)
        {
            Playback.State st = Playback.Snap();
            ReadOnlySpan<QueueEdge> rows = global::Wavee.Queue.Rows;
            w.WriteStartArray("seams");
            for (int i = Math.Max(0, st.Cursor.Index), n = 0; i >= 0 && i < rows.Length && n < count; i = global::Wavee.Queue.NextIndex(rows, i), n++)
            {
                int next = global::Wavee.Queue.NextIndex(rows, i);
                if (next < 0) break;
                Playback.Dj.SeamFacts f = Playback.Dj.Seam(st.Context, rows, i);
                MixTiming t = f.Timing;
                w.WriteStartObject();
                w.WriteString("a", Playback.Dj.Label(rows[i].ItemId, global::Wavee.Queue.RefAt(i)));
                w.WriteString("b", Playback.Dj.Label(rows[next].ItemId, global::Wavee.Queue.RefAt(next)));
                w.WriteString("kind", f.Kind.ToString());
                w.WriteNumber("aMs", f.AMs);
                w.WriteNumber("bMs", f.BMs);
                if (f.VoiceA is null && f.CuesA.EndCueMs > 0) w.WriteNumber("aEndCue", f.CuesA.EndCueMs);
                if (f.VoiceB is null && f.CuesB.StartCueMs > 0)
                {
                    w.WriteNumber("bStartCue", f.CuesB.StartCueMs);
                    w.WriteString("bCueOrigin", !f.CuesB.OriginKnown ? "?" : f.CuesB.StartIsHuman ? "human" : "ml");
                    w.WriteNumber("bCueConfidence", f.CuesB.StartConfidence);
                }
                if (!t.Mix) { w.WriteString("refused", t.WhyNot.ToString()); w.WriteEndObject(); continue; }
                w.WriteString("shape", t.Shape.ToString());
                w.WriteNumber("outStart", t.OutStartMs);
                w.WriteNumber("outDuration", t.OutDurationMs);
                w.WriteNumber("inStart", t.InStartMs);
                w.WriteNumber("inDuration", t.InDurationMs);
                w.WriteNumber("inDelay", t.InDelayMs);
                w.WriteNumber("overlap", t.AudibleEndMs - t.InAtMs);
                w.WriteString("announceAt", t.AnnounceAt.ToString());
                w.WriteStartObject("events");
                if (f.Kind == Playback.Dj.SeamKind.Outro)
                {
                    w.WriteNumber("duckStart", t.OutStartMs);
                    w.WriteNumber("duckLands", t.InAtMs);
                    w.WriteNumber("voiceStarts", t.InAtMs);
                    w.WriteNumber("trackEnds", t.AudibleEndMs);
                    if (f.BMs > 0) w.WriteNumber("voiceEnds", t.InAtMs + f.BMs);
                }
                else if (f.Kind == Playback.Dj.SeamKind.TalkOver)
                {
                    w.WriteNumber("trackStarts", t.InAtMs);
                    w.WriteNumber("trackStartsAtPosition", t.InStartMs);
                    w.WriteNumber("voiceEnds", t.AudibleEndMs);
                    w.WriteNumber("windowEnds", t.InAtMs + t.InDurationMs);
                }
                else
                {
                    w.WriteNumber("fadeOutStarts", t.OutStartMs);
                    w.WriteNumber("bStarts", t.InAtMs);
                    w.WriteNumber("bStartsAtPosition", t.InStartMs);
                    w.WriteNumber("bFadeInEnds", t.InAtMs + t.InDurationMs);
                    w.WriteNumber("bPositionAtFadeInEnd", t.InStartMs + t.InDurationMs);
                    w.WriteNumber("aFadeEnds", t.AudibleEndMs);
                }
                w.WriteNumber("bCurrentAt", t.AnnounceMs);
                w.WriteNumber("bPositionWhenCurrent", t.InStartMs + (t.AnnounceMs - t.InAtMs));
                w.WriteEndObject();
                w.WriteString("outCurve", Playback.DjCurves.Json(in t, incoming: false));
                w.WriteString("inCurve", Playback.DjCurves.Json(in t, incoming: true));
                if (every > 0)
                {
                    w.WriteString("outGain", Samples(in t, false, t.OutDurationMs, every));
                    w.WriteString("inGain", Samples(in t, true, t.InDurationMs, every));
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();

            static string Samples(in MixTiming t, bool incoming, uint window, int every)
            {
                var sb = new System.Text.StringBuilder();
                for (long ms = 0; ; ms += every)
                {
                    long at = Math.Min(ms, window);
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(at.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                      .Append(Playback.DjCurves.GainAt(in t, incoming, at).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
                    if (at >= window) break;
                }
                return sb.ToString();
            }
        }

        /// <summary>The AI DJ joins the pump scheduled (<see cref="Playback.Audio.DjJoins"/>): the plan, and A's position on its own clock at
        /// the window's start, B's start, the announce and the window's end as the mixer frames put them; whether B was heard and the join
        /// announced.</summary>
        static void WriteJoins(Utf8JsonWriter w)
        {
            w.WriteStartArray("joins");
            foreach (Playback.Audio.DjJoin j in Playback.Audio.DjJoins())
            {
                w.WriteStartObject();
                w.WriteString("a", Short(j.A));
                w.WriteString("b", Short(j.B));
                w.WriteString("source", j.Plan.Source.ToString());
                w.WriteString("shape", j.Plan.Shape.ToString());
                w.WriteString("announceAt", j.Plan.AnnounceAt.ToString());
                w.WriteNumber("planOutStart", j.Plan.OutStartMs);
                w.WriteNumber("planInAt", j.Plan.InAtMs);
                w.WriteNumber("planAnnounce", j.Plan.AnnounceMs);
                w.WriteNumber("planOutEnd", j.Plan.AudibleEndMs);
                w.WriteNumber("aPosAtWindow", j.APosAtWindow);
                w.WriteNumber("aPosAtBStart", j.APosAtIn);
                w.WriteNumber("aPosAtAnnounce", j.APosAtAnnounce);
                w.WriteNumber("aPosAtOutEnd", j.APosAtOutEnd);
                w.WriteNumber("framesWindowToB", j.InFrame - j.StartFrame);
                w.WriteNumber("rate", j.Rate);
                w.WriteBoolean("heard", j.SoundingClock >= 0);
                w.WriteBoolean("announced", j.AnnouncedClock >= 0);
                w.WriteBoolean("cancelled", j.Cancelled);
                w.WriteBoolean("lateShifted", j.LateShifted);
                // What the play report takes for the telemetry overlap fields (plan-dj §2.9): the ms both sides sounded, from the frames.
                w.WriteNumber("overlapMs", j.OverlapMs());
                if (j.EndClock >= 0) w.WriteNumber("cutAtFrame", j.EndClock);
                w.WriteBoolean("reportedA", j.ReportedA);
                w.WriteBoolean("reportedB", j.ReportedB);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }

        static void WriteSpoken(Utf8JsonWriter w, Playback.Dj.Spoken sp, ReadOnlySpan<QueueEdge> rows)
        {
            w.WriteString("slot", Playback.Dj.SlotName(sp.Slot));
            w.WriteString("belongsTo", sp.Track.IsEmpty ? "" : EntityUri.IdOf(sp.Track.Text).ToString());
            w.WriteNumber("belongsToIndex", global::Wavee.Queue.IndexOfItem(rows, sp.BelongsTo));
            w.WriteString("segment", sp.Segment);
            w.WriteString("mediaUri", sp.MediaUri);
            w.WriteString("provider", sp.Provider);
            w.WriteString("title", sp.Title);
            w.WriteString("artist", sp.Artist);
            w.WriteString("key", sp.Key);
            bool known = Spotify.DjTts.TryGet(sp.Key, out Spotify.DjTts.ClipInfo clip);
            w.WriteString("clip", known ? clip.State.ToString() : "none");
            w.WriteNumber("clipDurationMs", known ? clip.DurationMs : -1);
            w.WriteNumber("clipBytes", known ? clip.Bytes : 0);
            w.WriteBoolean("ensured", sp.Ensured);
            w.WriteNumber("ensuredAtMs", sp.EnsuredAtMs);
            w.WriteString("ensuredWhy", sp.EnsuredWhy);
            w.WriteBoolean("dropped", sp.Dropped);
            w.WriteString("caption", sp.Caption);
        }

        /// <summary>The fake service's session and its first <paramref name="pages"/> pages, each through
        /// <see cref="Spotify.Decode.ContextResolve"/>: the shape the resolver will read.</summary>
        static void WriteFakeAnswers(Utf8JsonWriter w, int pages)
        {
            Spotify.Decode.ClusterBuffer buffer = Spotify.Decode.ClusterBuffer.Rent();
            try
            {
                string path = Playback.RemotePlan.PageRoute(Entities.LibrarySeed.DjLexiconUrl) + "&reason=interactive";
                w.WriteStartArray("answers");
                for (int n = 0; n <= pages && path.Length > 0; n++)
                {
                    byte[]? body = Entities.FakeLexicon(path);
                    w.WriteStartObject();
                    w.WriteNumber("page", n);
                    w.WriteString("path", path);
                    if (body is null) { w.WriteBoolean("served", false); w.WriteEndObject(); break; }
                    Spotify.Decode.ContextPage page = Spotify.Decode.ContextResolve(body, buffer);
                    w.WriteNumber("bytes", body.Length);
                    w.WriteString("uri", System.Text.Encoding.UTF8.GetString(buffer.Utf8(page.Uri)));
                    w.WriteNumber("tracks", page.TrackCount);
                    w.WriteStartArray("rows");
                    foreach (ref readonly Spotify.Decode.ClusterTrack t in buffer.Tracks(page.TrackStart, page.TrackCount))
                        w.WriteStringValue(System.Text.Encoding.UTF8.GetString(buffer.Utf8(t.Uri)));
                    w.WriteEndArray();
                    string next = System.Text.Encoding.UTF8.GetString(buffer.Utf8(page.NextPageUrl));
                    w.WriteString("next", next);
                    w.WriteEndObject();
                    path = next.Length > 0 ? Playback.RemotePlan.PageRoute(next) : "";
                }
                w.WriteEndArray();
            }
            finally { Spotify.Decode.ClusterBuffer.Return(buffer); }
        }
    }
}
