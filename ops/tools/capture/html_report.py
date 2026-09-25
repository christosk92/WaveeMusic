"""ops/tools/capture/html_report.py — a self-contained HTML report (inline SVG timeline, light + dark, no external
deps) for one story or the whole segment's anomalies (§5's `--html` ask). Stdlib-only: string templating, no
Jinja/no CDN scripts/fonts — the report has to open from a bug-report .zip on an offline machine.
"""

from __future__ import annotations

import html
import os
import sys
from typing import Any, Dict, List, Optional

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import story as story_mod

_ANOMALY_COLOR = {
    story_mod.ANOMALY_ECHO_MISMATCH: "#e0a800",
    story_mod.ANOMALY_ECHO_MISSING: "#d9463f",
    story_mod.ANOMALY_UI_DISAGREES: "#e0a800",
    story_mod.ANOMALY_HTTP_ERROR: "#d9463f",
    story_mod.ANOMALY_DECODE_FAILED: "#d9463f",
    story_mod.ANOMALY_FRAME_IGNORED: "#8a8f98",
}

_PAGE_CSS = """
:root {
  --bg: #ffffff; --fg: #1b1f23; --muted: #6a737d; --line: #d0d7de; --accent: #2f6fed;
  --ok: #2ea043; --card-bg: #f6f8fa;
}
@media (prefers-color-scheme: dark) {
  :root { --bg: #0d1117; --fg: #e6edf3; --muted: #9198a1; --line: #30363d; --accent: #6ea8fe;
          --ok: #3fb950; --card-bg: #161b22; }
}
* { box-sizing: border-box; }
body { background: var(--bg); color: var(--fg); font: 13px/1.5 "Segoe UI", ui-sans-serif, system-ui, sans-serif;
       margin: 0; padding: 24px; }
h1 { font-size: 16px; margin: 0 0 4px 0; }
.sub { color: var(--muted); margin-bottom: 18px; }
.card { background: var(--card-bg); border: 1px solid var(--line); border-radius: 8px; padding: 16px; margin-bottom: 16px; }
table { border-collapse: collapse; width: 100%; }
th, td { text-align: left; padding: 6px 8px; border-bottom: 1px solid var(--line); font-variant-numeric: tabular-nums; }
th { color: var(--muted); font-weight: 600; }
tr.anomaly td { color: var(--fg); }
.dot { display: inline-block; width: 9px; height: 9px; border-radius: 50%; margin-right: 6px; }
svg text { fill: var(--fg); font: 12px "Segoe UI", ui-sans-serif, system-ui, sans-serif; }
svg line.guide { stroke: var(--line); stroke-width: 1; }
svg line.edge { stroke: var(--muted); stroke-width: 1; }
code { color: var(--muted); }
"""


def _row_color(anomaly: Optional[str]) -> str:
    if not anomaly:
        return "var(--ok)"
    return _ANOMALY_COLOR.get(anomaly, "#d9463f")


def render_story_svg(root: Dict[str, Any], records: List[Dict[str, Any]]) -> str:
    lines = story_mod.build_story(root, records)
    if not lines:
        return "<p>(empty story)</p>"
    row_h = 26
    left_pad = 24
    label_x = 320
    max_lat = max((l.latency_ms or 0) for l in lines) or 1
    timeline_w = 260
    width = label_x + 420
    height = row_h * len(lines) + 24

    def x_of(latency_ms: int) -> float:
        return left_pad + (latency_ms / max_lat) * timeline_w

    parts = [f'<svg viewBox="0 0 {width} {height}" width="{width}" height="{height}" xmlns="http://www.w3.org/2000/svg">']
    parts.append(f'<line class="guide" x1="{x_of(0)}" y1="8" x2="{x_of(0)}" y2="{height - 8}" />')
    for i, line in enumerate(lines):
        y = 16 + i * row_h
        cx = x_of(line.latency_ms or 0)
        color = _row_color(line.anomaly)
        kind = line.record["kind"] if isinstance(line.record["kind"], str) else str(line.record["kind"])
        detail_bits = [kind]
        if line.record.get("a"):
            detail_bits.append(html.escape(str(line.record["a"])))
        if line.matched_end is not None and line.matched_end.get("n0"):
            detail_bits.append(f"→ {line.matched_end['n0']}")
        if line.label:
            detail_bits.append(f"({line.label})")
        label = " ".join(detail_bits)
        lat_text = f"+{line.latency_ms}ms" if i > 0 else "t0"
        parts.append(f'<circle cx="{cx:.1f}" cy="{y}" r="5" fill="{color}" />')
        parts.append(f'<text x="{label_x}" y="{y + 4}">{" " * line.depth * 2}{html.escape(label)}</text>')
        parts.append(f'<text x="{label_x + 340}" y="{y + 4}" text-anchor="end"><code>{lat_text}</code></text>')
        if line.anomaly:
            parts.append(f'<text x="{cx + 10:.1f}" y="{y + 4}">⚠</text>')
    parts.append("</svg>")
    return "".join(parts)


def render_story_html(root: Dict[str, Any], records: List[Dict[str, Any]], title: str = "Capture story") -> str:
    svg = render_story_svg(root, records)
    text = story_mod.render_story_text(root, records)
    kind = root["kind"] if isinstance(root["kind"], str) else str(root["kind"])
    return f"""<!doctype html>
<html><head><meta charset="utf-8"><title>{html.escape(title)}</title>
<style>{_PAGE_CSS}</style></head>
<body>
<h1>{html.escape(title)}</h1>
<div class="sub">root #{root['id']} &middot; {html.escape(kind)} &middot; {html.escape(str(root.get('a') or ''))}</div>
<div class="card">{svg}</div>
<div class="card"><pre>{html.escape(text)}</pre></div>
</body></html>"""


def render_anomalies_html(anomalies: List[story_mod.Anomaly], title: str = "Capture anomalies") -> str:
    rows = []
    for a in anomalies:
        color = _row_color(a.kind)
        rows.append(
            f'<tr class="anomaly"><td><span class="dot" style="background:{color}"></span>{html.escape(a.kind)}</td>'
            f'<td>{a.unix_ms}</td><td>{a.root_id}</td><td>{html.escape(a.detail)}</td></tr>'
        )
    body = "\n".join(rows) if rows else '<tr><td colspan="4">No anomalies.</td></tr>'
    return f"""<!doctype html>
<html><head><meta charset="utf-8"><title>{html.escape(title)}</title>
<style>{_PAGE_CSS}</style></head>
<body>
<h1>{html.escape(title)}</h1>
<div class="sub">{len(anomalies)} anomalies, newest first</div>
<div class="card">
<table><thead><tr><th>Kind</th><th>unix_ms</th><th>root</th><th>Detail</th></tr></thead>
<tbody>{body}</tbody></table>
</div>
</body></html>"""
