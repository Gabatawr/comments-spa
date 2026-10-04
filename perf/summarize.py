#!/usr/bin/env python3
"""perf/summarize.py — extract RPS / latency / error-rate tables from k6
summary JSON produced by perf/k6/scenarios.js (handleSummary).

Usage:
  python3 perf/summarize.py perf/results/summary-100k-full.json
  python3 perf/summarize.py perf/results/summary-*.json   # table per file
"""
import json
import sys
from pathlib import Path


def fmt_ms(v):
    return "n/a" if v is None else f"{float(v):.2f}ms"


def fmt_pct(v):
    return "n/a" if v is None else f"{float(v) * 100:.2f}%"


def scenario_metrics(metrics):
    """Collect per-scenario http_req_duration trends and http_reqs counters."""
    durations = {}
    for key, val in metrics.items():
        if not key.startswith("http_req_duration{"):
            continue
        tag = key[len("http_req_duration{") : -1]
        # keep only the scenario tag for the summary table
        if tag.startswith("scenario:"):
            durations[tag.split(":", 1)[1]] = val.get("values", {})
    return durations


def thresholds(metrics):
    rows = []
    for key, val in metrics.items():
        for t, res in (val.get("thresholds") or {}).items():
            # k6 >= 0.5x: {"ok": bool}; older: bool
            ok = res if isinstance(res, bool) else bool(res.get("ok")) if isinstance(res, dict) else bool(res)
            rows.append((key, t, ok))
    return rows


def summarize(path):
    data = json.loads(Path(path).read_text())
    metrics = data.get("metrics", {})
    state = data.get("state", {}) or {}
    dur_ms = state.get("testRunDurationMs")
    if dur_ms is None:
        # fall back to root group duration minus setup/teardown if present
        rg = data.get("root_group", {}) or {}
        dur_ms = rg.get("duration")

    reqs = metrics.get("http_reqs", {}).get("values", {})
    count = reqs.get("count")
    seconds = (dur_ms or 0) / 1000.0
    rps = (count / seconds) if (count and seconds) else None

    print(f"=== {path} ===")
    if dur_ms:
        print(f"run_duration        : {seconds:.2f}s")
    print(f"http_reqs           : {count}")
    print(f"RPS (whole run)     : {rps:.1f}" if rps else "RPS (whole run)     : n/a")

    g = metrics.get("http_req_duration", {}).get("values", {})
    print(
        f"http_req_duration   : avg={fmt_ms(g.get('avg'))} "
        f"p95={fmt_ms(g.get('p(95)'))} p99={fmt_ms(g.get('p(99)'))} max={fmt_ms(g.get('max'))}"
    )
    print(f"http_req_failed     : {fmt_pct(metrics.get('http_req_failed', {}).get('values', {}).get('rate'))}")
    print(f"checks              : {fmt_pct(metrics.get('checks', {}).get('values', {}).get('rate'))}")

    ws = metrics.get("ws_connect_ms", {}).get("values", {})
    if ws:
        print(f"ws_connect_ms       : p95={fmt_ms(ws.get('p(95)'))} max={fmt_ms(ws.get('max'))}")
    for nm in ("create_captcha_ms", "create_peek_ms", "create_post_ms"):
        v = metrics.get(nm, {}).get("values", {})
        if v:
            print(f"{nm:<20}: avg={fmt_ms(v.get('avg'))} p95={fmt_ms(v.get('p(95)'))} p99={fmt_ms(v.get('p(99)'))}")
    hello = metrics.get("ws_hello_received", {}).get("values", {}).get("count")
    if hello is not None:
        print(f"ws_hello_received   : {hello}")

    print("\n-- per scenario --")
    print(f"{'scenario':<12} {'p95':>10} {'p99':>10} {'avg':>10} {'max':>10}")
    for name in sorted(scenario_metrics(metrics)):
        v = scenario_metrics(metrics)[name]
        print(
            f"{name:<12} {fmt_ms(v.get('p(95)')):>10} {fmt_ms(v.get('p(99)')):>10} "
            f"{fmt_ms(v.get('avg')):>10} {fmt_ms(v.get('max')):>10}"
        )

    print("\n-- thresholds --")
    for key, t, ok in thresholds(metrics):
        print(f"[{'PASS' if ok else 'FAIL'}] {key}: {t}")

    for extra in ("create_ok", "create_fail", "search_hits", "ws_errors"):
        v = metrics.get(extra, {}).get("values", {}).get("count")
        if v is not None:
            print(f"{extra:<20}: {v}")
    print()


def main(argv):
    if not argv:
        print(__doc__)
        return 2
    for p in argv:
        summarize(p)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
