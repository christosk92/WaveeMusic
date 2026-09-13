// ── Wavee.Tests/PlatformTests.cs — the settings store, ZoomAutoPolicy, the credential slot, the log line ──────────
//
// Wave 0's gate for the orchestrator-owned first cut of Platform/Platform.cs + Platform.Host.cs (plan §5, the Wave 1
// note). Four things are pinned here and nothing else is:
//   · ZoomAutoPolicy — ported from _old/Wavee.Tests/ZoomAutoPolicyTests.cs (G6: a chapter's pure rules port WITH their
//     existing tests). The one item that could not port verbatim is called out at its fact.
//   · The settings store — the typed key round trip, the "no store answers defaults" contract (ch 30 §7.1) and the
//     epoch that `Platform/Prefs.cs` will be built on.
//   · The credential slot — a round trip over a REAL file in a temp directory with the protector swapped for a no-op,
//     plus the two rules that matter more than the round trip: a blob from another scheme is refused, and NOTHING
//     ever deletes the stored credential except an explicit Clear.
//   · The log's pure line format, which is what the diagnostics page and every re-read of wavee-yyyyMMdd.log parse.
//
// These tests never start the engine loop, never open a window and never touch the real profile: every settings read
// goes through an in-memory store and every credential read through a temp file. `Platform`'s statics ARE process
// state, so the collection below disables parallelization and every fact that installs a store puts the old one back.

using Xunit;

namespace Wavee.Tests;

[CollectionDefinition(PlatformCollection.Name, DisableParallelization = true)]
public sealed class PlatformCollection
{
    public const string Name = "platform";
}

/// <summary>An <see cref="IAppSettings"/> in a dictionary. <see cref="WasWritten"/> is the probe the real interface
/// deliberately does not have — it is how a test asserts that a code path did NOT write a key, which is the whole
/// question the bootstrap-version guards exist to answer.</summary>
sealed class MemoryAppSettings : IAppSettings
{
    readonly Dictionary<string, object> _values = new();

    public T Get<T>(SettingKey<T> key) =>
        _values.TryGetValue(key.Name, out var value) && value is T typed ? typed : key.Default;

    public void Set<T>(SettingKey<T> key, T value) { if (value is not null) _values[key.Name] = value; }

    public bool WasWritten<T>(SettingKey<T> key) => _values.ContainsKey(key.Name);

    public int WrittenCount => _values.Count;
}

// ── the zoom policy ──────────────────────────────────────────────────────────────────────────────────────────────────

[Collection(PlatformCollection.Name)]
public class ZoomPolicyTests
{
    // ── the large-display-scaling §3.2 worked table, row for row ────────────────────────────────────────────────────
    [Theory]
    [InlineData(1664f, 1109f, 1f)]      // laptop maximized, 150% OS DPI — unchanged, correct today
    [InlineData(3440f, 1392f, 1.5f)]    // ultrawide maximized, 100% OS DPI — every structural promise kept
    [InlineData(1920f, 1080f, 1f)]      // 1.20 is the coin-flip; snap-down keeps it at 100%
    [InlineData(2560f, 1440f, 1.5f)]    // 1.60 → 150%
    [InlineData(3840f, 2160f, 2f)]      // 2.40 clamps to the 200% ceiling
    [InlineData(1366f, 768f, 1f)]       // 0.85 never shrinks below 100% in Auto
    [InlineData(3440f, 700f, 1f)]       // the wide sliver — the height guard: width alone would want 200%+
    public void Suggest_Auto_MatchesTheWorkedTable(float baseW, float baseH, float expected)
        => Assert.Equal(expected, ZoomAutoPolicy.Suggest(baseW, baseH, ZoomAutoMode.Auto));

    /// <summary>3840×2160@150% and 2560×1440@100% present the IDENTICAL base box (2560×1440) — the property that makes
    /// this policy a proportion fix, not a DPI hack. Suggest never sees the physical/DPI numbers, only the base dips,
    /// so this holds by construction; the test exists to catch a future refactor that starts taking DPI as an input.</summary>
    [Fact]
    public void Suggest_IsDpiIndependent_ForTheIdenticalBaseBox()
    {
        float a = ZoomAutoPolicy.Suggest(3840f / 1.5f, 2160f / 1.5f, ZoomAutoMode.Auto);
        float b = ZoomAutoPolicy.Suggest(2560f, 1440f, ZoomAutoMode.Auto);
        Assert.Equal(b, a);
        Assert.Equal(1.5f, a);
    }

    [Theory]
    [InlineData(1366f, 768f, ZoomAutoMode.Auto, 1f)]         // never shrinks below 100% unless asked
    [InlineData(1366f, 768f, ZoomAutoMode.Dense, 0.75f)]     // Dense may go below 100%
    public void Suggest_DenseFloor_OnlyAppliesInDenseMode(float baseW, float baseH, ZoomAutoMode mode, float expected)
        => Assert.Equal(expected, ZoomAutoPolicy.Suggest(baseW, baseH, mode));

    [Theory]
    [InlineData(0f, 900f)]
    [InlineData(1600f, 0f)]
    [InlineData(float.NaN, 900f)]
    [InlineData(1600f, float.NaN)]
    [InlineData(-100f, 900f)]
    public void Suggest_DegenerateInput_FallsBackToNeutralZoom(float baseW, float baseH)
        => Assert.Equal(1f, ZoomAutoPolicy.Suggest(baseW, baseH, ZoomAutoMode.Auto));

    [Fact]
    public void Suggest_NeverExceedsTheCeiling()
    {
        Assert.Equal(2f, ZoomAutoPolicy.Suggest(100_000f, 100_000f, ZoomAutoMode.Auto));
        Assert.Equal(2f, ZoomAutoPolicy.Suggest(100_000f, 100_000f, ZoomAutoMode.Dense));
    }

    [Theory]
    [InlineData(1000f, 1000f, 1200f, 1000f)]   // wider only
    [InlineData(1000f, 1000f, 1000f, 1200f)]   // taller only
    [InlineData(1000f, 1000f, 1200f, 1200f)]   // both
    public void Suggest_IsMonotonic_ALargerBaseBoxNeverSuggestsASmallerZoom(float w0, float h0, float w1, float h1)
    {
        foreach (var mode in new[] { ZoomAutoMode.Auto, ZoomAutoMode.Dense })
        {
            float z0 = ZoomAutoPolicy.Suggest(w0, h0, mode);
            float z1 = ZoomAutoPolicy.Suggest(w1, h1, mode);
            Assert.True(z1 >= z0, $"{mode}: ({w1}x{h1}) suggested {z1} < ({w0}x{h0})'s {z0}");
        }
    }

    /// <summary>Feed the suggested zoom's own resulting viewport back through the SAME base-extent recovery the shell
    /// uses (baseDip = viewportDip * zoom) and confirm it reproduces the original base box, and therefore the same
    /// suggestion — the no-op guard the debounced resize effect relies on to not thrash on its own SetZoom.</summary>
    [Theory]
    [InlineData(1664f, 1109f)]
    [InlineData(3440f, 1392f)]
    [InlineData(2560f, 1440f)]
    [InlineData(1366f, 768f)]
    public void Suggest_IsIdempotent_UnderItsOwnBaseExtentRoundTrip(float baseW, float baseH)
    {
        float z1 = ZoomAutoPolicy.Suggest(baseW, baseH, ZoomAutoMode.Auto);
        float viewportW = baseW / z1, viewportH = baseH / z1;
        float roundTrippedW = viewportW * z1, roundTrippedH = viewportH * z1;
        Assert.Equal(baseW, roundTrippedW, 3);
        Assert.Equal(baseH, roundTrippedH, 3);
        Assert.Equal(z1, ZoomAutoPolicy.Suggest(roundTrippedW, roundTrippedH, ZoomAutoMode.Auto));
    }

    static readonly float[] PlateauSet = [0.75f, 1f, 1.25f, 1.5f, 1.75f, 2f];

    [Theory]
    [InlineData(1664f, 1109f)]
    [InlineData(3440f, 1392f)]
    [InlineData(1920f, 1080f)]
    [InlineData(2560f, 1440f)]
    [InlineData(3840f, 2160f)]
    [InlineData(1366f, 768f)]
    [InlineData(3440f, 700f)]
    public void Suggest_EveryFixture_ReturnsAPlateauMember(float baseW, float baseH)
    {
        foreach (var mode in new[] { ZoomAutoMode.Auto, ZoomAutoMode.Dense })
            Assert.Contains(ZoomAutoPolicy.Suggest(baseW, baseH, mode), PlateauSet);
    }

    /// <summary>The design box is NOT a fresh number: DesignW is the page-measure cap and DesignH the tall-hero gate.
    /// 0.2.9's version of this fact asserted <c>WaveeSize.PageMaxW == ZoomAutoPolicy.DesignW</c>; `Platform/Design.cs`
    /// is Wave 4 and has no token yet, so the HALF that can be pinned today is pinned here and **Wave 4 owes the other
    /// half** — a convergence fact against the real token, which is the only thing that stops the literal drifting.</summary>
    [Fact]
    public void DesignBox_IsTheAppsOwnTwoNumbers()
    {
        Assert.Equal(1600f, ZoomAutoPolicy.DesignW);   // Wave 4: == Design's PageMaxW
        Assert.Equal(900f, ZoomAutoPolicy.DesignH);    // the detail frame's tall-hero gate (winH >= 900f)
    }

    /// <summary>Every plateau this policy can return must also be a real engine ZoomLadder rung — an AUTO suggestion
    /// has to land on a value Ctrl+± can reach too, or a later manual nudge steps off-ladder.</summary>
    [Fact]
    public void EveryPlateau_IsAMemberOfTheEngineZoomLadder()
    {
        foreach (float p in PlateauSet)
            Assert.Contains(p, FluentGpu.Foundation.ZoomLadder.Steps);
    }

    // ── the one-shot settings migration (ch 00 §9.5) ────────────────────────────────────────────────────────────────

    [Fact]
    public void MigrateMode_FreshInstall_LeavesModeAtItsOwnAutoDefault()
    {
        var settings = new MemoryAppSettings();
        ZoomAutoPolicy.MigrateMode(settings);

        Assert.False(settings.WasWritten(Platform.Keys.ZoomMode));   // nothing to override — the default IS Auto
        Assert.Equal((int)ZoomAutoMode.Auto, settings.Get(Platform.Keys.ZoomMode));
        Assert.Equal(ZoomAutoPolicy.MigrationTargetVersion, settings.Get(Platform.Keys.ZoomModeBootstrapVersion));
    }

    [Fact]
    public void MigrateMode_UpgradeWithACustomZoom_PinsManual_SoAutoNeverOverridesIt()
    {
        var settings = new MemoryAppSettings();
        settings.Set(Platform.Keys.ZoomLevel, 1.25f);   // the user already picked 125% on an older build

        ZoomAutoPolicy.MigrateMode(settings);

        Assert.Equal((int)ZoomAutoMode.Manual, settings.Get(Platform.Keys.ZoomMode));
    }

    [Fact]
    public void MigrateMode_UpgradeStillAtTheNeutralZoom_StaysAuto()
    {
        var settings = new MemoryAppSettings();
        settings.Set(Platform.Keys.ZoomLevel, 1f);   // never touched the picker (or reset it back to 100%)

        ZoomAutoPolicy.MigrateMode(settings);

        Assert.Equal((int)ZoomAutoMode.Auto, settings.Get(Platform.Keys.ZoomMode));
    }

    [Fact]
    public void MigrateMode_RunsExactlyOnce()
    {
        var settings = new MemoryAppSettings();
        settings.Set(Platform.Keys.ZoomLevel, 1.25f);
        ZoomAutoPolicy.MigrateMode(settings);

        settings.Set(Platform.Keys.ZoomMode, (int)ZoomAutoMode.Auto);   // the user picks Auto afterwards
        ZoomAutoPolicy.MigrateMode(settings);                           // a second launch must not re-pin Manual

        Assert.Equal((int)ZoomAutoMode.Auto, settings.Get(Platform.Keys.ZoomMode));
    }
}

// ── the settings store ───────────────────────────────────────────────────────────────────────────────────────────────

[Collection(PlatformCollection.Name)]
public class PlatformSettingsTests
{
    /// <summary>Install a store, run the body, and always put the process back the way it was — `Platform`'s store is
    /// process state and the next fact (or the next test class) must not inherit this one's.</summary>
    static void WithStore(IAppSettings store, Action body)
    {
        Platform.UseSettings(store);
        try { body(); }
        finally { Platform.UseSettings(null); }
    }

    [Fact]
    public void Settings_RoundTripEveryScalarKind_ThroughTheOneDoor()
    {
        var store = new MemoryAppSettings();
        WithStore(store, static () =>
        {
            Platform.Settings.Set(Platform.Keys.ThemeMode, 2);                  // int
            Platform.Settings.Set(Platform.Keys.MarqueeEnabled, false);         // bool
            Platform.Settings.Set(Platform.Keys.ZoomLevel, 1.25f);              // float
            Platform.Settings.Set(Platform.Keys.UiCulture, "nl-NL");            // string
            Platform.Settings.Set(Platform.Keys.UpdateLastCheckedMs, 1234L);    // long
            Platform.Settings.Set(Platform.Keys.VideoCustomAspectRatio, 2.35);  // double

            Assert.Equal(2, Platform.Settings.Get(Platform.Keys.ThemeMode));
            Assert.False(Platform.Settings.Get(Platform.Keys.MarqueeEnabled));
            Assert.Equal(1.25f, Platform.Settings.Get(Platform.Keys.ZoomLevel));
            Assert.Equal("nl-NL", Platform.Settings.Get(Platform.Keys.UiCulture));
            Assert.Equal(1234L, Platform.Settings.Get(Platform.Keys.UpdateLastCheckedMs));
            Assert.Equal(2.35, Platform.Settings.Get(Platform.Keys.VideoCustomAspectRatio));
        });
    }

    /// <summary>Ch 30 §7.1 / §9.3(8): an absent store answers every read with the key's default and swallows every
    /// write. The first page that dereferences a store unconditionally is the one that makes every later test harness
    /// start a shell, so this is a contract and not an accident.</summary>
    [Fact]
    public void Settings_WithNoBackingStore_AnswerDefaultsAndDropWrites()
    {
        Platform.UseSettings(null);
        Assert.Equal(Platform.Keys.ThemeMode.Default, Platform.Settings.Get(Platform.Keys.ThemeMode));
        Platform.Settings.Set(Platform.Keys.ThemeMode, 2);                       // must not throw
        Assert.Equal(Platform.Keys.ThemeMode.Default, Platform.Settings.Get(Platform.Keys.ThemeMode));
    }

    /// <summary>A key the store has never seen reads as its own default, not as zero — the reason every key carries
    /// one and the reason a bootstrap-version guard is the only way to tell "absent" from "written as the default".</summary>
    [Fact]
    public void Settings_AbsentKey_ReadsItsDeclaredDefault()
        => WithStore(new MemoryAppSettings(), static () =>
        {
            Assert.Equal(1, Platform.Settings.Get(Platform.Keys.RowDensity));                 // Default, not Compact
            Assert.True(Platform.Settings.Get(Platform.Keys.ColorWashesEnabled));
            Assert.Equal("wavee.curated.default", Platform.Settings.Get(Platform.Keys.CuratedTemplateId));
        });

    /// <summary>Every write bumps the epoch `Platform/Prefs.cs` (owner L, Wave 4) hangs its four preference epochs
    /// off, and publishes the same number on the signal a binder subscribes to.</summary>
    [Fact]
    public void Settings_EveryWrite_BumpsTheEpochAndPublishesIt()
        => WithStore(new MemoryAppSettings(), static () =>
        {
            uint before = Platform.SettingsEpoch;
            Platform.Settings.Set(Platform.Keys.TempoColumn, true);
            Platform.Settings.Set(Platform.Keys.PlaysColumn, true);
            Assert.Equal(before + 2u, Platform.SettingsEpoch);
            Assert.Equal(Platform.SettingsEpoch, Platform.SettingsChanged.Peek());
        });

    /// <summary>The per-subject seam (ch 30 §9.3(7)): one key per context uri / library kind / sidebar design, built
    /// at the call site. The SHAPE of the name is persisted, so this pins the strings, not just the mechanism.</summary>
    [Fact]
    public void PerSubjectKeys_SpellTheirPersistedNames()
    {
        Assert.Equal("detail.sort.col:spotify:album:x", Platform.Keys.DetailSortCol("spotify:album:x").Name);
        Assert.Equal("detail.sort.desc:spotify:album:x", Platform.Keys.DetailSortDesc("spotify:album:x").Name);
        Assert.Equal("library.artists.leftw", Platform.Keys.LibraryLeftW("artists").Name);
        Assert.Equal(280f, Platform.Keys.LibraryLeftW("artists").Default);      // artists is the one narrow default
        Assert.Equal(340f, Platform.Keys.LibraryLeftW("albums").Default);
        Assert.Equal("sidebar.library-v3.width", Platform.Keys.SidebarWidth("library-v3", 300f).Name);
        Assert.Equal(300f, Platform.Keys.SidebarWidth("library-v3", 300f).Default);
        Assert.Equal("npv.player.ipod.wheel", Platform.Keys.NpvOption("ipod", "wheel").Name);
    }

    /// <summary>Two keys may never share a storage name: the registry is one flat namespace and a collision is one
    /// preference silently overwriting another.</summary>
    [Fact]
    public void Keys_HaveNoDuplicateStorageNames()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in typeof(Platform.Keys).GetFields())
        {
            var value = f.GetValue(null);
            if (value is null) continue;
            var nameProp = value.GetType().GetProperty("Name");
            if (nameProp?.GetValue(value) is string name)
                Assert.True(seen.Add(name), "duplicate setting key name: " + name);
        }
        Assert.True(seen.Count > 100, "the key registry lost rows: " + seen.Count);
    }

    /// <summary>"system" (the default) resolves to the OS UI culture; an explicit tag is taken verbatim; the Spotify
    /// language is the two-letter fold, and anything that is not a clean two-letter language is "en".</summary>
    [Fact]
    public void Locale_ResolvesTheStoredPick_AndFoldsTheSpotifyLanguage()
    {
        var store = new MemoryAppSettings();
        store.Set(Platform.Keys.UiCulture, "nl-NL");
        var locale = AppLocale.Resolve(store);
        Assert.Equal("nl-NL", locale.UiCulture);
        Assert.Equal("nl", locale.SpotifyLanguage);

        Assert.Equal("en", AppLocale.LanguageOf(""));
        Assert.Equal("en", AppLocale.LanguageOf("x"));
        Assert.Equal("pt", AppLocale.LanguageOf("pt_BR"));
        Assert.Equal("de", AppLocale.LanguageOf("DE"));
    }
}

// ── the credential slot ──────────────────────────────────────────────────────────────────────────────────────────────

[Collection(PlatformCollection.Name)]
public class CredentialSlotTests : IDisposable
{
    // A REAL file, in a temp directory, with the protector swapped for a no-op: the round trip is the production one
    // (FileLocalStore's write-then-rename JSON), and nothing anywhere near %LOCALAPPDATA%\Wavee is opened.
    readonly string _dir = Path.Combine(Path.GetTempPath(), "wavee-platform-tests", Guid.NewGuid().ToString("N"));
    readonly ICredentialProtector _protector = new NoOpProtector();

    string StorePath => Path.Combine(_dir, "store.json");
    FileLocalStore NewStore() => new(StorePath);

    static readonly Credential Sample = new(CredentialKind.ReusableBlob, "someone", "c2VjcmV0", null);

    public CredentialSlotTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        // The ambient slot is process state. Hand it back an EMPTY store over a path inside this class's own temp
        // directory so no later fact can inherit this one's credential — and so nothing ever points at the real
        // profile, which is the whole reason these tests use a temp file in the first place.
        Platform.UseCredentialSlot(new FileLocalStore(Path.Combine(_dir, "reset.json")), new NoOpProtector());
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Credential_SurvivesARestart_ThroughTheFile()
    {
        Platform.SaveCredential(NewStore(), _protector, in Sample);

        Assert.True(Platform.TryLoadCredential(NewStore(), _protector, out var loaded));   // a FRESH store: a relaunch
        Assert.Equal(Sample, loaded);
    }

    [Fact]
    public void Credential_CarriesItsRefreshToken_WhenItHasOne()
    {
        var withRefresh = new Credential(CredentialKind.OAuthToken, "someone", "access", "refresh");
        Platform.SaveCredential(NewStore(), _protector, in withRefresh);

        Assert.True(Platform.TryLoadCredential(NewStore(), _protector, out var loaded));
        Assert.Equal(withRefresh, loaded);
    }

    [Fact]
    public void Credential_Absent_IsNotAFailure()
    {
        Assert.False(Platform.TryLoadCredential(NewStore(), _protector, out var loaded));
        Assert.True(loaded.IsEmpty);
    }

    /// <summary>The blob is scheme-tagged, so a credential protected by another protector (another machine, another
    /// platform, a profile copied across) is cleanly REFUSED rather than mis-decrypted — a re-auth, not a crash.</summary>
    [Fact]
    public void Credential_FromAnotherScheme_IsRefused()
    {
        Platform.SaveCredential(NewStore(), new XorProtector(), in Sample);

        Assert.False(Platform.TryLoadCredential(NewStore(), _protector, out _));
        Assert.True(Platform.TryLoadCredential(NewStore(), new XorProtector(), out var mine));
        Assert.Equal(Sample, mine);
    }

    /// <summary>THE rule that matters more than the round trip: a read that fails must never delete what it could not
    /// read. A transient protector failure (a roamed profile, a locked keystore) must not become a permanent logout —
    /// only an explicit Clear removes the blob.</summary>
    [Fact]
    public void Credential_ARefusedRead_LeavesTheBlobOnDisk()
    {
        Platform.SaveCredential(NewStore(), new XorProtector(), in Sample);

        Assert.False(Platform.TryLoadCredential(NewStore(), _protector, out _));            // refused…
        Assert.NotNull(NewStore().Get(Platform.CredentialKey));                             // …and still there

        Platform.ClearCredential(NewStore());                                               // only this removes it
        Assert.Null(NewStore().Get(Platform.CredentialKey));
    }

    /// <summary>A corrupt blob (hand-edited, truncated, a half-written file recovered by hand) is "no credential",
    /// never an exception out of boot — and, again, never a deletion.</summary>
    [Fact]
    public void Credential_CorruptBlob_ReadsAsAbsent_AndIsNotDeleted()
    {
        var store = NewStore();
        store.Set(Platform.CredentialKey, _protector.Scheme + ":not-base64!!");

        Assert.False(Platform.TryLoadCredential(store, _protector, out _));
        Assert.NotNull(NewStore().Get(Platform.CredentialKey));
    }

    /// <summary>The device id is created once and then never rotates: a fresh id per launch is a fresh phantom device
    /// in every other Spotify client's picker.</summary>
    [Fact]
    public void DeviceId_IsCreatedOnce_AndSurvivesARestart()
    {
        Platform.UseCredentialSlot(NewStore(), _protector);
        string first = Platform.DeviceId;
        Assert.False(string.IsNullOrEmpty(first));

        Platform.UseCredentialSlot(NewStore(), _protector);   // a relaunch over the same file
        Assert.Equal(first, Platform.DeviceId);               // Dispose puts the ambient slot back
    }

    /// <summary>A credential must never print its secret: it reaches a log the moment somebody interpolates it.</summary>
    [Fact]
    public void Credential_ToString_NeverRevealsTheSecret()
    {
        string text = Sample.ToString();
        Assert.DoesNotContain(Sample.Secret, text, StringComparison.Ordinal);
        Assert.DoesNotContain("someone", text, StringComparison.Ordinal);
        Assert.Contains("***", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "(none)")]
    [InlineData("", "(none)")]
    [InlineData("abc", "***")]
    [InlineData("abcdef", "***")]
    [InlineData("christos", "chr***os")]
    public void Redact_KeepsAHint_AndHidesTheRest(string? input, string expected)
        => Assert.Equal(expected, Platform.Redact(input));

    /// <summary>A second protector, so the scheme-mismatch rule is tested against a real different scheme rather than
    /// against "no protector at all".</summary>
    sealed class XorProtector : ICredentialProtector
    {
        public string Scheme => "xor";
        public byte[] Protect(byte[] plaintext) => Flip(plaintext);
        public byte[] Unprotect(byte[] ciphertext) => Flip(ciphertext);

        static byte[] Flip(byte[] input)
        {
            var copy = new byte[input.Length];
            for (int i = 0; i < input.Length; i++) copy[i] = (byte)(input[i] ^ 0x5A);
            return copy;
        }
    }
}

// ── the log line ─────────────────────────────────────────────────────────────────────────────────────────────────────

[Collection(PlatformCollection.Name)]
public class LogFormatTests
{
    static WaveeLogEntry Entry(WaveeLogLevel level = WaveeLogLevel.Info, string category = "connect",
        string eventId = "", string message = "started", string? op = null, long elapsed = -1,
        WaveeLogField[]? fields = null, string? ex = null)
        => new(17, 1720512345678, level, category, eventId, message, op, 4, elapsed, fields, ex);

    [Fact]
    public void Format_PlainLine_IsLevelCategoryMessage()
        => Assert.Equal("I [connect] started", Entry().Format());

    /// <summary>An event line separates its header from its message with " - ". The separator is what a re-read
    /// splits on, so it is part of the format and not a cosmetic.</summary>
    [Fact]
    public void Format_EventLine_CarriesTheHeaderThenTheMessage()
        => Assert.Equal("I [connect] session.start op=abc elapsed=12ms - started",
            Entry(eventId: "session.start", op: "abc", elapsed: 12).Format());

    [Theory]
    [InlineData(WaveeLogLevel.Trace, 'T')]
    [InlineData(WaveeLogLevel.Debug, 'D')]
    [InlineData(WaveeLogLevel.Info, 'I')]
    [InlineData(WaveeLogLevel.Warning, 'W')]
    [InlineData(WaveeLogLevel.Error, 'E')]
    [InlineData(WaveeLogLevel.Critical, 'C')]
    public void Format_LevelIsOneLetter(WaveeLogLevel level, char letter)
        => Assert.StartsWith(letter + " [", Entry(level).Format(), StringComparison.Ordinal);

    /// <summary>Fields are k=v, and a value that would break the k=v grammar (whitespace, '=', '|', a quote, empty)
    /// is quoted and escaped — otherwise one field with a space in it silently eats the rest of the line.</summary>
    [Fact]
    public void Format_Fields_AreQuotedOnlyWhenTheyHaveTo()
    {
        string line = Entry(fields:
        [
            WaveeLogField.Of("plain", "value"),
            WaveeLogField.Of("spaced", "two words"),
            WaveeLogField.Of("empty", ""),
            WaveeLogField.Of("quoted", "a\"b"),
            WaveeLogField.Of("count", 3),
            WaveeLogField.Of("flag", true),
            WaveeLogField.Secret("token"),
        ]).Format();

        Assert.Equal("""I [connect] started plain=value spaced="two words" empty="" quoted="a\"b" count=3 flag=true token=***""", line);
    }

    [Fact]
    public void Format_Exception_IsAppendedBehindAPipe()
        => Assert.EndsWith(" | boom", Entry(ex: "boom").Format(), StringComparison.Ordinal);

    /// <summary>The FILE line is the entry's format behind the seq/tid/t/sid/pid prefix. Those five tokens are what
    /// let the diagnostics page rebuild timestamps, session boundaries and the owning process when it re-reads
    /// wavee-yyyyMMdd.log; a build that drops one makes every older session unsplittable.</summary>
    [Fact]
    public void FormatFileLine_CarriesTheFivePrefixTokens()
    {
        var e = Entry(eventId: "session.start");
        string line = Log.FormatFileLine(in e);

        Assert.StartsWith("seq=17 tid=4 t=1720512345678 sid=" + Log.SessionId + " pid=", line, StringComparison.Ordinal);
        Assert.EndsWith(" " + e.Format(), line, StringComparison.Ordinal);
    }

    /// <summary>The ring is the diagnostics page's whole input, and it is oldest-to-newest. It also has to work with
    /// no file sink configured at all — which is exactly the shape a unit test runs in.</summary>
    [Fact]
    public void Ring_KeepsWhatWasLogged_OldestFirst()
    {
        Log.ClearRing();
        Log.Info("app", "one");
        Log.Warn("app", "two");

        var snapshot = Log.Snapshot();
        Assert.Equal(2, snapshot.Length);
        Assert.Equal("one", snapshot[0].Message);
        Assert.Equal("two", snapshot[1].Message);
        Assert.Equal(WaveeLogLevel.Warning, snapshot[1].Level);
        Assert.True(snapshot[1].Sequence > snapshot[0].Sequence);
        Log.ClearRing();
    }

    /// <summary>MinLevel is the master gate: an entry below it is never built, so it reaches neither the ring nor the
    /// file however low FileMinLevel is set.</summary>
    [Fact]
    public void Ring_MinLevel_DropsBeforeTheEntryIsEverBuilt()
    {
        var min = Log.MinLevel;
        try
        {
            Log.MinLevel = WaveeLogLevel.Warning;
            Log.ClearRing();
            Log.Info("app", "dropped");
            Log.Error("app", "kept");

            var snapshot = Log.Snapshot();
            Assert.Single(snapshot);
            Assert.Equal("kept", snapshot[0].Message);
        }
        finally { Log.MinLevel = min; Log.ClearRing(); }
    }

    /// <summary>An empty category is "app", never blank: a blank one makes a line unfilterable on the page that
    /// exists to filter them.</summary>
    [Fact]
    public void Ring_BlankCategory_BecomesApp()
    {
        Log.ClearRing();
        Log.Info("  ", "x");
        Assert.Equal("app", Log.Snapshot()[0].Category);
        Log.ClearRing();
    }
}
