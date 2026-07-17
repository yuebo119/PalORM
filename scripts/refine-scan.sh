#!/usr/bin/env bash
# ─── PalORM 精炼扫描 ───
# 用途：扫描 24 项精炼操作命中数
# 用法：bash scripts/refine-scan.sh

set -euo pipefail

echo "═══════ 精炼扫描 ═══════"
echo "时间: $(date '+%Y-%m-%d %H:%M:%S')"
echo ""

# 一类：减法（去冗余）
echo "─── 一类：减法 ───"
echo "A1 AssemblyInfo.cs: $(find src -name 'AssemblyInfo.cs' 2>/dev/null | wc -l) 文件"
echo "A2 GlobalUsings.cs: $(find src -name 'GlobalUsings.cs' 2>/dev/null | wc -l) 文件"
echo "A4 0引用私有方法: (需Roslyn分析，跳过)"

# 二类：现代化
echo ""
echo "─── 二类：现代化 ───"
echo "M1 new List<>: $(grep -rn 'new List<' src/ --include='*.cs' 2>/dev/null | wc -l) 处"
echo "M5 string.Format: $(grep -rn 'string\.Format' src/ --include='*.cs' 2>/dev/null | wc -l) 处"
echo "M6 new ArgumentNullException: $(grep -rn 'new ArgumentNullException' src/ --include='*.cs' 2>/dev/null | wc -l) 处"

# 三类：优化
echo ""
echo "─── 三类：优化 ───"
echo "O2 集合无预分配: (需Roslyn分析，跳过)"
echo "O3 .ToArray/.ToList: $(grep -rn '\.ToArray()\|\.ToList()' src/ --include='*.cs' 2>/dev/null | wc -l) 处"
echo "O5 LINQ链: $(grep -rn '\.Where.*\.Select\|\.Select.*\.Where' src/ --include='*.cs' 2>/dev/null | wc -l) 处"
echo "O9 Enum.ToString: $(grep -rn '\.ToString()' src/ --include='*.cs' 2>/dev/null | wc -l) 处"

echo ""
echo "═══════ 扫描完成 ═══════"
echo "注意：精炼操作需逐项评估后执行，扫描结果仅供参考命中量级。"
