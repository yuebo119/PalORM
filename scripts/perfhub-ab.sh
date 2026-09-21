#!/usr/bin/env bash
# PerfHub 交替 A/B 编排器（阶段 4.1）——跨版本对比的唯一可信执行方式。
#
# 用法:
#   bash scripts/perfhub-ab.sh <基线worktree路径> <轮数> [选项...]
#   例:  bash scripts/perfhub-ab.sh /c/v551 3 --dialects pg --tiers 2000
#
# 设计（v2 方案 §6）:
#   · 块 = 方言 × 档位。每个块内 HEAD 与基线背靠背各跑一遍，块间隔 <2 分钟——
#     机器与服务端状态漂移在两版间均匀分摊（顺序跑两轮的 v1 教训：地板单项漂移 50%）。
#   · 每轮每版每块各产出一份 history JSON（--label ab/<round>/<dialect>/<tier> 标记），
#     报告的 A/B 段按 label 配对、逐轮取中位、展示轮间散布。
#   · 基线 worktree 需已就绪（含 PerfHub 夹具 + IVT + .env.test），
#     见 docs/v5.8-perfhub-v2-plan.md 阶段 4.3 的就绪清单。

set -euo pipefail

BASELINE_DIR="${1:?用法: perfhub-ab.sh <基线worktree路径> <轮数> [选项...]}"
ROUNDS="${2:?缺少轮数}"
shift 2 || true
EXTRA_ARGS=("$@")

HEAD_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK="$HEAD_DIR/bench/perfhub/results"

# 选项默认值（可被 EXTRA_ARGS 覆盖时以解析到的为准）
DIALECTS="${DIALECTS:-sqlite,mysql,pg}"
TIERS="${TIERS:-2000}"

for arg in "${EXTRA_ARGS[@]:-}"; do
  case "$arg" in
    --dialects=*) DIALECTS="${arg#--dialects=}" ;;
    --tiers=*)    TIERS="${arg#--tiers=}" ;;
  esac
done

IFS=',' read -ra DL <<< "$DIALECTS"
IFS=',' read -ra TR <<< "$TIERS"

echo "[A/B] HEAD=$HEAD_DIR"
echo "[A/B] 基线=$BASELINE_DIR（须已含 PerfHub + IVT + .env.test）"
echo "[A/B] 轮数=$ROUNDS  方言=${DL[*]}  档位=${TR[*]}"
echo "[A/B] 结果写入 $WORK（label=ab/<round>/<dialect>/<tier>）"
echo

run_one() { # $1=项目目录 $2=version $3=round $4=dialect $5=tier
  echo "── $(date +%H:%M:%S) [$2] r$3 $4/$5 ──"
  (cd "$1" && dotnet run --project bench/PalORM.PerfHub -c Release -- \
    run --dialects "$4" --tiers "$5" --version "$2" \
    --label "ab/$3/$4/$5" ${EXTRA_ARGS[@]+"${EXTRA_ARGS[@]}"} \
    > /dev/null) || { echo "[A/B] 失败: $2 r$3 $4/$5"; exit 1; }
}

for round in $(seq 1 "$ROUNDS"); do
  for d in "${DL[@]}"; do
    for t in "${TR[@]}"; do
      # 轮内交替起跑顺序：奇数轮 HEAD 先、偶数轮基线先——连起跑顺序的系统性偏差也抵消
      if [ $((round % 2)) -eq 1 ]; then
        run_one "$HEAD_DIR" HEAD "$round" "$d" "$t"
        run_one "$BASELINE_DIR" v5.5.1 "$round" "$d" "$t"
      else
        run_one "$BASELINE_DIR" v5.5.1 "$round" "$d" "$t"
        run_one "$HEAD_DIR" HEAD "$round" "$d" "$t"
      fi
    done
  done
done

# 基线的 history JSON 拷回主仓（报告按 label 聚合，两版数据须同目录）
copied=0
for f in "$BASELINE_DIR"/bench/perfhub/results/history-*.json; do
  [ -e "$f" ] || continue
  base=$(basename "$f")
  [ -e "$WORK/$base" ] && continue
  cp "$f" "$WORK/" && copied=$((copied + 1))
done
echo
echo "[A/B] 完成：拷回基线历史 $copied 份。生成报告：dotnet run --project bench/PalORM.PerfHub -- report"
