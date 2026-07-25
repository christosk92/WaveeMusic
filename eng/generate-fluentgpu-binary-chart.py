"""Render the current on-disk Wavee NativeAOT measurement snapshot."""

import json
from pathlib import Path

import matplotlib as mpl
import matplotlib.pyplot as plt
from matplotlib.patches import FancyBboxPatch


ROOT = Path(__file__).resolve().parents[1]
DATA_PATH = ROOT / "benchmark-data" / "fluentgpu-binary-2026-07-26.json"
OUTPUTS = (
    ROOT / "assets" / "FluentGpuBinaryMeasurements.png",
    ROOT / "assets" / "FluentGpuBinaryMeasurements.svg",
)

INK = "#17243B"
MUTED = "#667085"
PAPER = "#FAF7F0"
PURPLE = "#7C5CFC"
ORANGE = "#FF8A4C"
GREEN = "#15866B"


def card(ax: plt.Axes, title: str, subtitle: str) -> None:
    ax.set_facecolor(PAPER)
    for spine in ax.spines.values():
        spine.set_visible(False)
    ax.tick_params(left=False, bottom=False, labelleft=False, labelbottom=False)
    ax.set_xlim(0, 1)
    ax.set_ylim(0, 1)
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
    ax.text(0.05, 0.92, title, transform=ax.transAxes, color=INK, fontsize=13, fontweight="bold", va="top")
    ax.text(0.05, 0.82, subtitle, transform=ax.transAxes, color=MUTED, fontsize=8.6, va="top")


def launch_card(ax: plt.Axes, data: dict) -> None:
    card(ax, "Window ready", "process start to main window handle")
    ax.text(0.5, 0.57, f'{data["warmP50Ms"]:.0f} ms', ha="center", va="center", fontsize=31, fontweight="bold", color=PURPLE)
    ax.text(0.5, 0.37, f'p50 · p95 {data["warmP95Ms"]:.0f} ms', ha="center", va="center", fontsize=13, color=INK)
    ax.text(0.5, 0.19, f'{data["validRuns"]}/{data["requestedRuns"]} launches opened a responding window', ha="center", va="center", fontsize=9, color=GREEN)


def cpu_card(ax: plt.Axes, data: dict) -> None:
    card(ax, "Loaded idle CPU", "fake Home · 30 s warm-up + 5 min sample")
    ax.text(0.5, 0.57, "<0.01%", ha="center", va="center", fontsize=34, fontweight="bold", color=GREEN)
    ax.text(0.5, 0.38, "of total 12-core capacity", ha="center", va="center", fontsize=12, color=INK)
    ax.text(0.5, 0.22, f'{data["cpuOneCoreEquivalentAveragePct"]:.3f}% of one core · {data["totalCpuTimeMs"]:.0f} ms CPU time', ha="center", va="center", fontsize=9.3, color=MUTED)


def stability_card(ax: plt.Axes, data: dict) -> None:
    card(ax, "Idle residency stayed flat", "process working set · first vs last 30 seconds")
    delta = data["workingSetFirstToLast30DeltaMiB"]
    sign = "+" if delta > 0 else ""
    ax.text(0.5, 0.58, f"{sign}{delta:.1f} MiB", ha="center", va="center", fontsize=31, fontweight="bold", color=GREEN)
    ax.text(0.5, 0.38, f'{data["workingSetAverageMiB"]:.1f} MiB average', ha="center", va="center", fontsize=13, color=INK)
    ax.text(0.5, 0.20, f'{data["workingSetMinMiB"]:.1f}–{data["workingSetMaxMiB"]:.1f} MiB across {data["samples"]} samples', ha="center", va="center", fontsize=9.2, color=MUTED)


def memory_card(ax: plt.Axes, data: dict) -> None:
    card(ax, "GPU-aware memory view", "UMA/iGPU · overlapping views, not additive buckets")
    ax.text(0.08, 0.61, f'{data["managedHeapMiB"]:.1f}', ha="left", va="center", fontsize=24, fontweight="bold", color=PURPLE)
    ax.text(0.08, 0.46, "MiB managed heap", ha="left", va="center", fontsize=9.2, color=MUTED)
    ax.text(0.50, 0.61, f'{data["trackedD3d12ResourcesMiB"]:.1f}', ha="left", va="center", fontsize=24, fontweight="bold", color=ORANGE)
    ax.text(0.50, 0.46, "MiB tracked D3D12", ha="left", va="center", fontsize=9.2, color=MUTED)
    ax.text(0.08, 0.26, f'{data["imageCacheUsedMiB"]:.1f} MiB images · {data["pixelPoolRetainedMiB"]:.0f} MiB pixel pool retained', ha="left", va="center", fontsize=9.3, color=INK)
    ax.text(0.08, 0.12, f'{data["processWorkingSetMiB"]:.1f} MiB process working set in this census', ha="left", va="center", fontsize=9.3, color=INK)


def binary_card(ax: plt.Axes, data: dict) -> None:
    card(ax, "NativeAOT executable", f'{data["architecture"]} · current unpackaged build')
    ax.text(0.5, 0.57, f'{data["mebibytes"]:.1f} MiB', ha="center", va="center", fontsize=33, fontweight="bold", color=PURPLE)
    ax.text(0.5, 0.37, "single native application executable", ha="center", va="center", fontsize=11.5, color=INK)
    ax.text(0.5, 0.20, "Not the final signed MSIX download or installed size.", ha="center", va="center", fontsize=8.8, color=MUTED)


def reliability_card(ax: plt.Axes, data: dict) -> None:
    card(ax, "Observed reliability", "these launch + idle captures")
    ax.text(
        0.5,
        0.60,
        f'{data["successfulWindowLaunches"]}/{data["attemptedWindowLaunches"]} launches',
        ha="center",
        va="center",
        fontsize=26,
        fontweight="bold",
        color=GREEN,
    )
    ax.text(
        0.5,
        0.39,
        f'{data["idleRespondingSamples"]}/{data["idleRequestedSamples"]} responding samples',
        ha="center",
        va="center",
        fontsize=13,
        color=INK,
    )
    ax.text(0.5, 0.20, "0 error-pattern matches in captured logs", ha="center", va="center", fontsize=9.2, color=MUTED)


def main() -> None:
    data = json.loads(DATA_PATH.read_text(encoding="utf-8-sig"))
    startup = data["startupToWindow"]
    idle = data["fiveMinuteIdle"]
    memory = data["gpuAwareIdleCensus"]
    binary = data["binary"]
    reliability = data["reliability"]
    environment = data["environment"]

    mpl.rcParams.update(
        {
            "figure.facecolor": PAPER,
            "axes.facecolor": PAPER,
            "savefig.facecolor": PAPER,
            "text.color": INK,
            "svg.fonttype": "none",
            "svg.hashsalt": "wavee-fluentgpu-binary-2026-07-26",
        }
    )
    plt.xkcd(scale=0.58, length=96, randomness=1.15)
    mpl.rcParams["font.family"] = "Comic Sans MS"

    fig = plt.figure(figsize=(15.5, 8.8), dpi=160)
    grid = fig.add_gridspec(2, 3, left=0.045, right=0.955, top=0.79, bottom=0.15, wspace=0.10, hspace=0.18)

    fig.text(0.055, 0.935, "The Wavee NativeAOT build, measured", fontsize=29, fontweight="bold", color=INK, va="top")
    fig.text(
        0.055,
        0.875,
        f'{environment["cpu"].split(" @")[0]} · {environment["displayHz"]} Hz · Windows build {environment["osBuild"]}',
        fontsize=12.5,
        color=MUTED,
        va="top",
    )
    fig.add_artist(mpl.lines.Line2D([0.055, 0.945], [0.835, 0.835], color=ORANGE, linewidth=3))

    launch_card(fig.add_subplot(grid[0, 0]), startup)
    cpu_card(fig.add_subplot(grid[0, 1]), idle)
    stability_card(fig.add_subplot(grid[0, 2]), idle)
    memory_card(fig.add_subplot(grid[1, 0]), memory)
    binary_card(fig.add_subplot(grid[1, 1]), binary)
    reliability_card(fig.add_subplot(grid[1, 2]), reliability)

    fig.text(
        0.055,
        0.075,
        "Current FluentGPU build only · fake Home · no playback · working set includes graphics residency on UMA",
        fontsize=9.2,
        color=MUTED,
    )
    fig.text(
        0.945,
        0.075,
        f'Build {binary["productVersion"].split("+")[-1][:8]} · exact data + raw CSVs in benchmark-data/',
        fontsize=9.2,
        color=MUTED,
        ha="right",
    )

    for output in OUTPUTS:
        output.parent.mkdir(parents=True, exist_ok=True)
        fig.savefig(
            output,
            bbox_inches="tight",
            pad_inches=0.22,
            metadata={"Creator": "Wavee Matplotlib binary measurement chart", "Date": "2026-07-26"},
        )
        if output.suffix == ".svg":
            svg = output.read_text(encoding="utf-8")
            with output.open("w", encoding="utf-8", newline="\n") as stream:
                stream.write("\n".join(line.rstrip() for line in svg.splitlines()) + "\n")
        print(output)
    plt.close(fig)


if __name__ == "__main__":
    main()
