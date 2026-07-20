#!/usr/bin/env bash
# PalORM AI 质量系统——引导安装脚本
#
# 用法：复制 .ai/ + scripts/install-ai-system.sh 到新项目根目录后运行：
#   bash install-ai-system.sh
#
# 功能：
#   1. 检测目标项目是否已有 AI 系统
#   2. 询问用户是否启用
#   3. 用户同意 → 复制模板文件 + 输出手动配置清单
#   4. 用户拒绝 → 不做任何改动
#
# 不自动修改 csproj / CI workflow——这些需要手动配置（输出清单指引）。

set -euo pipefail

# 颜色
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m'

echo ""
echo "═══════════════════════════════════════════"
echo " PalORM AI 质量系统——引导安装"
echo "═══════════════════════════════════════════"
echo ""

# ─── 1. 检测 ───
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" && pwd)"
TARGET_DIR="$(pwd)"

if [ -f "$TARGET_DIR/AGENTS.md" ] || [ -d "$TARGET_DIR/.ai" ]; then
    echo -e "${YELLOW}⚠  检测到目标目录已有 AI 系统文件。${NC}"
    echo "  如需重新安装，请先删除 AGENTS.md 和 .ai/ 目录。"
    exit 0
fi

echo "目标目录: $TARGET_DIR"
echo ""

# ─── 2. 询问 ───
echo "PalORM AI 质量系统包含："
echo "  • 项目级 AGENTS.md（ZCode 自动加载入口）"
echo "  • .ai/lessons.md（6 铁律 + 14 缺陷 + SOP + 决策矩阵）"
echo "  • .editorconfig（38 条 Sonar 规则，P0+P1 编译阻断）"
echo "  • scripts/tech-debt-scan.sh（12 类技术债一键扫描）"
echo "  • .github/PULL_REQUEST_TEMPLATE.md（PR 检查清单 6 类）"
echo ""
echo -e "${YELLOW}启用后 AI 会自动加载这些规范，在源头预防反模式。${NC}"
echo ""

read -rp "是否启用 PalORM AI 质量系统？[y/N] " response

if [[ ! "$response" =~ ^[Yy]$ ]]; then
    echo ""
    echo -e "${GREEN}已跳过。项目不受影响。${NC}"
    echo "如需后续启用，重新运行此脚本即可。"
    exit 0
fi

# ─── 3. 安装 ───
echo ""
echo "正在安装..."

# 查找模板目录
TEMPLATE_DIR=""
for candidate in \
    "$SCRIPT_DIR/../.ai/system-template" \
    "$TARGET_DIR/.ai/system-template" \
    "$SCRIPT_DIR/.ai/system-template"; do
    if [ -d "$candidate" ]; then
        TEMPLATE_DIR="$candidate"
        break
    fi
done

if [ -z "$TEMPLATE_DIR" ]; then
    echo -e "${RED}错误：找不到模板目录 .ai/system-template/${NC}"
    echo "请确保从包含 .ai/ 目录的位置运行此脚本。"
    exit 1
fi

echo "模板目录: $TEMPLATE_DIR"
echo ""

# 复制 AGENTS.md
if [ -f "$TEMPLATE_DIR/AGENTS.md.template" ]; then
    cp "$TEMPLATE_DIR/AGENTS.md.template" "$TARGET_DIR/AGENTS.md"
    echo -e "${GREEN}✓ AGENTS.md${NC}（ZCode 自动加载入口）"
fi

# 复制 .ai/ 目录
if [ -d "$TEMPLATE_DIR/.ai-template" ]; then
    cp -r "$TEMPLATE_DIR/.ai-template" "$TARGET_DIR/.ai"
    echo -e "${GREEN}✓ .ai/${NC}（lessons.md + gate/refine/review prompts）"
fi

# 复制技术债扫描脚本
if [ -f "$TEMPLATE_DIR/tech-debt-scan.sh.template" ]; then
    mkdir -p "$TARGET_DIR/scripts"
    cp "$TEMPLATE_DIR/tech-debt-scan.sh.template" "$TARGET_DIR/scripts/tech-debt-scan.sh"
    chmod +x "$TARGET_DIR/scripts/tech-debt-scan.sh"
    echo -e "${GREEN}✓ scripts/tech-debt-scan.sh${NC}（12 类技术债扫描）"
fi

# 复制 PR 模板
if [ -f "$TEMPLATE_DIR/PULL_REQUEST_TEMPLATE.md.template" ]; then
    mkdir -p "$TARGET_DIR/.github"
    cp "$TEMPLATE_DIR/PULL_REQUEST_TEMPLATE.md.template" "$TARGET_DIR/.github/PULL_REQUEST_TEMPLATE.md"
    echo -e "${GREEN}✓ .github/PULL_REQUEST_TEMPLATE.md${NC}（PR 检查清单）"
fi

# ─── 4. 输出手动配置清单 ───
echo ""
echo "═══════════════════════════════════════════"
echo -e "${YELLOW}安装完成！以下需手动配置：${NC}"
echo "═══════════════════════════════════════════"
echo ""
echo "1. .editorconfig"
echo "   从 PalORM 仓库复制 .editorconfig，按项目语言调整规则。"
echo ""
echo "2. SonarAnalyzer 包（如需 CI 编译阻断）"
echo "   Directory.Packages.props:"
echo "     <PackageVersion Include=\"SonarAnalyzer.CSharp\" Version=\"10.29.0.143774\" />"
echo "   Directory.Build.props:"
echo "     <PackageReference Include=\"SonarAnalyzer.CSharp\">"
echo "       <PrivateAssets>all</PrivateAssets>"
echo "     </PackageReference>"
echo ""
echo "3. CI 集成（如需 PR 门禁）"
echo "   .github/workflows/ci.yml gate job 添加:"
echo "     - name: Run tech debt scan"
echo "       run: bash scripts/tech-debt-scan.sh"
echo ""
echo "4. 按项目实际情况修改："
echo "   - .ai/lessons.md 的铁律（C# 特有的调整为项目语言）"
echo "   - scripts/tech-debt-scan.sh 的检查项"
echo "   - AGENTS.md 的启动清单（构建命令等）"
echo ""
echo "详见 .ai/system-template/INSTALL.md"
echo -e "${GREEN}═══════════════════════════════════════════${NC}"
