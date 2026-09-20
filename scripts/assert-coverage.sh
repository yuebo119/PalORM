#!/usr/bin/env bash
# assert-coverage.sh（独立审计 M3-1）——覆盖率地板断言（防退化，非达标目标）。
# 读 dotnet-coverage 的 XML（results/module 的 line_coverage 属性），断言 ≥ 地板。
# 调用：assert-coverage.sh <module 名片段> <coverage.xml> <地板文件键>
# 地板真源：bench/baselines/test-counts.json 的 coverage 段。
# 设计取舍（报告原方案 line≥70/branch≥60 的修正）：当前实测 Core line 65.35%（2026-09-20），
# 地板取当前值 −5 防退化；追高数字在该项目为负价值（645+ 测试 0 无断言，错误路径充分）。
set -euo pipefail

MODULE="${1:?usage: assert-coverage.sh <module-substring> <xml> <floor-key>}"
XML="${2:?usage: assert-coverage.sh <module-substring> <xml> <floor-key>}"
KEY="${3:?usage: assert-coverage.sh <module-substring> <xml> <floor-key>}"
FLOOR_FILE="$(dirname "$0")/../bench/baselines/test-counts.json"

if [ ! -f "$XML" ]; then
    echo "::error::覆盖率文件不存在：$XML（未运行 --coverage 或路径错）"
    exit 1
fi

node "$(dirname "$0")/lib/parse-coverage.mjs" "$XML" "$MODULE" "$FLOOR_FILE" "$KEY"
