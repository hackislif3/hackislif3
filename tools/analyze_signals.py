#!/usr/bin/env python3
"""
Out-of-sample statistics for DrTraderOrderFlow signal logs (stdlib only).

Input : outcomes_*.csv written by the indicator (EnableCsvLog = true)
        columns: id,kind,bar_time,horizon_bars,mode,mfe_ticks,mae_ticks,close_ret_ticks
Usage : python3 tools/analyze_signals.py outcomes_GC.csv --split 2026-09-01 --horizon 10 [--signals signals_GC.csv]

What it reports, separately for IN-SAMPLE (bar_time < split) and OUT-OF-SAMPLE (>= split):
  * per signal kind: n, hit rate (close_ret > 0), mean / median return in ticks, mean MFE / MAE,
    95% bootstrap CI of the mean return
  * score calibration (if --signals is given): hit rate per score bucket. A score is only meaningful
    as a probability if this table is monotonic AND stable out of sample. Until then it is a ranking.
Nothing here is used by the indicator: outcomes are measured after the fact, never fed back.
"""
import argparse
import csv
import random
import statistics
from collections import defaultdict
from datetime import datetime


def parse_time(s):
    for fmt in ("%Y-%m-%d %H:%M:%S.%f", "%Y-%m-%d %H:%M:%S"):
        try:
            return datetime.strptime(s, fmt)
        except ValueError:
            pass
    raise ValueError("bad time: " + s)


def bootstrap_ci(values, n=2000, seed=7):
    if len(values) < 5:
        return (float("nan"), float("nan"))
    rnd = random.Random(seed)
    means = []
    for _ in range(n):
        sample = [values[rnd.randrange(len(values))] for _ in values]
        means.append(sum(sample) / len(sample))
    means.sort()
    return (means[int(0.025 * n)], means[int(0.975 * n)])


def summarize(rows, title):
    print("\n=== " + title + " ===")
    if not rows:
        print("  (no rows)")
        return
    by_kind = defaultdict(list)
    for r in rows:
        by_kind[r["kind"]].append(r)
    print("  {:<26} {:>5} {:>7} {:>8} {:>8} {:>7} {:>7}  {}".format("kind", "n", "hit%", "mean", "median", "MFE", "MAE", "95% CI mean"))
    for kind in sorted(by_kind):
        rs = by_kind[kind]
        rets = [r["ret"] for r in rs]
        hit = 100.0 * sum(1 for x in rets if x > 0) / len(rets)
        lo, hi = bootstrap_ci(rets)
        print("  {:<26} {:>5} {:>7.1f} {:>8.2f} {:>8.2f} {:>7.1f} {:>7.1f}  [{:.2f}, {:.2f}]".format(
            kind, len(rs), hit, statistics.mean(rets), statistics.median(rets),
            statistics.mean(r["mfe"] for r in rs), statistics.mean(r["mae"] for r in rs), lo, hi))


def calibration(rows, scores, title):
    print("\n--- score calibration: " + title + " ---")
    buckets = defaultdict(list)
    for r in rows:
        s = scores.get(r["id"])
        if s is None:
            continue
        b = int(s // 10) * 10
        buckets[b].append(r["ret"])
    if not buckets:
        print("  (no matching signal ids)")
        return
    for b in sorted(buckets):
        v = buckets[b]
        print("  score {:>3}-{:<3} n={:<5} hit={:5.1f}%  mean={:6.2f} ticks".format(
            b, b + 9, len(v), 100.0 * sum(1 for x in v if x > 0) / len(v), statistics.mean(v)))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("outcomes")
    ap.add_argument("--split", required=True, help="YYYY-MM-DD: first out-of-sample day")
    ap.add_argument("--horizon", type=int, default=10)
    ap.add_argument("--signals", help="signals_*.csv for score calibration")
    ap.add_argument("--mode", choices=["ALL", "HIST", "RT"], default="ALL")
    a = ap.parse_args()
    split = datetime.strptime(a.split, "%Y-%m-%d")

    rows = []
    with open(a.outcomes, newline="", encoding="utf-8-sig") as f:
        for r in csv.DictReader(f):
            if int(r["horizon_bars"]) != a.horizon:
                continue
            if a.mode != "ALL" and r["mode"] != a.mode:
                continue
            rows.append({"id": r["id"], "kind": r["kind"], "t": parse_time(r["bar_time"]),
                         "ret": float(r["close_ret_ticks"]), "mfe": float(r["mfe_ticks"]), "mae": float(r["mae_ticks"])})
    ins = [r for r in rows if r["t"] < split]
    oos = [r for r in rows if r["t"] >= split]
    print("horizon {} bars, mode {}, in-sample {} rows, out-of-sample {} rows".format(a.horizon, a.mode, len(ins), len(oos)))
    summarize(ins, "IN-SAMPLE (tune parameters here only)")
    summarize(oos, "OUT-OF-SAMPLE (do not tune on this)")

    if a.signals:
        scores = {}
        with open(a.signals, newline="", encoding="utf-8-sig") as f:
            for r in csv.DictReader(f):
                scores[r["id"]] = float(r["score"])
        calibration(ins, scores, "in-sample")
        calibration(oos, scores, "out-of-sample")


if __name__ == "__main__":
    main()
