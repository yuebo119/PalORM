#!/usr/bin/env python3
"""PalORM 性能基线记录与回归检查。

单一实现供两处调用（避免两套解析逻辑漂移）：
  - .github/workflows/perf-gate.yml  → check（CI 门禁）
  - scripts/run-benchmarks.sh        → record（本地重录基线）

为什么用 BDN 的 JSON 而非控制台表格/CSV：
  控制台表格是格式化文本，列宽/单位随 BenchmarkDotNet 版本与结果量级变化
  （原门禁用 `grep 'PalORM_QueryAll' | grep 'ms |'` 抓中位数，单位一变就恒抓不到，
  而"抓不到"又被当成 warning 放行 → 门禁结构上不可能变红）。JSON 是稳定契约。

为什么阈值只设「分配字节数」与「相对 ADO.NET 比值」，不设绝对毫秒：
  实测同机两次运行 ADO.NET 地板从 6.97ms 漂到 10.0ms（43%），绝对毫秒做阈值必然误报。
  同一轮运行内 PalORM/ADO 的比值把机器差异约掉了，跨机器可比。

退出码：0 = 通过；1 = 回归或不可判定（两者都不放行）。
"""

from __future__ import annotations

import argparse
import glob
import json
import os
import sys

SCHEMA = 1
DEFAULT_ALLOCATED_PCT = 20.0
DEFAULT_RATIO_PCT = 10.0

# 门禁的哨兵：按"基准名的最后一段"判定，与根命名空间无关。
# FullName 形如 PalORM.Benchmarks.CrudBenchmarks.PalORM_QueryAll——对整串做前缀匹配
# 会永远失败（阳性对照实测：门禁恒红，同样致命）。
# 比值检查同时需要被测侧与手写对照侧，缺一侧则"不可判定"。
REQUIRED_LEAF_PREFIXES = ("PalORM_", "ADO_NET_")


def load_results(results_dir: str) -> tuple[dict[str, dict], dict]:
    """扫描 BDN 结果 JSON，返回 {FullName: {allocated_bytes, median_ns, mean_ns}} 与环境信息。

    同一基准可能在多个导出文件里出现（如同时导出 json 与 json-brief），
    后读到的覆盖先读到的；只要至少出现过一次即可。
    """
    files = sorted(glob.glob(os.path.join(results_dir, "**", "*-report*.json"), recursive=True))
    if not files:
        print(f"FATAL: {results_dir} 下未找到 BDN 结果 JSON（*-report*.json）", file=sys.stderr)
        sys.exit(1)

    benchmarks: dict[str, dict] = {}
    environment: dict = {}
    for path in files:
        try:
            with open(path, encoding="utf-8-sig") as handle:
                data = json.load(handle)
        except (OSError, json.JSONDecodeError) as exc:
            print(f"FATAL: 无法解析 {path}: {exc}", file=sys.stderr)
            sys.exit(1)

        if not environment and isinstance(data.get("HostEnvironmentInfo"), dict):
            environment = data["HostEnvironmentInfo"]

        for entry in data.get("Benchmarks", []):
            name = entry.get("FullName")
            memory = entry.get("Memory") or {}
            stats = entry.get("Statistics") or {}
            allocated = memory.get("BytesAllocatedPerOperation")
            if name is None or stats.get("Median") is None:
                print(f"FATAL: {path} 中的条目缺少 FullName/Statistics: {name!r}",
                      file=sys.stderr)
                sys.exit(1)
            if allocated is None:
                # 无 MemoryDiagnoser 的类（如 SqliteSpeedBenchmarks）不产出分配数据。
                # 跳过而非失败：覆盖度由「基线列了什么就必须出现什么」兜底——
                # 基线里有的基准本轮缺失会被判 FAIL，不会静默缩水。
                continue
            benchmarks[name] = {
                "allocated_bytes": float(allocated),
                "median_ns": float(stats["Median"]),
                "mean_ns": float(stats["Mean"]) if stats.get("Mean") is not None else None,
            }
    return benchmarks, environment


def cmd_record(args: argparse.Namespace) -> int:
    benchmarks, environment = load_results(args.results)
    relative = [name for name in benchmarks if name not in args.exclude]

    entries = []
    for name in sorted(relative):
        record = {
            "name": name,
            "allocated_bytes": round(benchmarks[name]["allocated_bytes"], 1),
            "median_ns": round(benchmarks[name]["median_ns"], 1),
        }
        # PalORM/X 形式的基准自动挂上与同轮 ADO.NET 的比值（若对照存在）
        peer = ratio_peer(name, benchmarks)
        if peer is not None:
            record["ratio_vs"] = peer
            record["ratio"] = round(benchmarks[name]["allocated_bytes"]
                                    / benchmarks[peer]["allocated_bytes"], 4)
        entries.append(record)

    baseline = {
        "schema": SCHEMA,
        "version": args.version,
        "date": args.date,
        "scope": args.scope,
        "environment": {
            "os": environment.get("OsVersion"),
            "processor": environment.get("ProcessorName"),
            "runtime": environment.get("RuntimeVersion"),
            "benchmarkdotnet": environment.get("BenchmarkDotNetVersion"),
        },
        "thresholds": {
            "allocated_pct": DEFAULT_ALLOCATED_PCT,
            "ratio_pct": DEFAULT_RATIO_PCT,
        },
        "notes": args.notes,
        "benchmarks": entries,
    }

    os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
    with open(args.out, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(baseline, handle, ensure_ascii=False, indent=2)
        handle.write("\n")

    print(f"已记录 {len(entries)} 项基准 → {args.out}")
    print(f"  其中带 ADO.NET 比值的 {sum(1 for e in entries if 'ratio' in e)} 项")
    return 0


def ratio_peer(name: str, benchmarks: dict[str, dict]) -> str | None:
    """给 PalORM_X 找同轮的手写对照：优先 ADO.NET，其次 Dapper。

    首选手写 ADO.NET 是因为它没有 ORM 层的可变因素，是"ORM 税"的干净基线；
    Dapper 作为回退用于 OrmComparison 组（那一组的设计对照就是 Dapper 的
    IL 缓存 miss，没有 ADO.NET 对手）。
    """
    head, _, leaf = name.rpartition(".")
    if not leaf.startswith("PalORM_"):
        return None
    suffix = leaf[len("PalORM_"):]
    for prefix in ("ADO_NET_", "Dapper_"):
        candidate = f"{head}.{prefix}{suffix}"
        if candidate in benchmarks:
            return candidate
    return None


def cmd_check(args: argparse.Namespace) -> int:
    with open(args.baseline, encoding="utf-8") as handle:
        baseline = json.load(handle)

    if baseline.get("schema") != SCHEMA:
        print(f"FATAL: 基线 schema={baseline.get('schema')} 与本脚本要求的 {SCHEMA} 不符",
              file=sys.stderr)
        return 1

    benchmarks, _ = load_results(args.results)

    # 哨兵：基准没真跑时立刻失败，而不是逐项报"缺失"（后者容易被误读成基线过期）
    leaves = [name.rpartition(".")[2] for name in benchmarks]
    for prefix in REQUIRED_LEAF_PREFIXES:
        if not any(leaf.startswith(prefix) for leaf in leaves):
            print(f"FATAL: 本轮结果里没有任何 {prefix}* 基准——基准未真正执行"
                  f"（共 {len(benchmarks)} 项）。检查 --filter 是否匹配到类名。", file=sys.stderr)
            return 1

    thresholds = baseline.get("thresholds", {})
    allocated_pct = float(thresholds.get("allocated_pct", DEFAULT_ALLOCATED_PCT))
    ratio_pct = float(thresholds.get("ratio_pct", DEFAULT_RATIO_PCT))

    print(f"基线: {args.baseline}（{baseline.get('version')} / {baseline.get('date')}）")
    print(f"阈值: 分配 +{allocated_pct:.0f}% · 比值 +{ratio_pct:.0f}%")
    print()
    print(f"{'基准':<46} {'分配 B/op':>14} {'vs 基线':>9} {'比值 vs ADO':>12} {'判定':>6}")
    print("-" * 96)

    failures: list[str] = []
    for entry in baseline.get("benchmarks", []):
        name = entry["name"]
        current = benchmarks.get(name)
        if current is None:
            failures.append(f"{name}: 本轮结果缺失（基线里有、本次没跑）")
            print(f"{name:<46} {'(缺失)':>14} {'-':>9} {'-':>12} {'FAIL':>6}")
            continue

        base_alloc = float(entry["allocated_bytes"])
        cur_alloc = current["allocated_bytes"]
        delta_pct = (cur_alloc / base_alloc - 1.0) * 100.0 if base_alloc else 0.0
        verdict = "OK"

        if cur_alloc > base_alloc * (1.0 + allocated_pct / 100.0):
            verdict = "FAIL"
            failures.append(
                f"{name}: 分配 {cur_alloc:.0f} B/op 超基线 {base_alloc:.0f} B/op 的 "
                f"+{allocated_pct:.0f}%（实测 +{delta_pct:.1f}%）")

        ratio_text = "-"
        if "ratio_vs" in entry and "ratio" in entry:
            peer = benchmarks.get(entry["ratio_vs"])
            if peer is None:
                verdict = "FAIL"
                failures.append(f"{name}: 比值对照 {entry['ratio_vs']} 本轮缺失，无法判定")
            else:
                cur_ratio = cur_alloc / peer["allocated_bytes"] if peer["allocated_bytes"] else 0.0
                ratio_text = f"{cur_ratio:.3f} (基线 {float(entry['ratio']):.3f})"
                if cur_ratio > float(entry["ratio"]) * (1.0 + ratio_pct / 100.0):
                    verdict = "FAIL"
                    failures.append(
                        f"{name}: 相对 ADO.NET 比值 {cur_ratio:.3f} 超基线 "
                        f"{float(entry['ratio']):.3f} 的 +{ratio_pct:.0f}%")

        print(f"{name:<46} {cur_alloc:>14.0f} {delta_pct:>8.1f}% {ratio_text:>12} {verdict:>6}")

    print()
    if failures:
        print(f"❌ 回归检查未通过（{len(failures)} 项）：")
        for line in failures:
            print(f"  - {line}")
        return 1

    print(f"✅ 通过：{len(baseline.get('benchmarks', []))} 项基准均在阈值内")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description="PalORM 性能基线记录与回归检查")
    sub = parser.add_subparsers(dest="command", required=True)

    record = sub.add_parser("record", help="从 BDN 结果 JSON 生成基线")
    record.add_argument("--results", required=True, help="BDN 结果目录（含 *-report*.json）")
    record.add_argument("--out", required=True, help="输出基线 JSON 路径")
    record.add_argument("--version", required=True)
    record.add_argument("--date", required=True)
    record.add_argument("--scope", default="")
    record.add_argument("--notes", default="")
    record.add_argument("--exclude", nargs="*", default=[],
                        help="不入基线的基准全名（需外部数据库的组等）")
    record.set_defaults(func=cmd_record)

    check = sub.add_parser("check", help="对照基线判定回归")
    check.add_argument("--results", required=True)
    check.add_argument("--baseline", required=True)
    check.set_defaults(func=cmd_check)

    args = parser.parse_args()
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
