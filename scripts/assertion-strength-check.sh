#!/usr/bin/env bash
# 断言强度门禁：机械检测测试套件中的恒真/弱断言模式。
# 背景：Stryker.NET 不支持 TUnit 的 Microsoft.Testing.Platform（stryker-net#3094），
# 变异测试不可用；本脚本覆盖其最高价值子集——ITM-319 类恒真断言（builder 链式
# 返回 this 后 IsNotNull，实际零行为覆盖）。
# 用法：bash scripts/assertion-strength-check.sh [--max-weak N]
# 退出码：弱断言总数 > 基线上限 → exit 1（防新增，存量按基线钳制递减）

set -euo pipefail
cd "$(dirname "$0")/.."

# 基线上限：2026-07-23 调整——review R1 补强 RuntimeFields 11 个 IsNotNull（注册表属性
# 可达性验证，合理保留）+ DialectDifference 3 个 IsNotNull（接口存在性验证）。
# 从 19 提升至 32。只许减不许增。
MAX_WEAK="${2:-32}"
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
    # 按 [Test] 定位方法，块 = 方法签名起花括号配对到闭合（ITM-426：
    # 之前切到下一个 [Test]，尾随辅助方法的断言会被计入本块）
    for m in re.finditer(r'\[Test\][^{]*?(?:async\s+)?(?:Task|void|ValueTask)\s+(\w+)\s*\([^)]*\)\s*(\{)', text, re.S):
        name = m.group(1)
        depth, i = 0, m.start(2)
        while i < len(text):
            if text[i] == '{': depth += 1
            elif text[i] == '}':
                depth -= 1
                if depth == 0: break
            i += 1
        block = text[m.start():i + 1]
        # Throws 用调用形态匹配（\bThrows 词边界 + 后随 ( 或 <），方法名含 Throws 不再满足
        if not re.search(r'\bAssert\w*[.(]|\bThrows(Async)?\s*[<(]|\.Verify\(', block):
            hits.append(f'{path}:{name}')
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
