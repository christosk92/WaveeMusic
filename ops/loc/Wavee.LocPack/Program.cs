using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Wavee.LocPack;

static class Program
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    static int Main(string[] args)
    {
        if (args.Length == 0) { Usage(); return 1; }
        try
        {
            return args[0] switch
            {
                "pack" => Pack(args.AsSpan(1)),
                "qa" => Qa(args.AsSpan(1)),
                "import" => Import(args.AsSpan(1)),
                "draft" => Draft(args.AsSpan(1)),
                "xlsx" => Xlsx(args.AsSpan(1)),
                "lint" => Lint(args.AsSpan(1)),
                _ => Fail($"unknown command '{args[0]}'"),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    static void Usage()
    {
        Console.WriteLine("""
            Wavee.LocPack — pack / draft / qa / import loc packets
              pack   --culture nl|ko-KR --packet id [--prefix a.b]...
              qa     <packet.json>
              import --culture nl|ko-KR <packet.json>
              draft  <packet.json> [--from sidecar.json]
              xlsx   <packet.json>   (writes UTF-8 CSV next to it; Sheets can import)
              lint   [--loc path]  (en-US casing gate; default src/apps/Wavee/assets/loc/en-US.json)
            """);
    }

    static readonly string[] DefaultAcronyms =
    [
        "BPM", "ISRC", "SHA-256", "CD", "LCD", "VU", "PPM", "EP", "OK", "12\u2033 LP", "OR", "U2", "URI", "URL",
        "API", "EQ", "USB", "GPU", "CPU", "RAM", "JSON", "XML", "HTTP", "HTTPS", "ID", "UI", "OS", "GB", "MB", "KB",
        "MS", "NP", "FAQ",
    ];

    static readonly string[] DefaultRailPrefixes =
    [
        "library.rail.",
        "library.scope.",
        "library.readerSort.",
        "podcast.badge.",
        "podcast.cadence.",
        "podcast.tabs.",
        "whatsNew.issue.",
    ];

    static readonly string[] DefaultLowercasePrefixes =
    [
        "library.rail.",
        "library.scope.",
        "library.readerSort.",
        "podcast.badge.",
        "podcast.cadence.",
        "podcast.tabs.",
        "whatsNew.issue.",
        "detail.preReleaseUnit",
        "podcast.",
        "home.",
        "lyrics.debug.",
        "diagnostics.runtime.",
        "diagnostics.inspector.",
        "settings.storage.",
    ];

    static readonly string[] DefaultProperNouns =
    [
        "Liked Songs", "Your Library", "Your Episodes", "Spotify Connect", "Spotify Free", "Spotify Premium",
        "Spotify Classic", "Microsoft Store", "iPod Classic", "English (United States)",
        "Discover Weekly & Release Radar", "Singles & EPs", "Filter Your Library", "Collapse Your Library",
        "Expand Your Library", "Copy Spotify URI", "Open GitHub", "Reset Wavee", "Open Wavee", "Hide Wavee",
        "Quit Wavee", "Get Wavee Beta", "In Wavee", "By Spotify", "Side A", "Side B", "Type I", "CD / MiniDisc",
        "Feed URL", "Episode URI", "Show URI", "BPM · Key", "Updating Wavee",
        "Wavee {version} “{name}” · Beta {beta}",
    ];

    static int Lint(ReadOnlySpan<string> args)
    {
        string? locPath = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--loc") locPath = args[++i];
            else return Fail($"unknown lint arg {args[i]}");
        }

        string root = RepoRoot();
        locPath ??= Path.Combine(LocDir(root), "en-US.json");
        var flat = LoadFlat(locPath);

        var violations = LocCasing.Violations(flat, DefaultAcronyms, DefaultLowercasePrefixes, DefaultProperNouns);
        foreach (var (key, _, voice, why) in violations)
        {
            string value = flat[key];
            Console.Error.WriteLine($"VIOLATION {key}\t{voice}\t{why}\t{value}");
        }

        var contradictions = LocCasing.Contradictions(flat, DefaultRailPrefixes);
        foreach (var (a, b) in contradictions)
            Console.Error.WriteLine($"CONTRADICTION {a}\t{b}");

        var clusters = LocCasing.SharedValueClusters(flat);
        int sharedPrinted = 0;
        foreach (var (value, keys) in clusters)
        {
            if (keys.Count < 8) continue;
            sharedPrinted++;
            Console.WriteLine($"SHARED {keys.Count}\t{value}\t{string.Join(",", keys)}");
        }

        Console.WriteLine($"lint violations={violations.Count} contradictions={contradictions.Count} clusters={sharedPrinted}");
        return violations.Count == 0 ? 0 : 1;
    }

    static int Fail(string m) { Console.Error.WriteLine(m); return 1; }

    static string RepoRoot()
    {
        string? dir = Directory.GetCurrentDirectory();
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Wavee.slnx"))) return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException("Run from inside the Wavee repo (no Wavee.slnx found).");
    }

    static string LocDir(string root) => Path.Combine(root, "src", "apps", "Wavee", "assets", "loc");

    static Dictionary<string, string> LoadFlat(string path)
        => LocCatalog.FlattenJson(File.ReadAllText(path));

    static int Pack(ReadOnlySpan<string> args)
    {
        string? culture = null, packetId = null;
        var prefixes = new List<string>();
        bool includeDiagnostics = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--culture": culture = args[++i]; break;
                case "--packet": packetId = args[++i]; break;
                case "--prefix": prefixes.Add(args[++i]); break;
                case "--include-diagnostics": includeDiagnostics = true; break;
                default: return Fail($"unknown pack arg {args[i]}");
            }
        }
        if (culture is null || packetId is null) return Fail("pack requires --culture and --packet");

        string root = RepoRoot();
        var en = LoadFlat(Path.Combine(LocDir(root), "en-US.json"));
        string satPath = Path.Combine(LocDir(root), culture == "nl" ? "nl.json" : culture + ".json");
        var sat = File.Exists(satPath) ? LoadFlat(satPath) : new Dictionary<string, string>(StringComparer.Ordinal);

        var surfaces = LocCatalog.LoadSurfaces(File.ReadAllText(Path.Combine(root, "ops", "loc", "surfaces.json")));
        var keep = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(root, "ops", "loc", "keep-english.json")))
                   ?? [];
        var keepSet = new HashSet<string>(keep, StringComparer.Ordinal);
        var glossary = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(root, "ops", "loc", "glossary.json")));

        bool Include(string key)
        {
            if (!includeDiagnostics && key.StartsWith("diagnostics.", StringComparison.Ordinal)) return false;
            if (prefixes.Count == 0) return true;
            foreach (string p in prefixes)
            {
                if (key.Equals(p, StringComparison.Ordinal) || key.StartsWith(p + ".", StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        Packet packet = LocCatalog.MissingOf(culture, en, sat, packetId, Include);
        string[] glossTerms = glossary.TryGetProperty("terms", out var t) && t.ValueKind == JsonValueKind.Array
            ? t.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToArray()
            : [];
        packet.Glossary = glossTerms;

        foreach (PacketRow row in packet.Rows)
        {
            string sid = LocCatalog.SurfaceOf(row.Key, surfaces);
            if (surfaces.TryGetValue(sid, out Surface? s))
            {
                row.Where = s.Where;
                if (packet.Screenshot.Length == 0) packet.Screenshot = s.Screenshot;
                if (packet.Surface == packetId) packet.Surface = s.Id;
            }
            else
                row.Where = $"Wavee UI (key {row.Key}).";
            row.KeepEnglish = keepSet.Contains(row.Key) || keepSet.Contains(row.English);
            if (row.KeepEnglish) row.Status = "skip";
        }

        string outDir = Path.Combine(root, "ops", "loc", "work", culture);
        Directory.CreateDirectory(outDir);
        string outPath = Path.Combine(outDir, packetId + ".json");
        File.WriteAllText(outPath, JsonSerializer.Serialize(packet, JsonOpts));
        Console.WriteLine($"wrote {outPath} ({packet.Rows.Count} rows)");
        return 0;
    }

    static Packet ReadPacket(string path)
        => JsonSerializer.Deserialize<Packet>(File.ReadAllText(path), JsonOpts)
           ?? throw new InvalidOperationException("packet JSON was empty");

    static int Qa(ReadOnlySpan<string> args)
    {
        if (args.Length < 1) return Fail("qa requires a packet path");
        Packet packet = ReadPacket(args[0]);
        int bad = 0;
        foreach (PacketRow row in packet.Rows)
        {
            if (!string.Equals(row.Status, "approved", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(row.Yours)) { Console.WriteLine($"empty yours: {row.Key}"); bad++; continue; }
            if (!LocCatalog.PlaceholdersMatch(row.English, row.Yours) || !LocCatalog.BracesBalanced(row.Yours))
            {
                Console.WriteLine($"QA fail: {row.Key}");
                bad++;
            }
        }
        Console.WriteLine(bad == 0 ? "qa ok" : $"qa {bad} failure(s)");
        return bad == 0 ? 0 : 1;
    }

    static int Import(ReadOnlySpan<string> args)
    {
        string? culture = null, packetPath = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--culture") culture = args[++i];
            else packetPath = args[i];
        }
        if (culture is null || packetPath is null) return Fail("import requires --culture and a packet path");
        Packet packet = ReadPacket(packetPath);
        string root = RepoRoot();
        string satPath = Path.Combine(LocDir(root), culture == "nl" ? "nl.json" : culture + ".json");
        var sat = File.Exists(satPath) ? LoadFlat(satPath) : new Dictionary<string, string>(StringComparer.Ordinal);
        var merged = LocCatalog.ImportApproved(sat, packet);
        JsonObject nested = LocCatalog.Nest(merged, culture);
        File.WriteAllText(satPath, nested.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"imported into {satPath}");
        return 0;
    }

    static int Draft(ReadOnlySpan<string> args)
    {
        if (args.Length < 1) return Fail("draft requires a packet path");
        string packetPath = args[0];
        string? sidecar = null;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--from") sidecar = args[++i];
        }
        Packet packet = ReadPacket(packetPath);
        Dictionary<string, string> drafts = new(StringComparer.Ordinal);
        if (sidecar is not null)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(sidecar));
            foreach (var p in doc.RootElement.EnumerateObject())
                drafts[p.Name] = p.Value.GetString() ?? "";
        }
        foreach (PacketRow row in packet.Rows)
        {
            if (row.Yours.Length > 0) continue;
            if (drafts.TryGetValue(row.Key, out string? d) && d.Length > 0)
            {
                row.AiDraft = d;
                if (row.Status == "todo") row.Status = "draft";
            }
        }
        File.WriteAllText(packetPath, JsonSerializer.Serialize(packet, JsonOpts));
        Console.WriteLine($"drafted {packetPath}");
        return 0;
    }

    static int Xlsx(ReadOnlySpan<string> args)
    {
        if (args.Length < 1) return Fail("xlsx requires a packet path");
        Packet packet = ReadPacket(args[0]);
        string csvPath = Path.ChangeExtension(args[0], ".csv");
        var sb = new StringBuilder();
        sb.AppendLine("key,english,current,ai_draft,yours,status,where,icu,keep_english");
        foreach (PacketRow row in packet.Rows)
        {
            sb.Append(Csv(row.Key)).Append(',')
              .Append(Csv(row.English)).Append(',')
              .Append(Csv(row.Current)).Append(',')
              .Append(Csv(row.AiDraft)).Append(',')
              .Append(Csv(row.Yours)).Append(',')
              .Append(Csv(row.Status)).Append(',')
              .Append(Csv(row.Where)).Append(',')
              .Append(Csv(row.Icu)).Append(',')
              .Append(row.KeepEnglish ? "true" : "false")
              .AppendLine();
        }
        File.WriteAllText(csvPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Console.WriteLine($"wrote {csvPath}");
        return 0;
    }

    static string Csv(string s)
    {
        if (s.IndexOfAny([',', '"', '\n', '\r']) < 0) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
