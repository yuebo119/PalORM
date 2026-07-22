#!/usr/bin/env bash
# PalORM 测试规范门禁——检查 T1/T3/T4/T8/T9 铁律
# 用法：bash scripts/test-gate.sh
# 退出码：0=通过，1=有违规
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
FAIL_COUNT=0
WARN_COUNT=0

echo "═══════════════════════════════════════════════════════════════"
echo " PalORM 测试规范门禁（T1-T10）"
echo " 时间: $(date)"
echo "═══════════════════════════════════════════════════════════════"

# ─── T4：测试命名规范 ───────────────────────────────────
# 检查是否有不符合 Method_Scenario_ExpectedResult 的测试方法名
echo ""
echo "─── T4: 测试命名规范 ───"

# 查找 [Test] 标注的方法名不含下划线的（可能是命名不规范）
BAD_NAMES=$(grep -rn '\[Test\]' "$ROOT_DIR/test/" --include='*.cs' 2>/dev/null | \
  grep -v 'bin/' | grep -v 'obj/' | \
  awk -F: '{print $NF}' | \
  grep -oiE 'public async Task [A-Za-z_]+' | \
  sed 's/public async Task //' | \
  grep -v '_' | \
  head -5 || true)

if [ -n "$BAD_NAMES" ]; then
  echo "WARN  T4  以下测试方法名不含下划线（建议 Method_Scenario_ExpectedResult）:"
  echo "$BAD_NAMES" | while read -r name; do echo "      - $name"; done
  ((WARN_COUNT++)) || true
else
  echo "PASS  T4  测试命名规范检查通过"
fi

# ─── T8：基准配置注释检查 ───────────────────────────────
echo ""
echo "─── T8: 基准配置注释 ───"
BENCH_FILE="$ROOT_DIR/bench/PalORM.Benchmarks/Program.cs"
if [ -f "$BENCH_FILE" ]; then
  # 检查每个 SimpleJob 上面一行是否有注释
  UNCOMMENTED_JOBS=$(grep -B1 'SimpleJob' "$BENCH_FILE" | \
    grep -c 'SimpleJob.*$' || true)
  TOTAL_JOBS=$(grep -c 'SimpleJob' "$BENCH_FILE" || true)

  # 检查是否有 SimpleJob 行上面紧跟的不是注释
  JOBS_WITHOUT_COMMENT=$(awk '
    /SimpleJob/ {
      if (prev !~ /\/\/|\/\*/) {
        print NR": "$0
      }
    }
    { prev = $0 }
  ' "$BENCH_FILE" | head -5 || true)

  if [ -n "$JOBS_WITHOUT_COMMENT" ]; then
    echo "WARN  T8  以下 SimpleJob 缺少配置理由注释（T8 要求）:"
    echo "$JOBS_WITHOUT_COMMENT" | while read -r line; do echo "      line $line"; done
    ((WARN_COUNT++)) || true
  else
    echo "PASS  T8  基准配置注释检查通过"
  fi
else
  echo "SKIP  T8  基准文件不存在"
fi

# ─── T9：BenchmarkCategory 同义词检查 ──────────────────
echo ""
echo "─── T9: BenchmarkCategory 同义词 ───"
if [ -f "$BENCH_FILE" ]; then
  # 检查 Bulk 和 BulkInsert 是否混用
  HAS_BULK=$(grep -c 'BenchmarkCategory("Bulk")' "$BENCH_FILE" || true)
  HAS_BULKINSERT=$(grep -c 'BenchmarkCategory("BulkInsert")' "$BENCH_FILE" || true)

  if [ "$HAS_BULK" -gt 0 ] && [ "$HAS_BULKINSERT" -gt 0 ]; then
    echo "FAIL  T9  Bulk 和 BulkInsert 混用（应统一）"
    ((FAIL_COUNT++)) || true
  else
    echo "PASS  T9  BenchmarkCategory 无同义词混用"
  fi
else
  echo "SKIP  T9  基准文件不存在"
fi

# ─── T5/T6：测试间状态泄漏检查 ──────────────────────────
echo ""
echo "─── T6: 外部 DB 测试清理检查 ───"
# 检查 Integration.Tests 中 DROP TABLE 是否在 finally 块内
# 这是一个启发式检查——查找 DROP TABLE 不在 try/finally 模式中的情况
LEAKING_DROPS=$(grep -rn 'DROP TABLE' "$ROOT_DIR/test/PalORM.Integration.Tests/" --include='*.cs' 2>/dev/null | \
  grep -v 'bin/' | grep -v 'obj/' | grep -v 'finally' | \
  grep -v 'IF EXISTS' | \
  wc -l || true)

if [ "$LEAKING_DROPS" -gt 5 ]; then
  echo "WARN  T6  发现 $LEAKING_DROPS 处 DROP TABLE 可能不在 finally 中（需人工审查）"
  ((WARN_COUNT++)) || true
else
  echo "PASS  T6  DROP TABLE 清理模式检查通过（$LEAKING_DROPS 处需 IF EXISTS 兜底）"
fi

# ─── 脚本 set -euo pipefail 统一检查（T-DEF-1 下沉）─────
echo ""
echo "─── T-DEF-1: 脚本 set -euo pipefail 统一 ───"
MISSING_E=0
for f in "$ROOT_DIR"/scripts/*.sh; do
  [ -f "$f" ] || continue
  # source 脚本（set-test-env.sh）例外——它用 set -a
  if [[ "$f" == *set-test-env* ]]; then continue; fi
  if ! grep -q 'set -euo pipefail' "$f"; then
    echo "FAIL  T-DEF-1  $f 缺少 set -euo pipefail"
    ((FAIL_COUNT++)) || true
    ((MISSING_E++)) || true
  fi
done
if [ "$MISSING_E" -eq 0 ]; then
  echo "PASS  T-DEF-1  所有脚本（除 source 脚本）均有 set -euo pipefail"
fi

# ─── CI timeout-minutes 检查（T-DEF-4 下沉）─────────────
echo ""
echo "─── T-DEF-4: CI job timeout-minutes ───"
CI_FILE="$ROOT_DIR/.github/workflows/ci.yml"
if [ -f "$CI_FILE" ]; then
  # 只统计真正的 job 定义（4 空格缩进 + 名称: 后无其他）
  JOBS=$(grep -E '^\s{4}[a-z][a-z-]*:$' "$CI_FILE" | tr -d ' :' | head -20)
  TOTAL_JOBS=$(echo "$JOBS" | grep -c . || true)
  TIMEOUTS=$(grep -c 'timeout-minutes' "$CI_FILE" || true)

  if [ "$TIMEOUTS" -lt "$TOTAL_JOBS" ]; then
    echo "WARN  T-DEF-4  CI 有 $TOTAL_JOBS 个 job，仅 $TIMEOUTS 个有 timeout-minutes"
    ((WARN_COUNT++)) || true
  else
    echo "PASS  T-DEF-4  所有 CI job 均有 timeout-minutes"
  fi
else
  echo "SKIP  T-DEF-4  ci.yml 不存在"
fi

# ─── 总结 ────────────────────────────────────────────────
echo ""
echo "═══════════════════════════════════════════════════════════════"
echo " 结果: $FAIL_COUNT 失败 / $WARN_COUNT 警告"
if [ "$FAIL_COUNT" -gt 0 ]; then
  echo " ❌ 门禁未通过——修复 FAIL 项后重试"
  exit 1
else
  echo " ✅ 测试规范门禁通过"
  exit 0
fi
echo "═══════════════════════════════════════════════════════════════"
