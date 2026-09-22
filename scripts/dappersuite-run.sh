#!/usr/bin/env bash
# Dapper 官方基准套件（三臂移植版）逐方言跑测——一次串行跑完 SQLite / MySQL / PostgreSQL。
#
# 用法:
#   bash scripts/dappersuite-run.sh                    # 三库全跑
#   bash scripts/dappersuite-run.sh sqlite             # 只跑一库
#   bash scripts/dappersuite-run.sh pg --filter '*First*'
#
# 为什么串行而非并行：BDN 计时对 CPU 争用敏感，三库并行会同时污染三份数字。
#
# 凭证：source scripts/set-test-env.sh（读 .env.test，不回显值）。sqlite 档不需要连接串。

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DIALECTS="${1:-sqlite,mysql,pg}"
shift || true
EXTRA_ARGS=("$@")

# shellcheck disable=SC1091
. "$ROOT_DIR/scripts/set-test-env.sh"

OUT_DIR="$ROOT_DIR/bench/PalORM.DapperSuite/results"
mkdir -p "$OUT_DIR"
STAMP="$(date +%Y%m%d-%H%M%S)"

IFS=',' read -r -a LIST <<< "$DIALECTS"
for dialect in "${LIST[@]}"; do
    echo "=== [$dialect] 开始（$STAMP） ==="
    LOG="$OUT_DIR/$dialect-$STAMP.log"
    if [ ${#EXTRA_ARGS[@]} -gt 0 ]; then
        DAPPER_SUITE_DIALECT="$dialect" dotnet run --project "$ROOT_DIR/bench/PalORM.DapperSuite" \
            -c Release -- "${EXTRA_ARGS[@]}" 2>&1 | tee "$LOG"
    else
        DAPPER_SUITE_DIALECT="$dialect" dotnet run --project "$ROOT_DIR/bench/PalORM.DapperSuite" \
            -c Release -- -f '*' --join 2>&1 | tee "$LOG"
    fi
    echo "=== [$dialect] 完成，日志 $LOG ==="
done

echo "全部方言跑测完成。BDN 报告在 $ROOT_DIR/BenchmarkDotNet.Artifacts/results/"
