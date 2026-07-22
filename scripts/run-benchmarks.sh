#!/usr/bin/env bash
# PalORM 性能基准标准运行脚本
# 用法：bash scripts/run-benchmarks.sh [sqlite|pg|mysql|all|scale|build]
#
# 配置层级：
#   快速（默认）：launchCount=1, warmupCount=3, iterationCount=5
#   严格（--strict）：SqlBuild 用 5/10/15，SqliteBenchmarks 用 3/5/10
#
# 输出：
#   bench/PalORM.Benchmarks/BenchmarkDotNet.Artifacts/（JSON + MD 报告）
#   控制台统计有效性检查（Error/Mean 比值）

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BENCH_DIR="$ROOT_DIR/bench/PalORM.Benchmarks"
TARGET="${1:-sqlite}"

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
      --filter '*SqliteBenchmarks*' 2>&1 | tee /tmp/bench-sqlite-$(date +%Y%m%d-%H%M%S).log
    ;;
  scale)
    echo ">>> 运行 BulkInsert 拐点扫描（100/1K/10K/100K）..."
    dotnet run --project "$BENCH_DIR" -c Release --no-build -- \
      --filter '*BulkInsertScaleBenchmarks*' 2>&1 | tee /tmp/bench-scale-$(date +%Y%m%d-%H%M%S).log
    ;;
  build)
    echo ">>> 运行 SQL 构建微基准（严格配置 5/10/15）..."
    dotnet run --project "$BENCH_DIR" -c Release --no-build -- \
      --filter '*SqlBuildBenchmarks*' 2>&1 | tee /tmp/bench-build-$(date +%Y%m%d-%H%M%S).log
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
    echo ">>> 运行全部 SQLite + Scale + Build 基准（约 30 分钟）..."
    dotnet run --project "$BENCH_DIR" -c Release --no-build -- \
      --filter '*' 2>&1 | tee /tmp/bench-all-$(date +%Y%m%d-%H%M%S).log
    ;;
  *)
    echo "用法: bash scripts/run-benchmarks.sh [sqlite|pg|mysql|scale|build|all]"
    exit 1
    ;;
esac

echo ""
echo "═══════════════════════════════════════════════════════════════"
echo " 基准运行完成"
echo " 报告: $BENCH_DIR/BenchmarkDotNet.Artifacts/results/"
echo "═══════════════════════════════════════════════════════════════"
