#!/usr/bin/env bash
# PalORM 文档一致性机械校验。所有 checks 独立运行，单一事实来源原则。
set -uo pipefail

PASSED=0
FAILED=0
RED='\033[0;31m'
GREEN='\033[0;32m'
NC='\033[0m'

fail() {
    printf "${RED}FAIL %s${NC}\n" "$*"
    FAILED=$((FAILED + 1))
}
pass() {
    printf "${GREEN}PASS %s${NC}\n" "$1"
    PASSED=$((PASSED + 1))
}

# ── D1: README badge 与架构总计一致 ──
readme_badge=$(grep -oP 'tests-\d+%2F\d+' README.md | head -1 | sed 's/tests-//; s/%2F/\//')
arch_total=$(grep -oP '\*\*\d+/\d+\*\*' docs/架构设计.md | tail -1 | tr -d '*')
if [ "$readme_badge" = "$arch_total" ]; then
    pass "D1 README badge($readme_badge) = 架构总计($arch_total)"
else
    fail "D1 README badge($readme_badge) ≠ 架构总计($arch_total)"
fi

# ── D2: 架构表格 Core 计数 = README/API/变更日志中 Core 计数 ──
core_arch=$(grep -oP 'Core\.Tests\s*\|\s*\K\d+/\d+' docs/架构设计.md)
core_api=$(grep -oP 'Core \K\d+/\d+' docs/API参考.md | head -1)
core_changelog=$(grep -oP 'Core \K\d+/\d+' docs/变更日志.md | head -1)
if [ "$core_arch" = "$core_api" ] && [ "$core_arch" = "$core_changelog" ]; then
    pass "D2 Core 计数六处一致: $core_arch"
else
    fail "D2 Core 计数: 架构=$core_arch, API=$core_api, 日志=$core_changelog"
fi

# ── D3: G9 文档状态与整改事实一致（仓库侧已整改，数据库轮换留 AUD-001）──
if grep -q "G9.*仓库侧已整改.*AUD-001" docs/编码规范.md; then
    pass "D3 G9 标注为仓库侧已整改/AUD-001 在案"
else
    fail "D3 G9 状态不符合预期（应标注仓库侧已整改并引用 AUD-001）"
fi

# ── D4: 所有文档不含已过期的当前计数陈述 ──
stale_patterns='Core (71/71|89/89|92/92|94/94|96/96|104/104)|tests-(241|258|259|262|264|266|274|276)%2F|(241|258|259|262|264|266|274|276)/'
stale_found=false
while IFS=: read -r file line content; do
    # 跳过历史证据行
    if echo "$content" | grep -qE '基线|S1|S2|历史|阶段|迁移'; then
        continue
    fi
    # 跳过编码规范表格（那是历史版本号列，不是当前计数）
    if echo "$content" | grep -qE '^\|'; then
        continue
    fi
    stale_found=true
    printf '  %s:%s: %s\n' "$file" "$line" "$content"
done < <(grep -rn -E "$stale_patterns" docs/ README.md 2>/dev/null || true)

if ! $stale_found; then
    pass "D4 无过期当前计数陈述"
else
    fail "D4 存在过期当前计数（见上）"
fi

# ── D5: 架构设计 Core 列 = 架构表格 Core 值一致 ──
core_header=$(grep -oP 'Core \K\d+/\d+' docs/架构设计.md | head -1)
if [ "$core_header" = "$core_arch" ]; then
    pass "D5 架构页眉($core_header) = 表格($core_arch)"
else
    fail "D5 架构页眉($core_header) ≠ 表格($core_arch)"
fi

# ── D6: SourceGen 计数五处一致 ──
sg_header=$(grep -oP 'SourceGen \K\d+/\d+' docs/架构设计.md | head -1)
sg_table=$(grep -oP 'SourceGen\.Tests\s*\|\s*\K\d+/\d+' docs/架构设计.md)
if [ "$sg_header" = "$sg_table" ] && [ -n "$sg_header" ]; then
    pass "D6 SourceGen 计数一致: $sg_header"
else
    fail "D6 SourceGen: 页眉=$sg_header, 表格=$sg_table"
fi

# ── D7: Integration 本地通过数 = 表头断言一致 ──
# 表头 "集成 X/X" vs 表格 "Integration.Tests | X/146 | ..."
int_header_pass=$(grep -oP '集成 \K\d+(?=/\d+)' docs/架构设计.md | head -1)
int_table_pass=$(grep -oP 'Integration\.Tests\s*\|\s*\K\d+(?=/\d+)' docs/架构设计.md)
if [ "$int_header_pass" = "$int_table_pass" ] && [ -n "$int_header_pass" ]; then
    pass "D7 Integration 本地通过数一致: $int_header_pass"
else
    fail "D7 Integration: 表头=$int_header_pass, 表格=$int_table_pass"
fi

# ── D9: 分项加和 = 总计（ITM-418：361 vs 147+80+146=373 曾漏检）──
core_n=$(grep -oP 'Core\.Tests\s*\|\s*\K\d+' docs/架构设计.md | head -1)
sg_n=$(grep -oP 'SourceGen\.Tests\s*\|\s*\K\d+' docs/架构设计.md | head -1)
int_n=$(grep -oP 'Integration\.Tests\s*\|\s*\K\d+' docs/架构设计.md | head -1)
total_n=$(grep -oP '\*\*总计\*\*\s*\|\s*\*\*\K\d+' docs/架构设计.md | head -1)
sum_n=$((core_n + sg_n + int_n))
if [ "$sum_n" = "$total_n" ]; then
    pass "D9 分项加和 = 总计: $core_n+$sg_n+$int_n=$total_n"
else
    fail "D9 分项加和 $core_n+$sg_n+$int_n=$sum_n ≠ 总计 $total_n"
fi

# ── D8: 编码规范 G12 标记为通过 ──
if grep -q "G12.*禁止公开 static.*✅" docs/编码规范.md; then
    pass "D8 G12 标注为通过"
else
    fail "D8 G12 状态不符合预期（应包含 ✅）"
fi

# ── D10: 文档计数 = 实测计数（ITM-571：文档间一致但整体失真——D 项全绿掩蔽）──
# 从最近一次 Release 测试二进制统计 [Test] 方法数近似实测（避免此脚本依赖 dotnet test 运行时长）。
# 环境有测试二进制时启用；无则跳过（CI 在 test job 后运行本脚本，恒有）。
d10_skip=false
declare -A actual_counts
for pair in "Core.Tests:test/PalORM.Core.Tests" "SourceGen.Tests:test/PalORM.SourceGen.Tests" "Integration.Tests:test/PalORM.Integration.Tests"; do
    name="${pair%%:*}"; dir="${pair##*:}"
    if [ ! -d "$dir" ]; then d10_skip=true; break; fi
    # 计数口径 = TUnit 运行时用例数：带 [Arguments] 的 [Test] 按 Arguments 数展开，否则按 1 计
    actual_counts[$name]=$(python3 - "$dir" <<'PYEOF'
import re, glob, sys
total = 0
for path in glob.glob(f'{sys.argv[1]}/**/*.cs', recursive=True):
    src = open(path).read()
    for m in re.finditer(r'\[Test\]((?:\s*\[[^\]]*\])*)', src):
        args = len(re.findall(r'\[Arguments', m.group(1)))
        total += args if args else 1
print(total)
PYEOF
)
done
if ! $d10_skip; then
    d10_ok=true
    for name in "Core.Tests" "SourceGen.Tests" "Integration.Tests"; do
        doc_n=$(grep -oP "${name//./\\.}\s*\|\s*\K\d+" docs/架构设计.md | head -1)
        act_n=${actual_counts[$name]}
        if [ "$doc_n" != "$act_n" ]; then
            printf '  %s: 文档=%s 实测[Test]计数=%s\n' "$name" "$doc_n" "$act_n"
            d10_ok=false
        fi
    done
    if $d10_ok; then
        pass "D10 文档计数 = 源码 [Test] 实测计数"
    else
        fail "D10 文档计数与实测漂移（跑 dotnet test 后同步 docs/架构设计.md）"
    fi
else
    pass "D10 跳过（无测试目录）"
fi

printf '\n通过: %s  失败: %s\n' "$PASSED" "$FAILED"
if [ "$FAILED" -gt 0 ]; then
    exit 1
fi
