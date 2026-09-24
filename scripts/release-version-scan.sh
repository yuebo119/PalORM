#!/usr/bin/env bash
# release-version-scan.sh——升版本旧号残留机械扫描（发布规范 §1.3 的脚本化，2026-09-25）。
#
# 背景（教训 B91）：人肉 grep 清单漏了 GeneratedCodeMetadata 的 ToolVersion 字面量，
# v5.6.0 升版时靠 doc-consistency D11 才兜住没带病发布。本脚本把四类属性模式 +
# src 双引号版本字面量一次扫齐；"文档计数=实测"类校验（D10）仍归 doc-consistency，不重复造。
#
# 用法: bash scripts/release-version-scan.sh <旧版本> [新版本]
#   <旧版本>  升级前版本号（如 5.5.1）——扫描其残留
#   [新版本]  可选——正向核对其已落真源（Directory.Build.props 的 <Version>）
#
# 扫描面刻意排除 CHANGELOG / docs 历史叙述 / docs/review 审计——那些 5.x 字样是
# 历史记录与当时规划，改了是篡改审计史（B88 的分层纪律）。
#
# 退出码: 0 = 零残留（且新版本已落真源）；1 = 有残留或正向缺失（打印 ::error:: 清单）
set -euo pipefail

OLD="${1:?usage: release-version-scan.sh <old-version> [new-version]}"
NEW="${2:-}"
ROOT=$(git rev-parse --show-toplevel)
cd "$ROOT"

# 点号转义为字面量（grep BRE 里 . 通配）
OLD_RE=$(printf '%s' "$OLD" | sed 's/\./\\./g')
NEW_RE=$(printf '%s' "$NEW" | sed 's/\./\\./g')
fail=0

report() { # <描述> <匹配输出>
    if [ -n "$2" ]; then
        printf '::error::版本残留（%s）:\n%s\n' "$1" "$2"
        fail=1
    fi
}

report "props/csproj 的 <Version> 属性" \
    "$(grep -rn "<Version>${OLD_RE}</Version>" --include='*.props' --include='*.csproj' --exclude-dir=obj --exclude-dir=bin . 2>/dev/null || true)"

report "PackageReference Version= 属性" \
    "$(grep -rn "Version=\"${OLD_RE}\"" --include='*.csproj' --exclude-dir=obj --exclude-dir=bin . 2>/dev/null || true)"

report "README badge version-" \
    "$(grep -n "version-${OLD_RE}" README.md 2>/dev/null || true)"

report "src 双引号版本字面量（含 ToolVersion）" \
    "$(grep -rn "\"${OLD_RE}\"" --include='*.cs' --exclude-dir=obj --exclude-dir=bin src/ 2>/dev/null || true)"

if [ -n "$NEW" ]; then
    if ! grep -q "<Version>${NEW_RE}</Version>" Directory.Build.props; then
        printf '::error::正向核对失败：Directory.Build.props 未落新版本 %s\n' "$NEW"
        fail=1
    fi
fi

if [ "$fail" -eq 0 ]; then
    printf 'PASS 版本残留扫描（旧 %s%s）\n' "$OLD" "${NEW:+ → 新 $NEW}"
fi
exit "$fail"
