// ── Wavee.Tests/StageFrameContractTests.cs — the stage frame's published keys and props (themes plan 6.0, T7a1) ────────────────
//
// The fullscreen stage's frame is the structure's: `Stage.Root`, `Stage.TopBar`, `Stage.Transport`, `Stage.Options`, `Stage.Gallery` and
// `Stage.Caption`. What is pure about the contract: the keys' ids (a published id never changes), Classic registering all six under its
// template ids, the root's props being one cached object (so the frame's props stay equal across re-pushes and an equal record skips the
// template), and the caption's props comparing by value (the host rebuilds one per render; a render that decided the same thing skips the
// template, a changed line or generation does not). The mounted facts are `Wavee.ShellTests\StageFrameMountedTests.cs`.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;
using Wavee.Themes.Classic;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests;

public class StageFrameContractTests
{
    [Fact]
    public void The_published_ids_are_the_plans()
    {
        Assert.Equal("Stage.Root", ViewKeys.StageRoot.Id);
        Assert.Equal("Stage.TopBar", ViewKeys.StageTopBar.Id);
        Assert.Equal("Stage.Transport", ViewKeys.StageTransport.Id);
        Assert.Equal("Stage.Options", ViewKeys.StageOptions.Id);
        Assert.Equal("Stage.Gallery", ViewKeys.StageGallery.Id);
        Assert.Equal("Stage.Caption", ViewKeys.StageCaption.Id);
    }

    [Fact]
    public void Classic_registers_the_six_stage_keys_under_its_template_ids()
    {
        var classic = ClassicTheme.Create();

        Assert.Equal("classic.stage.root", classic.Registry.TemplateIdOf(ViewKeys.StageRoot.Id));
        Assert.Equal("classic.stage.top-bar", classic.Registry.TemplateIdOf(ViewKeys.StageTopBar.Id));
        Assert.Equal("classic.stage.transport", classic.Registry.TemplateIdOf(ViewKeys.StageTransport.Id));
        Assert.Equal("classic.stage.options", classic.Registry.TemplateIdOf(ViewKeys.StageOptions.Id));
        Assert.Equal("classic.stage.gallery", classic.Registry.TemplateIdOf(ViewKeys.StageGallery.Id));
        Assert.Equal("classic.stage.caption", classic.Registry.TemplateIdOf(ViewKeys.StageCaption.Id));
        Assert.Empty(classic.Validate());
    }

    [Fact]
    public void The_roots_props_are_one_cached_object_that_mounts_the_core_surface()
    {
        Assert.Same(Stage.RootProps, Stage.RootProps);
        Assert.NotNull(Stage.RootProps.Mount);
    }

    static StageCaptionProps Caption(string generation = "1", string? active = "b", bool dots = false) => new()
    {
        Width = 800f, X = 100f, Bottom = 180f, BlockMaxHeight = 220f,
        ActiveSize = 48f, ActiveLineHeight = 56f, ContextSize = 22f, ContextLineHeight = 29f,
        Generation = generation,
        PreviousIndex = 0, PreviousText = "a",
        NextIndex = 2, NextText = "c",
        ActiveIndex = 1, ActiveText = active,
        Dots = dots, Anchor = 1, Bloom = new ColorF(1f, 0.5f, 0.5f, 1f),
        OnMain = static _ => { }, OnGlow = static _ => { }, OnDots = static _ => { },
        DotAlpha = s_dots, Ticker = Array.Empty<Element>(),
    };

    static readonly FloatSignal[] s_dots = [new(0f), new(0f), new(0f)];

    [Fact]
    public void A_caption_render_that_decided_the_same_thing_equals_the_last_and_a_changed_line_does_not()
    {
        // the delegates and the arrays are the host's cached objects, so two renders of one decision compare equal
        var first = Caption();
        var again = first with { };
        Assert.Equal(first, again);

        Assert.NotEqual(first, first with { ActiveText = "other" });
        Assert.NotEqual(first, first with { Generation = "2" });
        Assert.NotEqual(first, first with { Dots = true });
        Assert.NotEqual(first, first with { Wipe = new StageCaptionWipe(0.25f, 12f, 4f, 300f) });
    }

    [Fact]
    public void The_wipe_numbers_compare_by_value()
        => Assert.Equal(new StageCaptionWipe(0.5f, 12f, 4f, 300f), new StageCaptionWipe(0.5f, 12f, 4f, 300f));

    [Fact]
    public void The_transport_facts_compare_by_value()
    {
        var facts = new StageTransportFacts(true, true, true, true, false, false, true, false, true, null, "Playing from album", "Album");
        Assert.Equal(facts, facts with { });
        Assert.NotEqual(facts, facts with { Playing = false });
        Assert.NotEqual(facts, facts with { Source = "Other" });
        Assert.Equal(new StageDeviceFacts("g", "This PC"), new StageDeviceFacts("g", "This PC"));
    }
}
