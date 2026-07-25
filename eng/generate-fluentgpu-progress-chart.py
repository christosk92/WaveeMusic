"""Generate the README's reproducible FluentGPU progress chart.

The curated values and their source commits live in benchmark-data so the
chart remains a rendering step, not a second copy of the measurements.
"""

import json
from pathlib import Path

import matplotlib as mpl
import matplotlib.pyplot as plt
from matplotlib.patches import FancyBboxPatch


ROOT = Path(__file__).resolve().parents[1]
DATA_PATH = ROOT / "benchmark-data" / "fluentgpu-progress.json"
OUTPUTS = (
    ROOT / "assets" / "FluentGpuProgress.png",
    ROOT / "assets" / "FluentGpuProgress.svg",
)

INK = "#17243B"
MUTED = "#667085"
PAPER = "#FAF7F0"
BEFORE = "#D4D8E1"
AFTER = "#7C5CFC"
ACCENT = "#FF8A4C"
GOOD = "#15866B"


def card(ax: plt.Axes, title: str, subtitle: str) -> None:
    ax.set_facecolor(PAPER)
    for spine in ax.spines.values():
        spine.set_visible(False)
    ax.tick_params(left=False, bottom=False, labelleft=False, labelbottom=False)
    ax.add_patch(
        FancyBboxPatch(
            (0, 0),
            1,
            1,
            boxstyle="round,pad=0.018,rounding_size=0.035",
            transform=ax.transAxes,
            linewidth=1.4,
            edgecolor=INK,
            facecolor="none",
            clip_on=False,
        )
    )
    ax.text(
        0.05,
        0.92,
        title,
        transform=ax.transAxes,
        color=INK,
        fontsize=13,
        fontweight="bold",
        va="top",
    )
    ax.text(
        0.05,
        0.82,
        subtitle,
        transform=ax.transAxes,
        color=MUTED,
        fontsize=8.8,
        va="top",
    )


def comparison(
    ax: plt.Axes,
    title: str,
    subtitle: str,
    before: float,
    after: float,
    before_label: str,
    after_label: str,
    change_label: str,
    target: float | None = None,
) -> None:
    card(ax, title, subtitle)
    top = max(before, after) * 1.65
    ax.set_xlim(-0.65, 1.65)
    ax.set_ylim(0, top)
    ax.bar(
        [0, 1],
        [before, after],
        width=0.56,
        color=[BEFORE, AFTER],
        edgecolor=INK,
        linewidth=1.1,
        zorder=3,
    )
    if target is not None:
        ax.axhline(target, color=ACCENT, linewidth=1.25, linestyle=(0, (5, 4)), zorder=1)
        ax.text(1.58, target + top * 0.018, "target", ha="right", va="bottom", fontsize=8.2, color=ACCENT)
    ax.axhline(0, color=INK, linewidth=1.15, zorder=2)
    ax.text(0, -top * 0.055, "before", ha="center", va="top", fontsize=9.5, color=MUTED)
    ax.text(1, -top * 0.055, "now", ha="center", va="top", fontsize=9.5, color=MUTED)
    ax.text(0, before + top * 0.025, before_label, ha="center", va="bottom", fontsize=14, color=INK, fontweight="bold")
    ax.text(1, after + top * 0.025, after_label, ha="center", va="bottom", fontsize=14, color=AFTER, fontweight="bold")
    ax.annotate(
        change_label,
        xy=(1, after),
        xytext=(0.54, top * 0.48),
        textcoords="data",
        ha="center",
        va="center",
        fontsize=10,
        fontweight="bold",
        color=GOOD,
        arrowprops=dict(arrowstyle="->", color=GOOD, linewidth=1.4),
    )


def allocation_card(ax: plt.Axes, checks: int) -> None:
    card(ax, "Managed allocation", "steady paint path · not the whole app")
    ax.set_xlim(0, 1)
    ax.set_ylim(0, 1)
    ax.text(0.5, 0.56, "0 B", ha="center", va="center", fontsize=42, fontweight="bold", color=GOOD)
    ax.text(0.5, 0.36, "per steady paint frame", ha="center", va="center", fontsize=11.5, color=INK)
    ax.text(0.5, 0.22, f"{checks} headless checks passed", ha="center", va="center", fontsize=9.5, color=AFTER, fontweight="bold")
    ax.text(
        0.5,
        0.10,
        "Reconcile, loading, and app code can still allocate.",
        ha="center",
        va="center",
        fontsize=8.1,
        color=MUTED,
    )


def render_thread_card(ax: plt.Axes, before_min: float, before_max: float, after: float) -> None:
    card(ax, "UI-thread GPU wait", "the fence stall moved to the render thread")
    ax.set_xlim(0, 1)
    ax.set_ylim(0, 1)
    ax.text(
        0.5,
        0.60,
        f"{before_min:g}–{before_max:g} ms",
        ha="center",
        va="center",
        fontsize=27,
        fontweight="bold",
        color=INK,
    )
    ax.annotate(
        "",
        xy=(0.68, 0.42),
        xytext=(0.32, 0.42),
        arrowprops=dict(arrowstyle="->", color=GOOD, linewidth=1.8),
    )
    ax.text(0.5, 0.29, f"UI-loop submit: {after:.1f} ms", ha="center", va="center", fontsize=13.5, color=GOOD, fontweight="bold")
    ax.text(0.5, 0.14, "GPU work still happens—just not on the UI loop.", ha="center", va="center", fontsize=8.1, color=MUTED)


def soak_card(ax: plt.Axes, minutes: int, rounds: int, device_losses: int) -> None:
    card(ax, "Resize + scroll soak", "real D3D12 window · async renderer")
    ax.set_xlim(0, 1)
    ax.set_ylim(0, 1)
    ax.text(0.5, 0.60, f"{rounds} rounds", ha="center", va="center", fontsize=28, fontweight="bold", color=INK)
    ax.text(0.5, 0.40, f"{device_losses} device losses", ha="center", va="center", fontsize=16, color=GOOD, fontweight="bold")
    ax.text(0.5, 0.22, f"{minutes}-minute validation run", ha="center", va="center", fontsize=10, color=MUTED)


def main() -> None:
    data = json.loads(DATA_PATH.read_text(encoding="utf-8"))
    scroll = data["scrollCadence"]
    async_render = data["asyncRender"]
    allocation = data["allocation"]
    soak = data["soak"]

    mpl.rcParams.update(
        {
            "figure.facecolor": PAPER,
            "axes.facecolor": PAPER,
            "savefig.facecolor": PAPER,
            "text.color": INK,
            "svg.fonttype": "none",
            "svg.hashsalt": "wavee-fluentgpu-progress-v1",
        }
    )
    # Native Matplotlib XKCD styling keeps the values and chart construction
    # reproducible while applying a deterministic hand-drawn path treatment.
    plt.xkcd(scale=0.58, length=96, randomness=1.15)
    mpl.rcParams["font.family"] = "Comic Sans MS"

    fig = plt.figure(figsize=(15.5, 8.8), dpi=160)
    grid = fig.add_gridspec(2, 3, left=0.045, right=0.955, top=0.79, bottom=0.15, wspace=0.10, hspace=0.18)

    fig.text(0.055, 0.935, "Wavee, frame by frame", fontsize=30, fontweight="bold", color=INK, va="top")
    fig.text(
        0.055,
        0.875,
        "FluentGPU progress · measured, scoped, and reproducible",
        fontsize=13,
        color=MUTED,
        va="top",
    )
    fig.add_artist(mpl.lines.Line2D([0.055, 0.945], [0.835, 0.835], color=ACCENT, linewidth=3))

    comparison(
        fig.add_subplot(grid[0, 0]),
        "Presented sample-time spread",
        "matched 120 Hz runs · lower is better",
        scroll["sampleSpreadBeforeMs"],
        scroll["sampleSpreadAfterMs"],
        f'{scroll["sampleSpreadBeforeMs"]:g} ms',
        f'{scroll["sampleSpreadAfterMs"]:g} ms',
        "−77%",
    )
    comparison(
        fig.add_subplot(grid[0, 1]),
        "Frames inside the timing mode",
        "within 1 ms · higher is better",
        scroll["framesInModeBeforePct"],
        scroll["framesInModeAfterPct"],
        f'~{scroll["framesInModeBeforePct"]:.0f}%',
        f'~{scroll["framesInModeAfterPct"]:.0f}%',
        "3.7×",
    )
    comparison(
        fig.add_subplot(grid[0, 2]),
        "Publishes per present",
        "target = exactly one",
        scroll["publishesPerPresentBefore"],
        scroll["publishesPerPresentAfter"],
        f'{scroll["publishesPerPresentBefore"]:.2f}',
        f'{scroll["publishesPerPresentAfter"]:.2f}',
        "phase-locked",
        target=1.0,
    )
    render_thread_card(
        fig.add_subplot(grid[1, 0]),
        async_render["uiThreadFenceWaitBeforeMinMs"],
        async_render["uiThreadFenceWaitBeforeMaxMs"],
        async_render["uiLoopSubmitAfterMs"],
    )
    allocation_card(fig.add_subplot(grid[1, 1]), allocation["headlessChecks"])
    soak_card(
        fig.add_subplot(grid[1, 2]),
        soak["durationMinutes"],
        soak["rounds"],
        soak["deviceLosses"],
    )

    fig.text(
        0.055,
        0.075,
        f'Timing: {scroll["pairedRuns"]} paired runs, {data["measuredAt"]} · Allocation: FluentGpu.VerticalSlice full suite',
        fontsize=9.5,
        color=MUTED,
    )
    fig.text(
        0.945,
        0.075,
        "Exact sources and scope: benchmark-data/fluentgpu-progress.json",
        fontsize=9.5,
        color=MUTED,
        ha="right",
    )

    for output in OUTPUTS:
        output.parent.mkdir(parents=True, exist_ok=True)
        fig.savefig(
            output,
            bbox_inches="tight",
            pad_inches=0.22,
            metadata={"Creator": "Wavee Matplotlib benchmark chart", "Date": "2026-07-25"},
        )
        if output.suffix == ".svg":
            svg = output.read_text(encoding="utf-8")
            with output.open("w", encoding="utf-8", newline="\n") as stream:
                stream.write("\n".join(line.rstrip() for line in svg.splitlines()) + "\n")
        print(output)
    plt.close(fig)


if __name__ == "__main__":
    main()
