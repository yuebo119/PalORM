#!/usr/bin/env bash
# PalORM 变异测试脚本——使用 Stryker.NET 验证测试有效性
# 用法：bash scripts/run-mutation-tests.sh
#
# 变异测试会修改源代码注入错误，验证现有测试能否捕获。
# 变异分数 >80% 表示测试体系能有效捕获代码错误。
#
# 前置条件：dotnet tool install -g dotnet-stryker
#
# 配置：test/PalORM.Core.Tests/stryker-config.json
# 报告：StrykerOutput/（HTML + JSON）

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

echo "═══════════════════════════════════════════════════════════════"
echo " PalORM 变异测试（Stryker.NET）"
echo " 时间: $(date)"
echo "═══════════════════════════════════════════════════════════════"

# 检查 dotnet-stryker 是否安装
if ! dotnet stryker --version &>/dev/null; then
    echo ">>> 安装 dotnet-stryder 全局工具..."
    dotnet tool install -g dotnet-stryker
fi

# 进入测试项目目录运行
cd "$ROOT_DIR/test/PalORM.Core.Tests"

echo ">>> 开始变异测试（Core 项目 CRUD/Query/Bulk/SessionState 路径）..."
echo ">>> 变异目标：DataSession.Crud / MultiValueBulkInsert / QueryBuilderExtensions / SessionOperationState"
echo ">>> 预计运行时间：10-30 分钟"
echo ""

dotnet stryker --config-file stryker-config.json

echo ""
echo "═══════════════════════════════════════════════════════════════"
echo " 变异测试完成"
echo " HTML 报告: StrykerOutput/<timestamp>/reports/mutation-report.html"
echo " JSON 报告: StrykerOutput/<timestamp>/reports/mutation-report.json"
echo ""
echo " 变异分数判据："
echo "   >80% = 高（绿）  60-80% = 中（黄）  <60% = 低（红）"
echo "═══════════════════════════════════════════════════════════════"
