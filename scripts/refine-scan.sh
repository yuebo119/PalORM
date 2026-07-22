#!/usr/bin/env bash
# ─── PalORM 精炼扫描 ───
# 对 .ai/refine/prompt.md 的 24 项操作矩阵输出机械命中数。
# 可 grep 的 17 项输出命中量级；7 项需 Roslyn/IDE 分析，明确标注 [人工]。
# 命中数是候选量级，不是执行清单——逐项评估后才可执行。

set -euo pipefail

cd "$(dirname "$0")/.."

hits() {
    grep -rn -E "$1" src/ --include='*.cs' 2>/dev/null \
        | grep -v '/obj/\|/bin/\|/Generated/\|\.g\.cs' | wc -l | tr -d ' '
}

echo "═══════ 精炼扫描（24 项操作矩阵）═══════"
echo "时间: $(date '+%Y-%m-%d %H:%M:%S')"
echo "范围: src/ 手写代码（排除 bin/obj/Generated/*.g.cs）"
echo ""

echo "─── 一类：减法（A1-A8）───"
echo "A1 0引用类型/方法: [人工] 需 Roslyn 引用分析；grep 计数有四类盲区（生成物类名/公共注解/工厂 lambda/扩展方法）——见误判库 P9，2026-07-19 十候选全证伪"
echo "A2 重复实现: [人工] 需语义比对"
echo "A3 ≤10行标记接口/常量类: [人工] 需逐文件评估合并可行性"
echo "A4 死 catch 块: $(hits 'catch[[:space:]]*\((Db)?Exception[^)]*\)[[:space:]]*\{[[:space:]]*\}') 处空 catch 候选"
echo "A5 冗余 using: 0 处（TreatWarningsAsErrors + IDE0005 编译期强制）"
echo "A6 SuppressMessage 注解: $(hits 'SuppressMessage') 处（逐项核对 Justification 是否仍必要）"
echo "A7 未使用内建类: [人工] 需 Roslyn 引用分析"
echo "A8 回退兼容路径: $(hits '(legacy|fallback)') 处关键词候选"

echo ""
echo "─── 二类：现代化（M1-M10）───"
echo "M1 new List<>: $(hits 'new List<[^>]+>\(\)') 处裸构造（带容量/拷贝参数的合规不计）"
echo "M2 传统构造注入: [人工] 主构造函数迁移需逐类评估（🔴 风险）"
echo "M3 get+校验→required: $(hits '\brequired\s') 处已用 required（其余候选人工确认，🔴 风险）"
echo "M4 类型判断链: $(hits 'else if.*\bis\b') 处 is 链候选"
echo "M5 string.Format: $(hits 'string\.Format') 处"
echo "M6 new ArgumentNullException: $(hits 'new ArgumentNullException') 处（可改 ThrowIfNull）"
echo "M7 ref struct 枚举器: [人工] 需热路径分析"
echo "M8 struct→readonly record struct: $(hits '(^|[^a-zA-Z_])struct[[:space:]]+[A-Z]') 处 struct 声明（逐项评估，🔴 风险）"
echo "M9 object 锁字段→Lock: $(hits 'readonly object _(sync|lock|gate)') 处（lock 语句本体不计——Lock 类型迁移后语句无需改动）"
echo "M10 params T[]: $(hits 'params[[:space:]]+[A-Za-z0-9_<>,[:space:]]*\[\]') 处（可改 params Span<T>）"

echo ""
echo "─── 三类：优化（O1-O9）───"
echo "O1 Dictionary→FrozenDictionary: $(hits 'new Dictionary<') 处 new Dictionary（只读场景评估）"
echo "O2 集合无预分配: [人工] 需容量可知性分析"
echo "O3 .ToArray/.ToList: $(hits '\.ToArray\(\)|\.ToList\(\)') 处"
echo "O4 foreach+yield: $(hits 'yield return') 处 yield（枚举器评估）"
echo "O5 LINQ链热路径: $(hits '\.Where\(.*\.Select\(|\.Select\(.*\.Where\(') 处"
o6_count=$(grep -rn -A3 -E '(for|foreach|while)[[:space:]]*\(' src/ --include='*.cs' 2>/dev/null \
    | grep -v '/obj/\|/bin/\|\.g\.cs' | grep -cE '\+=[[:space:]]*(\"|\$)' || true)
echo "O6 字符串 += 循环内累加: ${o6_count} 处（单次条件追加不计——2026-07-19 二轮证实旧模式全为误报）"
echo "O7 ValueTask 多重 await: [人工] 需数据流分析（🔴 风险）"
echo "O8 object 参数装箱: $(hits '\(object[[:space:]]|,[[:space:]]*object[[:space:]]') 处 object 参数候选"
echo "O9 Enum.ToString: $(hits '(dialect|[Kk]ind|[Ll]evel|[Cc]onvention|[Oo]utcome)[a-zA-Z_]*\.ToString\(\)') 处枚举词干 ToString（全量 ToString 含诊断消息，不再作为候选口径）"

echo ""
echo "═══════ 扫描完成 ═══════"
echo "机械扫描 17 项；[人工] 标注 7 项（A1/A2/A3/A7/M2/M7/O2/O7）需 Roslyn 或语义分析。"
echo "命中数为候选量级。执行顺序按 prompt 的 P0→P1→P2，每项执行后 dotnet build，每批后 dotnet test。"
echo "纪律：① 批量文本替换前核对行尾（CRLF 文件禁止全文重写——TableModel ±454 假 diff 教训）；"
echo "     ② O 类性能项声称显著收益时须 BenchmarkDotNet 实证，否则效果栏只写机制不写倍数。"
