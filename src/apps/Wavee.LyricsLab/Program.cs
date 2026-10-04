using System.Text.Encodings.Web;
using System.Text.Json;
using Wavee;

namespace Wavee.LyricsLab;

/// <summary>Dumps, per track: meta.json (the lyrics request), lyrics.&lt;source&gt;.json (every source's candidate) and
/// audio.ogg (the decrypted Ogg Vorbis 320 stream). Runs the headless session composition against a COPIED profile:
/// the credential slot refuses removal, settings are an in-memory overlay, and the device id is the headless one.
/// Usage: Wavee.LyricsLab --headless --profile &lt;scratch dir with store.json&gt; --out &lt;dir&gt; --tracks id1,id2,...</summary>
static class Program
{
    static Diagnostics.HeadlessLoop s_loop = null!;
    static int s_tickQueued;

    static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--check-lrc") return CheckLrc(args[1..]);
        if (args.Length >= 2 && args[0] == "--ai-mem") return AiMemory(args[1], args.Length >= 3 ? args[2] : "en");
        if (args.Length >= 2 && args[0] == "--ai-bench") return AiBench(args[1], args.Length >= 3 ? args[2] : "en");
        string profile = Arg(args, "--profile"), outDir = Arg(args, "--out"), tracks = Arg(args, "--tracks"), search = Arg(args, "--search");
        string langArg = Arg(args, "--langs");
        string[] langs = (langArg.Length > 0 ? langArg : "en,es").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool skipAudio = args.Contains("--no-audio");
        if (!args.Contains("--headless") || profile.Length == 0 || (search.Length == 0 && (outDir.Length == 0 || tracks.Length == 0)))
        {
            Console.Error.WriteLine("usage: Wavee.LyricsLab --headless --profile <scratch dir> --out <dir> --tracks id1,id2 [--no-audio] [--ai <ai dir> [--langs en,es,nl,ko]]");
            Console.Error.WriteLine("       Wavee.LyricsLab --headless --profile <scratch dir> --search \"query one;query two\"");
            return 64;
        }
        string live = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wavee"));
        if (string.Equals(Path.GetFullPath(profile).TrimEnd('\\'), live, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("refusing to run against the live profile; copy store.json into a scratch folder");
            return 78;
        }

        // Platform.Boot parses argv itself; --headless on the command line keeps it from arming a factory reset.
        Platform.ProfileRoot = profile;
        Platform.Boot();
        Platform.UseSettings(new Diagnostics.Headless.OverlaySettings(Platform.BackingSettings));
        Platform.UseCredentialSlot(new Diagnostics.Headless.ProtectedLocalStore(new FileLocalStore(Platform.StorePath), "headless",
            refused: static k => Console.Error.WriteLine("refused to remove " + k)), new DpapiProtector());
        if (!Platform.HasStoredCredential()) { Console.Error.WriteLine("no stored credential in " + profile); return 67; }

        s_loop = new Diagnostics.HeadlessLoop();
        Playback.ToUi = s_loop.Post;
        Spotify.Post = s_loop.Post;
        Store.Post = s_loop.Post;
        Palette.Post = s_loop.Post;
        Playback.FrameNowMs = static () => s_loop.NowMs;

        // App.RegisterShapes is internal (the store schema; the headless host calls it first, so this does too).
        typeof(App).GetMethod("RegisterShapes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, null);
        Store.Use(null);
        Entities.Boot(Platform.Scope);
        Spotify.Boot();
        Playback.Boot();
        Spotify.Api.Boot();
        // The private key path, when this checkout carries the junction (Wavee.csproj compiles it in); by reflection so
        // this tool also builds and runs on a public-only checkout.
        Type.GetType("Wavee.PlayPlayHost, Wavee")?.GetMethod("Install")?.Invoke(null, null);

        using var tick = new Timer(static _ =>
        {
            if (Interlocked.CompareExchange(ref s_tickQueued, 1, 0) == 0) s_loop.Post(Tick);
        }, null, 100, 100);

        int exit = 1;
        string[] ids = tracks.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Task.Run(async () =>
        {
            try
            {
                exit = search.Length > 0 ? await SearchWork(search)
                    : Arg(args, "--ai").Length > 0 ? await AiWork(ids, outDir, Arg(args, "--ai"), langs)
                    : await Work(ids, outDir, skipAudio);
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); exit = 70; }
            finally
            {
                Spotify.Disconnect();                                 // closes the sockets, keeps the credential
                await Task.Delay(500);
                s_loop.Stop();
            }
        });
        s_loop.Post(Spotify.Login);
        s_loop.Run();

        Platform.Shutdown();
        return exit;
    }

    static void Tick()
    {
        Volatile.Write(ref s_tickQueued, 0);
        Entities.Now = Store.ToApp(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Fetch.Pump();
        Entities.Publish();
    }

    static async Task<int> Work(string[] ids, string outDir, bool skipAudio)
    {
        long deadline = Environment.TickCount64 + 30_000;
        while (!Spotify.Current.IsOnline)
        {
            if (Spotify.Current.Phase == Spotify.SessionPhase.Failed)
            {
                Console.Error.WriteLine("login failed: " + Spotify.Current.Fault + " (the stored credential was kept)");
                return 77;
            }
            if (Environment.TickCount64 > deadline) { Console.Error.WriteLine("login timeout at " + Spotify.Current.Phase); return 75; }
            await Task.Delay(100);
        }
        Console.WriteLine("online");

        var http = new Lyrics.HttpFetch();
        var grey = Lyrics.HttpFetch.Grey;
        var sources = new List<Lyrics.ISource>
        {
            new Lyrics.Sources.SpotifyNative(Spotify.Api.GetTextAsync, Spotify.SpclientBaseUrl),
            new Lyrics.Sources.Amll(http),
            new Lyrics.Sources.Musixmatch(grey),
            new Lyrics.Sources.Qq(grey),
            new Lyrics.Sources.Netease(grey),
            new Lyrics.Sources.Kugou(grey),
            new Lyrics.Sources.LrcLib(http),
        };

        int failures = 0;
        foreach (string id in ids)
        {
            string dir = Path.Combine(outDir, id);
            Directory.CreateDirectory(dir);
            Lyrics.Request? req = await ResolveRequest(id);
            if (req is null) { Console.Error.WriteLine(id + ": no metadata"); failures++; continue; }
            WriteMeta(Path.Combine(dir, "meta.json"), req);
            Console.WriteLine($"{id}: {req.Title} - {req.ArtistsJoined} ({req.DurationMs} ms)");

            var fetches = sources.Select(async s =>
            {
                try
                {
                    using var cts = new CancellationTokenSource(20_000);
                    Lyrics.Candidate? c = await s.FetchAsync(req, cts.Token);
                    if (c is null) { Console.WriteLine($"  {s.Id,-11} miss"); return; }
                    WriteDoc(Path.Combine(dir, "lyrics." + s.Id + ".json"), c);
                    Console.WriteLine($"  {s.Id,-11} {c.Sync,-9} {c.LineCount} lines");
                }
                catch (Exception ex) { Console.WriteLine($"  {s.Id,-11} error {ex.GetType().Name}: {ex.Message}"); }
            });
            await Task.WhenAll(fetches);

            if (!skipAudio && !DumpAudio(req, Path.Combine(dir, "audio.ogg"))) failures++;
        }
        return failures == 0 ? 0 : 1;
    }

    /// <summary>--ai-mem &lt;ai dir&gt; [en,es]: load the models (no Spotify) and print the process memory before and after,
    /// so the steady-state cost of the loaded NPU sessions can be measured on its own.</summary>
    static int AiMemory(string aiDir, string langs)
    {
        static string Mem()
        {
            using var p = System.Diagnostics.Process.GetCurrentProcess();
            return $"working set {p.WorkingSet64 >> 20} MB, private {p.PrivateMemorySize64 >> 20} MB, peak ws {p.PeakWorkingSet64 >> 20} MB";
        }
        Console.WriteLine("before: " + Mem());
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using (var models = AiLyrics.LoadedModels.Load(Path.Combine(aiDir, "runtime"), Path.Combine(aiDir, "models"), langs.Split(','),
            (done, total, cached) => Console.WriteLine($"  {done}/{total}{(cached ? " (cached)" : "")} at {sw.Elapsed.TotalSeconds:0.0}s: {Mem()}"),
            CancellationToken.None))
        {
            GC.Collect(); GC.WaitForPendingFinalizers();
            Console.WriteLine($"loaded in {sw.Elapsed.TotalSeconds:0.0}s: {Mem()}");
            Thread.Sleep(3000);
            Console.WriteLine("after 3 s: " + Mem());
        }
        Console.WriteLine("disposed: " + Mem());
        return 0;
    }

    /// <summary>The real C# pipeline (AiLyrics.TrackJob on the NPU) over each track: Spotify's lyrics, the decoded audio, a
    /// simulated playhead that starts with the job. Writes align.vocals.cs.json (the lab's format) and prints timings.</summary>
    static async Task<int> AiWork(string[] ids, string outDir, string aiDir, string[] langs)
    {
        while (!Spotify.Current.IsOnline)
        {
            if (Spotify.Current.Phase == Spotify.SessionPhase.Failed) { Console.Error.WriteLine("login failed"); return 77; }
            await Task.Delay(100);
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var models = AiLyrics.LoadedModels.Load(Path.Combine(aiDir, "runtime"), Path.Combine(aiDir, "models"), langs,
            (done, total, cached) => Console.WriteLine($"  preparing {done}/{total}{(cached ? " (cached)" : "")} at {sw.Elapsed.TotalSeconds:0.0}s"),
            CancellationToken.None);
        Console.WriteLine($"models loaded in {sw.Elapsed.TotalSeconds:0.0}s, ORT {models.Ort.Version}, NPU vendor '{models.Ort.NpuVendor}'");
        var spotify = new Lyrics.Sources.SpotifyNative(Spotify.Api.GetTextAsync, Spotify.SpclientBaseUrl);
        int failures = 0;
        foreach (string id in ids)
        {
            string dir = Path.Combine(outDir, id);
            Directory.CreateDirectory(dir);
            Lyrics.Request? req = await ResolveRequest(id);
            if (req is null) { failures++; continue; }
            var cand = await spotify.FetchAsync(req, CancellationToken.None);
            if (cand is null) { Console.Error.WriteLine(id + ": no Spotify lyrics"); failures++; continue; }
            var src = cand.Document;
            string lang = AiLyrics.Rules.LanguageOf(src);
            if (!models.Aligners.ContainsKey(lang)) { Console.Error.WriteLine($"{id}: language '{lang}' not loaded (--langs)"); failures++; continue; }
            Console.WriteLine($"{req.Title} - {req.ArtistsJoined}: language {lang}, {src.Lines.Count} lines, sync {src.Sync}");
            using var pcm = AiLyrics.SpotifyPcm.Open(req.Uri, req.DurationMs, CancellationToken.None, out var fault);
            if (pcm is null) { Console.Error.WriteLine(id + ": audio " + fault); failures++; continue; }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var job = new AiLyrics.TrackJob(src, lang, models.Separator, models.Aligners[lang], pcm, () => clock.Elapsed.TotalSeconds);
            var readyAt = new double[src.Lines.Count];
            Array.Fill(readyAt, double.NaN);
            double firstPublish = double.NaN;
            var doc = job.Run((d, p) =>
            {
                double now = clock.Elapsed.TotalSeconds;
                if (double.IsNaN(firstPublish)) firstPublish = now;
                for (int i = 0; i < p.LinesReady && i < readyAt.Length; i++) if (double.IsNaN(readyAt[i])) readyAt[i] = now;
            }, CancellationToken.None);
            int late = 0, timed = 0;
            using (var fs = File.Create(Path.Combine(dir, "align.vocals.cs.json")))
            using (var w = new Utf8JsonWriter(fs, s_json))
            {
                w.WriteStartArray();
                for (int i = 0; i < doc.Lines.Count; i++)
                {
                    var l = doc.Lines[i];
                    w.WriteStartObject();
                    w.WriteString("text", src.Lines[i].Text);
                    w.WriteNumber("refStartMs", src.Lines[i].StartMs);
                    w.WriteStartArray("words");
                    var words = AiLyrics.Align.Words(l.Text);
                    if (l.IsWordByWord)
                    {
                        timed++;
                        if (l.StartMs / 1000.0 < readyAt[i]) late++;
                        for (int k = 0; k < l.Syllables.Count && k < words.Count; k++)
                        {
                            w.WriteStartObject();
                            w.WriteString("w", words[k].Text);
                            w.WriteNumber("s", l.Syllables[k].StartMs);
                            w.WriteNumber("e", l.Syllables[k].EndMs);
                            w.WriteEndObject();
                        }
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            Console.WriteLine($"{req.Title} - {req.ArtistsJoined}: {req.DurationMs / 1000}s in {job.Elapsed.Elapsed.TotalSeconds:0.0}s " +
                              $"(separate {job.SeparateSeconds:0.0}s, align {job.AlignSeconds:0.0}s), first lines {firstPublish:0.0}s, " +
                              $"word-synced lines {timed}/{doc.Lines.Count}, ready after sung {late}");
        }
        return failures == 0 ? 0 : 1;
    }

    /// <summary>--search "q1;q2": the app's own track search (the Pathfinder searchTracks query), printing the top hits as
    /// "id  title - artists (duration)" so tracks can be picked for the other modes.</summary>
    static async Task<int> SearchWork(string queries)
    {
        long deadline = Environment.TickCount64 + 30_000;
        while (!Spotify.Current.IsOnline)
        {
            if (Spotify.Current.Phase == Spotify.SessionPhase.Failed) { Console.Error.WriteLine("login failed"); return 77; }
            if (Environment.TickCount64 > deadline) { Console.Error.WriteLine("login timeout"); return 75; }
            await Task.Delay(100);
        }
        foreach (string q in queries.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var r = Spotify.Api.Search(Spotify.Api.SearchFacet.Tracks, q, 0, 6, CancellationToken.None);
            Console.WriteLine($"# {q} (status {r.Status})");
            if (r.Status != 200) continue;
            using var doc = JsonDocument.Parse(r.Body);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            Walk(doc.RootElement, seen);
        }
        return 0;

        static void Walk(JsonElement e, HashSet<string> seen)
        {
            if (e.ValueKind == JsonValueKind.Array) { foreach (var x in e.EnumerateArray()) Walk(x, seen); return; }
            if (e.ValueKind != JsonValueKind.Object) return;
            if (e.TryGetProperty("uri", out var u) && u.ValueKind == JsonValueKind.String && u.GetString() is { } uri
                && uri.StartsWith("spotify:track:", StringComparison.Ordinal) && e.TryGetProperty("name", out var n) && seen.Add(uri))
            {
                var artists = new List<string>();
                if (e.TryGetProperty("artists", out var a) && a.ValueKind == JsonValueKind.Object && a.TryGetProperty("items", out var items))
                    foreach (var it in items.EnumerateArray())
                        if (it.TryGetProperty("profile", out var p) && p.TryGetProperty("name", out var an)) artists.Add(an.GetString() ?? "");
                long ms = e.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Object && d.TryGetProperty("totalMilliseconds", out var t) ? t.GetInt64() : 0;
                Console.WriteLine($"  {uri["spotify:track:".Length..]}  {n.GetString()} - {string.Join(", ", artists)} ({ms / 1000}s)");
            }
            foreach (var p in e.EnumerateObject()) Walk(p.Value, seen);
        }
    }

    /// <summary>--ai-bench &lt;ai dir&gt; [en,es]: load the models (no Spotify), print each graph's preparation time
    /// (compile or cached), then time one aligner chunk (10 s of audio through the five graphs) per language.</summary>
    static int AiBench(string aiDir, string langs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        double last = 0;
        string[] list = langs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        using var models = AiLyrics.LoadedModels.Load(Path.Combine(aiDir, "runtime"), Path.Combine(aiDir, "models"), list,
            (done, total, cached) =>
            {
                double now = sw.Elapsed.TotalSeconds;
                if (done > 0 && now > last) Console.WriteLine($"  graph {done}/{total}{(cached ? " (cached)" : " (compiled)")}: {now - last:0.0}s");
                last = now;
            },
            CancellationToken.None);
        Console.WriteLine($"models loaded in {sw.Elapsed.TotalSeconds:0.0}s, ORT {models.Ort.Version}");
        var rng = new Random(1);
        var chunk = new float[AiLyrics.Aligner.Samples];
        foreach (string lang in list)
        {
            var a = models.Aligners[lang];
            var logp = new float[AiLyrics.Aligner.StepFrames * a.Vocab.Classes];
            for (int i = 0; i < chunk.Length; i++)
                chunk[i] = (float)(Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble()));
            int classes = a.Run(chunk, logp);                                                  // warm-up
            const int runs = 20;
            var t = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < runs; i++) a.Run(chunk, logp);
            Console.WriteLine($"{lang}: {classes} classes (vocab {a.Vocab.Classes}, blank {a.Vocab.Blank}, separator {a.Vocab.Separator}), " +
                              $"{t.Elapsed.TotalMilliseconds / runs:0.0} ms per 10 s chunk ({8.0 * runs / t.Elapsed.TotalSeconds:0}x real time at the 8 s step)");
        }
        return 0;
    }

    static bool DumpAudio(Lyrics.Request req, string path)
    {
        using var cts = new CancellationTokenSource(120_000);
        Spotify.Audio.Opened o = Spotify.Audio.Open(req.Uri, Spotify.Audio.Quality.VeryHigh320, req.DurationMs, cts.Token);
        if (!o.Ok) { Console.Error.WriteLine($"  audio      open failed: {o.Fault}"); return false; }
        try
        {
            using (var file = File.Create(path)) o.Stream!.CopyTo(file);
            byte[] head = new byte[4];
            using (var f = File.OpenRead(path)) f.ReadExactly(head);
            bool ogg = head is [(byte)'O', (byte)'g', (byte)'g', (byte)'S'];
            Console.WriteLine($"  audio      {o.Fmt} {new FileInfo(path).Length / 1024} KB gain={o.GainDb:0.0} dB {(ogg ? "OggS" : "NOT OGG")}");
            return ogg;
        }
        finally { o.Stream?.Dispose(); o.Body?.Dispose(); }
    }

    /// <summary>The same base62 -> Request mapping as the headless host: the entity is read on the loop thread.</summary>
    static async Task<Lyrics.Request?> ResolveRequest(string trackId)
    {
        if (!Base62.TryDecode(trackId.AsSpan(), out UInt128 gid)) return null;
        EntityId id = EntityId.ForGid(EntityKind.Track, gid);
        long deadline = Environment.TickCount64 + 10_000;
        while (true)
        {
            var tcs = new TaskCompletionSource<Lyrics.Request?>(TaskCreationOptions.RunContinuationsAsynchronously);
            s_loop.Post(() =>
            {
                Track track = Entities.Track(id);
                if (!track.Knows(TrackFields.Identity)) Entities.Ensure(track, TrackFields.Identity, FetchPriority.Visible);
                tcs.TrySetResult(Lyrics.RequestFrom(track, trackId));
            });
            Lyrics.Request? r = await tcs.Task;
            if (r is not null || Environment.TickCount64 >= deadline) return r;
            await Task.Delay(100);
        }
    }

    static readonly JsonWriterOptions s_json = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static void WriteMeta(string path, Lyrics.Request r)
    {
        using var fs = File.Create(path);
        using var w = new Utf8JsonWriter(fs, s_json);
        w.WriteStartObject();
        w.WriteString("trackId", r.TrackId);
        w.WriteString("uri", r.Uri);
        w.WriteString("title", r.Title);
        w.WriteString("artists", r.ArtistsJoined);
        w.WriteString("album", r.Album);
        w.WriteNumber("durationMs", r.DurationMs);
        w.WriteString("isrc", r.Isrc);
        w.WriteEndObject();
    }

    static void WriteDoc(string path, Lyrics.Candidate c)
    {
        Lyrics.Doc d = c.Document;
        using var fs = File.Create(path);
        using var w = new Utf8JsonWriter(fs, s_json);
        w.WriteStartObject();
        w.WriteString("provider", c.ProviderId);
        w.WriteString("basis", c.Basis.ToString());
        w.WriteString("sync", d.Sync.ToString());
        w.WriteNumber("confidence", c.Confidence);
        w.WriteNumber("offsetMsApplied", d.OffsetMsApplied);
        w.WriteStartArray("lines");
        foreach (Lyrics.Line l in d.Lines)
        {
            w.WriteStartObject();
            w.WriteNumber("startMs", l.StartMs);
            if (l.EndMs is long e) w.WriteNumber("endMs", e); else w.WriteNull("endMs");
            w.WriteString("text", l.Text);
            w.WriteStartArray("words");
            foreach (Lyrics.Syllable s in l.Syllables)
            {
                w.WriteStartObject();
                w.WriteNumber("startMs", s.StartMs);
                w.WriteNumber("endMs", s.EndMs);
                w.WriteString("text", s.Text);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    /// <summary>Parses each generated enhanced-LRC file with Wavee's own parser and checks that it is word-synced, that
    /// every word has a time, and that word times run forward inside each line and across lines.</summary>
    static int CheckLrc(string[] paths)
    {
        int bad = 0;
        foreach (string path in paths)
        {
            Lyrics.Doc doc = Lyrics.Text.ParseLrc(File.ReadAllText(path), "check", "local-ai");
            int words = 0, untimed = 0, backwards = 0, lineBackwards = 0, wordByWordLines = 0;
            long prevLine = long.MinValue;
            foreach (Lyrics.Line l in doc.Lines)
            {
                if (l.IsWordByWord) wordByWordLines++;
                if (l.StartMs < prevLine) lineBackwards++;
                prevLine = l.StartMs;
                long prev = long.MinValue;
                foreach (Lyrics.Syllable s in l.Syllables)
                {
                    words++;
                    if (s.EndMs < s.StartMs) untimed++;
                    if (s.StartMs < prev) backwards++;
                    prev = s.StartMs;
                }
            }
            bool ok = doc.Sync == Lyrics.SyncKind.Syllable && wordByWordLines == doc.Lines.Count && untimed == 0 && backwards == 0 && lineBackwards == 0;
            if (!ok) bad++;
            Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {Path.GetFileName(Path.GetDirectoryName(path))}: sync={doc.Sync} lines={doc.Lines.Count} " +
                              $"wordByWordLines={wordByWordLines} words={words} untimed={untimed} backwardsWords={backwards} backwardsLines={lineBackwards}");
        }
        return bad == 0 ? 0 : 1;
    }

    static string Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : "";
    }
}
