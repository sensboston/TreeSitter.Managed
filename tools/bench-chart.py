# Builds the benchmark chart and table of docs/BENCHMARKS.md from BenchmarkDotNet markdown
# logs of bench/TreeSitter.Managed.Benchmarks (RuntimeBenchmarks: the port and the native
# runtime in the same process).
#
#   python tools/bench-chart.py <out-dir> <label>=<log>[,<log>...] ...
#
# Several logs for one label are averaged (the ratio of each run). Needs matplotlib.
import sys
import os
import statistics
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt

OPS = [("parse", "Full parse"), ("reparse", "Edit + reparse"), ("query", "Query (highlights.scm)")]
GRAMMARS = [("c", "C"), ("json", "JSON"), ("python", "Python")]


def read_ratios(path):
    ratios = {}
    for line in open(path, encoding="utf-8", errors="ignore"):
        if not line.startswith("|"):
            continue
        cells = [c.strip() for c in line.strip().strip("|").split("|")]
        if len(cells) > 7 and cells[0].startswith("Managed"):
            ratios[(cells[1], cells[2])] = float(cells[6])
    return ratios


def main():
    out_dir = sys.argv[1]
    platforms = []
    for arg in sys.argv[2:]:
        label, logs = arg.split("=", 1)
        runs = [read_ratios(p) for p in logs.split(",")]
        avg = {k: statistics.mean(r[k] for r in runs if k in r) for k in runs[0]}
        platforms.append((label, avg))

    fig, axes = plt.subplots(1, len(OPS), figsize=(15, 4.8), sharey=True)
    width = 0.8 / len(platforms)
    for ax, (op, title) in zip(axes, OPS):
        for i, (label, avg) in enumerate(platforms):
            xs = [g + (i - (len(platforms) - 1) / 2) * width for g in range(len(GRAMMARS))]
            ax.bar(xs, [avg[(op, g)] for g, _ in GRAMMARS], width, label=label)
        ax.axhline(1.0, color="black", linewidth=1)
        ax.set_xticks(range(len(GRAMMARS)), [n for _, n in GRAMMARS])
        ax.set_title(title)
        ax.grid(axis="y", alpha=0.3)
    axes[0].set_ylabel("time of the port / time of native (1.0 = same speed)")
    handles, labels = axes[0].get_legend_handles_labels()
    fig.legend(handles, labels, loc="lower center", ncol=len(platforms), fontsize=9)
    fig.suptitle("TreeSitter.Managed against the native tree-sitter runtime (lower is better)")
    fig.tight_layout(rect=(0, 0.07, 1, 1))
    fig.savefig(os.path.join(out_dir, "benchmark-native.png"), dpi=110)

    print("| Operation | Grammar | " + " | ".join(l for l, _ in platforms) + " |")
    print("|---|---|" + "---:|" * len(platforms))
    for op, title in OPS:
        for g, name in GRAMMARS:
            print(f"| {title} | {name} | " + " | ".join(f"{avg[(op, g)]:.2f}" for _, avg in platforms) + " |")


main()
