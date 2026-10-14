// ── Wavee.ShellTests/QueueMountedTests.cs — the queue surfaces, mounted (themes plan 6.1, T7d) ─────────────────────────────────
//
// The queue is the structure's since T7d: `Queue.Panel` is the rail's body and `Queue.StagePane` the fullscreen stage's Up next column. The core
// hosts (`Queue.RailPanelHost`, `Queue.StagePaneHost`) keep the model, the page counters, the slots, the reorder lane and the verbs
// (`Wavee.Tests\QueueModelTests.cs` pins the projection, the paging and the verbs; `QueueContractTests.cs` the keys). What only a mounted tree can say:
//
//   registered   Classic is the registered template of both keys
//   rail         the real rail on the queue draws the now-playing card (keyed by the track) and, with nothing upcoming, the one lane that still
//                takes a foreign drop (`lane:empty`)
//   stage        the real stage on the Queue mode draws the same empty lane inside its Up next column

using Wavee;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed class QueueMountedTests(ShellHost host)
{
    [Fact]
    public void Classic_is_the_registered_template_of_both_queue_keys()
        => host.Run(() =>
        {
            Assert.Equal("classic.queue.panel", ThemeHost.Registry.TemplateIdOf(ViewKeys.QueuePanel.Id));
            Assert.Equal("classic.queue.stage-pane", ThemeHost.Registry.TemplateIdOf(ViewKeys.QueueStagePane.Id));
        });

    [Fact]
    public void The_rail_draws_the_now_playing_card_and_the_empty_lane_through_the_registered_panel()
        => host.Mounted(shell =>
        {
            var previous = Rail.QueueBody;
            Rail.QueueBody = static () => Queue.PanelSlot();   // the composition root installs it; the headless shell does not
            try
            {
                Play("spotify:track:tr40", fromMs: 20_000);
                if (!(Shell.Ui.RailOpen.Peek() && Shell.Ui.Mode.Peek() == Shell.RailMode.Queue))
                    Shell.Ui.Toggle(Shell.RailMode.Queue);
                Until(shell, () => !shell.Find("lane:empty").IsNull, "the rail's queue lane mounted", 900);
                shell.RunFrames(30);

                Assert.Single(shell.FindPrefix("np:"));                  // the now-playing card, keyed by the track
                Assert.True(shell.Find("lane:upcoming").IsNull, "nothing is upcoming, so only the empty lane is mounted");
            }
            finally
            {
                if (Shell.Ui.RailOpen.Peek() && Shell.Ui.Mode.Peek() == Shell.RailMode.Queue) Shell.Ui.Toggle(Shell.RailMode.Queue);
                Rail.QueueBody = previous;
                shell.RunFrames(30);
            }
        });

    [Fact]
    public void The_stage_draws_its_up_next_column_through_the_registered_pane()
        => host.Mounted(shell =>
        {
            try
            {
                Prefs.Stage.SetMode((int)Stage.Mode.Queue);
                Play("spotify:track:tr40", fromMs: 20_000);
                Stage.Open(null, "test");
                Until(shell, () => !shell.Find("stagelane:empty").IsNull, "the stage's queue lane mounted", 900);
                shell.RunFrames(30);

                Assert.True(shell.Find("stagelane:upcoming").IsNull, "nothing is upcoming, so only the empty lane is mounted");
            }
            finally
            {
                Stage.Close(null, "test");
                shell.RunFrames(60);
                Prefs.Stage.SetMode(Platform.Keys.StageMode.Default);
            }
        });
}
