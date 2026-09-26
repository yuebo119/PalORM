#!/usr/bin/env bash
# Dapper 官方基准套件（三臂移植版）逐方言跑测——一次串行跑完 SQLite / MySQL / PostgreSQL。
#
# 角色：**DapperSuite 侧的编排步骤**，由统一入口 `bash scripts/perf.sh full` 的第 3 步调用。
# 单独跑用于调试本夹具，或换驱动版本时做官方形状哨兵复测；用户入口是 perf.sh。
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

# NuGetAudit=false——理由与机制见 perf.sh 同名导出注释（BDN 图恢复需要，含被引用的 src 工程）。
export NuGetAudit=false

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
FAILED=()
for dialect in "${LIST[@]}"; do
    echo "=== [$dialect] 开始（$STAMP） ==="
    LOG="$OUT_DIR/$dialect-$STAMP.log"
    if [ ${#EXTRA_ARGS[@]} -gt 0 ]; then
        RUN=("${EXTRA_ARGS[@]}")
    else
        RUN=(-f '*' --join)
    fi
    # 单方言失败不中断整批：一个库不可达（实测：托管库的虚拟机停机）不该让已跑方言的结果作废
    if DAPPER_SUITE_DIALECT="$dialect" dotnet run --project "$ROOT_DIR/bench/PalORM.DapperSuite" \
        -c Release -- "${RUN[@]}" 2>&1 | tee "$LOG"; then
        # BDN 在"全部基准 NA"（库不可达）时仍退 0 并打印 "Benchmarks with issues"——
        # 只看退出码会把这种情况误报成成功，故追加日志检查
        if grep -q "Benchmarks with issues" "$LOG"; then
            echo "=== [$dialect] 基准未产出有效结果（库不可达或设置错误，见 $LOG）+ 继续下一方言 ===" >&2
            FAILED+=("$dialect")
        else
            echo "=== [$dialect] 完成，日志 $LOG ==="
        fi
    else
        echo "=== [$dialect] 失败（日志 $LOG）+ 继续下一方言 ===" >&2
        FAILED+=("$dialect")
    fi
done

if [ ${#FAILED[@]} -gt 0 ]; then
    echo "失败方言：${FAILED[*]}（其余方言已跑完，结果库仍有登记）" >&2
    exit 1
fi

echo "全部方言跑测完成。BDN 报告在 $ROOT_DIR/BenchmarkDotNet.Artifacts/results/"
