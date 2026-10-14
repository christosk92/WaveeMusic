// ── Wavee.Tests/QueueContractTests.cs — the queue's published keys (themes plan 6.0, T7d) ─────────────────────────────────────
//
// The queue is the structure's since T7d: `Queue.Panel` (the rail's panel) and `Queue.StagePane` (the stage's Up next column). What is pure
// about the contract: the ids (a published id never changes), Classic registering both under its template id, and the section tag the
// keys carry. The mounted facts are `Wavee.ShellTests\QueueMountedTests.cs`; the model and the verbs are `QueueModelTests.cs`.

using Wavee.Themes.Classic;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests;

public class QueueContractTests
{
    [Fact]
    public void The_published_ids_are_the_plans()
    {
        Assert.Equal("Queue.Panel", ViewKeys.QueuePanel.Id);
        Assert.Equal("Queue.StagePane", ViewKeys.QueueStagePane.Id);
    }

    [Fact]
    public void Classic_registers_both_queue_views()
    {
        var classic = ClassicTheme.Create();

        Assert.Equal("classic.queue.panel", classic.Registry.TemplateIdOf(ViewKeys.QueuePanel.Id));
        Assert.Equal("classic.queue.stage-pane", classic.Registry.TemplateIdOf(ViewKeys.QueueStagePane.Id));
        Assert.Empty(classic.Validate());
    }

    [Theory]
    [InlineData(QueueSection.Queue, "q")]
    [InlineData(QueueSection.NextUp, "u")]
    [InlineData(QueueSection.Autoplay, "a")]
    public void A_section_keys_its_slots_by_one_letter(QueueSection section, string tag)
    {
        Assert.Equal(tag, QueueSections.Tag(section));
    }
}
