#!/usr/bin/env bash
# PalORM 性能测评统一入口（docs/性能基准规范.md v2 §5）。
#
# 用法:
#   bash scripts/perf.sh smoke                    # 三套夹具最小档冒烟（SQLite，约 5 分钟）
#   bash scripts/perf.sh full                     # 全量（约 30-40 分钟，含三方言）
#   bash scripts/perf.sh compare <基线worktree> <轮数> [选项]   # 交替 A/B（转发 perfhub-ab.sh）
#   bash scripts/perf.sh gate                     # 只跑门禁（BDN 基线 + 结果库基线）
#   bash scripts/perf.sh report                   # 只从结果库重建索引报告
#
# 设计：本脚本只做编排，不改任何夹具的口径——每步都调用该夹具自己的既有入口，
# 保证"从统一入口跑"与"单独跑某套夹具"得到同样的结果。

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GATE_PROJECT="$ROOT_DIR/tools/PalORM.PerfGate"
BDN_RESULTS="$ROOT_DIR/BenchmarkDotNet.Artifacts/results"
BASELINE_BDN="$ROOT_DIR/bench/baselines/perf-baseline.json"
BASELINE_INDEX="$ROOT_DIR/bench/baselines/perfhub-index-baseline.json"

usage() {
    cat <<'EOF'
用法:
  bash scripts/perf.sh smoke                    # 三套夹具最小档冒烟（SQLite，约 5 分钟）
  bash scripts/perf.sh full                     # 全量（约 30-40 分钟，含三方言）
  bash scripts/perf.sh compare <基线worktree> <轮数> [选项]   # 交替 A/B（转发 perfhub-ab.sh）
  bash scripts/perf.sh gate                     # 只跑门禁（BDN 基线 + 结果库基线）
  bash scripts/perf.sh report                   # 只从结果库重建索引报告

设计：本脚本只做编排，不改任何夹具的口径——每步都调用该夹具自己的既有入口，
保证"从统一入口跑"与"单独跑某套夹具"得到同样的结果。
EOF
}

step() {
    echo ""
    echo "═══════════════════════════════════════════"
    echo " $1"
    echo "═══════════════════════════════════════════"
}

gate() {
    step "[门禁 1/2] 微基准 BDN 基线"
    if [ -f "$BASELINE_BDN" ]; then
        dotnet run --project "$GATE_PROJECT" -c Release -- \
            check --results "$BDN_RESULTS" --baseline "$BASELINE_BDN"
    else
        echo "跳过：缺 $BASELINE_BDN"
    fi

    step "[门禁 2/2] 结果库基线（PerfHub 能力矩阵 + DapperSuite 哨兵）"
    if [ -f "$BASELINE_INDEX" ]; then
        dotnet run --project "$GATE_PROJECT" -c Release -- \
            check-index --baseline "$BASELINE_INDEX"
    else
        echo "跳过：缺 $BASELINE_INDEX（先跑 record-index 录制）"
    fi
}

index() {
    step "[索引] 从结果库重建 bench/reports/perf-index.md"
    dotnet run --project "$GATE_PROJECT" -c Release -- index
}

cmd="${1:-}"
if [ -n "$cmd" ]; then shift; fi

case "$cmd" in
    smoke)
        step "[1/4] 微基准冒烟（单点查询，BDN）"
        dotnet run --project "$ROOT_DIR/bench/PalORM.Benchmarks" -c Release -- \
            --filter '*ADO_NET_GetByKey*'

        step "[2/4] PerfHub 冒烟（SQLite 2000 档，quick）"
        dotnet run --project "$ROOT_DIR/bench/PalORM.PerfHub" -c Release -- \
            run --dialects sqlite --tiers 2000 --quick

        step "[3/4] DapperSuite 冒烟（SQLite 单行，官方形状）"
        DAPPER_SUITE_DIALECT=sqlite dotnet run --project "$ROOT_DIR/bench/PalORM.DapperSuite" -c Release -- \
            --filter '*SqlCommand*' --join

        step "[4/4] 索引报告"
        index
        ;;
    full)
        step "[1/5] 微基准全量 + 负载 + 内存 + 启动 + BDN 门禁"
        bash "$ROOT_DIR/scripts/run-full-perf.sh"

        step "[2/5] PerfHub 三方言全量"
        dotnet run --project "$ROOT_DIR/bench/PalORM.PerfHub" -c Release -- \
            run --dialects sqlite,mysql,pg --tiers 2000,20000

        step "[3/5] DapperSuite 三方言（官方形状锚点）"
        bash "$ROOT_DIR/scripts/dappersuite-run.sh"

        step "[4/5] 门禁（BDN 基线 + 结果库基线）"
        gate

        step "[5/5] 索引报告"
        index
        ;;
    compare)
        # 交替 A/B 是跨版本对比的唯一可信方式（规范 §5），直接转发编排器
        bash "$ROOT_DIR/scripts/perfhub-ab.sh" "$@"
        ;;
    gate)
        gate
        ;;
    report)
        index
        ;;
    *)
        usage
        exit 1
        ;;
esac
