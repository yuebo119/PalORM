#!/usr/bin/env bash
# PalORM 性能基准标准运行脚本
# 用法：
#   bash scripts/run-benchmarks.sh [sqlite|pg|mysql|all|scale|build|speed]
#   bash scripts/run-benchmarks.sh sqlite --save-baseline  # 保存基线 JSON
#   bash scripts/run-benchmarks.sh sqlite --compare v4.0   # 与基线对比
#
# 配置层级：
#   快速（默认）：launchCount=1, warmupCount=3, iterationCount=5
#   严格（SqlBuild/Speed）：5/10/15
#
# 输出：
#   BenchmarkDotNet.Artifacts/（仓库根；JSON + MD 报告）
#   bench/baselines/（基线 JSON，入 git）
#   控制台统计有效性检查（Error/Mean 比值）

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BENCH_DIR="$ROOT_DIR/bench/PalORM.Benchmarks"
# BDN 把报告写在**仓库根**（dotnet run --project 的 CWD 是仓库根），不在基准项目目录下
ARTIFACTS_DIR="$ROOT_DIR/BenchmarkDotNet.Artifacts"
BASELINE_DIR="$ROOT_DIR/bench/baselines"
TARGET="${1:-sqlite}"
EXTRA="${2:-}"

echo "═══════════════════════════════════════════════════════════════"
echo " PalORM 性能基准运行器"
echo " 目标: $TARGET"
echo " 时间: $(date)"
echo "═══════════════════════════════════════════════════════════════"

# 构建
echo ">>> 构建 Release..."
dotnet build "$BENCH_DIR/PalORM.Benchmarks.csproj" -c Release --nologo 2>&1 | tail -3

# 运行
case "$TARGET" in
  sqlite)
    echo ">>> 运行 SQLite 基准（CRUD + Bulk + Transaction + Advanced）..."
    dotnet run --project "$BENCH_DIR" -c Release --no-build -- \
      --filter '*CrudBenchmarks*' '*BulkBenchmarksFixed*' '*BulkBenchmarks*' \
               '*GcBenchmarks*' '*SqlBuildBenchmarks*' '*SqliteSpeedBenchmarks*' \
               '*FeatureBenchmarks*' '*OrmComparisonBenchmarks*' '*BinaryBenchmarks*' \
      --exporters json 2>&1 | tee /tmp/bench-sqlite-$(date +%Y%m%d-%H%M%S).log
    ;;
  scale)
    echo ">>> 运行 BulkInsert 拐点扫描（100/1K/10K/100K）..."
    dotnet run --project "$BENCH_DIR" -c Release --no-build -- \
      --filter '*BulkBenchmarks*' 2>&1 | tee /tmp/bench-scale-$(date +%Y%m%d-%H%M%S).log
    ;;
  build)
    echo ">>> 运行 SQL 构建微基准（严格配置 5/10/15）..."
    dotnet run --project "$BENCH_DIR" -c Release --no-build -- \
      --filter '*SqlBuildBenchmarks*' --exporters json 2>&1 | tee /tmp/bench-build-$(date +%Y%m%d-%H%M%S).log
    ;;
  pg)
    if [ -z "${PALORM_BENCH_PG:-}" ]; then
      echo "ERROR: 设置 PALORM_BENCH_PG 环境变量"
      echo '  export PALORM_BENCH_PG="Host=...;Port=5432;Username=...;Password=...;Database=palorm_bench"'
      exit 1
    fi
    echo ">>> 运行 PostgreSQL 基准..."
    dotnet run --project "$BENCH_DIR" -c Release --no-build -- \
      --filter '*PgBenchmarks*' 2>&1 | tee /tmp/bench-pg-$(date +%Y%m%d-%H%M%S).log
    ;;
  mysql)
    if [ -z "${PALORM_BENCH_MYSQL:-}" ]; then
      echo "ERROR: 设置 PALORM_BENCH_MYSQL 环境变量"
      echo '  export PALORM_BENCH_MYSQL="Server=...;Port=3306;User ID=...;Password=...;Database=palorm_bench"'
      exit 1
    fi
    echo ">>> 运行 MySQL 基准..."
    dotnet run --project "$BENCH_DIR" -c Release --no-build -- \
      --filter '*MySqlBenchmarks*' 2>&1 | tee /tmp/bench-mysql-$(date +%Y%m%d-%H%M%S).log
    ;;
  all)
    echo ">>> 运行全部 SQLite + Scale + Build + Speed 基准（约 30 分钟）..."
    dotnet run --project "$BENCH_DIR" -c Release --no-build -- \
      --filter '*' 2>&1 | tee /tmp/bench-all-$(date +%Y%m%d-%H%M%S).log
    ;;
  speed)
    echo ">>> 运行纯速度基准（无 MemoryDiagnoser 交叉验证）..."
    dotnet run --project "$BENCH_DIR" -c Release --no-build -- \
      --filter '*SqliteSpeedBenchmarks*' 2>&1 | tee /tmp/bench-speed-$(date +%Y%m%d-%H%M%S).log
    ;;
  *)
    echo "用法: bash scripts/run-benchmarks.sh [sqlite|pg|mysql|scale|build|speed|all]"
    echo "  追加 --save-baseline 保存基线 JSON"
    echo "  追加 --compare <version> 与已有基线对比"
    exit 1
    ;;
esac

# 后处理：基线保存 / 对比
if [ "$EXTRA" = "--save-baseline" ]; then
  TIMESTAMP=$(date +%Y%m%d-%H%M%S)
  BASELINE_FILE="$BASELINE_DIR/snapshot-$TIMESTAMP.json"
  mkdir -p "$BASELINE_DIR"

  # 从 BenchmarkDotNet 的 JSON 报告生成基线（与 perf-gate 同一数据源，避免 CSV 解析漂移）。
  # 说明：原实现读 CSV 并以 `tail | while` 拼接 JSON——管道使 while 在子 shell 中执行，
  # FIRST 标志无法跨迭代保持，条目之间不输出逗号，生成的是非法 JSON。
  JSON_DIR="$ARTIFACTS_DIR/results"
  mapfile -t JSONS < <(ls "$JSON_DIR"/*-report*.json 2>/dev/null || true)
  if [ "${#JSONS[@]}" -gt 0 ]; then
    dotnet run --project "$ROOT_DIR/tools/PalORM.PerfGate" -c Release -- \
      record --results "$JSON_DIR" --out "$BASELINE_FILE" \
        --version "$(git -C "$ROOT_DIR" describe --tags --abbrev=0 2>/dev/null || echo dev)" \
        --date "$(date +%Y-%m-%d)" \
        --scope "run-benchmarks.sh $TARGET" \
      || { echo "❌ 基线生成失败"; exit 1; }
    echo "✅ 基线已保存: $BASELINE_FILE"
  else
    echo "⚠ 未找到 BDN JSON 报告，跳过基线保存"
  fi
fi

if [[ "$EXTRA" == --compare* ]]; then
  VERSION="${EXTRA#--compare }"
  VERSION="${VERSION// /}"
  BASELINE_FILE="$BASELINE_DIR/$VERSION.json"
  if [ -f "$BASELINE_FILE" ]; then
    echo ""
    echo "═══════════════════════════════════════════════════════════════"
    echo " 基线对比: 当前运行 vs $VERSION"
    echo "═══════════════════════════════════════════════════════════════"
    echo "（对比功能需人工或后续脚本自动化——当前输出基线 JSON 供 diff 工具使用）"
    echo "基线文件: $BASELINE_FILE"
  else
    echo "⚠ 基线 $VERSION 不存在，可用版本:"
    ls "$BASELINE_DIR"/*.json 2>/dev/null | xargs -I{} basename {} .json || echo "  (无)"
  fi
fi

echo ""
echo "═══════════════════════════════════════════════════════════════"
echo " 基准运行完成"
echo " 报告: $ARTIFACTS_DIR/results/"
echo "═══════════════════════════════════════════════════════════════"
