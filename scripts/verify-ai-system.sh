#!/usr/bin/env bash
# .ai 系统机械校验：提示词引用可解析、口径数字与事实一致、配套脚本存在。
# 11 项检查全部通过退出 0，任一失败退出 1。

set -uo pipefail

cd "$(dirname "$0")/.."

PASSED=0
FAILED=0

pass() {
    printf 'PASS V%s: %s\n' "$1" "$2"
    PASSED=$((PASSED + 1))
}

fail() {
    printf 'FAIL V%s: %s（%s）\n' "$1" "$2" "$3"
    FAILED=$((FAILED + 1))
}

printf '═══════ .ai 系统一致性校验 ═══════\n'
printf '时间：%s\n\n' "$(date '+%Y-%m-%d %H:%M:%S')"

# V1 引擎 + 双 profile + 工具提示词与模板齐全
missing=""
for f in \
    .ai/deep-check-engine.md \
    .ai/metrics.md \
    .ai/review-system/prompt.md \
    .ai/review-system/known-false-positives.md \
    .ai/gate-system/prompt.md \
    .ai/refine-system/prompt.md \
    .ai/review-system/prompt.md \
    .ai/review-system/template.md \
    .ai/review-system/action-items-template.md; do
    [ -f "$f" ] || missing="$missing $f"
done
if [ -z "$missing" ]; then
    pass 1 '引擎/双profile/工具提示词与模板齐全'
else
    fail 1 '提示词/模板缺失' "$missing"
fi

# V2 提示词引用的权威文档与脚本存在
missing=""
for f in \
    docs/编码规范.md \
    docs/踩坑目录.md \
    docs/API参考.md \
    docs/架构设计.md \
    scripts/review-snapshot.sh \
    scripts/verify-action-items.sh \
    scripts/gate-check.sh \
    scripts/refine-scan.sh \
    scripts/stub-check.sh \
    scripts/assertion-strength-check.sh; do
    [ -f "$f" ] || missing="$missing $f"
done
if [ -z "$missing" ]; then
    pass 2 '提示词引用的文档与脚本全部存在'
else
    fail 2 '被引用文件缺失' "$missing"
fi

# V3 STD 规则数：提示词口径 = 编码规范实际去重计数
actual_std=$(grep -oE 'STD-[A-Z]+-[0-9]+' docs/编码规范.md | sort -u | wc -l | tr -d ' ')
claimed=$(grep -hoE '[0-9]+ 条 STD 规则' \
    .ai/review-system/prompt.md .ai/gate-system/prompt.md \
    .ai/refine-system/prompt.md .ai/review-system/prompt.md 2>/dev/null \
    | grep -oE '[0-9]+' | sort -u)
if [ "$(printf '%s\n' "$claimed" | wc -l | tr -d ' ')" = "1" ] && [ "$claimed" = "$actual_std" ]; then
    pass 3 "STD 规则数口径一致（$actual_std 条）"
else
    fail 3 'STD 规则数口径漂移' "实际 $actual_std，提示词声称：$(printf '%s' "$claimed" | tr '\n' ' ')"
fi

# V4 踩坑目录口径：提示词声称数 = 目录标题数 = 表内条目数
pit_title=$(grep -m1 -oE '[0-9]+ 项跨语言 ORM 陷阱' docs/踩坑目录.md | grep -oE '[0-9]+')
pit_rows=$(grep -cE '^\| ?[0-9]+' docs/踩坑目录.md)
pit_claimed=$(grep -hoE '[0-9]+ 项陷阱' .ai/review-system/prompt.md .ai/gate-system/prompt.md .ai/review-system/prompt.md 2>/dev/null | grep -oE '[0-9]+' | sort -u)
if [ "$pit_title" = "$pit_rows" ] && [ "$(printf '%s\n' "$pit_claimed" | sort -u)" = "$pit_title" ]; then
    pass 4 "踩坑目录口径一致（$pit_title 项）"
else
    fail 4 '踩坑目录口径漂移' "标题 $pit_title，表行 $pit_rows，提示词：$(printf '%s' "$pit_claimed" | tr '\n' ' ')"
fi

# V5 门禁编号：gate 提示词表、gate-check.sh、编码规范清单三方最大 G 编号一致
g_prompt=$(grep -oE '^\| G[0-9]+' .ai/gate-system/prompt.md | grep -oE '[0-9]+' | sort -n | tail -1)
g_script=$(grep -oE '"?G[0-9]+"?' scripts/gate-check.sh | grep -oE '[0-9]+' | sort -n | tail -1)
g_doc=$(grep -oE 'G1-G[0-9]+' docs/编码规范.md | grep -oE '[0-9]+$' | sort -n | tail -1)
if [ "$g_prompt" = "$g_script" ] && [ "$g_script" = "$g_doc" ]; then
    pass 5 "门禁编号三方一致（G1-G$g_script）"
else
    fail 5 '门禁编号不同步' "提示词 G$g_prompt，脚本 G$g_script，编码规范 G$g_doc"
fi

# V6 API 口径：提示词 113/112 与 API参考.md 自述一致
api_ok=true
grep -q '113 API 中 112 个已实现' docs/API参考.md || api_ok=false
grep -q '113 API 覆盖' .ai/review-system/prompt.md || api_ok=false
if $api_ok; then
    pass 6 'API 口径一致（113 = 112 实现 + 1 移除）'
else
    fail 6 'API 口径漂移' '113/112 口径在 API参考.md 或 audit prompt 中不成立'
fi

# V7 误判知识库可加载且模式数达标（排除 HTML 注释中的模板行）
kb_count=$(grep -cE '^### 模式 (P?[0-9]+)：' .ai/review-system/known-false-positives.md)
if [ "$kb_count" -ge 14 ]; then
    pass 7 "误判知识库加载正常（$kb_count 条模式）"
else
    fail 7 '误判知识库模式数不足' "仅 $kb_count 条，预期 ≥14"
fi

# V8 视角发现率账本存在且含六流记录（P3 自适应的数据载体）
if [ -f .ai/review-system/perspective-stats.md ] \
    && [ "$(grep -cE '^\| (架构|安全|资源|并发|错误|AOT) ?流' .ai/review-system/perspective-stats.md)" -eq 6 ]; then
    pass 8 '视角发现率账本存在且六流记录完整'
else
    fail 8 '视角发现率账本缺失或六流记录不全' '.ai/review-system/perspective-stats.md'
fi

# V9 README 文件地图与实际目录一致（地图里列出的文件必须存在）
missing=""
for f in $(grep -oE '(gate-system|refine-system|review-system)[a-z0-9/-]*\.md' .ai/README.md | sort -u); do
    [ -f ".ai/$f" ] || missing="$missing .ai/$f"
done
if [ -z "$missing" ]; then
    pass 9 'README 文件地图与实际一致'
else
    fail 9 'README 文件地图指向不存在的文件' "$missing"
fi

# V10 机械防线存在：快照基线非空 + 对称性测试 + 断言强度脚本（README 防线清单的账实一致）
defense_ok=true
[ -f test/PalORM.SourceGen.Tests/SnapshotTests.cs ] || defense_ok=false
[ -f test/PalORM.SourceGen.Tests/DialectSymmetryTests.cs ] || defense_ok=false
snap_count=$(ls test/PalORM.SourceGen.Tests/Snapshots/*.snap 2>/dev/null | wc -l | tr -d ' ')
[ "$snap_count" -ge 10 ] || defense_ok=false
if $defense_ok; then
    pass 10 "机械防线齐全（快照基线 $snap_count 份 + 对称性测试 + 断言强度门禁）"
else
    fail 10 '机械防线缺失' "SnapshotTests/DialectSymmetryTests/Snapshots（$snap_count 份，需 ≥10）"
fi

# V11 metrics 账本存在且逃逸账本结构完整
if [ -f .ai/metrics.md ] && grep -q '## 缺陷逃逸账本' .ai/metrics.md && grep -q '## 轮次记录' .ai/metrics.md; then
    pass 11 'metrics 账本存在且结构完整'
else
    fail 11 'metrics 账本缺失或结构不完整' '.ai/metrics.md 需含「轮次记录」与「缺陷逃逸账本」'
fi

printf '\n通过：%s  失败：%s  总计：%s\n' "$PASSED" "$FAILED" "$((PASSED + FAILED))"
printf '═══════ 校验完成 ═══════\n'

[ "$FAILED" -eq 0 ] || exit 1
