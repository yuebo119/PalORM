#!/usr/bin/env bash
# review 范围清单生成器——地毯式逐行的覆盖度账本基建（质量为先宗旨配套）。
# 用法:
#   bash scripts/review-scope.sh              # 全量档:src/ 全部手写代码清单+分片建议
#   bash scripts/review-scope.sh --diff       # 标准档:本次 diff 触及文件的全文清单
#   bash scripts/review-scope.sh --partitions N  # 指定分片数(默认按行数均衡 4 片)
# 产出:文件清单(路径+行数)+ 分片方案 + 可粘贴报告段 1 的覆盖度账本模板。
# 逐行覆盖是否完成由人工勾选账本,但"应读清单"由本脚本机械生成——漏读文件在账本上可见。

set -euo pipefail
cd "$(dirname "$0")/.."

MODE="full"
PARTS=4
while [ $# -gt 0 ]; do
    case "$1" in
        --diff) MODE="diff"; shift ;;
        --partitions) PARTS="$2"; shift 2 ;;
        *) echo "未知参数: $1"; exit 1 ;;
    esac
done

if [ "$MODE" = "diff" ]; then
    FILES=$(git diff HEAD~1 --name-only -- 'src/**/*.cs' 2>/dev/null | grep -v '\.g\.cs' || true)
    [ -z "$FILES" ] && { echo "本次 diff 未触及 src/ 手写代码"; exit 0; }
else
    FILES=$(git ls-files 'src/**/*.cs' | grep -v '\.g\.cs')
fi

echo "═══════ review 范围清单（$MODE 档）═══════"
echo "生成: $(date '+%Y-%m-%d %H:%M:%S') · 基线: $(git rev-parse --short HEAD)"
echo ""

TOTAL=0
MANIFEST=""
while IFS= read -r f; do
    [ -f "$f" ] || continue
    n=$(wc -l < "$f" | tr -d ' ')
    TOTAL=$((TOTAL + n))
    MANIFEST="$MANIFEST$n $f\n"
done <<< "$FILES"

COUNT=$(printf '%b' "$MANIFEST" | grep -c . || true)
echo "─── 应读清单（$COUNT 文件 · $TOTAL 行）───"
printf '%b' "$MANIFEST" | sort -rn | awk '{printf "  %5d 行  %s\n", $1, $2}'

echo ""
echo "─── 分片方案（${PARTS} 片按行数均衡——供并行子代理各领一片地毯）───"
printf '%b' "$MANIFEST" | sort -rn | awk -v parts="$PARTS" '
{ files[NR] = $2; lines[NR] = $1 }
END {
    for (p = 1; p <= parts; p++) load[p] = 0
    for (i = 1; i <= NR; i++) {
        min = 1
        for (p = 2; p <= parts; p++) if (load[p] < load[min]) min = p
        part[i] = min; load[min] += lines[i]
    }
    for (p = 1; p <= parts; p++) {
        printf "  片 %d（%d 行）:", p, load[p]
        for (i = 1; i <= NR; i++) if (part[i] == p) printf " %s", files[i]
        printf "\n"
    }
}'

echo ""
echo "─── 覆盖度账本模板（粘贴报告段 1，逐文件勾销）───"
printf '%b' "$MANIFEST" | sort -rn | awk '{printf "- [ ] %s (%d 行)\n", $2, $1}'
echo ""
echo "账本规则: 全部勾销 = 地毯完成;未勾销文件出现在报告 = 报告视为草稿。"
