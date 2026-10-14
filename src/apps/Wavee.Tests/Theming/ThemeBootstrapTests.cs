// ── Wavee.Tests/Theming/ThemeBootstrapTests.cs — the migration mechanism of themes plan 7.1 ───────────────────────────
//
// No step is shipped in T1, so the mechanism is tested with FAKE steps that have the shape every real step will have:
// they read legacy keys, write theme option keys, copy only when the target is absent and the legacy key is present, and
// never delete. The permutation machinery is what the M-PRs reuse: `AssertOrderIndependent(steps, seed)` runs every merge
// order of a step set over the same legacy store and demands one end state. The shipped steps run through it too, so a
// step added later that violates disjointness fails here without a new test.

using Wavee.Theming;
using Xunit;

namespace Wavee.Tests.Theming;

/// <summary>An <see cref="IAppSettings"/> that can show everything it holds, so an end state can be compared.</summary>
sealed class SnapshotSettings : IAppSettings
{
    readonly SortedDictionary<string, object> _values = new(StringComparer.Ordinal);

    public T Get<T>(SettingKey<T> key) => _values.TryGetValue(key.Name, out var value) && value is T typed ? typed : key.Default;
    public void Set<T>(SettingKey<T> key, T value) { if (value is not null) _values[key.Name] = value; }

    public bool TryGet<T>(SettingKey<T> key, out T value)
    {
        if (!_values.ContainsKey(key.Name)) { value = key.Default; return false; }
        value = Get(key);
        return true;
    }

    public bool Has(string name) => _values.ContainsKey(name);
    public void Remove(string name) => _values.Remove(name);

    /// <summary>Every key and value, in key order, as text.</summary>
    public string Dump() => string.Join(";", _values.Select(kv => kv.Key + "=" + kv.Value));

    public SnapshotSettings Clone()
    {
        var copy = new SnapshotSettings();
        foreach (var kv in _values) copy._values[kv.Key] = kv.Value;
        return copy;
    }
}

public sealed class ThemeBootstrapTests
{
    // Legacy keys (what a retired switch stored) and the option keys that replace them.
    static readonly SettingKey<int> LegacyDensity = new("legacy.density", 0);
    static readonly SettingKey<bool> LegacyNarrow = new("legacy.narrow", false);
    static readonly SettingKey<float> LegacyScale = new("legacy.scale", 1f);
    static readonly SettingKey<string> LegacyFont = new("legacy.font", "");

    static readonly SettingKey<int> DensityKey = new("theme.classic.structure.density", 1);
    static readonly SettingKey<bool> Narrow = new("theme.classic.structure.narrow", false);
    static readonly SettingKey<float> Scale = new("theme.classic.style.scale", 1f);
    static readonly SettingKey<string> Font = new("theme.classic.style.font", "");

    /// <summary>DensityKey 0..2 are valid; anything else is not copied (the option default applies).</summary>
    static ThemeBootstrapStep DensityStep(Action<string>? onRun = null) => new(
        "Density", ["legacy.density"], ["theme.classic.structure.density"],
        s =>
        {
            onRun?.Invoke("Density");
            ThemeBootstrap.Copy(s, LegacyDensity, DensityKey, static v => (v is >= 0 and <= 2, v));
        });

    static ThemeBootstrapStep NarrowStep(Action<string>? onRun = null) => new(
        "Narrow", ["legacy.narrow"], ["theme.classic.structure.narrow"],
        s =>
        {
            onRun?.Invoke("Narrow");
            ThemeBootstrap.Copy(s, LegacyNarrow, Narrow);
        });

    static ThemeBootstrapStep ScaleStep() => new(
        "Scale", ["legacy.scale"], ["theme.classic.style.scale"],
        static s => ThemeBootstrap.Copy(s, LegacyScale, Scale, static v => (v >= 0.5f && v <= 2f, v)));

    static ThemeBootstrapStep FontStep() => new(
        "Font", ["legacy.font"], ["theme.classic.style.font"],
        static s => ThemeBootstrap.Copy(s, LegacyFont, Font, static v => (v.Length > 0, v.Trim())));

    static ThemeBootstrapStep[] AllSteps() => [DensityStep(), NarrowStep(), ScaleStep(), FontStep()];

    static SnapshotSettings LegacyStore(int density = 2, bool narrow = true, float scale = 1.5f, string font = " Segoe ")
    {
        var s = new SnapshotSettings();
        s.Set(LegacyDensity, density);
        s.Set(LegacyNarrow, narrow);
        s.Set(LegacyScale, scale);
        s.Set(LegacyFont, font);
        return s;
    }

    // ── one step ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Run_CopiesLegacyValuesToTheirOptionKeys_AndRecordsTheDoneMarker()
    {
        var s = LegacyStore();
        int ran = ThemeBootstrap.Run(s, AllSteps());

        Assert.Equal(4, ran);
        Assert.Equal(2, s.Get(DensityKey));
        Assert.True(s.Get(Narrow));
        Assert.Equal(1.5f, s.Get(Scale));
        Assert.Equal("Segoe", s.Get(Font));
        foreach (string name in new[] { "Density", "Narrow", "Scale", "Font" })
            Assert.True(s.Get(new SettingKey<bool>("theme.bootstrap.done." + name, false)));
    }

    [Fact]
    public void Run_NeverDeletesALegacyKey()
    {
        var s = LegacyStore();
        ThemeBootstrap.Run(s, AllSteps());
        Assert.Equal(2, s.Get(LegacyDensity));
        Assert.True(s.Get(LegacyNarrow));
        Assert.Equal(1.5f, s.Get(LegacyScale));
        Assert.Equal(" Segoe ", s.Get(LegacyFont));
    }

    [Fact]
    public void Run_SecondRun_IsANoOp()
    {
        var s = LegacyStore();
        int runs = 0;
        var steps = new[] { DensityStep(_ => runs++), NarrowStep(_ => runs++) };

        Assert.Equal(2, ThemeBootstrap.Run(s, steps));
        string afterFirst = s.Dump();
        Assert.Equal(0, ThemeBootstrap.Run(s, steps));
        Assert.Equal(2, runs);
        Assert.Equal(afterFirst, s.Dump());
    }

    [Fact]
    public void Run_KeepsAnOptionValueThatAlreadyExists()
    {
        var s = LegacyStore(density: 0);
        s.Set(DensityKey, 2);   // the user already chose, on a build that has the option
        ThemeBootstrap.Run(s, AllSteps());
        Assert.Equal(2, s.Get(DensityKey));
    }

    [Fact]
    public void Run_WithoutALegacyKey_WritesNoOptionKey_ButStillRecordsTheStep()
    {
        var s = new SnapshotSettings();
        ThemeBootstrap.Run(s, AllSteps());
        Assert.False(s.Has(DensityKey.Name));
        Assert.False(s.Has(Narrow.Name));
        Assert.False(s.Has(Scale.Name));
        Assert.False(s.Has(Font.Name));
        Assert.True(s.Get(new SettingKey<bool>("theme.bootstrap.done.Density", false)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(99)]
    public void Run_InvalidLegacyValue_IsNotCopied_SoTheDefaultApplies(int legacy)
    {
        var s = LegacyStore(density: legacy);
        ThemeBootstrap.Run(s, AllSteps());
        Assert.False(s.Has(DensityKey.Name));
        Assert.Equal(1, s.Get(DensityKey));   // the option default
        Assert.Equal(legacy, s.Get(LegacyDensity));
    }

    [Theory]
    [InlineData(0.1f)]
    [InlineData(5f)]
    [InlineData(float.NaN)]
    public void Run_OutOfRangeSlider_IsNotCopied(float legacy)
    {
        var s = LegacyStore(scale: legacy);
        ThemeBootstrap.Run(s, AllSteps());
        Assert.False(s.Has(Scale.Name));
    }

    [Fact]
    public void Run_AStepThatThrows_IsRetriedNextLaunch_AndDoesNotStopTheOthers()
    {
        var s = LegacyStore();
        bool fail = true;
        var flaky = new ThemeBootstrapStep("Flaky", ["legacy.x"], ["theme.classic.x"], _ => { if (fail) throw new InvalidOperationException("disk"); });
        var steps = new[] { flaky, NarrowStep() };

        Assert.Equal(1, ThemeBootstrap.Run(s, steps));
        Assert.False(s.Has("theme.bootstrap.done.Flaky"));
        Assert.True(s.Has("theme.bootstrap.done.Narrow"));

        fail = false;
        Assert.Equal(1, ThemeBootstrap.Run(s, steps));
        Assert.True(s.Has("theme.bootstrap.done.Flaky"));
    }

    [Fact]
    public void Copy_ReportsWhetherItWrote()
    {
        var s = LegacyStore();
        Assert.True(ThemeBootstrap.Copy(s, LegacyNarrow, Narrow));
        Assert.False(ThemeBootstrap.Copy(s, LegacyNarrow, Narrow));                       // the target exists now
        Assert.False(ThemeBootstrap.Copy(s, new SettingKey<bool>("legacy.absent", false), new SettingKey<bool>("theme.t.absent", false)));
        Assert.False(s.Has("theme.t.absent"));
    }

    // ── disjointness: what makes the merge order irrelevant ─────────────────────────────────────────────────────────────

    static ThemeBootstrapStep Step(string name, string[] reads, string[] writes) => new(name, reads, writes, static _ => { });

    [Fact]
    public void Disjointness_AcceptsIndependentSteps()
        => Assert.Null(ThemeBootstrap.Disjointness(AllSteps()));

    [Fact]
    public void Disjointness_RejectsTwoStepsWritingTheSameKey()
        => Assert.Contains("both write", ThemeBootstrap.Disjointness([Step("A", ["a"], ["k"]), Step("B", ["b"], ["k"])]));

    [Fact]
    public void Disjointness_RejectsAStepReadingWhatAnotherWrites()
        => Assert.Contains("reads 'k'", ThemeBootstrap.Disjointness([Step("A", ["a"], ["k"]), Step("B", ["k"], ["z"])]));

    [Fact]
    public void Disjointness_RejectsAStepReadingItsOwnOutput()
        => Assert.Contains("both reads and writes", ThemeBootstrap.Disjointness([Step("A", ["k"], ["k"])]));

    [Fact]
    public void Disjointness_RejectsDuplicateOrEmptyNames_AndWritesToADoneMarker()
    {
        Assert.Contains("named 'A'", ThemeBootstrap.Disjointness([Step("A", [], ["x"]), Step("A", [], ["y"])]));
        Assert.Contains("no name", ThemeBootstrap.Disjointness([Step(" ", [], ["x"])]));
        Assert.Contains("done marker", ThemeBootstrap.Disjointness([Step("A", [], ["theme.bootstrap.done.B"])]));
    }

    [Fact]
    public void ShippedSteps_AreNamedAndDisjoint()
    {
        Assert.Null(ThemeBootstrap.Disjointness(ThemeBootstrap.Steps));
        Assert.Equal(ThemeBootstrap.Steps.Count, ThemeBootstrap.Steps.Select(step => step.Name).Distinct().Count());
    }

    // ── every merge order gives one end state ───────────────────────────────────────────────────────────────────────────

    static IEnumerable<T[]> Permutations<T>(IReadOnlyList<T> items)
    {
        var current = items.ToArray();
        return Heap(current.Length, current);

        // Heap's algorithm; each permutation is yielded as a copy, so the shared array can keep being permuted.
        static IEnumerable<T[]> Heap(int k, T[] a)
        {
            if (k <= 1)
            {
                yield return (T[])a.Clone();
                yield break;
            }
            foreach (var p in Heap(k - 1, a)) yield return p;
            for (int i = 0; i < k - 1; i++)
            {
                int swap = (k % 2 == 0) ? i : 0;
                (a[swap], a[k - 1]) = (a[k - 1], a[swap]);
                foreach (var p in Heap(k - 1, a)) yield return p;
            }
        }
    }

    /// <summary>Runs every order of <paramref name="steps"/> over a copy of <paramref name="seed"/> and returns the distinct end
    /// states. Order independence means the answer has one element. The machinery the M-PRs reuse.</summary>
    static HashSet<string> EndStates(IReadOnlyList<ThemeBootstrapStep> steps, SnapshotSettings seed)
        => EndStates(Permutations(steps), seed);

    /// <summary>Every order is n! runs: past eight steps (40 320) the shipped set is checked over a fixed sample instead (the given order, its reverse, every
    /// rotation and a seeded run of shuffles), which still catches any pair of steps that interact: `Disjointness` already rejects the structural cases.</summary>
    static IEnumerable<ThemeBootstrapStep[]> SampledOrders(IReadOnlyList<ThemeBootstrapStep> steps)
    {
        var all = steps.ToArray();
        yield return all;
        yield return all.Reverse().ToArray();
        for (int r = 1; r < all.Length; r++) yield return all.Skip(r).Concat(all.Take(r)).ToArray();
        var rng = new Random(20261014);
        for (int i = 0; i < 2000; i++) yield return all.OrderBy(_ => rng.Next()).ToArray();
    }

    static HashSet<string> EndStates(IEnumerable<ThemeBootstrapStep[]> orders, SnapshotSettings seed)
    {
        var states = new HashSet<string>(StringComparer.Ordinal);
        foreach (var order in orders)
        {
            var store = seed.Clone();
            ThemeBootstrap.Run(store, order);
            ThemeBootstrap.Run(store, order);   // and a second launch changes nothing
            states.Add(store.Dump());
        }
        return states;
    }

    [Fact]
    public void EveryMergeOrder_GivesTheSameEndState()
    {
        Assert.Single(EndStates(AllSteps(), LegacyStore()));
        Assert.Single(EndStates(AllSteps(), LegacyStore(density: 9, narrow: false, scale: 7f, font: "")));   // invalid values too
        Assert.Single(EndStates(AllSteps(), new SnapshotSettings()));                                          // a fresh install
    }

    [Fact]
    public void EveryMergeOrder_WithAnExistingOptionValue_KeepsIt()
    {
        var seed = LegacyStore();
        seed.Set(Narrow, false);
        var states = EndStates(AllSteps(), seed);
        Assert.Single(states);
        Assert.Contains("theme.classic.structure.narrow=False", states.Single());
    }

    [Fact]
    public void AnyPartialShipment_GivesTheSameValuesForTheStepsItHas()
    {
        // A patch release ships any subset: the values a step produces never depend on which others ran.
        var seed = LegacyStore();
        var full = seed.Clone();
        ThemeBootstrap.Run(full, AllSteps());
        var partial = seed.Clone();
        ThemeBootstrap.Run(partial, [ScaleStep(), DensityStep()]);

        Assert.Equal(full.Get(DensityKey), partial.Get(DensityKey));
        Assert.Equal(full.Get(Scale), partial.Get(Scale));
        Assert.False(partial.Has(Narrow.Name));
    }

    [Fact]
    public void Permutations_EnumerateEveryOrderOnce()
    {
        var orders = Permutations(new[] { 1, 2, 3, 4 }).Select(o => string.Join("", o)).ToList();
        Assert.Equal(24, orders.Count);
        Assert.Equal(24, orders.Distinct().Count());
    }

    [Fact]
    public void ShippedSteps_AreOrderIndependentOverALegacyStoreOfTheirOwnReads()
    {
        // Each M-PR's step lands in ThemeBootstrap.Steps; its legacy keys are seeded here with one probe value per kind so the
        // permutation check covers it without a new test. With no step shipped this is the empty product, still one end state.
        var seed = new SnapshotSettings();
        foreach (var step in ThemeBootstrap.Steps)
            foreach (string key in step.ReadsLegacyKeys)
            {
                seed.Set(new SettingKey<int>(key, 0), 1);
                seed.Set(new SettingKey<bool>(key, false), true);
            }
        Assert.Single(EndStates(ThemeBootstrap.Steps.Count <= 8 ? Permutations(ThemeBootstrap.Steps) : SampledOrders(ThemeBootstrap.Steps), seed));
    }

    [Fact]
    public void ThemeBootstrap_RunWithTheShippedSteps_AgainstAnEmptyStore_WritesNothingButMarkers()
    {
        var s = new SnapshotSettings();
        ThemeBootstrap.Run(s);
        Assert.All(s.Dump().Split(';', StringSplitOptions.RemoveEmptyEntries), entry => Assert.StartsWith("theme.bootstrap.done.", entry));
    }
}
