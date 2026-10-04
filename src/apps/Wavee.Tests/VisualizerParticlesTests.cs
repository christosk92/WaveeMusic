// ── Wavee.Tests/VisualizerParticlesTests.cs — the face simulations' pure core (Shell/Visualizer.Particles.cs) ──────────
//
// Pure: every body takes the caller's dt, levels and colours and writes a caller-owned `Sprite[]`, so nothing here needs a
// scope or an engine. Pins the counts, the bounds over a long random run, determinism, and FRAME-RATE INDEPENDENCE — the
// same wall time at 30, 60 and 144 Hz lands on the same picture. The tickers and fields that bind them are
// Visualizer.Classics.UI.cs / Visualizer.ITunes.UI.cs (covered by the stage walks, not a unit test). Plan:
// viz-app-plan.md §6.1.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class VisualizerParticlesTests
{
    static readonly Visualizer.Particles.Tints Dark = new(ColorF.FromRgba(200, 40, 90), ColorF.FromRgba(40, 120, 220), ColorF.FromRgba(240, 190, 40), Dark: true);
    static readonly Visualizer.Particles.Tints Light = Dark with { Dark = false };

    static float[] Levels(float v)
    {
        var a = new float[Visualizer.Bands.Count];
        Array.Fill(a, v);
        return a;
    }

    /// <summary>A loud low end and a quiet top: some particles repel, some fall back.</summary>
    static float[] Tilted()
    {
        var a = new float[Visualizer.Bands.Count];
        for (int i = 0; i < a.Length; i++) a[i] = 1f - i / (float)(a.Length - 1);
        return a;
    }

    /// <summary>Advance <paramref name="step"/> for <paramref name="seconds"/> of wall time at <paramref name="hz"/>.</summary>
    static void Run(int hz, float seconds, Action<float> step)
    {
        int ticks = (int)MathF.Round(seconds * hz);
        float dt = 1f / hz;
        for (int i = 0; i < ticks; i++) step(dt);
    }

    // ── shared ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Dt_clamps_and_treats_first_tick_and_restart_as_one_frame()
    {
        Assert.Equal(Visualizer.Particles.StepSec, Visualizer.Particles.Dt(0L, 5_000L));
        Assert.Equal(0.016f, Visualizer.Particles.Dt(1_000L, 1_016L), 1e-4f);
        Assert.Equal(Visualizer.Particles.MaxDtSec, Visualizer.Particles.Dt(1_000L, 1_200L));
        Assert.Equal(Visualizer.Particles.StepSec, Visualizer.Particles.Dt(1_000L, 1_000L + Visualizer.Particles.RestartGapMs + 1));
        Assert.Equal(0f, Visualizer.Particles.Dt(1_000L, 990L));
    }

    [Fact]
    public void Hash_is_deterministic_and_in_unit_range()
    {
        for (int i = 0; i < 500; i++)
        {
            float h = Visualizer.Particles.Hash(i, 7);
            Assert.InRange(h, 0f, 1f);
            Assert.Equal(h, Visualizer.Particles.Hash(i, 7));
        }
    }

    [Fact]
    public void Tints_glow_is_light_on_dark_and_a_darker_tint_on_light()
    {
        var c = Dark.A;
        var dark = Dark.Glow(c, 0.6f);
        var light = Light.Glow(c, 0.6f);
        Assert.Equal(c.R, dark.R); Assert.Equal(0.6f, dark.A);
        Assert.True(light.R < c.R && light.G <= c.G && light.B <= c.B);
        Assert.True(light.A < dark.A);
        var hot = Dark.Hot(c, 0.5f, 0.9f);
        Assert.True(hot.R > c.R && hot.R < 1f);   // lightened, never white
    }

    // ── Warp ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Warp_stars_stay_in_their_volume_over_a_long_random_run()
    {
        var w = new Visualizer.Particles.Warp(Visualizer.Particles.Warp.Count);
        var rng = new Random(11);
        for (int i = 0; i < 10_000; i++)
        {
            w.Step((float)(0.004 + rng.NextDouble() * 0.05), (float)rng.NextDouble(), (float)rng.NextDouble());
            if (i % 997 != 0) continue;
            for (int s = 0; s < w.N; s++)
            {
                Assert.InRange(w.Z[s], Visualizer.Particles.Warp.NearZ, 1f);
                Assert.InRange(w.X[s], -1f, 1f); Assert.InRange(w.Y[s], -1f, 1f);
            }
        }
    }

    [Fact]
    public void Warp_writes_at_most_its_count_and_every_streak_starts_inside_the_box()
    {
        var w = new Visualizer.Particles.Warp(Visualizer.Particles.Warp.Count);
        var dst = new Sprite[Visualizer.Particles.Warp.Count];
        w.Step(0.5f, 0.8f, 1f);
        int n = w.Write(dst, Visualizer.Particles.Warp.WeakCount, 1600f, 900f, 800f, 414f, in Dark, 1f);
        Assert.InRange(n, 1, Visualizer.Particles.Warp.WeakCount);
        for (int i = 0; i < n; i++)
        {
            Assert.True(dst[i].W >= 2f && dst[i].H > 0f && dst[i].Soft == 1f);
            // the head (centre + half the length along the rotation) is the projected star, inside the box
            float hx = dst[i].X + MathF.Cos(dst[i].Rot) * dst[i].W * 0.5f, hy = dst[i].Y + MathF.Sin(dst[i].Rot) * dst[i].W * 0.5f;
            Assert.InRange(hx, -0.01f, 1600.01f); Assert.InRange(hy, -0.01f, 900.01f);
        }
    }

    [Fact]
    public void Warp_is_frame_rate_independent()
    {
        var a = new Visualizer.Particles.Warp(Visualizer.Particles.Warp.Count);
        var b = new Visualizer.Particles.Warp(Visualizer.Particles.Warp.Count);
        var c = new Visualizer.Particles.Warp(Visualizer.Particles.Warp.Count);
        Run(60, 3f, dt => a.Step(dt, 0.6f, 0.3f));
        Run(144, 3f, dt => b.Step(dt, 0.6f, 0.3f));
        Run(30, 3f, dt => c.Step(dt, 0.6f, 0.3f));
        Assert.Equal(a.Speed, b.Speed, 1e-7f); Assert.Equal(a.Speed, c.Speed, 1e-7f);
        float span = 1f - Visualizer.Particles.Warp.NearZ;
        for (int s = 0; s < a.N; s++)
        {
            float d1 = MathF.Abs(a.Z[s] - b.Z[s]), d2 = MathF.Abs(a.Z[s] - c.Z[s]);
            Assert.True(MathF.Min(d1, span - d1) < 2e-3f, $"star {s}: {a.Z[s]} vs {b.Z[s]}");
            Assert.True(MathF.Min(d2, span - d2) < 2e-3f, $"star {s}: {a.Z[s]} vs {c.Z[s]}");
        }
    }

    [Fact]
    public void Warp_settle_moves_nothing_and_takes_the_level_speed()
    {
        var w = new Visualizer.Particles.Warp(40);
        float z0 = w.Z[5];
        w.Settle(1f, 0f);
        Assert.Equal(Visualizer.Particles.Warp.Target(1f, 0f), w.Speed);
        Assert.Equal(z0, w.Z[5]);
    }

    // ── Magneto ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Magneto_bodies_stay_in_bounds_and_finite_over_a_long_random_run()
    {
        var m = new Visualizer.Particles.Magneto(Visualizer.Particles.Magneto.Count);
        var levels = new float[Visualizer.Bands.Count];
        var rng = new Random(5);
        for (int i = 0; i < 10_000; i++)
        {
            for (int b = 0; b < levels.Length; b++) levels[b] = (float)rng.NextDouble();
            m.Step((float)(0.004 + rng.NextDouble() * 0.06), levels, (float)rng.NextDouble());
        }
        for (int p = 0; p < m.N; p++)
        {
            Assert.InRange(m.X[p], -Visualizer.Particles.Magneto.BoundX, Visualizer.Particles.Magneto.BoundX);
            Assert.InRange(m.Y[p], -Visualizer.Particles.Magneto.BoundY, Visualizer.Particles.Magneto.BoundY);
            Assert.True(float.IsFinite(m.VX[p]) && float.IsFinite(m.VY[p]));
        }
    }

    [Fact]
    public void Magneto_is_deterministic()
    {
        var a = new Visualizer.Particles.Magneto(48);
        var b = new Visualizer.Particles.Magneto(48);
        var levels = Tilted();
        for (int i = 0; i < 300; i++) { a.Step(1f / 60f, levels, 0.4f); b.Step(1f / 60f, levels, 0.4f); }
        Assert.Equal(a.X, b.X); Assert.Equal(a.Y, b.Y);
    }

    [Fact]
    public void Magneto_is_frame_rate_independent()
    {
        var levels = Tilted();
        var a = new Visualizer.Particles.Magneto(Visualizer.Particles.Magneto.Count);
        var b = new Visualizer.Particles.Magneto(Visualizer.Particles.Magneto.Count);
        var c = new Visualizer.Particles.Magneto(Visualizer.Particles.Magneto.Count);
        Run(60, 2f, dt => a.Step(dt, levels, 0.5f));
        Run(144, 2f, dt => b.Step(dt, levels, 0.5f));
        Run(30, 2f, dt => c.Step(dt, levels, 0.5f));
        var da = new Sprite[200]; var db = new Sprite[200]; var dc = new Sprite[200];
        int na = a.WriteBodies(da, a.N, 500f, 400f, 800f, levels, 0f, in Dark, 1f);
        int nb = b.WriteBodies(db, b.N, 500f, 400f, 800f, levels, 0f, in Dark, 1f);
        int nc = c.WriteBodies(dc, c.N, 500f, 400f, 800f, levels, 0f, in Dark, 1f);
        Assert.Equal(na, nb); Assert.Equal(na, nc);
        for (int i = 0; i < na; i++)
        {
            Assert.Equal(da[i].X, db[i].X, 0.05f); Assert.Equal(da[i].Y, db[i].Y, 0.05f);
            Assert.Equal(da[i].X, dc[i].X, 0.05f); Assert.Equal(da[i].Y, dc[i].Y, 0.05f);
        }
    }

    [Fact]
    public void Magneto_time_starts_at_zero_and_trails_the_physics_by_one_step()
    {
        var m = new Visualizer.Particles.Magneto(12);
        Assert.Equal(0f, m.Time);                                     // before the first step: never negative
        m.Step(Visualizer.Particles.StepSec * 0.5f, Levels(0.5f), 0f);
        Assert.Equal(0f, m.Time);
        Run(60, 2f, dt => m.Step(dt, Levels(0.5f), 0f));
        Assert.InRange(m.Time, 2f - 2f * Visualizer.Particles.StepSec, 2f);
    }

    [Fact]
    public void Magneto_loud_bands_push_particles_out_and_quiet_bands_pull_them_in()
    {
        static float Spread(Visualizer.Particles.Magneto m) { float s = 0f; for (int i = 0; i < m.N; i++) s += MathF.Sqrt(m.X[i] * m.X[i] + m.Y[i] * m.Y[i]); return s / m.N; }
        var loud = new Visualizer.Particles.Magneto(72);
        var quiet = new Visualizer.Particles.Magneto(72);
        Run(60, 4f, dt => loud.Step(dt, Levels(1f), 0f));
        Run(60, 4f, dt => quiet.Step(dt, Levels(0f), 0f));
        Assert.True(Spread(loud) > Spread(quiet));
    }

    [Fact]
    public void Magneto_writes_two_discs_per_particle_plus_the_cores_and_rays_within_reach()
    {
        var m = new Visualizer.Particles.Magneto(Visualizer.Particles.Magneto.Count);
        var levels = Levels(0.5f);
        var discs = new Sprite[Visualizer.Particles.Magneto.Count * 2 + 4];
        Assert.Equal(Visualizer.Particles.Magneto.WeakCount * 2 + 4, m.WriteBodies(discs, Visualizer.Particles.Magneto.WeakCount, 500f, 400f, 800f, levels, 1f, in Dark, 1f));
        var rays = new Sprite[Visualizer.Particles.Magneto.Count];
        int n = m.WriteRays(rays, 500f, 400f, 800f, levels, in Light, 1.5f);
        Assert.InRange(n, 0, Visualizer.Particles.Magneto.Count);
        for (int i = 0; i < n; i++)
        {
            float dx = rays[i].W - rays[i].X, dy = rays[i].H - rays[i].Y;   // Segment: X,Y = P0 (a core), W,H = P1
            Assert.True(MathF.Sqrt(dx * dx + dy * dy) <= 800f * Visualizer.Particles.Magneto.RayReach + 0.01f);
            Assert.Equal(1.5f, rays[i].Rot);
        }
    }

    // ── Ambience ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ambience_writes_two_discs_per_orb_inside_its_orbit_ellipse()
    {
        var a = new Visualizer.Particles.Ambience(Visualizer.Particles.Ambience.Count);
        var dst = new Sprite[Visualizer.Particles.Ambience.Count * 2];
        var levels = Levels(0.7f);
        for (int i = 0; i < 2_000; i++) a.Step(0.033f);
        int n = a.Write(dst, Visualizer.Particles.Ambience.WeakCount, 960f, 500f, 1000f, levels, in Dark, 1f);
        Assert.Equal(Visualizer.Particles.Ambience.WeakCount * 2, n);
        for (int i = 0; i < n; i++)
        {
            // radius ≤ (0.46 + 0.04)·m, stretched 1.3 × 0.8
            Assert.InRange(dst[i].X, 960f - 650.1f, 960f + 650.1f);
            Assert.InRange(dst[i].Y, 500f - 400.1f, 500f + 400.1f);
            Assert.True(dst[i].W > 0f && dst[i].W == dst[i].H);
        }
    }

    [Fact]
    public void Ambience_orbits_are_frame_rate_independent()
    {
        var a = new Visualizer.Particles.Ambience(34);
        var b = new Visualizer.Particles.Ambience(34);
        Run(60, 5f, a.Step);
        Run(144, 5f, b.Step);
        for (int i = 0; i < 34; i++)
        {
            var (ax, ay) = a.Offset(i, 1000f); var (bx, by) = b.Offset(i, 1000f);
            Assert.Equal(ax, bx, 0.05f); Assert.Equal(ay, by, 0.05f);
        }
    }

    // ── Kaleido ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Kaleido_writes_parts_times_copies_capsules_mirrored()
    {
        var k = new Visualizer.Particles.Kaleido();
        var dst = new Sprite[96];
        var levels = Levels(0.5f);
        Assert.Equal(96, k.Write(dst, 500f, 500f, 800f, levels, in Dark, Visualizer.Particles.Kaleido.Parts, Visualizer.Particles.Kaleido.Copies));
        Assert.Equal(24, k.Write(dst, 500f, 500f, 800f, levels, in Dark, Visualizer.Particles.Kaleido.PreviewParts, Visualizer.Particles.Kaleido.PreviewCopies));
        // copy 0 and its mirror (copy 1) sit at the same radius
        k.Write(dst, 500f, 500f, 800f, levels, in Dark, 8, 12);
        for (int p = 0; p < 8; p++)
        {
            float r0 = MathF.Sqrt(MathF.Pow(dst[p].X - 500f, 2) + MathF.Pow(dst[p].Y - 500f, 2));
            float r1 = MathF.Sqrt(MathF.Pow(dst[8 + p].X - 500f, 2) + MathF.Pow(dst[8 + p].Y - 500f, 2));
            Assert.Equal(r0, r1, 0.01f);
        }
    }

    [Fact]
    public void Kaleido_spin_is_frame_rate_independent()
    {
        var a = new Visualizer.Particles.Kaleido();
        var b = new Visualizer.Particles.Kaleido();
        Run(60, 4f, dt => a.Step(dt, 0.5f));
        Run(144, 4f, dt => b.Step(dt, 0.5f));
        Assert.Equal(a.Rot, b.Rot, 1e-4f);
        Assert.Equal(a.TimeMs, b.TimeMs, 0.01);
    }

    // ── Tunnel ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Tunnel_depths_stay_in_unit_range_and_the_phase_wraps()
    {
        var t = new Visualizer.Particles.Tunnel();
        var rng = new Random(3);
        for (int i = 0; i < 10_000; i++)
        {
            t.Step((float)(rng.NextDouble() * 0.1), (float)rng.NextDouble(), (float)rng.NextDouble());
            Assert.InRange(t.Phase, 0f, Visualizer.Particles.Tunnel.Frames);
            Assert.InRange(t.Spin, 0f, MathF.Tau);
        }
        for (int k = 0; k < Visualizer.Particles.Tunnel.Frames; k++)
        {
            float d = Visualizer.Particles.Tunnel.Depth(k, t.Phase, Visualizer.Particles.Tunnel.Frames);
            Assert.InRange(d, 0f, 1f);
            Assert.InRange(Visualizer.Particles.Tunnel.Alpha(d), 0f, 1f);
        }
    }

    [Fact]
    public void Tunnel_phase_is_frame_rate_independent()
    {
        var a = new Visualizer.Particles.Tunnel();
        var b = new Visualizer.Particles.Tunnel();
        Run(60, 6f, dt => a.Step(dt, 0.7f, 0.2f));
        Run(144, 6f, dt => b.Step(dt, 0.7f, 0.2f));
        Assert.Equal(a.Phase, b.Phase, 1e-3f);
        Assert.Equal(a.Spin, b.Spin, 1e-4f);
    }

    // ── Feedback ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Feedback_decay_compounds_to_the_same_fade_at_any_rate()
    {
        float d60 = Visualizer.Particles.Feedback.DriftDecay;
        Assert.Equal(d60, Visualizer.Particles.Feedback.Decay(d60, 1f / 60f), 1e-5f);
        double keep60 = Math.Pow(1.0 - Visualizer.Particles.Feedback.Decay(d60, 1f / 60f), 60);
        double keep144 = Math.Pow(1.0 - Visualizer.Particles.Feedback.Decay(d60, 1f / 144f), 144);
        Assert.Equal(keep60, keep144, 1e-4);
        Assert.Equal(0f, Visualizer.Particles.Feedback.Decay(d60, 0f));
    }

    [Fact]
    public void Feedback_warp_is_identity_at_zero_dt_and_keeps_its_pivot()
    {
        Assert.True(Visualizer.Particles.Feedback.Drift(0.5f, 1f, 0f, 0f, -40f).IsIdentity);
        var m = Visualizer.Particles.Feedback.About(0.3f, 1.05f, 1.02f, 120f, -60f);
        var p = m.Transform(new Point2(120f, -60f));
        Assert.Equal(120f, p.X, 1e-3f); Assert.Equal(-60f, p.Y, 1e-3f);
        // per-advance zoom compounds to the same per-second zoom at 60 and 144 Hz
        var z60 = Visualizer.Particles.Feedback.Drift(0f, 1f, 1f / 60f, 0f, 0f);
        var z144 = Visualizer.Particles.Feedback.Drift(0f, 1f, 1f / 144f, 0f, 0f);
        float s60 = MathF.Sqrt(z60.M11 * z60.M11 + z60.M12 * z60.M12), s144 = MathF.Sqrt(z144.M11 * z144.M11 + z144.M12 * z144.M12);
        Assert.Equal(MathF.Pow(s60, 60f), MathF.Pow(s144, 144f), 1e-2f);
    }

    [Fact]
    public void Drift_ring_is_mirrored_and_closes_without_a_seam()
    {
        const int last = 64;
        Assert.Equal(Visualizer.Particles.Feedback.RingBand(0, last), Visualizer.Particles.Feedback.RingBand(last, last));
        Assert.Equal(40, Visualizer.Particles.Feedback.RingBand(last / 2, last));
        for (int k = 0; k <= last; k++) Assert.Equal(Visualizer.Particles.Feedback.RingBand(k, last), Visualizer.Particles.Feedback.RingBand(last - k, last));
    }
}
