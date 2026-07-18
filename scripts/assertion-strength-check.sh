#!/usr/bin/env bash
# 断言强度门禁：机械检测测试套件中的恒真/弱断言模式。
# 背景：Stryker.NET 不支持 TUnit 的 Microsoft.Testing.Platform（stryker-net#3094），
# 变异测试不可用；本脚本覆盖其最高价值子集——ITM-319 类恒真断言（builder 链式
# 返回 this 后 IsNotNull，实际零行为覆盖）。
# 用法：bash scripts/assertion-strength-check.sh [--max-weak N]
# 退出码：弱断言总数 > 基线上限 → exit 1（防新增，存量按基线钳制递减）

set -uo pipefail
cd "$(dirname "$0")/.."

# 基线上限：2026-07-18 实测存量 19 处 = IsNotNull 弱断言 16（ITM-319 报告口径）
# + 活性测试 3（Savepoint_Rollback/SessionConcurrency×2——"完成不挂起"即断言，可接受但计入账面）。
# 只许减不许增。
MAX_WEAK="${2:-19}"
[ "${1:-}" = "--max-weak" ] && MAX_WEAK="$2"

printf '═══════ 断言强度扫描 ═══════\n'

# 模式 1：IsNotNull 弱断言——对引用类型链式 API 恒真（builder 返回 this）
weak_notnull=$(grep -rn "\.IsNotNull()" test/ --include="*.cs" \
    | grep -v "/obj/" | grep -v "/bin/" || true)
weak_notnull_count=$(printf '%s' "$weak_notnull" | grep -c . || true)

# 模式 2：测试方法零 Assert（[Test] 方法体内无 Assert. 调用）
# 逐文件用 python 做方法级扫描（bash 正则做不了跨行方法体）
zero_assert=$(python3 - <<'PYEOF'
import re, pathlib
hits = []
for path in pathlib.Path('test').rglob('*.cs'):
    if '/obj/' in str(path) or '/bin/' in str(path):
        continue
    text = path.read_text(encoding='utf-8')
    # 按 [Test] 切块，块 = 到下一个 [Test] 或文件尾
    blocks = re.split(r'(?=\[Test\])', text)
    for block in blocks:
        if not block.startswith('[Test]'):
            continue
        method = re.search(r'(?:async\s+)?(?:Task|void|ValueTask)\s+(\w+)\s*\(', block)
        if not method:
            continue
        if not re.search(r'\bAssert\w*[.(]|Throws|\.Verify\(', block):
            hits.append(f'{path}:{method.group(1)}')
print('\n'.join(hits))
PYEOF
)
zero_assert_count=$(printf '%s' "$zero_assert" | grep -c . || true)

if [ "$weak_notnull_count" -gt 0 ]; then
    printf '\n⚠ IsNotNull 弱断言 %s 处（链式 builder 上恒真——改行为断言）：\n' "$weak_notnull_count"
    printf '%s\n' "$weak_notnull" | head -20
fi
if [ "$zero_assert_count" -gt 0 ]; then
    printf '\n⚠ 零断言测试方法 %s 个：\n' "$zero_assert_count"
    printf '%s\n' "$zero_assert" | head -20
fi

total=$((weak_notnull_count + zero_assert_count))
printf '\n弱断言合计：%s（基线上限 %s）\n' "$total" "$MAX_WEAK"
printf '═══════ 扫描完成 ═══════\n'

if [ "$total" -gt "$MAX_WEAK" ]; then
    printf 'FAIL：弱断言超出基线——新增测试必须使用行为断言；基线只许下调（改本脚本 MAX_WEAK 默认值）\n'
    exit 1
fi
