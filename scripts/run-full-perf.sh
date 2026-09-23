#!/usr/bin/env bash
# PalORM 一键全量性能测评（依据 docs/性能基准规范.md）
#
# 角色：**微基准侧的编排步骤**，由统一入口 `bash scripts/perf.sh full` 的第 1 步调用
#（该调用带 SKIP_REPORT=1，报告由编排层在三套夹具都写完结果库之后统一生成）。
# 单独跑只在调试本脚本自身时用；用户入口是 perf.sh。
#
# 用法：
#   bash scripts/run-full-perf.sh            # 本机全量（约 8-10 分钟）
#   WITH_REMOTE=1 bash scripts/run-full-perf.sh   # 追加远程真库批量档（需 .env.test）
#
# 步骤：构建 → 负载×2 → 内存曲线 → 启动量具 → BDN 微基准 → 门禁判定 + markdown 报告
# 报告输出：bench/reports/perf-report-<时间戳>.md（bench/reports/ 已 gitignore）

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BENCH_DIR="$ROOT_DIR/bench/PalORM.Benchmarks"
GATE="tools/PalORM.PerfGate"
RESULTS_DIR="$ROOT_DIR/BenchmarkDotNet.Artifacts/results"
REPORT_DIR="$ROOT_DIR/bench/reports"
STAMP="$(date +%Y%m%d-%H%M%S)"
REPORT_MD="$REPORT_DIR/perf-report-$STAMP.md"
LOG_DIR="$ROOT_DIR/bench/reports/logs-$STAMP"
mkdir -p "$REPORT_DIR" "$LOG_DIR"

step() { echo ""; echo "═══════════════════════════════════════════"; echo " $1"; echo "═══════════════════════════════════════════"; }

step "[0/6] 构建（Release）"
dotnet build "$BENCH_DIR/PalORM.Benchmarks.csproj" -c Release --nologo 2>&1 | tail -2
dotnet build "$ROOT_DIR/$GATE/PalORM.PerfGate.csproj" -c Release --nologo 2>&1 | tail -2

step "[1/6] 负载测试 第 1 轮（维度 3/4/11）"
dotnet run --project "$BENCH_DIR" -c Release --no-build -- --workload 2>&1 | tee "$LOG_DIR/workload-run1.log" | grep -E "threads|种子"
step "[1/6] 负载测试 第 2 轮（运行间方差对照）"
dotnet run --project "$BENCH_DIR" -c Release --no-build -- --workload 2>&1 | tee "$LOG_DIR/workload-run2.log" | grep -E "threads"
WORKLOAD_JSON="$(ls -t "$BENCH_DIR/bin/Release/net11.0/"workload-sqlite-*.json | head -1)"

step "[2/6] 大结果集内存 + 构建分配（维度 1/7）"
dotnet run --project "$BENCH_DIR" -c Release --no-build -- --memory 2>&1 | tee "$LOG_DIR/memory.log" | grep -E "memory"
MEMORY_JSON="$(ls -t "$BENCH_DIR/bin/Release/net11.0/"memory-sqlite.json | head -1)"

step "[3/6] 启动量具（维度 9：单方法 IL 上界，两维）"
STARTUP="fail"
if dotnet run --project "$ROOT_DIR/test/PalORM.SourceGen.Tests" -c Release --no-build -- \
  --treenode-filter "/*/*/RegistryScaleTests/*" 2>&1 | grep -q "成功: 2"; then
  STARTUP="ok"
fi
echo "启动量具: $STARTUP"
# 落盘供编排层（perf.sh report）在生成统一报告时取用——统一报告在全部夹具之后才生成，
# 不能依赖本脚本的进程内变量
printf '%s' "$STARTUP" > "$REPORT_DIR/last-startup-status.txt"

step "[4/6] BDN 微基准（维度 1，与门禁同参 1/3/5，约 5 分钟）"
# 先清结果目录——混入陈旧报告会让门禁读到截断/异构 JSON（实测先例）
rm -rf "$ROOT_DIR/BenchmarkDotNet.Artifacts"
# PALORM_BENCH_LABEL：显式声明这是"门禁同参集"（可复现的操作性子集，与临时单基准跑区分开）
PALORM_BENCH_LABEL=gate-set dotnet run --project "$BENCH_DIR" -c Release --no-build -- \
  --filter '*CrudBenchmarks*' '*OrmComparisonBenchmarks*' \
  --launchCount 1 --warmupCount 3 --iterationCount 5 --exporters json \
  2>&1 | tee "$LOG_DIR/bdn.log" | grep -E "Global total"

step "[5/6] 门禁判定"
dotnet run --project "$ROOT_DIR/$GATE" -c Release --no-build -- \
  check --results "$RESULTS_DIR" --baseline "$ROOT_DIR/bench/baselines/perf-baseline.json" \
  2>&1 | tee "$LOG_DIR/gate.log" | tail -3
GATE_EXIT=${PIPESTATUS[0]}

step "[6/6] 生成 markdown 报告"
# SKIP_REPORT=1：由编排层（perf.sh full）在**全部夹具跑完后**统一生成一份报告。
# 本脚本单独跑时报告只能含 BDN 明细 + 当时已有的结果库批次（PerfHub/DapperSuite 尚未跑）。
if [ "${SKIP_REPORT:-0}" = "1" ]; then
  echo "已跳过（SKIP_REPORT=1，报告由编排层在全部夹具完成后统一生成）"
else
set +e
dotnet run --project "$ROOT_DIR/$GATE" -c Release --no-build -- \
  report --results "$RESULTS_DIR" --baseline "$ROOT_DIR/bench/baselines/perf-baseline.json" \
  --workload "$WORKLOAD_JSON" --memory "$MEMORY_JSON" --startup "$STARTUP" \
  --envelopes "$ROOT_DIR/bench/results" \
  --index-baseline "$ROOT_DIR/bench/baselines/perfhub-index-baseline.json" \
  --out "$REPORT_MD"
set -e
fi

if [ "${WITH_REMOTE:-0}" = "1" ] && [ -f "$ROOT_DIR/.env.test" ]; then
  echo "（WITH_REMOTE=1：远程批量档由 .ai/perf-probe/RemoteBulk.cs 承担，属 gitignored 本地探针，不在本脚本内复刻）"
fi

echo ""
echo "═══════════════════════════════════════════"
echo " 全量测评完成"
# 报告行只在真生成了报告时打印——SKIP_REPORT=1 时 $REPORT_MD 是预留路径，
# 打印它会让人去找一个不存在的文件（实测：编排层跑完后横幅指向空文件）
if [ "${SKIP_REPORT:-0}" = "1" ]; then
  echo " 报告: 由编排层（perf.sh full 的第 5 步）在全部夹具完成后生成"
else
  echo " 报告: $REPORT_MD"
fi
echo " 日志: $LOG_DIR"
echo " 门禁: $([ "$GATE_EXIT" = "0" ] && echo 通过 || echo "存在回归（见 gate.log）")"
echo "═══════════════════════════════════════════"
