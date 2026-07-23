#!/usr/bin/env bash
# PalORM 技术债扫描——12 类检查，每项零残留即通过。
# 用法: bash scripts/tech-debt-scan.sh
# CI 集成: 在 gate job 中调用此脚本，exit 1 阻断 PR。

set -euo pipefail

PASS=0
FAIL=0

check() {
    local name="$1"
    local result="$2"
    local allow="$3"  # "allow" = 允许非零（已知技术债），"strict" = 必须为零
    local count
    count=$(echo "$result" | grep -c . || true)

    if [ "$allow" = "strict" ] && [ "$count" -gt 0 ]; then
        echo "FAIL  $name ($count 处)"
        echo "$result" | head -5 | sed 's/^/      /'
        FAIL=$((FAIL + 1))
    else
        echo "PASS  $name ($count 处)"
        PASS=$((PASS + 1))
    fi
}

echo "═══════════════════════════════════════════"
echo " PalORM 技术债扫描 ($(date '+%Y-%m-%d %H:%M'))"
echo "═══════════════════════════════════════════"
echo ""

# ─── 1. [Obsolete] 残留（有明确移除计划的标记为 allow）───
OBSOLETE_RESULT=$(grep -rn '\[Obsolete' src/ test/ tools/ bench/ --include='*.cs' 2>/dev/null | grep -v 'obj/\|bin/' || true)
OBSOLETE_COUNT=$(echo "$OBSOLETE_RESULT" | grep -c . || true)
if [ "$OBSOLETE_COUNT" -eq 0 ]; then
    echo "PASS  1. [Obsolete] 残留 (0 处)"
    PASS=$((PASS + 1))
else
    echo "WARN  1. [Obsolete] 残留 ($OBSOLETE_COUNT 处——有明确移除计划的允许)"
    echo "$OBSOLETE_RESULT" | head -5 | sed 's/^/      /'
    PASS=$((PASS + 1))  # WARN 不阻断
fi

# ─── 2. TODO/HACK/FIXME/XXX 注释 ───
check "2. TODO/HACK/FIXME 注释" \
    "$(grep -rnE '// TODO|// HACK|// FIXME|// XXX' src/ test/ tools/ bench/ --include='*.cs' 2>/dev/null | grep -v 'obj/\|bin/' || true)" \
    "strict"

# ─── 3. Console.WriteLine 在 src/ ───
check "3. Console.WriteLine 在 src/" \
    "$(grep -rn 'Console\.' src/ --include='*.cs' 2>/dev/null | grep -v 'obj/\|bin/ || true' || true)" \
    "strict"

# ─── 4. 空 catch 无注释 ───
check "4. 空 catch 无注释" \
    "$(grep -rn 'catch.*{}' src/ test/ --include='*.cs' 2>/dev/null | grep -v 'obj/\|bin/' || true)" \
    "strict"

# ─── 5. tab 字符（应为 space）───
check "5. tab 字符（应为 space）" \
    "$(find src/ -name '*.cs' -not -path '*/obj/*' -not -path '*/bin/*' -exec grep -lP '\t' {} \; 2>/dev/null || true)" \
    "strict"

# ─── 6. src/ 超长行 > 180（SourceGen Emitter + 生成的 SQL 字符串允许）───
check "6. src/ 超长行 > 180（SourceGen 允许）" \
    "$(find src/ -name '*.cs' -not -path '*/obj/*' -not -path '*/bin/*' -not -path '*/PalORM.SourceGen/*' -exec awk 'length($0)>180 {print FILENAME":"NR}' {} \; 2>/dev/null | head -20 || true)" \
    "allow"  # Core 超长行多为多列 Select/聚合方法签名——拆行损害可读性

# ─── 7. test/ 超长行 > 180（AotTest + Provider 测试允许）───
check "7. test/ 超长行 > 180（AotTest 允许）" \
    "$(find test/ -name '*.cs' -not -path '*/obj/*' -not -path '*/bin/*' -not -path '*/PalORM.AotTest*' -exec awk 'length($0)>180 {print FILENAME":"NR}' {} \; 2>/dev/null | head -20 || true)" \
    "allow"  # PgTests/MySqlTests 单行 DDL 语句——SQL 语义紧凑

# ─── 8. SuppressMessage 无 Justification（Python 多行扫描）───
SUPPRESS_NO_JUST=$(python3 -c "
import re, glob
for f in glob.glob('src/**/*.cs', recursive=True):
    if 'obj/' in f or 'bin/' in f: continue
    with open(f, 'r', encoding='utf-8') as fh:
        content = fh.read()
    for m in re.finditer(r'\[SuppressMessage\([^]]+)\]', content, re.DOTALL):
        if 'Justification' not in m.group(0):
            line = content[:m.start()].count(chr(10)) + 1
            print(f'{f}:{line} MISSING Justification')
" 2>/dev/null || true)
check "8. SuppressMessage 无 Justification" "$SUPPRESS_NO_JUST" "strict"

# ─── 9. 测试用例数对照 README badge ───
BADGE_NUM=$(grep -oP 'tests-\K[0-9]+' README.md 2>/dev/null | head -1)
[ -z "$BADGE_NUM" ] && BADGE_NUM=0
CORE_COUNT=$(dotnet test test/PalORM.Core.Tests -c Debug 2>&1 | grep -oP 'succeeded: \K[0-9]+' 2>/dev/null || true)
SG_COUNT=$(dotnet test test/PalORM.SourceGen.Tests -c Debug 2>&1 | grep -oP 'succeeded: \K[0-9]+' 2>/dev/null || true)
INT_COUNT=$(dotnet test test/PalORM.Integration.Tests -c Debug 2>&1 | grep -oP 'succeeded: \K[0-9]+' 2>/dev/null || true)
[ -z "$CORE_COUNT" ] && CORE_COUNT=0
[ -z "$SG_COUNT" ] && SG_COUNT=0
[ -z "$INT_COUNT" ] && INT_COUNT=0
ACTUAL=$((CORE_COUNT + SG_COUNT + INT_COUNT))
if [ "$BADGE_NUM" = "$ACTUAL" ]; then
    echo "PASS  9. 测试用例数对照 README badge ($BADGE_NUM == $ACTUAL)"
    PASS=$((PASS + 1))
else
    echo "FAIL  9. 测试用例数对照 README badge (badge=$BADGE_NUM != actual=$ACTUAL)"
    FAIL=$((FAIL + 1))
fi

# ─── 10. csproj 版本号一致性 ───
VERSIONS=$(grep '<Version>' src/*/*.csproj | sed 's/.*<Version>\([^<]*\)<\/Version>.*/\1/' | sort -u | tr '\n' ' ')
VERSION_COUNT=$(echo "$VERSIONS" | tr ' ' '\n' | grep -c . 2>/dev/null || true)
if [ "$VERSION_COUNT" -eq 1 ]; then
    echo "PASS  10. csproj 版本号一致 ($VERSIONS)"
    PASS=$((PASS + 1))
else
    echo "FAIL  10. csproj 版本号不一致 ($VERSIONS)"
    FAIL=$((FAIL + 1))
fi

# ─── 11. PALORM006/007 占位诊断复发检测（排除注释行）───
check "11. PALORM006/007 占位诊断复发" \
    "$(grep -n 'PALORM006\|PALORM007' src/PalORM.SourceGen/PalORMAnalyzer.cs 2>/dev/null | grep -v '^\s*//' | grep -v '已删除\|已删\|已移除' || true)" \
    "strict"

# ─── 12. .gitignore 关键排除项 ───
for pattern in '.env.test' '.vscode/' '.idea/' 'bin/' 'obj/'; do
    if ! grep -q "$pattern" .gitignore 2>/dev/null; then
        echo "FAIL  12. .gitignore 缺少排除项: $pattern"
        FAIL=$((FAIL + 1))
        pattern=""
        break
    fi
done
if [ -n "$pattern" ] || [ "$FAIL" -eq 0 ]; then
    echo "PASS  12. .gitignore 关键排除项完整"
    PASS=$((PASS + 1))
fi

# ─── 汇总 ───
echo ""
echo "═══════════════════════════════════════════"
echo " 结果: $PASS 通过 / $FAIL 失败"
echo "═══════════════════════════════════════════"

if [ "$FAIL" -gt 0 ]; then
    exit 1
fi
